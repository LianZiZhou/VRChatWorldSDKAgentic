using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// What Udon actually permits, queried from the SDK.
    ///
    /// "Is this API available in Udon?" is the question that wastes the most agent time, because the answer
    /// is not guessable: Udon exposes a hand-curated subset of Unity, and the only authority is the node
    /// definition registry inside the SDK. Guessing costs a full write/compile/reload cycle per attempt.
    ///
    /// Two views are offered. The query answers one specific question immediately. The export dumps the whole
    /// registry to a file, which is the useful form for a knowledge pack: it is generated from the *installed*
    /// SDK, so it cannot drift out of date the way hand-written documentation does.
    /// </summary>
    public static class UdonWhitelist
    {
        public static void Register()
        {
            BridgeRouter.Register("udon.whitelist_query",
                "Ask whether a type, member or event is available in Udon. " +
                "Args: query (substring), kind(any|type|event|member), limit.",
                delegate (Dictionary<string, object> a) { return Query(a); }, defaultTimeoutMs: 120000);

            BridgeRouter.Register("udon.syncable",
                "Whether a type can be synced, and whether it supports linear/smooth interpolation. " +
                "Authoritative: calls VRC.Udon.UdonNetworkTypes. Args: type.",
                delegate (Dictionary<string, object> a) { return Syncable(a); });

            JobStore.RegisterKind("udon.export_whitelist", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return ExportJob(args, ctx);
            });
        }

        // ------------------------------------------------------------------ registry access

        private static IEnumerable GetNodeDefinitions(string prefix)
        {
            var managerType = Reflect.ResolveType("VRC.Udon.Editor.UdonEditorManager");
            var instanceProperty = managerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
            object manager = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
            if (manager == null) throw new InvalidOperationException("UdonEditorManager.Instance is null.");

            if (string.IsNullOrEmpty(prefix))
            {
                var all = managerType.GetMethod("GetNodeDefinitions", new Type[0]);
                if (all == null) throw new MissingMethodException("VRChat SDK API moved: UdonEditorManager.GetNodeDefinitions()");
                return all.Invoke(manager, null) as IEnumerable;
            }

            var byPrefix = managerType.GetMethod("GetNodeDefinitions", new[] { typeof(string) });
            if (byPrefix == null) throw new MissingMethodException("VRChat SDK API moved: UdonEditorManager.GetNodeDefinitions(string)");
            return byPrefix.Invoke(manager, new object[] { prefix }) as IEnumerable;
        }

        private static string ReadFullName(object nodeDefinition)
        {
            if (nodeDefinition == null) return null;
            var type = nodeDefinition.GetType();
            var field = type.GetField("fullName", BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(nodeDefinition) as string;
            var property = type.GetProperty("fullName", BindingFlags.Public | BindingFlags.Instance);
            return property == null ? null : property.GetValue(nodeDefinition, null) as string;
        }

        // ------------------------------------------------------------------ query

        private static Dictionary<string, object> Query(Dictionary<string, object> a)
        {
            string query = Json.GetString(a, "query");
            if (string.IsNullOrEmpty(query)) throw new ArgumentException("udon.whitelist_query needs \"query\"");
            string kind = (Json.GetString(a, "kind", "any") ?? "any").ToLowerInvariant();
            int limit = Mathf.Clamp(Json.GetInt(a, "limit", 40), 1, 500);

            string prefix = kind == "event" ? "Event_" : null;
            var matches = new List<object>();
            int total = 0;

            foreach (object definition in GetNodeDefinitions(prefix))
            {
                string fullName = ReadFullName(definition);
                if (string.IsNullOrEmpty(fullName)) continue;
                if (fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                total++;
                if (matches.Count < limit) matches.Add(fullName);
            }

            var result = new Dictionary<string, object>
            {
                { "query", query },
                { "kind", kind },
                { "matchCount", total },
                { "matches", matches },
                { "truncated", total > matches.Count }
            };

            // Mangled node names are hard to read, so say what the shape means rather than leaving the
            // caller to reverse-engineer it.
            result["nameFormat"] =
                "Node names are Udon's own mangled form: SanitizedTypeName.__methodName__ParamTypes__ReturnType. " +
                "A match means the operation exists in Udon; absence means it is not exposed.";

            if (total == 0)
                result["hint"] = "No node matched. Udon exposes a curated subset of Unity, so an absent API genuinely " +
                                 "cannot be called from Udon - find another way rather than assuming a spelling problem.";
            return result;
        }

        // ------------------------------------------------------------------ syncable

        private static Dictionary<string, object> Syncable(Dictionary<string, object> a)
        {
            string typeName = Json.GetString(a, "type");
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("udon.syncable needs \"type\"");

            Type type;
            try { type = Reflect.ResolveType(typeName); }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "type", typeName }, { "resolved", false }, { "error", ex.Message }
                };
            }

            var networkTypes = Reflect.ResolveType("VRC.Udon.UdonNetworkTypes");
            Func<string, object> call = delegate (string method)
            {
                var info = networkTypes.GetMethod(method, BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Type) }, null);
                return info == null ? null : info.Invoke(null, new object[] { type });
            };

            bool canSync = call("CanSync") is bool && (bool)call("CanSync");
            return new Dictionary<string, object>
            {
                { "type", type.FullName },
                { "resolved", true },
                { "canSync", canSync },
                { "canSyncLinear", call("CanSyncLinear") },
                { "canSyncSmooth", call("CanSyncSmooth") },
                { "source", "VRC.Udon.UdonNetworkTypes" },
                { "note", canSync
                    ? "Arrays additionally require the behaviour to use Manual sync."
                    : "Not syncable. Break it into syncable fields, or send it through a [NetworkCallable] event." }
            };
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// Dumps the node registry and the event list to JSON.
        ///
        /// Generated from the installed SDK on purpose: a hand-written list of what Udon exposes is wrong the
        /// moment VRChat ships an update, and a subtly wrong allow-list is worse than none.
        /// </summary>
        private static IEnumerator ExportJob(Dictionary<string, object> args, JobContext ctx)
        {
            string outputPath = Json.GetString(args, "outputPath");
            if (string.IsNullOrEmpty(outputPath))
                outputPath = Path.Combine(BridgePaths.StateDir, "udon-whitelist.json");

            ctx.Report(0.1f, "reading node definitions");
            yield return null;

            var all = new List<string>();
            foreach (object definition in GetNodeDefinitions(null))
            {
                string fullName = ReadFullName(definition);
                if (!string.IsNullOrEmpty(fullName)) all.Add(fullName);
            }
            all.Sort(StringComparer.Ordinal);

            ctx.Report(0.5f, "reading events");
            yield return null;

            var events = new List<string>();
            foreach (object definition in GetNodeDefinitions("Event_"))
            {
                string fullName = ReadFullName(definition);
                if (!string.IsNullOrEmpty(fullName)) events.Add(fullName);
            }
            events.Sort(StringComparer.Ordinal);

            ctx.Report(0.75f, "grouping by type");
            yield return null;

            // Grouped by the leading type name, which is how a reader actually wants to consult it.
            var byType = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (string name in all)
            {
                int split = name.IndexOf(".__", StringComparison.Ordinal);
                string owner = split > 0 ? name.Substring(0, split) : "<other>";
                if (!byType.ContainsKey(owner)) byType[owner] = new List<object>();
                byType[owner].Add(name);
            }

            var payload = new Dictionary<string, object>
            {
                { "generatedUtc", DateTime.UtcNow.ToString("o") },
                { "unityVersion", Application.unityVersion },
                { "source", "VRC.Udon.Editor.UdonEditorManager.GetNodeDefinitions()" },
                { "nodeCount", all.Count },
                { "eventCount", events.Count },
                { "typeCount", byType.Count },
                { "events", events },
                { "byType", byType },
                { "nameFormat", "SanitizedTypeName.__methodName__ParamTypes__ReturnType" }
            };

            try
            {
                string directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(outputPath, Json.Write(payload, 6), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new IOException("Could not write the whitelist to \"" + outputPath + "\": " + ex.Message, ex);
            }

            ctx.SetResult(new Dictionary<string, object>
            {
                { "outputPath", outputPath.Replace('\\', '/') },
                { "nodeCount", all.Count },
                { "eventCount", events.Count },
                { "typeCount", byType.Count },
                { "bytes", new FileInfo(outputPath).Length }
            });
        }
    }
}
