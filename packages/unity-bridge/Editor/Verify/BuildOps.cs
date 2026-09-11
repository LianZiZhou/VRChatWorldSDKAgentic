using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Local world builds through the SDK's public builder API.
    ///
    /// Everything here is a job because a world build takes minutes and is <c>async</c>: the SDK's
    /// Task-based pipeline only advances while EditorApplication.update keeps ticking, which is exactly why
    /// this toolchain runs a GUI editor instead of batch mode. Polling the Task from a frame-stepped job
    /// keeps the update loop free to do the work.
    ///
    /// Upload is deliberately absent. The autonomy boundary for this project is local Build &amp; Test, so
    /// BuildAndUpload and the VRCApi publish calls are not exposed at all rather than merely discouraged.
    /// </summary>
    public static class BuildOps
    {
        private const string DryRunKey = "VRC.SDKBase.DryRun";

        public static void Register()
        {
            BridgeRouter.Register("world.build_status",
                "Active build target, which targets this editor can build, and whether the SDK dry-run flag is set.",
                delegate (Dictionary<string, object> a) { return BuildStatus(); });

            BridgeRouter.Register("world.set_dry_run",
                "Toggle the SDK's dry-run flag, which makes build and upload return success without doing the " +
                "work. Useful for smoke-testing the pipeline. Args: enabled.",
                delegate (Dictionary<string, object> a)
                {
                    bool enabled = Json.GetBool(a, "enabled", false);
                    SessionState.SetBool(DryRunKey, enabled);
                    return new Dictionary<string, object> { { "dryRun", SessionState.GetBool(DryRunKey, false) } };
                });

            BridgeRouter.Register("world.switch_platform",
                "Switch the active build target. Args: platform (Windows|Android|iOS). " +
                "This reloads the script domain, so expect in-flight jobs to be interrupted.",
                delegate (Dictionary<string, object> a) { return SwitchPlatform(a); }, defaultTimeoutMs: 300000);

            JobStore.RegisterKind("verify.build_local", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return BuildLocal(args, ctx);
            });
        }

        // ------------------------------------------------------------------ status

        private static Dictionary<string, object> BuildStatus()
        {
            var supported = new List<object>();
            foreach (var pair in new[]
            {
                new KeyValuePair<BuildTargetGroup, BuildTarget>(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64),
                new KeyValuePair<BuildTargetGroup, BuildTarget>(BuildTargetGroup.Android, BuildTarget.Android),
                new KeyValuePair<BuildTargetGroup, BuildTarget>(BuildTargetGroup.iOS, BuildTarget.iOS),
            })
            {
                supported.Add(new Dictionary<string, object>
                {
                    { "target", pair.Value.ToString() },
                    { "moduleInstalled", BuildPipeline.IsBuildTargetSupported(pair.Key, pair.Value) }
                });
            }

            return new Dictionary<string, object>
            {
                { "activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "targets", supported },
                { "dryRun", SessionState.GetBool(DryRunKey, false) },
                { "lastBuildPath", SessionState.GetString("VRC.SDK3.Editor_patToLastBuild", null) },
                { "numClients", ReadVrcSetting("NumClients") },
                { "forceNoVR", ReadVrcSetting("ForceNoVR") }
            };
        }

        private static object ReadVrcSetting(string name)
        {
            try
            {
                var settings = Reflect.ResolveType("VRC.SDKBase.Editor.VRCSettings");
                var property = settings.GetProperty(name, BindingFlags.Static | BindingFlags.Public);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception) { return null; }
        }

        private static Dictionary<string, object> SwitchPlatform(Dictionary<string, object> a)
        {
            string platform = (Json.GetString(a, "platform") ?? "").Trim();
            BuildTarget target;
            BuildTargetGroup group;

            switch (platform.ToLowerInvariant())
            {
                case "windows":
                case "standalonewindows64":
                case "pc":
                    target = BuildTarget.StandaloneWindows64; group = BuildTargetGroup.Standalone; break;
                case "android":
                case "quest":
                    target = BuildTarget.Android; group = BuildTargetGroup.Android; break;
                case "ios":
                    target = BuildTarget.iOS; group = BuildTargetGroup.iOS; break;
                default:
                    throw new ArgumentException("platform must be Windows, Android or iOS (got \"" + platform + "\")");
            }

            if (!BuildPipeline.IsBuildTargetSupported(group, target))
                throw new InvalidOperationException(
                    "This editor has no build module for " + target + ". Install it through Unity Hub " +
                    "(Add Modules) before building for that platform.");

            if (EditorUserBuildSettings.activeBuildTarget == target)
            {
                return new Dictionary<string, object>
                {
                    { "activeBuildTarget", target.ToString() },
                    { "switched", false },
                    { "note", "Already on this target." }
                };
            }

            // Synchronous on purpose. The async variant returns immediately and the reload lands later,
            // which makes it impossible for the caller to know when it is safe to continue.
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            return new Dictionary<string, object>
            {
                { "requestedTarget", target.ToString() },
                { "switched", switched },
                { "note", "Switching the target reloads the script domain. Poll unity.compile_status until it " +
                          "goes idle, and resubmit any job that was interrupted." }
            };
        }

        // ------------------------------------------------------------------ build

        /// <summary>
        /// Builds the open scene into a local world bundle, the same operation as the SDK panel's Build.
        ///
        /// Preconditions are checked up front and reported rather than thrown, because each one has a
        /// different fix and "the build failed" is useless to an agent: the control panel window must
        /// exist (TryGetBuilder refuses without it), layers and the collision matrix must be set up
        /// (the SDK blocks the build otherwise), and the scene must pass validation.
        /// </summary>
        private static IEnumerator BuildLocal(Dictionary<string, object> args, JobContext ctx)
        {
            long consoleCursor = LogCapture.LatestSeq;
            bool dryRun = Json.GetBool(args, "dryRun", false);
            bool previousDryRun = SessionState.GetBool(DryRunKey, false);
            if (dryRun) SessionState.SetBool(DryRunKey, true);

            try
            {
                ctx.Report(0.05f, "checking preconditions");

                var layersOk = Json.AsObject(null);
                object builder = null;
                string why = null;

                try { builder = AcquireWorldBuilder(out why); }
                catch (Exception ex) { why = ex.Message; }

                if (builder == null)
                {
                    ctx.SetResult(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "stage", "precondition" },
                        { "reason", why },
                        { "hint", "world.validate reports the same preconditions in more detail." }
                    });
                    yield break;
                }

                string exportTemp = EnsureExportTempDirectory();

                ctx.Report(0.15f, "starting build");
                yield return null;

                var buildMethod = builder.GetType().GetMethod("Build", new Type[0]);
                if (buildMethod == null) throw new MissingMethodException("VRChat SDK API moved: IVRCSdkWorldBuilderApi.Build()");

                object taskObject;
                try { taskObject = buildMethod.Invoke(builder, null); }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    ctx.SetResult(FailureResult("invoke", inner, consoleCursor));
                    yield break;
                }

                var task = taskObject as Task;
                if (task == null) throw new InvalidOperationException("Build() did not return a Task.");

                var started = DateTime.UtcNow;
                int ticks = 0;
                while (!task.IsCompleted)
                {
                    if (ctx.CancelRequested) yield break;
                    if (++ticks % 30 == 0)
                        ctx.Report(0.2f, "building, " + (DateTime.UtcNow - started).TotalSeconds.ToString("0") + "s elapsed");
                    yield return null;
                }

                if (task.IsFaulted)
                {
                    var failure = task.Exception == null ? null : task.Exception.GetBaseException();
                    ctx.SetResult(FailureResult("build", failure, consoleCursor));
                    yield break;
                }

                string path = null;
                var resultProperty = task.GetType().GetProperty("Result");
                if (resultProperty != null)
                {
                    object value = resultProperty.GetValue(task, null);
                    path = value as string;
                }

                long size = 0;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) size = new FileInfo(path).Length;

                ctx.SetResult(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "stage", "built" },
                    { "dryRun", dryRun },
                    { "bundlePath", path },
                    { "bundleBytes", size },
                    { "bundleMegabytes", size / (1024.0 * 1024.0) },
                    { "platform", EditorUserBuildSettings.activeBuildTarget.ToString() },
                    { "elapsedSeconds", (DateTime.UtcNow - started).TotalSeconds },
                    { "exportTempDirectory", exportTemp },
                    { "console", LogCapture.Read(consoleCursor, 200, "warning", null) }
                });
            }
            finally
            {
                if (dryRun) SessionState.SetBool(DryRunKey, previousDryRun);
            }
        }

        private static Dictionary<string, object> FailureResult(string stage, Exception failure, long consoleCursor)
        {
            var result = new Dictionary<string, object>
            {
                { "ok", false },
                { "stage", stage },
                { "exception", failure == null ? null : failure.GetType().FullName },
                { "message", failure == null ? "unknown failure" : failure.Message },
                { "console", LogCapture.Read(consoleCursor, 200, "warning", null) }
            };

            // ValidationException carries the actual list of blocking problems, which is the only part an
            // agent can act on.
            if (failure != null)
            {
                var errorsProperty = failure.GetType().GetProperty("Errors");
                if (errorsProperty != null)
                {
                    object errors = errorsProperty.GetValue(failure, null);
                    result["validationErrors"] = errors;
                }
            }
            return result;
        }

        /// <summary>
        /// Creates the temp folder the SDK's world exporter writes through.
        ///
        /// VRCWorldAssetExporter stages the bundle under %TEMP%\&lt;companyName&gt;\&lt;productName&gt; and starts by
        /// File.Delete-ing a stale file there. If the folder has been cleaned away — Windows temp cleanup,
        /// or a reboot — that delete throws DirectoryNotFoundException and the whole build fails with the
        /// unhelpful "Scene export failed. Check console output for details." Observed exactly that: the
        /// same scene built fine, then failed later for no reason visible in the project.
        ///
        /// Creating the folder is cheap and idempotent, so it is done before every build rather than
        /// leaving an agent to diagnose an environmental failure that looks like a content failure.
        /// </summary>
        private static string EnsureExportTempDirectory()
        {
            try
            {
                string company = string.IsNullOrEmpty(Application.companyName) ? "DefaultCompany" : Application.companyName;
                string product = string.IsNullOrEmpty(Application.productName) ? "Project" : Application.productName;
                string path = Path.Combine(Path.Combine(Path.GetTempPath(), company), product);
                Directory.CreateDirectory(path);
                return path.Replace('\\', '/');
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[agentic-vrcw] Could not create the SDK export temp directory: " + ex.Message);
                return null;
            }
        }

        /// <summary>Gets the world builder, opening the SDK panel if needed, or explains why it cannot.</summary>
        internal static object AcquireWorldBuilder(out string why)
        {
            why = null;

            var panelType = Reflect.ResolveType("VRCSdkControlPanel");
            var windowField = panelType.GetField("window", BindingFlags.Static | BindingFlags.Public);
            var panel = windowField == null ? null : windowField.GetValue(null) as EditorWindow;

            if (panel == null)
            {
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                panel = windowField == null ? null : windowField.GetValue(null) as EditorWindow;
            }
            if (panel == null)
            {
                why = "The SDK control panel window is not open; TryGetBuilder refuses to return a builder without it.";
                return null;
            }

            var builderApiType = Reflect.ResolveType("VRC.SDK3.Editor.IVRCSdkWorldBuilderApi");
            var tryGet = panelType.GetMethod("TryGetBuilder", BindingFlags.Static | BindingFlags.Public);
            if (tryGet == null) { why = "VRChat SDK API moved: VRCSdkControlPanel.TryGetBuilder"; return null; }

            var callArgs = new object[] { null };
            bool got = (bool)tryGet.MakeGenericMethod(builderApiType).Invoke(null, callArgs);
            if (!got || callArgs[0] == null)
            {
                why = "The SDK panel exposed no world builder. Signing in may be required, and if the V3 " +
                      "pipeline is enabled it takes priority over the normal builder.";
                return null;
            }

            object builder = callArgs[0];

            // IsValidBuilder is what populates the builder's scene list; Build() throws without it.
            var isValid = builder.GetType().GetMethod("IsValidBuilder", new[] { typeof(string).MakeByRefType() });
            if (isValid != null)
            {
                var validArgs = new object[] { null };
                bool valid = (bool)isValid.Invoke(builder, validArgs);
                if (!valid)
                {
                    why = "The SDK reports the builder is not ready: " + (validArgs[0] as string ?? "no reason given");
                    return null;
                }
            }

            return builder;
        }
    }
}
