using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Turns off the editor's interactive prompts.
    ///
    /// A modal dialog is the worst failure this toolchain has: it stops Unity's update loop, which stops
    /// the bridge's main-thread pump, so every call times out as EDITOR_BUSY and an unattended run simply
    /// stops with nobody to click the button. It was hit for real — Unity's "detected external changes,
    /// reload?" prompt appeared mid-run and blocked the editor until a human clicked Reload.
    ///
    /// That particular dialog only exists when Asset Pipeline "Auto Refresh" is off, so the fix is to
    /// make sure it is on. The rest of the prefs here remove the other dialogs Unity can raise on its own
    /// during normal asset work.
    ///
    /// Call this once after the editor starts, before any asset writing.
    /// </summary>
    public static class EditorHardening
    {
        /// <summary>
        /// Prefs that make the editor non-interactive, with why each one matters.
        /// Keys are Unity's own EditorPrefs keys, which is why they look like this.
        /// </summary>
        private static readonly PrefSpec[] Specs =
        {
            new PrefSpec
            {
                Key = "kAutoRefresh",
                Kind = PrefKind.Int,
                Desired = 1,
                Why = "Auto Refresh off makes Unity prompt \"detected external changes, reload?\" instead of " +
                      "importing. That dialog blocks the editor's update loop and with it this bridge."
            },
            new PrefSpec
            {
                Key = "DirectoryMonitoring",
                Kind = PrefKind.Int,
                Desired = 1,
                Why = "Directory monitoring lets Unity notice asset changes promptly rather than batching " +
                      "them until it regains focus."
            },
            new PrefSpec
            {
                Key = "VerifySavingAssets",
                Kind = PrefKind.Bool,
                Desired = false,
                Why = "\"Verify Saving Assets\" opens a confirmation list every time assets are saved."
            },
        };

        public static void Register()
        {
            BridgeRouter.Register("bridge.harden_editor",
                "Disable the editor's interactive prompts so unattended runs cannot be blocked by a modal " +
                "dialog. Args: apply (default true), disableDomainReloadOnPlay.",
                delegate (Dictionary<string, object> a) { return Harden(a); });
        }

        private enum PrefKind { Int, Bool }

        private sealed class PrefSpec
        {
            public string Key;
            public PrefKind Kind;
            public object Desired;
            public string Why;
        }

        private static Dictionary<string, object> Harden(Dictionary<string, object> a)
        {
            bool apply = Json.GetBool(a, "apply", true);
            var settings = new List<object>();
            var changed = new List<string>();

            foreach (var spec in Specs)
            {
                object before = Read(spec);
                bool needsChange = !Equals(before, spec.Desired);

                if (needsChange && apply)
                {
                    Write(spec, spec.Desired);
                    changed.Add(spec.Key);
                }

                settings.Add(new Dictionary<string, object>
                {
                    { "key", spec.Key },
                    { "before", before },
                    { "after", Read(spec) },
                    { "desired", spec.Desired },
                    { "compliant", Equals(Read(spec), spec.Desired) },
                    { "why", spec.Why }
                });
            }

            var result = new Dictionary<string, object>
            {
                { "applied", apply },
                { "changed", changed },
                { "settings", settings }
            };

            // Domain reload on entering play mode is what ClientSim's own test guidance says to turn off,
            // and it also makes play-mode verification far faster. It changes play-mode semantics
            // (statics are not reset), so it stays opt-in.
            if (Json.GetBool(a, "disableDomainReloadOnPlay", false))
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
                result["enterPlayModeOptions"] = EditorSettings.enterPlayModeOptions.ToString();
                result["enterPlayModeOptionsEnabled"] = EditorSettings.enterPlayModeOptionsEnabled;
                changed.Add("enterPlayModeOptions");
            }
            else
            {
                result["enterPlayModeOptionsEnabled"] = EditorSettings.enterPlayModeOptionsEnabled;
                result["enterPlayModeOptions"] = EditorSettings.enterPlayModeOptions.ToString();
            }

            return result;
        }

        private static object Read(PrefSpec spec)
        {
            return spec.Kind == PrefKind.Int
                ? (object)EditorPrefs.GetInt(spec.Key, -1)
                : EditorPrefs.GetBool(spec.Key, false);
        }

        private static void Write(PrefSpec spec, object value)
        {
            if (spec.Kind == PrefKind.Int) EditorPrefs.SetInt(spec.Key, Convert.ToInt32(value));
            else EditorPrefs.SetBool(spec.Key, Convert.ToBoolean(value));
        }
    }
}
