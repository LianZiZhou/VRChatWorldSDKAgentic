using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Brings the bundled generic Unity MCP package (CoplayDev, MIT) online, and keeps it there.
    ///
    /// That package covers the generic editor surface this bridge deliberately does not reimplement —
    /// lightmap and GI baking, NavMesh, prefabs, the Test Runner, profiling, animation, VFX. Three things
    /// stand between "installed" and "usable", none of which happens on its own:
    ///
    ///   1. Unity only rescans `Packages/` for embedded packages at editor start. Dropping a package in
    ///      while the editor runs does nothing until `PackageManager.Client.Resolve()` is called — an
    ///      AssetDatabase refresh is not enough, and the symptom is an installed package whose assemblies
    ///      never compile.
    ///   2. Its bridge is gated behind the EditorPref `MCPForUnity.AutoStartOnLoad`, which defaults to
    ///      false. Without it the server reports "No Unity Editor instances found".
    ///   3. Its bridge does not survive a domain reload: it closes its listener and deletes the status file
    ///      its server uses to find the editor. Nothing in the package brings it back, and setting the pref
    ///      does not either — so it has to be started again after every recompile. CompanionAutoEnable does
    ///      that, which is the difference between shipping the package and shipping a working one.
    ///
    /// Everything here is reflective and degrades to a report. The companion package is optional: this
    /// assembly must compile and run in a project that does not have it.
    /// </summary>
    public static class CompanionSetup
    {
        private const string PackageName = "com.coplaydev.unity-mcp";
        private const string AutoStartPref = "MCPForUnity.AutoStartOnLoad";
        private const string TelemetryDisabledPref = "MCPForUnity.TelemetryDisabled";
        private static readonly string[] TelemetryOptOutVars =
            { "DISABLE_TELEMETRY", "UNITY_MCP_DISABLE_TELEMETRY", "MCP_DISABLE_TELEMETRY" };
        private const string BridgeHostType = "MCPForUnity.Editor.Services.Transport.Transports.StdioBridgeHost";

        public static void Register()
        {
            BridgeRouter.Register("companion.status",
                "Whether the bundled generic Unity MCP package is installed, compiled and serving.",
                delegate (Dictionary<string, object> a) { return Status(); });

            BridgeRouter.Register("companion.setup",
                "Make the bundled generic Unity MCP package usable: resolve the package, enable its " +
                "auto-start pref, and start its bridge. Idempotent. Args: apply (default true).",
                delegate (Dictionary<string, object> a) { return Setup(a); }, defaultTimeoutMs: 300000);
        }

        // ------------------------------------------------------------------ status

        private static Dictionary<string, object> Status()
        {
            string packageDir = Path.Combine(BridgePaths.ProjectRoot, Path.Combine("Packages", PackageName));
            bool installed = Directory.Exists(packageDir) && File.Exists(Path.Combine(packageDir, "package.json"));

            var loaded = new List<object>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; } catch (Exception) { continue; }
                if (name != null && name.StartsWith("MCPForUnity", StringComparison.Ordinal)) loaded.Add(name);
            }

            var status = ReadStatusFile();
            bool serving = IsServing(status);
            bool autoStart = EditorPrefs.GetBool(AutoStartPref, false);

            var result = new Dictionary<string, object>
            {
                { "packageName", PackageName },
                { "installed", installed },
                { "packageDirectory", installed ? packageDir.Replace('\\', '/') : null },
                { "assembliesLoaded", loaded },
                { "compiled", loaded.Count > 0 },
                { "autoStartOnLoad", autoStart },
                { "telemetryDisabled", EditorPrefs.GetBool(TelemetryDisabledPref, false) },
                { "telemetryOptOutEnv", Environment.GetEnvironmentVariable("DISABLE_TELEMETRY") == "1" },
                { "bridge", status },
                { "serving", serving }
            };

            var nextSteps = new List<string>();
            if (!installed)
                nextSteps.Add("The package is not in Packages/. Run the installer, which links it from third_party/unity-mcp/MCPForUnity.");
            else if (loaded.Count == 0)
                nextSteps.Add("Installed but not compiled: Unity only rescans Packages/ at startup. Call companion.setup, which calls PackageManager.Client.Resolve().");
            else if (!autoStart)
                nextSteps.Add("Compiled but its bridge will not auto-start: the EditorPref " + AutoStartPref + " defaults to false. Call companion.setup.");
            else if (status == null)
                nextSteps.Add("Auto-start is enabled but no status file exists yet. Call companion.setup to start the bridge now.");
            else if (!serving)
                nextSteps.Add("A status file exists but nothing is listening on port " +
                              Json.GetInt(status, "unity_port", 0) + ". It was left behind by an editor that is " +
                              "gone — its own server will chase that dead port too. Call companion.setup.");

            result["ready"] = installed && loaded.Count > 0 && autoStart && serving;
            result["nextSteps"] = nextSteps;
            return result;
        }

        /// <summary>
        /// Reads the companion's own status file, which is how its Python server finds the editor.
        /// Path and name come from its StdioBridgeHost: ~/.unity-mcp/unity-mcp-status-&lt;projectHash&gt;.json.
        /// </summary>
        private static Dictionary<string, object> ReadStatusFile()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-mcp");
                if (!Directory.Exists(dir)) return null;

                foreach (string file in Directory.GetFiles(dir, "unity-mcp-status-*.json"))
                {
                    var parsed = Json.AsObject(Json.Parse(File.ReadAllText(file)));
                    string project = Json.GetString(parsed, "project_path", "");
                    // Several editors may be running; match the one describing this project.
                    if (project != null && project.Replace('\\', '/')
                            .StartsWith(BridgePaths.ProjectRoot.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        parsed["statusFile"] = file.Replace('\\', '/');
                        return parsed;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Whether anything is actually listening on the port the status file advertises.
        ///
        /// The file is not evidence. It survives a crash, a kill and a reboot, and the companion only rewrites
        /// it on a state change rather than on a timer, so neither its presence nor its timestamp says whether
        /// the bridge is alive. After one machine restart this reported a healthy companion for a port nothing
        /// had listened on since the previous day, and the server it was meant to serve answered every call
        /// with "No Unity Editor instances found". A connect attempt is the only honest test — the same reason
        /// the editor registry checks process liveness rather than believing its own file.
        /// </summary>
        private static bool IsServing(Dictionary<string, object> status)
        {
            if (status == null) return false;
            int port = Json.GetInt(status, "unity_port", 0);
            if (port <= 0 || port > 65535) return false;

            try
            {
                using (var probe = new System.Net.Sockets.TcpClient())
                {
                    var connect = probe.BeginConnect(System.Net.IPAddress.Loopback, port, null, null);
                    // Short: this is loopback, and companion.status is called from tools that should not block.
                    if (!connect.AsyncWaitHandle.WaitOne(400)) return false;
                    probe.EndConnect(connect);
                    return probe.Connected;
                }
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ telemetry

        /// <summary>
        /// Switches the companion's usage reporting off, on both halves of it.
        ///
        /// Its default is opt-out: the Unity half posts events unless an EditorPref or an environment
        /// variable says not to, and the Python half reads only environment variables — it has no persisted
        /// setting at all. That second detail is the one that matters here. Passing the variables in the MCP
        /// client's server entry covers the server the client launches, but the Unity package also launches
        /// a server of its own on load, and that one inherits the editor's environment instead. Setting the
        /// variables inside the editor process is what covers it, because child processes inherit them.
        ///
        /// This is a changed default, not a judgement: a toolchain meant to run unattended should not begin
        /// reporting to a third party because nobody thought to look. Reversing it is one EditorPref.
        /// </summary>
        internal static List<string> DisableCompanionTelemetry()
        {
            var done = new List<string>();

            if (!EditorPrefs.GetBool(TelemetryDisabledPref, false))
            {
                EditorPrefs.SetBool(TelemetryDisabledPref, true);
                done.Add("set EditorPref " + TelemetryDisabledPref + " = true (the editor-side reporter)");
            }

            for (int i = 0; i < TelemetryOptOutVars.Length; i++)
            {
                string name = TelemetryOptOutVars[i];
                if (Environment.GetEnvironmentVariable(name) == "1") continue;
                Environment.SetEnvironmentVariable(name, "1");
                done.Add("set " + name + "=1 in the editor process, which any server it launches inherits");
            }

            return done;
        }

        // ------------------------------------------------------------------ used by the on-load hook

        /// <summary>
        /// null = the companion package is not installed; false = installed but its assemblies have not
        /// compiled yet; true = ready to be switched on.
        /// </summary>
        internal static bool? StatusForAutoEnable()
        {
            var status = Status();
            if (!(bool)status["installed"]) return null;
            return (bool)status["compiled"];
        }

        /// <summary>Enable and start the companion without producing a report. Safe to call twice.</summary>
        internal static void EnableQuietly()
        {
            DisableCompanionTelemetry();
            if (!EditorPrefs.GetBool(AutoStartPref, false)) EditorPrefs.SetBool(AutoStartPref, true);
            if (IsServing(ReadStatusFile())) return;

            var host = Reflect.ResolveType(BridgeHostType);
            var start = host == null ? null : host.GetMethod("StartAutoConnect",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (start != null) start.Invoke(null, null);
        }

        // ------------------------------------------------------------------ setup

        private static Dictionary<string, object> Setup(Dictionary<string, object> a)
        {
            bool apply = Json.GetBool(a, "apply", true);
            var before = Status();
            var actions = new List<string>();

            if (!(bool)before["installed"])
            {
                before["actionsTaken"] = actions;
                before["error"] = "The companion package is not installed in Packages/. The installer links it; " +
                                  "this method cannot create it.";
                return before;
            }

            if (!apply)
            {
                before["actionsTaken"] = actions;
                before["note"] = "apply was false, so nothing was changed.";
                return before;
            }

            // 1. Make Unity notice the package. Only needed when it was dropped in while the editor ran.
            if (!(bool)before["compiled"])
            {
                try
                {
                    var client = Reflect.ResolveType("UnityEditor.PackageManager.Client");
                    var resolve = client == null ? null : client.GetMethod("Resolve",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (resolve == null) actions.Add("PackageManager.Client.Resolve not found in this Unity version");
                    else
                    {
                        resolve.Invoke(null, null);
                        actions.Add("PackageManager.Client.Resolve() — a domain reload follows, so poll companion.status afterwards");
                    }
                }
                catch (Exception ex) { actions.Add("Resolve failed: " + ex.Message); }
            }

            // 2. Its bridge only auto-starts when this pref is set, and it defaults to false.
            if (!EditorPrefs.GetBool(AutoStartPref, false))
            {
                EditorPrefs.SetBool(AutoStartPref, true);
                actions.Add("set EditorPref " + AutoStartPref + " = true");
            }

            // Before starting anything, so the server it launches inherits the opt-out.
            actions.AddRange(DisableCompanionTelemetry());

            // 3. Start it now, so the caller does not have to restart the editor. Keyed on whether it is
            //    actually serving, not on whether a status file happens to exist.
            if ((bool)before["compiled"] && !(bool)before["serving"])
            {
                try
                {
                    var host = Reflect.ResolveType(BridgeHostType);
                    var start = host == null ? null : host.GetMethod("StartAutoConnect",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (start == null) actions.Add("StdioBridgeHost.StartAutoConnect not found — the companion's API may have moved");
                    else { start.Invoke(null, null); actions.Add("StdioBridgeHost.StartAutoConnect()"); }
                }
                catch (Exception ex) { actions.Add("StartAutoConnect failed: " + ex.Message); }
            }

            var after = Status();
            after["actionsTaken"] = actions;
            if (!(bool)after["compiled"])
                after["note"] = "Its assemblies are still compiling after Resolve(). Poll companion.status until ready " +
                                "is true, then its server can reach the editor.";
            return after;
        }
    }

    /// <summary>
    /// Turns the bundled generic MCP package on the first time an editor loads with it present.
    ///
    /// Its bridge is gated behind an EditorPref that defaults to false, so without this the package ships
    /// installed but mute, and the server reports "No Unity Editor instances found" — the opposite of
    /// out-of-the-box. Enabling it is recorded in a marker pref, so turning it off afterwards sticks:
    /// this runs once per project, not once per reload.
    ///
    /// Runs from EditorApplication.update rather than directly in the static constructor, for two reasons:
    /// the companion's own [InitializeOnLoad] has to have run before StartAutoConnect means anything, and
    /// delayCall has been observed not to fire at all after a domain reload.
    /// </summary>
    [InitializeOnLoad]
    internal static class CompanionAutoEnable
    {
        private const string MarkerPref = "Agentic.Vrcw.CompanionAutoEnabled";
        private const string OptOutPref = "Agentic.Vrcw.CompanionAutoEnableDisabled";
        private static int _ticks;

        static CompanionAutoEnable()
        {
            if (EditorPrefs.GetBool(OptOutPref, false)) return;

            // Telemetry first, and from the static constructor rather than the tick below.
            //
            // The companion starts a server of its own during editor startup, and that child inherits the
            // editor's environment at the moment it is spawned. Setting the opt-out ten update ticks later
            // was demonstrably too late on a cold boot: the variable was still unset when the editor had
            // already come up. Doing it here moves it to assembly load, which needs no main loop and no
            // companion API.
            //
            // Not airtight, and worth saying so: [InitializeOnLoad] order between assemblies is not defined,
            // so a companion that spawned its server before this assembly loaded would still miss it. The
            // server an MCP client launches is covered regardless, because its opt-out is written into
            // .mcp.json.
            try { CompanionSetup.DisableCompanionTelemetry(); }
            catch (Exception) { /* the companion may not be installed; the tick reports properly */ }

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            // A handful of ticks of headroom: on the very first load the package manager may still be
            // registering packages, and the companion's own loader defers its startup too.
            if (++_ticks < 10) return;
            EditorApplication.update -= Tick;

            try
            {
                var state = CompanionSetup.StatusForAutoEnable();
                if (state == null) return;               // not installed: nothing to do, reconsider next load
                if (!state.Value) return;                // installed but not compiled yet: next load catches it

                // Every load, not once, and this is the whole point of the hook.
                //
                // Its stdio bridge does not survive a domain reload — it closes the listener and deletes its
                // own status file, which is how its server finds the editor — and setting the auto-start
                // pref is not enough to bring it back: nothing in the package acts on that pref at load
                // time. So after any recompile the generic half was simply gone, and an agent mid-task would
                // find its tools answering "no Unity Editor instances found" against a healthy editor.
                // Starting it here on every load is what makes "bundled" mean "available".
                //
                // Telemetry for the same reason: the Python half reads its opt-out from environment
                // variables only, and an environment does not survive a restart the way a pref does.
                CompanionSetup.DisableCompanionTelemetry();
                CompanionSetup.EnableQuietly();

                // Say so once per project. Repeating it on every recompile would bury the console.
                if (EditorPrefs.GetBool(MarkerPref, false)) return;
                EditorPrefs.SetBool(MarkerPref, true);
                Debug.Log("[agentic-vrcw] Enabled the bundled generic Unity MCP package (" +
                          "MCPForUnity.AutoStartOnLoad), with its telemetry off. Set the EditorPref " +
                          OptOutPref + " to true to stop this happening in future projects.");
            }
            catch (Exception ex)
            {
                // Never let an optional companion break editor startup.
                Debug.LogWarning("[agentic-vrcw] Could not auto-enable the companion MCP package: " + ex.Message);
            }
        }
    }
}
