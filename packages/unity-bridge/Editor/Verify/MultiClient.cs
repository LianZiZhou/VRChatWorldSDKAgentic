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
    /// Build &amp; Test against real VRChat clients — the only way to verify networking.
    ///
    /// ClientSim is single-player and simulates no networking at all, so nothing in verify.clientsim can
    /// catch an ownership mistake, a late-joiner that never receives state, or a synced variable that never
    /// converges. The SDK's own Build &amp; Test can launch up to 8 local clients into one instance
    /// (VRCSettings.NumClients), and their logs are readable, so that is the path.
    ///
    /// Preconditions are reported rather than thrown. This needs a VRChat client installed, a signed-in
    /// account and a PC build target, and when any of those is missing the useful answer is which one —
    /// not an exception. On a machine with no VRChat client this reports exactly that and stops.
    /// </summary>
    public static class MultiClient
    {
        public static void Register()
        {
            BridgeRouter.Register("verify.multiclient_preflight",
                "Everything that must be true before Build & Test can launch real clients, and which parts are.",
                delegate (Dictionary<string, object> a) { return Preflight(); });

            JobStore.RegisterKind("verify.multiclient", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return RunMultiClient(args, ctx);
            });
        }

        // ------------------------------------------------------------------ preflight

        private static Dictionary<string, object> Preflight()
        {
            string clientPath = FindVRChatClient();
            bool loggedIn = IsLoggedIn();
            string builderWhy;
            object builder = null;
            try { builder = BuildOps.AcquireWorldBuilder(out builderWhy); }
            catch (Exception ex) { builderWhy = ex.Message; }

            bool pcTarget = EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64;
            bool logDirExists = Directory.Exists(VrcLogReader.LogDirectory);

            var blockers = new List<string>();
            if (string.IsNullOrEmpty(clientPath))
                blockers.Add("No VRChat client found. Build & Test launches the real client, so one must be installed " +
                             "and its path known to the SDK (SDK panel > Settings > VRChat Client).");
            if (!loggedIn)
                blockers.Add("Not signed in. The SDK panel only registers builders while APIUser.IsLoggedIn, so sign in " +
                             "once in the control panel.");
            if (builder == null)
                blockers.Add("No world builder available: " + builderWhy);
            if (!pcTarget)
                blockers.Add("Active build target is " + EditorUserBuildSettings.activeBuildTarget +
                             ". Build & Test launches a PC client, so switch to Windows first (world.switch_platform).");

            return new Dictionary<string, object>
            {
                { "ready", blockers.Count == 0 },
                { "blockers", blockers },
                { "vrchatClientPath", clientPath },
                { "loggedIn", loggedIn },
                { "builderAvailable", builder != null },
                { "activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "logDirectory", VrcLogReader.LogDirectory.Replace('\\', '/') },
                { "logDirectoryExists", logDirExists },
                { "maxClients", 8 },
                { "requiredLaunchFlags", VrcLogReader.RequiredLaunchFlags },
                { "telemetryContract",
                    "Udon cannot send data out (VRCUrl is not constructible at runtime), so a world under test " +
                    "should Debug.Log lines of the form " + VrcLogReader.TelemetryTag + "{\"k\":\"v\"} and this " +
                    "reads them back from the client logs." }
            };
        }

        /// <summary>
        /// Locates the VRChat client: first whatever the SDK has recorded, then the usual Steam layouts.
        /// </summary>
        private static string FindVRChatClient()
        {
            // The SDK keeps its own notion of the client path; prefer it so we launch what the SDK would.
            foreach (string typeName in new[] { "VRC.SDKBase.Editor.SDKClientUtilities", "SDKClientUtilities" })
            {
                try
                {
                    var type = Reflect.ResolveType(typeName);
                    foreach (string methodName in new[] { "GetSavedVRCInstallPath", "GetVRCInstallPath", "FindVRCInstallPath" })
                    {
                        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (method == null || method.GetParameters().Length != 0) continue;
                        string path = method.Invoke(null, null) as string;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path.Replace('\\', '/');
                    }
                }
                catch (Exception) { /* type or member absent in this SDK build */ }
            }

            foreach (string candidate in new[]
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\VRChat\VRChat.exe",
                @"C:\Program Files\Steam\steamapps\common\VRChat\VRChat.exe",
                @"D:\Steam\steamapps\common\VRChat\VRChat.exe",
                @"D:\SteamLibrary\steamapps\common\VRChat\VRChat.exe",
                @"E:\Steam\steamapps\common\VRChat\VRChat.exe",
                @"E:\SteamLibrary\steamapps\common\VRChat\VRChat.exe",
            })
            {
                if (File.Exists(candidate)) return candidate.Replace('\\', '/');
            }
            return null;
        }

        private static bool IsLoggedIn()
        {
            try
            {
                var apiUser = Reflect.ResolveType("VRC.Core.APIUser");
                var property = apiUser.GetProperty("IsLoggedIn", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return property != null && (bool)property.GetValue(null, null);
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ the job

        private static IEnumerator RunMultiClient(Dictionary<string, object> args, JobContext ctx)
        {
            int clients = Mathf.Clamp(Json.GetInt(args, "clients", 2), 1, 8);
            int observeSeconds = Mathf.Clamp(Json.GetInt(args, "observeSeconds", 60), 5, 1800);
            bool forceNoVR = Json.GetBool(args, "forceNoVR", true);

            ctx.Report(0.05f, "preflight");
            var preflight = Preflight();
            if (!(bool)preflight["ready"])
            {
                ctx.SetResult(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "stage", "preflight" },
                    { "blockers", preflight["blockers"] },
                    { "preflight", preflight }
                });
                yield break;
            }

            var startedUtc = DateTime.UtcNow;
            object previousNumClients = ReadVrcSetting("NumClients");
            object previousForceNoVR = ReadVrcSetting("ForceNoVR");

            try
            {
                WriteVrcSetting("NumClients", clients);
                WriteVrcSetting("ForceNoVR", forceNoVR);

                string why;
                object builder = BuildOps.AcquireWorldBuilder(out why);
                if (builder == null)
                {
                    ctx.SetResult(new Dictionary<string, object> { { "ok", false }, { "stage", "builder" }, { "reason", why } });
                    yield break;
                }

                ctx.Report(0.15f, "building and launching " + clients + " client(s)");
                yield return null;

                var method = builder.GetType().GetMethod("BuildAndTest", new Type[0]);
                if (method == null) throw new MissingMethodException("VRChat SDK API moved: IVRCSdkWorldBuilderApi.BuildAndTest()");

                object taskObject;
                try { taskObject = method.Invoke(builder, null); }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    ctx.SetResult(new Dictionary<string, object>
                    {
                        { "ok", false }, { "stage", "buildAndTest" },
                        { "exception", inner.GetType().FullName }, { "message", inner.Message }
                    });
                    yield break;
                }

                var task = taskObject as Task;
                int guard = 0;
                while (task != null && !task.IsCompleted && guard++ < 60 * 60 * 30)
                {
                    if (ctx.CancelRequested) yield break;
                    if (guard % 60 == 0) ctx.Report(0.3f, "building, " + (DateTime.UtcNow - startedUtc).TotalSeconds.ToString("0") + "s");
                    yield return null;
                }

                if (task != null && task.IsFaulted)
                {
                    var failure = task.Exception == null ? null : task.Exception.GetBaseException();
                    ctx.SetResult(new Dictionary<string, object>
                    {
                        { "ok", false }, { "stage", "buildAndTest" },
                        { "message", failure == null ? "unknown failure" : failure.Message }
                    });
                    yield break;
                }

                // The clients start after the build finishes; give them time to load the world and talk to
                // each other before reading their logs.
                ctx.Report(0.6f, "observing clients for " + observeSeconds + "s");
                double until = EditorApplication.timeSinceStartup + observeSeconds;
                while (EditorApplication.timeSinceStartup < until)
                {
                    if (ctx.CancelRequested) yield break;
                    yield return null;
                }

                ctx.Report(0.9f, "reading client logs");
                var logs = ReadLogsSince(startedUtc, clients);

                var exceptions = logs["udonExceptions"] as List<object> ?? new List<object>();
                ctx.SetResult(new Dictionary<string, object>
                {
                    { "ok", exceptions.Count == 0 },
                    { "stage", "observed" },
                    { "clients", clients },
                    { "observeSeconds", observeSeconds },
                    { "udonExceptionCount", exceptions.Count },
                    { "logs", logs },
                    { "note", "Sync correctness must be asserted from the telemetry this reports: have the world " +
                              "Debug.Log " + VrcLogReader.TelemetryTag + " lines carrying the state you care about." }
                });
            }
            finally
            {
                if (previousNumClients != null) WriteVrcSetting("NumClients", previousNumClients);
                if (previousForceNoVR != null) WriteVrcSetting("ForceNoVR", previousForceNoVR);
            }
        }

        private static Dictionary<string, object> ReadLogsSince(DateTime startedUtc, int clients)
        {
            var args = new Dictionary<string, object>
            {
                // One log file per client, plus headroom for a client that restarted.
                { "maxFiles", Mathf.Clamp(clients + 2, 2, 12) },
                { "maxLines", 8000 },
                { "sinceUtc", startedUtc.AddMinutes(-1).ToString("o") }
            };

            BridgeMethod method;
            if (!BridgeRouter.TryGet("verify.read_vrc_logs", out method))
                return new Dictionary<string, object> { { "error", "verify.read_vrc_logs is not registered" } };
            return method.Handler(args) as Dictionary<string, object> ?? new Dictionary<string, object>();
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

        private static void WriteVrcSetting(string name, object value)
        {
            try
            {
                var settings = Reflect.ResolveType("VRC.SDKBase.Editor.VRCSettings");
                var property = settings.GetProperty(name, BindingFlags.Static | BindingFlags.Public);
                if (property == null || !property.CanWrite) return;
                property.SetValue(null, Convert.ChangeType(value, property.PropertyType), null);
            }
            catch (Exception) { }
        }
    }
}
