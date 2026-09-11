using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Asset organisation: what is in the project, what is dead weight, material authoring, and a
    /// provenance ledger.
    ///
    /// The ledger exists because an autonomous agent assembling a world from downloaded assets creates a
    /// licensing problem that is invisible later: nobody can tell, six months on, where a texture came from
    /// or whether it could be shipped. Every import records its source and licence, and ATTRIBUTION.md is
    /// generated from that record rather than written by hand.
    ///
    /// assets.unused matters for a different reason: VRChat refuses an over-size upload outright, and unused
    /// assets pulled in by a stray reference are the easiest megabytes to get back.
    /// </summary>
    public static class AssetOps
    {
        private const string LedgerFile = "asset-ledger.json";

        public static void Register()
        {
            BridgeRouter.Register("assets.inventory",
                "Assets by type with sizes and totals. Args: folder, maxAssets.",
                delegate (Dictionary<string, object> a) { return Inventory(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("assets.unused",
                "Assets not reachable from any scene, with the size they would free. Args: folder, scenes[], maxAssets.",
                delegate (Dictionary<string, object> a) { return Unused(a); }, defaultTimeoutMs: 300000);

            BridgeRouter.Register("assets.make_material",
                "Create or update a material. Args: path, shader, textures{prop:assetPath}, colors{prop:[r,g,b,a]}, floats{prop:v}.",
                delegate (Dictionary<string, object> a) { return MakeMaterial(a); }, defaultTimeoutMs: 120000);

            BridgeRouter.Register("assets.import_file",
                "Copy an external file into the project and import it, recording its provenance. " +
                "Args: sourcePath, destFolder, name, source, licence, sourceUrl.",
                delegate (Dictionary<string, object> a) { return ImportFile(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("assets.ledger",
                "Read the provenance ledger, and optionally regenerate ATTRIBUTION.md from it. " +
                "Args: writeAttribution, attributionPath.",
                delegate (Dictionary<string, object> a) { return Ledger(a); });
        }

        // ------------------------------------------------------------------ inventory

        private static Dictionary<string, object> Inventory(Dictionary<string, object> a)
        {
            string folder = NormalizeFolder(Json.GetString(a, "folder", "Assets"));
            int maxAssets = Mathf.Clamp(Json.GetInt(a, "maxAssets", 5000), 1, 100000);

            var byType = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            long totalBytes = 0;
            int counted = 0;

            foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                if (counted >= maxAssets) break;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                if (!File.Exists(path)) continue;

                counted++;
                long bytes = new FileInfo(path).Length;
                totalBytes += bytes;

                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (!byType.ContainsKey(extension))
                    byType[extension] = new Dictionary<string, object> { { "extension", extension }, { "count", 0 }, { "bytes", 0L }, { "largest", null } };

                var bucket = byType[extension];
                bucket["count"] = Convert.ToInt32(bucket["count"]) + 1;
                long bucketBytes = Convert.ToInt64(bucket["bytes"]) + bytes;
                bucket["bytes"] = bucketBytes;

                var largest = bucket["largest"] as Dictionary<string, object>;
                if (largest == null || bytes > Convert.ToInt64(largest["bytes"]))
                {
                    bucket["largest"] = new Dictionary<string, object>
                    {
                        { "path", UdonReflection.NormalizePath(path) }, { "bytes", bytes }
                    };
                }
            }

            var types = new List<object>(byType.Values);
            types.Sort(delegate (object x, object y)
            {
                return Convert.ToInt64(((Dictionary<string, object>)y)["bytes"])
                    .CompareTo(Convert.ToInt64(((Dictionary<string, object>)x)["bytes"]));
            });
            foreach (Dictionary<string, object> bucket in types)
                bucket["megabytes"] = Convert.ToInt64(bucket["bytes"]) / (1024.0 * 1024.0);

            return new Dictionary<string, object>
            {
                { "folder", folder },
                { "assetCount", counted },
                { "totalBytes", totalBytes },
                { "totalMegabytes", totalBytes / (1024.0 * 1024.0) },
                { "byExtension", types },
                { "truncated", counted >= maxAssets }
            };
        }

        // ------------------------------------------------------------------ unused

        /// <summary>
        /// Assets no scene can reach.
        ///
        /// Reachability is computed with AssetDatabase.GetDependencies over the scenes, which is the same
        /// notion of "used" the build uses. Scripts and editor-only assets are excluded, because a script is
        /// reachable through code rather than through a reference and deleting one on this evidence would be
        /// destructive.
        /// </summary>
        private static Dictionary<string, object> Unused(Dictionary<string, object> a)
        {
            string folder = NormalizeFolder(Json.GetString(a, "folder", "Assets"));
            int maxAssets = Mathf.Clamp(Json.GetInt(a, "maxAssets", 5000), 1, 100000);

            var scenePaths = Json.GetStringList(a, "scenes");
            if (scenePaths.Count == 0)
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                    scenePaths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            if (scenePaths.Count == 0)
                return new Dictionary<string, object>
                {
                    { "checked", false },
                    { "reason", "No scenes found, so nothing can be judged unused." }
                };

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dependency in AssetDatabase.GetDependencies(scenePaths.ToArray(), true))
                used.Add(UdonReflection.NormalizePath(dependency));
            foreach (string scene in scenePaths) used.Add(UdonReflection.NormalizePath(scene));

            var unused = new List<object>();
            long reclaimable = 0;
            int examined = 0;

            foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                if (examined >= maxAssets) break;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                if (!File.Exists(path)) continue;

                string normalized = UdonReflection.NormalizePath(path);
                examined++;
                if (used.Contains(normalized)) continue;
                if (IsExcludedFromUnusedCheck(normalized)) continue;

                long bytes = new FileInfo(path).Length;
                reclaimable += bytes;
                unused.Add(new Dictionary<string, object>
                {
                    { "path", normalized },
                    { "bytes", bytes },
                    { "megabytes", bytes / (1024.0 * 1024.0) }
                });
            }

            unused.Sort(delegate (object x, object y)
            {
                return Convert.ToInt64(((Dictionary<string, object>)y)["bytes"])
                    .CompareTo(Convert.ToInt64(((Dictionary<string, object>)x)["bytes"]));
            });

            return new Dictionary<string, object>
            {
                { "checked", true },
                { "folder", folder },
                { "scenesConsidered", scenePaths },
                { "assetsExamined", examined },
                { "unusedCount", unused.Count },
                { "reclaimableBytes", reclaimable },
                { "reclaimableMegabytes", reclaimable / (1024.0 * 1024.0) },
                { "unused", unused },
                { "note", "Scripts, editor-only assets and UdonSharp program assets are excluded: they are reached " +
                          "through code or the SDK, not through scene references, so this evidence cannot condemn them." }
            };
        }

        private static bool IsExcludedFromUnusedCheck(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.EndsWith(".cs") || lower.EndsWith(".asmdef") || lower.EndsWith(".asmref")) return true;
            if (lower.EndsWith(".dll") || lower.EndsWith(".meta")) return true;
            if (lower.Contains("/editor/") || lower.Contains("/editoronly/")) return true;
            // UdonSharp program assets are referenced by the SDK's own bookkeeping.
            if (lower.Contains("/serializedudonprograms/")) return true;
            if (lower.EndsWith(".asset") && lower.Contains("udon")) return true;
            return false;
        }

        // ------------------------------------------------------------------ materials

        private static Dictionary<string, object> MakeMaterial(Dictionary<string, object> a)
        {
            string path = UdonReflection.NormalizePath(Json.GetString(a, "path"));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("assets.make_material needs \"path\"");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path must be under Assets/");
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) path += ".mat";

            string shaderName = Json.GetString(a, "shader", "Standard");
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new ArgumentException("No shader named \"" + shaderName + "\".");

            EnsureFolder(path.Substring(0, path.LastIndexOf('/')));

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = material == null;
            if (created)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            var assigned = new List<object>();
            var skipped = new List<string>();

            object raw;
            if (a.TryGetValue("textures", out raw))
            {
                foreach (var pair in Json.AsObject(raw))
                {
                    if (!material.HasProperty(pair.Key)) { skipped.Add(pair.Key + " (shader has no such property)"); continue; }
                    string texturePath = UdonReflection.NormalizePath(Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    if (texture == null) { skipped.Add(pair.Key + " (no texture at " + texturePath + ")"); continue; }
                    material.SetTexture(pair.Key, texture);
                    assigned.Add(pair.Key + " = " + texturePath);
                }
            }

            if (a.TryGetValue("colors", out raw))
            {
                foreach (var pair in Json.AsObject(raw))
                {
                    if (!material.HasProperty(pair.Key)) { skipped.Add(pair.Key + " (shader has no such property)"); continue; }
                    material.SetColor(pair.Key, (Color)UdonValues.Convert(pair.Value, typeof(Color), pair.Key));
                    assigned.Add(pair.Key + " = color");
                }
            }

            if (a.TryGetValue("floats", out raw))
            {
                foreach (var pair in Json.AsObject(raw))
                {
                    if (!material.HasProperty(pair.Key)) { skipped.Add(pair.Key + " (shader has no such property)"); continue; }
                    material.SetFloat(pair.Key, Convert.ToSingle(pair.Value, CultureInfo.InvariantCulture));
                    assigned.Add(pair.Key + " = float");
                }
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "path", path },
                { "created", created },
                { "shader", shader.name },
                { "assigned", assigned },
                { "skipped", skipped }
            };
        }

        // ------------------------------------------------------------------ import + provenance

        /// <summary>
        /// Brings an external file into the project and records where it came from.
        ///
        /// Provenance is required rather than optional. An agent that imports assets without recording a
        /// source and licence produces a world nobody can later clear for distribution, which is a problem
        /// that cannot be fixed after the fact.
        /// </summary>
        private static Dictionary<string, object> ImportFile(Dictionary<string, object> a)
        {
            string sourcePath = Json.GetString(a, "sourcePath");
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new ArgumentException("sourcePath does not exist: " + sourcePath);

            string source = Json.GetString(a, "source");
            string licence = Json.GetString(a, "licence");
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(licence))
                throw new ArgumentException(
                    "assets.import_file requires \"source\" and \"licence\". Importing an asset without recording " +
                    "where it came from leaves a world that cannot be cleared for distribution later.");

            string destFolder = NormalizeFolder(Json.GetString(a, "destFolder", "Assets/Imported"));
            string name = Json.GetString(a, "name", Path.GetFileName(sourcePath));
            EnsureFolder(destFolder);

            string destPath = destFolder + "/" + name;
            File.Copy(sourcePath, destPath, true);
            AssetDatabase.ImportAsset(destPath, ImportAssetOptions.ForceSynchronousImport);

            var entry = new Dictionary<string, object>
            {
                { "asset", UdonReflection.NormalizePath(destPath) },
                { "source", source },
                { "licence", licence },
                { "sourceUrl", Json.GetString(a, "sourceUrl") },
                { "sha256", Sha256(destPath) },
                { "bytes", new FileInfo(destPath).Length },
                { "importedUtc", DateTime.UtcNow.ToString("o") }
            };
            AppendLedger(entry);

            return new Dictionary<string, object>
            {
                { "imported", UdonReflection.NormalizePath(destPath) },
                { "ledgerEntry", entry },
                { "ledgerFile", LedgerPath().Replace('\\', '/') }
            };
        }

        private static string LedgerPath()
        {
            return Path.Combine(BridgePaths.StateDir, LedgerFile);
        }

        private static List<object> ReadLedger()
        {
            try
            {
                string path = LedgerPath();
                if (!File.Exists(path)) return new List<object>();
                var parsed = Json.Parse(File.ReadAllText(path)) as List<object>;
                return parsed ?? new List<object>();
            }
            catch (Exception) { return new List<object>(); }
        }

        private static void AppendLedger(Dictionary<string, object> entry)
        {
            var entries = ReadLedger();

            // Re-importing the same asset replaces its record rather than stacking duplicates.
            string asset = Json.GetString(entry, "asset");
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var existing = entries[i] as Dictionary<string, object>;
                if (existing != null && Json.GetString(existing, "asset") == asset) entries.RemoveAt(i);
            }

            entries.Add(entry);
            File.WriteAllText(LedgerPath(), Json.Write(entries, 5), new UTF8Encoding(false));
        }

        private static Dictionary<string, object> Ledger(Dictionary<string, object> a)
        {
            var entries = ReadLedger();
            var result = new Dictionary<string, object>
            {
                { "ledgerFile", LedgerPath().Replace('\\', '/') },
                { "entryCount", entries.Count },
                { "entries", entries }
            };

            // Flag records whose asset has since been deleted, so the attribution file stays truthful.
            var missing = new List<string>();
            foreach (object raw in entries)
            {
                var entry = raw as Dictionary<string, object>;
                if (entry == null) continue;
                string asset = Json.GetString(entry, "asset");
                if (!string.IsNullOrEmpty(asset) && !File.Exists(asset)) missing.Add(asset);
            }
            result["missingAssets"] = missing;

            if (Json.GetBool(a, "writeAttribution", false))
            {
                string target = Json.GetString(a, "attributionPath",
                    Path.Combine(BridgePaths.ProjectRoot, "ATTRIBUTION.md"));
                File.WriteAllText(target, BuildAttribution(entries), new UTF8Encoding(false));
                result["attributionFile"] = target.Replace('\\', '/');
            }

            return result;
        }

        private static string BuildAttribution(List<object> entries)
        {
            var sb = new StringBuilder();
            sb.Append("# Attribution\n\n");
            sb.Append("Generated from the asset provenance ledger. Do not edit by hand - it is rewritten from\n");
            sb.Append("`.agentic/asset-ledger.json` whenever attribution is regenerated.\n\n");

            if (entries.Count == 0)
            {
                sb.Append("No third-party assets have been imported through the agentic asset pipeline.\n");
                return sb.ToString();
            }

            sb.Append("| Asset | Source | Licence | URL |\n|---|---|---|---|\n");
            foreach (object raw in entries)
            {
                var entry = raw as Dictionary<string, object>;
                if (entry == null) continue;
                sb.Append("| ").Append(Json.GetString(entry, "asset", ""))
                  .Append(" | ").Append(Json.GetString(entry, "source", ""))
                  .Append(" | ").Append(Json.GetString(entry, "licence", ""))
                  .Append(" | ").Append(Json.GetString(entry, "sourceUrl", "") ?? "")
                  .Append(" |\n");
            }
            return sb.ToString();
        }

        private static string Sha256(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ helpers

        private static string NormalizeFolder(string folder)
        {
            string normalized = UdonReflection.NormalizePath(folder ?? "Assets").TrimEnd('/');
            if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("folder must be under Assets (got \"" + folder + "\")");
            return normalized;
        }

        internal static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
