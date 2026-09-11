using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// The M0 method surface: enough for an agent to see what the editor is doing and to run arbitrary
    /// editor code, which together cover the long tail without needing hundreds of micro-tools.
    /// </summary>
    public static class BridgeMethods
    {
        public static void RegisterAll()
        {
            RegisterIntrospection();
            RegisterExecution();
            RegisterJobs();
            UdonOps.Register();
        }

        // ---------- introspection ----------

        private static void RegisterIntrospection()
        {
            // Off the main thread on purpose: health has to stay answerable precisely when the editor
            // is too busy to answer anything else.
            BridgeRouter.Register("bridge.health", "Bridge liveness, pump health and editor identity.",
                delegate (Dictionary<string, object> a)
                {
                    return new Dictionary<string, object>
                    {
                        { "bridgeVersion", BridgePaths.BridgeVersion },
                        { "port", BridgeServer.Port },
                        { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                        { "projectPath", BridgePaths.ProjectRoot.Replace('\\', '/') },
                        { "pumpTicks", MainThread.TickCount },
                        { "lastPumpUtc", MainThread.LastTickUtc.ToString("o") },
                        { "secondsSincePump", (DateTime.UtcNow - MainThread.LastTickUtc).TotalSeconds },
                        { "queueDepth", MainThread.QueueDepth },
                        { "methodCount", BridgeRouter.Count },
                        { "jobKinds", JobStore.KindNames }
                    };
                }, requiresMainThread: false);

            BridgeRouter.Register("bridge.describe", "List every bridge method.",
                delegate (Dictionary<string, object> a) { return BridgeRouter.Describe(); },
                requiresMainThread: false);

            BridgeRouter.Register("unity.console",
                "Console entries after a sequence number. Args: sinceSeq, limit, level(all|error|warning), grep.",
                delegate (Dictionary<string, object> a)
                {
                    return LogCapture.Read(
                        Json.GetLong(a, "sinceSeq", 0),
                        Json.GetInt(a, "limit", 200),
                        Json.GetString(a, "level", "all"),
                        Json.GetString(a, "grep"));
                }, requiresMainThread: false);

            BridgeRouter.Register("unity.compile_status",
                "Editor compile/import state plus structured C# diagnostics from the last compile.",
                delegate (Dictionary<string, object> a) { return CompileWatcher.Read(); });

            BridgeRouter.Register("unity.editor_state",
                "Open scenes, active build target, and other coarse editor state.",
                delegate (Dictionary<string, object> a) { return EditorStateProbe.Read(); });
        }

        // ---------- execution ----------

        private static void RegisterExecution()
        {
            BridgeRouter.Register("unity.eval_csharp",
                "Compile and run a C# snippet in the editor. Args: code (statements; use 'return x;'), usings[], maxDepth.",
                delegate (Dictionary<string, object> a)
                {
                    string code = Json.GetString(a, "code");
                    if (string.IsNullOrEmpty(code)) throw new ArgumentException("eval_csharp needs \"code\"");
                    return CSharpEval.Run(code, Json.GetStringList(a, "usings"), Json.GetInt(a, "maxDepth", Json.DefaultMaxDepth),
                        Json.GetBool(a, "includeReferences", false));
                }, defaultTimeoutMs: 120000);

            // Deterministic alternative to eval: no compiler involved, so it works even if the Mono
            // CodeDom provider is unavailable, and it is the right tool for the many cases that are
            // just "call this one SDK static".
            BridgeRouter.Register("unity.invoke_static",
                "Invoke a static method by name. Args: type (assembly-qualified or full name), method, args[], nonPublic.",
                delegate (Dictionary<string, object> a) { return Reflect.InvokeStatic(a); });

            BridgeRouter.Register("unity.get_static",
                "Read a static field or property. Args: type, member, nonPublic.",
                delegate (Dictionary<string, object> a) { return Reflect.GetStatic(a); });

            BridgeRouter.Register("unity.menu_item",
                "Execute an editor menu item, e.g. \"VRChat SDK/Show Control Panel\". Args: path.",
                delegate (Dictionary<string, object> a)
                {
                    string path = Json.GetString(a, "path");
                    if (string.IsNullOrEmpty(path)) throw new ArgumentException("menu_item needs \"path\"");
                    bool executed = EditorApplication.ExecuteMenuItem(path);
                    return new Dictionary<string, object> { { "path", path }, { "executed", executed } };
                });

            BridgeRouter.Register("unity.refresh_assets",
                "AssetDatabase.Refresh, optionally saving dirty assets first. Args: saveAssets, forceSynchronous.",
                delegate (Dictionary<string, object> a)
                {
                    if (Json.GetBool(a, "saveAssets", false)) AssetDatabase.SaveAssets();
                    var options = Json.GetBool(a, "forceSynchronous", true)
                        ? ImportAssetOptions.ForceSynchronousImport
                        : ImportAssetOptions.Default;
                    AssetDatabase.Refresh(options);
                    return new Dictionary<string, object>
                    {
                        { "refreshed", true },
                        { "isCompiling", EditorApplication.isCompiling },
                        { "isUpdatingAssets", EditorApplication.isUpdating }
                    };
                }, defaultTimeoutMs: 120000);
        }

        // ---------- jobs ----------

        private static void RegisterJobs()
        {
            // All four are off the main thread by design. If job.status needed the main thread it
            // would queue behind the very job it is reporting on, and polling would always time out.
            BridgeRouter.Register("job.submit", "Start a long-running job. Args: kind, params{}.",
                delegate (Dictionary<string, object> a)
                {
                    string kind = Json.GetString(a, "kind");
                    if (string.IsNullOrEmpty(kind)) throw new ArgumentException("job.submit needs \"kind\"");
                    object rawParams;
                    a.TryGetValue("params", out rawParams);
                    return JobStore.Submit(kind, Json.AsObject(rawParams));
                }, requiresMainThread: false);

            BridgeRouter.Register("job.status", "Poll one job. Args: id.",
                delegate (Dictionary<string, object> a)
                {
                    string id = Json.GetString(a, "id");
                    if (string.IsNullOrEmpty(id)) throw new ArgumentException("job.status needs \"id\"");
                    var record = JobStore.Get(id);
                    if (record == null) throw new ArgumentException("No such job: " + id);
                    return record;
                }, requiresMainThread: false);

            BridgeRouter.Register("job.list", "Recent jobs, newest first. Args: limit.",
                delegate (Dictionary<string, object> a) { return JobStore.List(Json.GetInt(a, "limit", 25)); },
                requiresMainThread: false);

            BridgeRouter.Register("job.cancel", "Ask a running job to stop. Args: id.",
                delegate (Dictionary<string, object> a)
                {
                    string id = Json.GetString(a, "id");
                    bool requested = JobStore.Cancel(id);
                    return new Dictionary<string, object> { { "id", id }, { "cancelRequested", requested } };
                }, requiresMainThread: false);

            // Proves the whole job path end to end (submit -> progress -> poll -> result) without
            // depending on any VRChat API. The real kinds land in later milestones.
            JobStore.RegisterKind("debug.tick", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return DebugTick(args, ctx);
            });
        }

        private static IEnumerator DebugTick(Dictionary<string, object> args, JobContext ctx)
        {
            int ticks = Mathf.Clamp(Json.GetInt(args, "ticks", 60), 1, 100000);
            for (int i = 0; i < ticks; i++)
            {
                if (ctx.CancelRequested) yield break;
                ctx.Report((float)(i + 1) / ticks, "tick " + (i + 1) + "/" + ticks);
                yield return null;
            }
            ctx.SetResult(new Dictionary<string, object> { { "ticks", ticks }, { "finishedUtc", DateTime.UtcNow.ToString("o") } });
        }
    }
}
