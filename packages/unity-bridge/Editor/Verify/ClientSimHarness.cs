using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Behavioural verification of Udon logic in a running ClientSim session.
    ///
    /// This layer has no upstream equivalent any more: ClientSim's integration-test framework was removed
    /// from the SDK ("Removing the Tests folder from the source... they're not currently working"), and the
    /// shipped samples reference a VRC.ClientSim.Tests assembly that is no longer distributed. The public
    /// seam survived though — ClientSimRuntimeLoader.BeginUnityTesting, ClientSimSettings, and
    /// ClientSimMain.HasInstance are all public — so a harness can be rebuilt on supported API.
    ///
    /// It runs as a job rather than as NUnit play-mode tests, which is only possible because entering play
    /// mode with EnterPlayModeOptions.DisableDomainReload leaves the job's enumerator alive. The job sets
    /// that option itself, since without it the domain reload on Play would destroy the running job.
    ///
    /// What it does and does not prove:
    ///   - PROVES the Udon program's logic: an Interact entry point flips state, a synced variable lands,
    ///     an event chain fires, objects end up active or inactive as intended.
    ///   - DOES NOT prove reachability. `interact` invokes the behaviour's interact entry point directly
    ///     rather than aiming a simulated player at a collider, so it will not notice that a button is
    ///     behind a wall or has no collider. scene.spatial_audit covers geometry; only a real client
    ///     covers the full input path.
    ///   - DOES NOT prove networking. ClientSim is single-player with no network simulation, so ownership,
    ///     late-joiner state and sync convergence need verify.multiclient against real clients.
    /// </summary>
    public static class ClientSimHarness
    {
        private const int MaxStepFrames = 60 * 60 * 5; // 5 minutes of frames, as a runaway guard

        public static void Register()
        {
            BridgeRouter.Register("verify.scenario_schema",
                "The step types a ClientSim scenario accepts, and what each one proves.",
                delegate (Dictionary<string, object> a) { return Schema(); }, requiresMainThread: false);

            JobStore.RegisterKind("verify.clientsim", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return RunScenario(args, ctx);
            });
        }

        private static Dictionary<string, object> Schema()
        {
            return new Dictionary<string, object>
            {
                { "steps", new List<object>
                    {
                        Step("wait_frames", "{ \"wait_frames\": 10 }", "Advance N editor frames."),
                        Step("wait_seconds", "{ \"wait_seconds\": 1.5 }", "Advance until N seconds of play time have passed."),
                        Step("interact", "{ \"interact\": { \"gameObject\": \"P/Door\", \"type\": \"Door\" } }",
                             "Invoke the behaviour's Interact entry point, as a player's use action would. Does not check reachability."),
                        Step("send_event", "{ \"send_event\": { \"gameObject\": \"P/Door\", \"event\": \"Apply\" } }",
                             "Send a custom Udon event by name."),
                        Step("set_var", "{ \"set_var\": { \"gameObject\": \"P/Door\", \"name\": \"isOpen\", \"value\": true } }",
                             "Write a program variable, to set up a precondition."),
                        Step("assert_var", "{ \"assert_var\": { \"gameObject\": \"P/Door\", \"name\": \"isOpen\", \"equals\": true } }",
                             "Compare a program variable. Use \"equals\", or \"notEquals\"."),
                        Step("assert_active", "{ \"assert_active\": { \"gameObject\": \"P/Visual\", \"equals\": false } }",
                             "Compare a GameObject's activeInHierarchy."),
                        Step("assert_position", "{ \"assert_position\": { \"gameObject\": \"P/Box\", \"near\": [0,1,0], \"tolerance\": 0.1 } }",
                             "Compare a transform's world position."),
                        Step("teleport", "{ \"teleport\": { \"position\": [0,0,5] } }",
                             "Move the local player via VRCPlayerApi.TeleportTo."),
                        Step("respawn", "{ \"respawn\": true }", "Respawn the local player."),
                        Step("assert_no_errors", "{ \"assert_no_errors\": { \"ignoreKnownSdkNoise\": true, \"ignore\": [\"regex\"] } }",
                             "Fail if any error reached the console since the scenario started. Known SDK noise caused by " +
                             "running with domain reload disabled is reported under `ignored` rather than failing the step."),
                        Step("log", "{ \"log\": \"checkpoint reached\" }", "Write a marker to the console.")
                    }
                },
                { "limits", new List<object>
                    {
                        "Single player only: ClientSim does not simulate networking, so sync and ownership need verify.multiclient.",
                        "interact bypasses the physical input path, so it cannot catch an unreachable or collider-less button."
                    }
                }
            };
        }

        private static Dictionary<string, object> Step(string name, string example, string proves)
        {
            return new Dictionary<string, object> { { "step", name }, { "example", example }, { "meaning", proves } };
        }

        /// <summary>
        /// Console errors the SDK produces on its own during repeated play sessions.
        ///
        /// These are not the world's bugs, and leaving them in would make assert_no_errors fail on every
        /// run — which trains a reader to ignore it, the worst possible outcome for an assertion. They are
        /// still reported, under `ignored`, so nothing is hidden.
        ///
        /// Both are consequences of how this harness has to run rather than of anything a world does:
        /// disabling domain reload (which ClientSim requires, and without which the verification job would
        /// be destroyed on Play) leaves SDK statics holding objects from the previous session.
        /// </summary>
        private static readonly KeyValuePair<Regex, string>[] KnownSdkNoise =
        {
            new KeyValuePair<Regex, string>(
                new Regex(@"Texture2DDefaultTextureHolder\.ResetTextures", RegexOptions.Compiled),
                "VRChat SDK: UdonManager's static default-texture holder still references textures destroyed by the " +
                "previous play session, because domain reload is disabled. Harmless - the textures are reinitialised."),
            new KeyValuePair<Regex, string>(
                new Regex(@"UdonSharpEditorUtility.*RunBehaviourSetup", RegexOptions.Compiled),
                "UdonSharp: a deferred EditorApplication.delayCall that updates hideFlags on a backing UdonBehaviour " +
                "fires after the behaviour is gone. Harmless, and internal to the SDK."),
        };

        private static string MatchKnownNoise(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var pair in KnownSdkNoise)
            {
                if (pair.Key.IsMatch(text)) return pair.Value;
            }
            return null;
        }

        private static string MatchPatterns(string text, List<string> patterns)
        {
            if (string.IsNullOrEmpty(text) || patterns == null) return null;
            foreach (string pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;
                try
                {
                    if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                        return "matched the caller's ignore pattern \"" + pattern + "\"";
                }
                catch (ArgumentException) { /* caller supplied a bad regex */ }
            }
            return null;
        }

        // ------------------------------------------------------------------ the job

        private static IEnumerator RunScenario(Dictionary<string, object> args, JobContext ctx)
        {
            object rawSteps;
            args.TryGetValue("steps", out rawSteps);
            var steps = rawSteps as List<object> ?? new List<object>();
            if (steps.Count == 0) throw new ArgumentException("verify.clientsim needs a non-empty \"steps\" array");

            bool stopOnFailure = Json.GetBool(args, "stopOnFailure", true);
            string scenarioName = Json.GetString(args, "name", "scenario");
            long consoleCursor = LogCapture.LatestSeq;

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("The editor is already in play mode; exit it before running a scenario.");

            // Domain reload on Play would destroy this job mid-flight, so it must be off. ClientSim's own
            // test guidance requires the same thing.
            var previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            var previousSettings = ConfigureClientSim(args);
            var results = new List<object>();
            bool enteredPlayMode = false;
            string fatal = null;

            try
            {
                ctx.Report(0.05f, "entering play mode");
                EditorApplication.EnterPlaymode();

                int guard = 0;
                while (!EditorApplication.isPlaying && guard++ < 60 * 60)
                {
                    if (ctx.CancelRequested) yield break;
                    yield return null;
                }
                if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play mode did not start within 60s.");
                enteredPlayMode = true;

                ctx.Report(0.15f, "waiting for ClientSim to come up");
                guard = 0;
                while (!HasClientSimInstance() && guard++ < 60 * 60)
                {
                    if (ctx.CancelRequested) yield break;
                    yield return null;
                }
                bool clientSimUp = HasClientSimInstance();

                // A scenario can still be useful without ClientSim (plain Udon logic runs either way), so
                // this is recorded rather than fatal — but it is recorded loudly, because the absence
                // changes what the run proves.
                results.Add(new Dictionary<string, object>
                {
                    { "step", "clientsim_startup" },
                    { "ok", clientSimUp },
                    { "detail", clientSimUp
                        ? "ClientSim instance is running with a local player."
                        : "ClientSim did not start. VRCPlayerApi-dependent behaviour will not work; check that " +
                          "Enable ClientSim is on in the ClientSim settings window." }
                });

                for (int i = 0; i < steps.Count; i++)
                {
                    if (ctx.CancelRequested) yield break;
                    ctx.Report(0.15f + 0.8f * i / steps.Count, "step " + (i + 1) + "/" + steps.Count);

                    var step = steps[i] as Dictionary<string, object>;
                    if (step == null) continue;

                    var outcome = new Dictionary<string, object> { { "index", i } };
                    var runner = ExecuteStep(step, outcome, consoleCursor);

                    // Steps are enumerators so that waits and assertions share one frame-stepped path.
                    bool stepFailed = false;
                    while (true)
                    {
                        bool moved;
                        try { moved = runner.MoveNext(); }
                        catch (Exception ex)
                        {
                            outcome["ok"] = false;
                            outcome["error"] = ex.GetType().Name + ": " + ex.Message;
                            stepFailed = true;
                            break;
                        }
                        if (!moved) break;
                        yield return null;
                    }

                    if (!outcome.ContainsKey("ok")) outcome["ok"] = true;
                    if (!(bool)outcome["ok"]) stepFailed = true;
                    results.Add(outcome);

                    if (stepFailed && stopOnFailure) break;
                }
            }
            finally
            {
                if (enteredPlayMode) EditorApplication.ExitPlaymode();
                RestoreClientSim(previousSettings);
                EditorSettings.enterPlayModeOptionsEnabled = previousOptionsEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
            }

            // Let play mode actually finish before reporting, so the console delta includes teardown.
            int exitGuard = 0;
            while (EditorApplication.isPlaying && exitGuard++ < 60 * 30) yield return null;

            var consoleDelta = LogCapture.Read(consoleCursor, 300, "all", null);
            int failures = 0;
            foreach (Dictionary<string, object> r in results) if (!(bool)r["ok"]) failures++;

            ctx.SetResult(new Dictionary<string, object>
            {
                { "name", scenarioName },
                { "ok", failures == 0 && fatal == null },
                { "stepCount", results.Count },
                { "failureCount", failures },
                { "steps", results },
                { "console", consoleDelta },
                { "proves", "Udon program logic under a running ClientSim session." },
                { "doesNotProve", "Reachability of interactables, or anything about networking/sync." }
            });
        }

        // ------------------------------------------------------------------ steps

        private static IEnumerator ExecuteStep(Dictionary<string, object> step, Dictionary<string, object> outcome, long consoleCursor)
        {
            object value;

            if (step.TryGetValue("wait_frames", out value))
            {
                int frames = Mathf.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, MaxStepFrames);
                outcome["step"] = "wait_frames";
                outcome["detail"] = frames + " frames";
                for (int i = 0; i < frames; i++) yield return null;
                yield break;
            }

            if (step.TryGetValue("wait_seconds", out value))
            {
                float seconds = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                outcome["step"] = "wait_seconds";
                outcome["detail"] = seconds + "s";
                double until = EditorApplication.timeSinceStartup + seconds;
                int guard = 0;
                while (EditorApplication.timeSinceStartup < until && guard++ < MaxStepFrames) yield return null;
                yield break;
            }

            if (step.TryGetValue("log", out value))
            {
                outcome["step"] = "log";
                Debug.Log("[scenario] " + Convert.ToString(value, CultureInfo.InvariantCulture));
                yield break;
            }

            if (step.TryGetValue("interact", out value))
            {
                var spec = Json.AsObject(value);
                outcome["step"] = "interact";
                var target = UdonTarget.Resolve(Json.GetString(spec, "gameObject"), Json.GetString(spec, "type"));
                outcome["target"] = UdonOps.HierarchyPath(target.gameObject.transform) + " (" + target.typeName + ")";
                // "_interact" is Udon's internal name for the Interact event (UdonNodeExtensions.cs).
                UdonTarget.SendEvent(target.udonBehaviour, "_interact");
                yield return null;
                yield break;
            }

            if (step.TryGetValue("send_event", out value))
            {
                var spec = Json.AsObject(value);
                string eventName = Json.GetString(spec, "event");
                if (string.IsNullOrEmpty(eventName)) throw new ArgumentException("send_event needs \"event\"");
                outcome["step"] = "send_event";
                outcome["detail"] = eventName;
                var target = UdonTarget.Resolve(Json.GetString(spec, "gameObject"), Json.GetString(spec, "type"));
                UdonTarget.SendEvent(target.udonBehaviour, eventName);
                yield return null;
                yield break;
            }

            if (step.TryGetValue("set_var", out value))
            {
                var spec = Json.AsObject(value);
                string name = Json.GetString(spec, "name");
                outcome["step"] = "set_var";
                outcome["detail"] = name;
                var target = UdonTarget.Resolve(Json.GetString(spec, "gameObject"), Json.GetString(spec, "type"));
                object existing = UdonTarget.GetVariable(target.udonBehaviour, name);
                object raw;
                spec.TryGetValue("value", out raw);
                object converted = existing == null ? raw : UdonValues.Convert(raw, existing.GetType(), name);
                UdonTarget.SetVariable(target.udonBehaviour, name, converted);
                yield return null;
                yield break;
            }

            if (step.TryGetValue("assert_var", out value))
            {
                var spec = Json.AsObject(value);
                string name = Json.GetString(spec, "name");
                outcome["step"] = "assert_var";
                var target = UdonTarget.Resolve(Json.GetString(spec, "gameObject"), Json.GetString(spec, "type"));
                object actual = UdonTarget.GetVariable(target.udonBehaviour, name);
                outcome["variable"] = name;
                outcome["actual"] = actual;

                object expected;
                if (spec.TryGetValue("equals", out expected))
                {
                    bool same = ValuesMatch(actual, expected);
                    outcome["expected"] = expected;
                    outcome["ok"] = same;
                    if (!same) outcome["error"] = name + " is " + Describe(actual) + ", expected " + Describe(expected);
                }
                else if (spec.TryGetValue("notEquals", out expected))
                {
                    bool same = ValuesMatch(actual, expected);
                    outcome["notExpected"] = expected;
                    outcome["ok"] = !same;
                    if (same) outcome["error"] = name + " is " + Describe(actual) + ", which it should not be";
                }
                else throw new ArgumentException("assert_var needs \"equals\" or \"notEquals\"");
                yield break;
            }

            if (step.TryGetValue("assert_active", out value))
            {
                var spec = Json.AsObject(value);
                outcome["step"] = "assert_active";
                var go = UdonReflection.FindGameObject(Json.GetString(spec, "gameObject"));
                bool actual = go.activeInHierarchy;
                bool expected = Json.GetBool(spec, "equals", true);
                outcome["actual"] = actual;
                outcome["expected"] = expected;
                outcome["ok"] = actual == expected;
                if (actual != expected)
                    outcome["error"] = go.name + ".activeInHierarchy is " + actual + ", expected " + expected;
                yield break;
            }

            if (step.TryGetValue("assert_position", out value))
            {
                var spec = Json.AsObject(value);
                outcome["step"] = "assert_position";
                var go = UdonReflection.FindGameObject(Json.GetString(spec, "gameObject"));
                object near;
                if (!spec.TryGetValue("near", out near)) throw new ArgumentException("assert_position needs \"near\"");
                var expected = (Vector3)UdonValues.Convert(near, typeof(Vector3), "near");
                float tolerance = spec.ContainsKey("tolerance")
                    ? Convert.ToSingle(spec["tolerance"], CultureInfo.InvariantCulture) : 0.1f;
                float distance = Vector3.Distance(go.transform.position, expected);
                outcome["actual"] = go.transform.position;
                outcome["expected"] = expected;
                outcome["distance"] = distance;
                outcome["ok"] = distance <= tolerance;
                if (distance > tolerance)
                    outcome["error"] = go.name + " is " + distance.ToString("0.000") + "m from the expected position (tolerance " + tolerance + ")";
                yield break;
            }

            if (step.TryGetValue("teleport", out value))
            {
                var spec = Json.AsObject(value);
                outcome["step"] = "teleport";
                object position;
                if (!spec.TryGetValue("position", out position)) throw new ArgumentException("teleport needs \"position\"");
                var target = (Vector3)UdonValues.Convert(position, typeof(Vector3), "position");
                TeleportLocalPlayer(target);
                outcome["detail"] = target.ToString("0.##");
                yield return null;
                yield break;
            }

            if (step.TryGetValue("respawn", out value))
            {
                outcome["step"] = "respawn";
                InvokeOnLocalPlayer("Respawn");
                yield return null;
                yield break;
            }

            if (step.TryGetValue("assert_no_errors", out value))
            {
                outcome["step"] = "assert_no_errors";
                var spec = value as Dictionary<string, object> ?? new Dictionary<string, object>();
                bool ignoreSdkNoise = Json.GetBool(spec, "ignoreKnownSdkNoise", true);
                var extraPatterns = Json.GetStringList(spec, "ignore");

                var raw = LogCapture.Read(consoleCursor, 100, "error", null);
                var entries = raw["entries"] as List<LogCapture.Entry> ?? new List<LogCapture.Entry>();

                var real = new List<object>();
                var ignored = new List<object>();
                foreach (var entry in entries)
                {
                    string text = (entry.msg ?? "") + "\n" + (entry.stack ?? "");
                    string reason = ignoreSdkNoise ? MatchKnownNoise(text) : null;
                    if (reason == null) reason = MatchPatterns(text, extraPatterns);

                    var described = new Dictionary<string, object>
                    {
                        { "seq", entry.seq }, { "type", entry.type }, { "message", entry.msg }
                    };
                    if (reason != null) { described["ignoredBecause"] = reason; ignored.Add(described); }
                    else real.Add(described);
                }

                outcome["errorCount"] = real.Count;
                outcome["ignoredCount"] = ignored.Count;
                outcome["ignored"] = ignored;
                outcome["ok"] = real.Count == 0;
                if (real.Count > 0)
                {
                    outcome["error"] = real.Count + " error(s) reached the console during this scenario";
                    outcome["entries"] = real;
                }
                yield break;
            }

            throw new ArgumentException("Unrecognised scenario step: " + string.Join(", ", new List<string>(step.Keys).ToArray()) +
                ". Call verify.scenario_schema for the supported steps.");
        }

        // ------------------------------------------------------------------ player control

        private static void TeleportLocalPlayer(Vector3 position)
        {
            object player = GetLocalPlayer();
            if (player == null) throw new InvalidOperationException("No local player; ClientSim is not running with spawnPlayer enabled.");

            var method = player.GetType().GetMethod("TeleportTo", new[] { typeof(Vector3), typeof(Quaternion) });
            if (method != null)
            {
                method.Invoke(player, new object[] { position, Quaternion.identity });
                return;
            }
            throw new MissingMethodException("VRChat SDK API moved: VRCPlayerApi.TeleportTo(Vector3, Quaternion)");
        }

        private static void InvokeOnLocalPlayer(string methodName)
        {
            object player = GetLocalPlayer();
            if (player == null) throw new InvalidOperationException("No local player; ClientSim is not running with spawnPlayer enabled.");
            var method = player.GetType().GetMethod(methodName, new Type[0]);
            if (method == null) throw new MissingMethodException("VRChat SDK API moved: VRCPlayerApi." + methodName + "()");
            method.Invoke(player, null);
        }

        private static object GetLocalPlayer()
        {
            try
            {
                var networking = UdonReflection.Resolve("VRC.SDKBase.Networking");
                var property = networking.GetProperty("LocalPlayer", BindingFlags.Static | BindingFlags.Public);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception) { return null; }
        }

        private static bool HasClientSimInstance()
        {
            try
            {
                var main = UdonReflection.Resolve("VRC.SDK3.ClientSim.ClientSimMain");
                var has = main.GetMethod("HasInstance", BindingFlags.Static | BindingFlags.Public);
                return has != null && (bool)has.Invoke(null, null);
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ settings

        private sealed class SettingsSnapshot
        {
            public object settings;
            public Dictionary<string, object> previous = new Dictionary<string, object>();
        }

        /// <summary>
        /// Forces the ClientSim settings a scenario needs, remembering the previous values.
        ///
        /// These live in EditorPrefs via ClientSimSettings, so they persist; leaving a user's settings
        /// rewritten after a verification run would be an unpleasant surprise.
        /// </summary>
        private static SettingsSnapshot ConfigureClientSim(Dictionary<string, object> args)
        {
            var snapshot = new SettingsSnapshot();
            try
            {
                var settingsType = UdonReflection.Resolve("VRC.SDK3.ClientSim.ClientSimSettings");
                var instanceProperty = settingsType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                object settings = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
                if (settings == null) return snapshot;
                snapshot.settings = settings;

                object overridesRaw;
                args.TryGetValue("settings", out overridesRaw);
                var overrides = Json.AsObject(overridesRaw);

                // Defaults a scenario needs regardless of what the user had configured.
                var desired = new Dictionary<string, object>
                {
                    { "enableClientSim", true },
                    { "spawnPlayer", true },
                    { "initializationDelay", 0f },
                    { "localPlayerIsMaster", true },
                    { "stopOnScriptChanges", false },
                    { "hideMenuOnLaunch", true },
                    { "deleteEditorOnly", false }
                };
                foreach (var pair in overrides) desired[pair.Key] = pair.Value;

                foreach (var pair in desired)
                {
                    var field = settingsType.GetField(pair.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (field == null) continue;
                    snapshot.previous[pair.Key] = field.GetValue(settings);
                    field.SetValue(settings, UdonValues.Convert(pair.Value, field.FieldType, pair.Key));
                }

                SaveClientSimSettings(settingsType, settings);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[agentic-vrcw] Could not configure ClientSim settings: " + ex.Message);
            }
            return snapshot;
        }

        private static void RestoreClientSim(SettingsSnapshot snapshot)
        {
            if (snapshot == null || snapshot.settings == null) return;
            try
            {
                var settingsType = snapshot.settings.GetType();
                foreach (var pair in snapshot.previous)
                {
                    var field = settingsType.GetField(pair.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (field != null) field.SetValue(snapshot.settings, pair.Value);
                }
                SaveClientSimSettings(settingsType, snapshot.settings);
            }
            catch (Exception) { }
        }

        private static void SaveClientSimSettings(Type settingsType, object settings)
        {
            var save = settingsType.GetMethod("SaveSettings", BindingFlags.Static | BindingFlags.Public);
            if (save != null) save.Invoke(null, new[] { settings });
        }

        // ------------------------------------------------------------------ comparison

        private static bool ValuesMatch(object actual, object expected)
        {
            if (actual == null && expected == null) return true;
            if (actual == null || expected == null) return false;

            if (actual is bool || expected is bool)
                return Convert.ToBoolean(actual) == Convert.ToBoolean(expected);

            if (IsNumeric(actual) && IsNumeric(expected))
            {
                double a = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                double b = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
                return Math.Abs(a - b) < 0.0001;
            }

            if (actual is string || expected is string)
                return string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
                                     Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.Ordinal);

            if (actual is Vector3)
            {
                var b = (Vector3)UdonValues.Convert(expected, typeof(Vector3), "expected");
                return Vector3.Distance((Vector3)actual, b) < 0.0001f;
            }

            var unityObject = actual as UnityEngine.Object;
            if (unityObject != null)
            {
                // A reference can only sensibly be compared by name from JSON.
                return string.Equals(unityObject.name, Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }

            return Equals(actual, expected);
        }

        private static bool IsNumeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int
                || value is uint || value is long || value is ulong || value is float || value is double || value is decimal;
        }

        private static string Describe(object value)
        {
            if (value == null) return "null";
            var unityObject = value as UnityEngine.Object;
            if (unityObject != null) return unityObject.name + " (" + value.GetType().Name + ")";
            return Convert.ToString(value, CultureInfo.InvariantCulture) + " (" + value.GetType().Name + ")";
        }
    }
}
