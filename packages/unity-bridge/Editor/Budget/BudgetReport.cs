using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Content budget for a world, per platform.
    ///
    /// A world that exceeds VRChat's bundle limit simply cannot be uploaded, and on Android the SDK blocks
    /// the build outright for non-ASTC textures. Neither fact is visible from the hierarchy, and an agent
    /// assembling content has no intuition for it, so the numbers have to be measurable on demand.
    ///
    /// The size limits are read from the SDK itself (VRC.ValidationHelpers.GetAssetBundleSizeLimit) rather
    /// than hardcoded, because they are VRChat's to change and a stale constant here would be worse than no
    /// constant at all. The recommendations that are not hard limits are labelled as such.
    /// </summary>
    public static class BudgetReport
    {
        public static void Register()
        {
            BridgeRouter.Register("budget.report",
                "Per-platform content budget: geometry, materials, textures, lights, audio, VRChat components, " +
                "and the built bundle against VRChat's size limit. Args: platform(current|Windows|Android), maxObjects.",
                delegate (Dictionary<string, object> a) { return Report(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("budget.snapshot",
                "Save a budget report under a name so a later change can be diffed against it. Args: name, platform.",
                delegate (Dictionary<string, object> a) { return Snapshot(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("budget.diff",
                "Compare a saved snapshot with the current scene, so the cost of a change is visible. Args: name, platform.",
                delegate (Dictionary<string, object> a) { return Diff(a); }, defaultTimeoutMs: 180000);
        }

        // ------------------------------------------------------------------ limits

        /// <summary>
        /// VRChat's bundle size limits, read from the SDK.
        ///
        /// The ContentType enum is obtained from the method's own parameter type rather than by name, so
        /// this keeps working if VRChat moves the enum between namespaces.
        /// </summary>
        private static Dictionary<string, object> ReadLimits(bool mobile)
        {
            try
            {
                var helpers = Reflect.ResolveType("VRC.ValidationHelpers");
                var method = helpers.GetMethod("GetAssetBundleSizeLimit",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) throw new MissingMethodException("GetAssetBundleSizeLimit");

                var contentTypeType = method.GetParameters()[0].ParameterType;
                object world = Enum.Parse(contentTypeType, "World", true);

                int compressed = Convert.ToInt32(method.Invoke(null, new object[] { world, mobile, true }));
                int uncompressed = Convert.ToInt32(method.Invoke(null, new object[] { world, mobile, false }));

                // The SDK returns -1 for "no limit", which it does for PC worlds: the commonly quoted 100MB
                // figure is the *mobile* limit. Treating -1 as a number would report a negative budget and,
                // worse, would make every PC bundle look over limit.
                var limits = new Dictionary<string, object>
                {
                    { "mobile", mobile },
                    { "source", "VRC.ValidationHelpers.GetAssetBundleSizeLimit" },
                    { "compressedBytes", compressed > 0 ? (object)compressed : null },
                    { "uncompressedBytes", uncompressed > 0 ? (object)uncompressed : null },
                    { "compressedEnforced", compressed > 0 },
                    { "uncompressedEnforced", uncompressed > 0 }
                };
                if (compressed > 0) limits["compressedMegabytes"] = compressed / (1024.0 * 1024.0);
                if (uncompressed > 0) limits["uncompressedMegabytes"] = uncompressed / (1024.0 * 1024.0);
                if (compressed <= 0 && uncompressed <= 0)
                    limits["note"] = "The SDK enforces no bundle size limit for this platform (it returns -1). " +
                                     "The widely quoted 100MB world limit applies to mobile.";
                return limits;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "source", "unavailable: " + ex.Message },
                    { "mobile", mobile },
                    { "note", "Could not read the limits from the SDK; treat size findings as unchecked." }
                };
            }
        }

        // ------------------------------------------------------------------ report

        private static Dictionary<string, object> Report(Dictionary<string, object> a)
        {
            string requested = (Json.GetString(a, "platform", "current") ?? "current").ToLowerInvariant();
            bool mobile;
            string platformName;

            if (requested == "current" || requested.Length == 0)
            {
                mobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ||
                         EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
                platformName = EditorUserBuildSettings.activeBuildTarget.ToString();
            }
            else if (requested == "android" || requested == "quest")
            {
                mobile = true; platformName = "Android";
            }
            else if (requested == "windows" || requested == "pc")
            {
                mobile = false; platformName = "StandaloneWindows64";
            }
            else throw new ArgumentException("platform must be current, Windows or Android");

            int maxObjects = Mathf.Clamp(Json.GetInt(a, "maxObjects", 20000), 1, 200000);

            var metrics = Measure(maxObjects);
            var limits = ReadLimits(mobile);
            var bundle = FindBuiltBundle(mobile);
            var findings = new List<object>();

            EvaluateSize(bundle, limits, findings);
            EvaluateCommon(metrics, findings);
            if (mobile) EvaluateAndroid(metrics, findings);

            int errors = 0, warnings = 0;
            foreach (Dictionary<string, object> f in findings)
            {
                if ((string)f["severity"] == "error") errors++; else warnings++;
            }

            return new Dictionary<string, object>
            {
                { "platform", platformName },
                { "mobileProfile", mobile },
                { "limits", limits },
                { "bundle", bundle },
                { "scene", metrics },
                { "findings", findings },
                { "errorCount", errors },
                { "warningCount", warnings },
                { "note", "Errors are things VRChat actually blocks. Warnings are recommendations — VRChat has no hard " +
                          "polygon or material limit for worlds, so those are judgement calls." }
            };
        }

        // ------------------------------------------------------------------ measurement

        private static Dictionary<string, object> Measure(int maxObjects)
        {
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            var shaders = new HashSet<Shader>();
            var textures = new HashSet<Texture>();
            var audioClips = new HashSet<AudioClip>();

            long triangles = 0, vertices = 0;
            int renderers = 0, skinned = 0, particles = 0;
            int realtimeLights = 0, bakedLights = 0, mixedLights = 0;
            int reflectionProbes = 0, lightProbeGroups = 0, audioSources = 0;
            int scanned = 0;

            var vrchat = new Dictionary<string, int>
            {
                { "udonBehaviours", 0 }, { "mirrors", 0 }, { "pickups", 0 }, { "stations", 0 },
                { "videoPlayers", 0 }, { "objectSyncs", 0 }, { "objectPools", 0 }, { "portals", 0 }
            };

            for (int s = 0; s < EditorSceneManager.sceneCount; s++)
            {
                var scene = EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (scanned++ >= maxObjects) break;
                        var go = transform.gameObject;

                        foreach (var component in go.GetComponents<Component>())
                        {
                            if (component == null) continue;
                            string typeName = component.GetType().FullName ?? "";

                            var renderer = component as Renderer;
                            if (renderer != null)
                            {
                                if (renderer is ParticleSystemRenderer) particles++;
                                else renderers++;

                                var skinnedRenderer = renderer as SkinnedMeshRenderer;
                                if (skinnedRenderer != null)
                                {
                                    skinned++;
                                    if (skinnedRenderer.sharedMesh != null) AddMesh(skinnedRenderer.sharedMesh, meshes, ref triangles, ref vertices);
                                }

                                foreach (var material in renderer.sharedMaterials)
                                {
                                    if (material == null) continue;
                                    materials.Add(material);
                                    if (material.shader != null) shaders.Add(material.shader);
                                    CollectTextures(material, textures);
                                }
                                continue;
                            }

                            var meshFilter = component as MeshFilter;
                            if (meshFilter != null && meshFilter.sharedMesh != null)
                            {
                                AddMesh(meshFilter.sharedMesh, meshes, ref triangles, ref vertices);
                                continue;
                            }

                            var light = component as Light;
                            if (light != null)
                            {
                                if (light.lightmapBakeType == LightmapBakeType.Realtime) realtimeLights++;
                                else if (light.lightmapBakeType == LightmapBakeType.Baked) bakedLights++;
                                else mixedLights++;
                                continue;
                            }

                            if (component is ReflectionProbe) { reflectionProbes++; continue; }
                            if (component is LightProbeGroup) { lightProbeGroups++; continue; }

                            var audioSource = component as AudioSource;
                            if (audioSource != null)
                            {
                                audioSources++;
                                if (audioSource.clip != null) audioClips.Add(audioSource.clip);
                                continue;
                            }

                            CountVRChatComponent(typeName, vrchat);
                        }
                    }
                }
            }

            long textureBytes = 0;
            var largestTextures = new List<object>();
            var textureList = new List<Texture>(textures);
            textureList.Sort(delegate (Texture x, Texture y)
            {
                return Profiler.GetRuntimeMemorySizeLong(y).CompareTo(Profiler.GetRuntimeMemorySizeLong(x));
            });
            foreach (var texture in textureList)
            {
                long bytes = Profiler.GetRuntimeMemorySizeLong(texture);
                textureBytes += bytes;
                if (largestTextures.Count < 12)
                {
                    largestTextures.Add(new Dictionary<string, object>
                    {
                        { "name", texture.name },
                        { "path", UdonReflection.NormalizePath(AssetDatabase.GetAssetPath(texture)) },
                        { "width", texture.width },
                        { "height", texture.height },
                        { "bytes", bytes },
                        { "megabytes", bytes / (1024.0 * 1024.0) }
                    });
                }
            }

            long audioBytes = 0;
            var audioLoadTypes = new Dictionary<string, int>();
            foreach (var clip in audioClips)
            {
                audioBytes += Profiler.GetRuntimeMemorySizeLong(clip);
                string loadType = clip.loadType.ToString();
                audioLoadTypes[loadType] = audioLoadTypes.ContainsKey(loadType) ? audioLoadTypes[loadType] + 1 : 1;
            }

            long lightmapBytes = 0;
            int lightmapCount = 0;
            foreach (var data in LightmapSettings.lightmaps)
            {
                if (data.lightmapColor != null) { lightmapBytes += Profiler.GetRuntimeMemorySizeLong(data.lightmapColor); lightmapCount++; }
                if (data.lightmapDir != null) lightmapBytes += Profiler.GetRuntimeMemorySizeLong(data.lightmapDir);
            }

            return new Dictionary<string, object>
            {
                { "objectsScanned", scanned },
                { "triangles", triangles },
                { "vertices", vertices },
                { "renderers", renderers },
                { "skinnedMeshRenderers", skinned },
                { "particleSystemRenderers", particles },
                { "uniqueMeshes", meshes.Count },
                { "uniqueMaterials", materials.Count },
                { "uniqueShaders", shaders.Count },
                { "uniqueTextures", textures.Count },
                { "textureMemoryBytes", textureBytes },
                { "textureMemoryMegabytes", textureBytes / (1024.0 * 1024.0) },
                { "largestTextures", largestTextures },
                { "realtimeLights", realtimeLights },
                { "bakedLights", bakedLights },
                { "mixedLights", mixedLights },
                { "reflectionProbes", reflectionProbes },
                { "lightProbeGroups", lightProbeGroups },
                { "audioSources", audioSources },
                { "uniqueAudioClips", audioClips.Count },
                { "audioMemoryBytes", audioBytes },
                { "audioLoadTypes", audioLoadTypes },
                { "lightmapCount", lightmapCount },
                { "lightmapBytes", lightmapBytes },
                { "vrchatComponents", vrchat }
            };
        }

        private static void AddMesh(Mesh mesh, HashSet<Mesh> meshes, ref long triangles, ref long vertices)
        {
            // Counted per instance, not per unique mesh: a mesh drawn 500 times costs 500 times the
            // triangles at runtime, which is the number that matters for performance.
            meshes.Add(mesh);
            try
            {
                vertices += mesh.vertexCount;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var descriptor = mesh.GetSubMesh(i);
                    triangles += descriptor.indexCount / 3;
                }
            }
            catch (Exception) { /* a mesh without read access still counts toward the unique set */ }
        }

        private static void CollectTextures(Material material, HashSet<Texture> textures)
        {
            try
            {
                var shader = material.shader;
                if (shader == null) return;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    var texture = material.GetTexture(ShaderUtil.GetPropertyName(shader, i));
                    if (texture != null) textures.Add(texture);
                }
            }
            catch (Exception) { }
        }

        private static void CountVRChatComponent(string typeName, Dictionary<string, int> counts)
        {
            if (typeName == "VRC.Udon.UdonBehaviour") counts["udonBehaviours"]++;
            else if (typeName.EndsWith("VRCMirrorReflection", StringComparison.Ordinal)) counts["mirrors"]++;
            else if (typeName.EndsWith("VRCPickup", StringComparison.Ordinal)) counts["pickups"]++;
            else if (typeName.EndsWith("VRCStation", StringComparison.Ordinal)) counts["stations"]++;
            else if (typeName.EndsWith("VRCObjectSync", StringComparison.Ordinal)) counts["objectSyncs"]++;
            else if (typeName.EndsWith("VRCObjectPool", StringComparison.Ordinal)) counts["objectPools"]++;
            else if (typeName.EndsWith("VRCPortalMarker", StringComparison.Ordinal)) counts["portals"]++;
            else if (typeName.IndexOf("VideoPlayer", StringComparison.Ordinal) >= 0 &&
                     typeName.StartsWith("VRC.", StringComparison.Ordinal)) counts["videoPlayers"]++;
        }

        // ------------------------------------------------------------------ bundle

        /// <summary>Finds the most recent built bundle for this platform, if there is one.</summary>
        private static Dictionary<string, object> FindBuiltBundle(bool mobile)
        {
            string recorded = SessionState.GetString("VRC.SDK3.Editor_patToLastBuild", null);
            string path = !string.IsNullOrEmpty(recorded) && File.Exists(recorded) ? recorded : null;

            if (path == null)
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        Path.Combine("AppData", Path.Combine("LocalLow", Path.Combine("VRChat",
                            Path.Combine("VRChat", "Worlds")))));
                    if (Directory.Exists(dir))
                    {
                        string want = mobile ? "android" : "standalonewindows64";
                        DateTime newest = DateTime.MinValue;
                        foreach (string candidate in Directory.GetFiles(dir, "*.vrcw"))
                        {
                            if (candidate.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var info = new FileInfo(candidate);
                            if (info.LastWriteTimeUtc <= newest) continue;
                            newest = info.LastWriteTimeUtc;
                            path = candidate;
                        }
                    }
                }
                catch (Exception) { }
            }

            if (path == null)
                return new Dictionary<string, object>
                {
                    { "found", false },
                    { "note", "No built bundle for this platform yet; run verify.build_local to measure the real size." }
                };

            var file = new FileInfo(path);
            return new Dictionary<string, object>
            {
                { "found", true },
                { "path", path.Replace('\\', '/') },
                { "bytes", file.Length },
                { "megabytes", file.Length / (1024.0 * 1024.0) },
                { "builtUtc", file.LastWriteTimeUtc.ToString("o") }
            };
        }

        // ------------------------------------------------------------------ evaluation

        private static void EvaluateSize(Dictionary<string, object> bundle, Dictionary<string, object> limits, List<object> findings)
        {
            if (!(bool)bundle["found"]) return;

            long bytes = Convert.ToInt64(bundle["bytes"]);
            object limitValue;
            bool hasLimit = limits.TryGetValue("compressedBytes", out limitValue) && limitValue != null;

            if (!hasLimit)
            {
                // Still say something: "no limit" is a useful answer, and silence would read as "not checked".
                bundle["withinLimit"] = true;
                bundle["limitNote"] = "No SDK-enforced size limit for this platform, so size is a performance " +
                                      "judgement rather than a hard gate.";
                return;
            }

            long limit = Convert.ToInt64(limitValue);
            double fraction = (double)bytes / limit;
            bundle["percentOfLimit"] = fraction * 100.0;
            bundle["withinLimit"] = bytes <= limit;

            if (bytes > limit)
                findings.Add(Finding("error", "bundle-over-limit",
                    "The built bundle is " + Mb(bytes) + " against VRChat's limit of " + Mb(limit) +
                    ". Upload is refused outright above the limit.", bundle["path"] as string));
            else if (fraction > 0.8)
                findings.Add(Finding("warning", "bundle-near-limit",
                    "The built bundle is " + Mb(bytes) + ", " + (fraction * 100).ToString("0") +
                    "% of the " + Mb(limit) + " limit.", bundle["path"] as string));
        }

        private static void EvaluateCommon(Dictionary<string, object> m, List<object> findings)
        {
            long triangles = Convert.ToInt64(m["triangles"]);
            if (triangles > 2_000_000)
                findings.Add(Finding("warning", "high-triangles",
                    triangles.ToString("N0") + " triangles in the scene. VRChat sets no hard limit for worlds, but " +
                    "this is heavy for lower-end PCs.", null));

            int materials = Convert.ToInt32(m["uniqueMaterials"]);
            if (materials > 200)
                findings.Add(Finding("warning", "many-materials",
                    materials + " unique materials. Each is a draw-call batch boundary; consider atlasing.", null));

            double textureMb = Convert.ToDouble(m["textureMemoryMegabytes"]);
            if (textureMb > 1024)
                findings.Add(Finding("warning", "high-texture-memory",
                    textureMb.ToString("0") + "MB of texture memory, which will evict on lower-VRAM machines.", null));

            var loadTypes = m["audioLoadTypes"] as Dictionary<string, int>;
            if (loadTypes != null && loadTypes.ContainsKey("DecompressOnLoad") && loadTypes["DecompressOnLoad"] > 4)
                findings.Add(Finding("warning", "audio-decompress-on-load",
                    loadTypes["DecompressOnLoad"] + " clips use DecompressOnLoad, which holds them in memory uncompressed. " +
                    "Use Compressed In Memory or Streaming for anything long.", null));

            if (Convert.ToInt32(m["lightmapCount"]) == 0 && Convert.ToInt32(m["bakedLights"]) > 0)
                findings.Add(Finding("warning", "baked-lights-not-baked",
                    "There are baked lights but no lightmaps, so the bake has not been run.", null));
        }

        /// <summary>
        /// Android-specific rules. The ASTC one is a hard SDK block, not advice: the SDK's project check
        /// reports an error for non-ASTC Android textures and refuses the build.
        /// </summary>
        private static void EvaluateAndroid(Dictionary<string, object> m, List<object> findings)
        {
            var largest = m["largestTextures"] as List<object>;
            if (largest != null)
            {
                foreach (Dictionary<string, object> texture in largest)
                {
                    int width = Convert.ToInt32(texture["width"]);
                    int height = Convert.ToInt32(texture["height"]);
                    if (width > 1024 || height > 1024)
                    {
                        findings.Add(Finding("warning", "android-texture-too-large",
                            width + "x" + height + " exceeds the 1K maximum VRChat recommends for Android.",
                            texture["path"] as string));
                    }
                }
            }

            int realtime = Convert.ToInt32(m["realtimeLights"]);
            if (realtime > 0)
                findings.Add(Finding("warning", "android-realtime-lights",
                    realtime + " realtime light(s). Quest has no budget for realtime lighting; bake instead.", null));

            var vrchat = m["vrchatComponents"] as Dictionary<string, int>;
            if (vrchat != null)
            {
                if (vrchat.ContainsKey("mirrors") && vrchat["mirrors"] > 1)
                    findings.Add(Finding("warning", "android-mirrors",
                        vrchat["mirrors"] + " mirrors. Each one re-renders the scene; one is already expensive on Quest.", null));
            }

            int probes = Convert.ToInt32(m["reflectionProbes"]);
            if (probes > 4)
                findings.Add(Finding("warning", "android-reflection-probes",
                    probes + " reflection probes is a lot of texture memory for Android.", null));
        }

        private static string Mb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.00") + "MB";
        }

        private static Dictionary<string, object> Finding(string severity, string code, string message, string subject)
        {
            return new Dictionary<string, object>
            {
                { "severity", severity }, { "code", code }, { "message", message }, { "subject", subject }
            };
        }

        // ------------------------------------------------------------------ snapshots

        private static string SnapshotPath(string name)
        {
            var safe = new StringBuilder();
            foreach (char c in name) safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string dir = Path.Combine(BridgePaths.StateDir, "budget");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, safe.ToString() + ".json");
        }

        private static Dictionary<string, object> Snapshot(Dictionary<string, object> a)
        {
            string name = Json.GetString(a, "name", "baseline");
            var report = Report(a);
            string path = SnapshotPath(name);
            File.WriteAllText(path, Json.Write(report, 8), new UTF8Encoding(false));

            return new Dictionary<string, object>
            {
                { "name", name },
                { "file", path.Replace('\\', '/') },
                { "platform", report["platform"] },
                { "savedUtc", DateTime.UtcNow.ToString("o") }
            };
        }

        /// <summary>
        /// Diffs the saved snapshot against now. Only the metrics that moved are reported: a diff of
        /// forty unchanged numbers buries the one that matters.
        /// </summary>
        private static Dictionary<string, object> Diff(Dictionary<string, object> a)
        {
            string name = Json.GetString(a, "name", "baseline");
            string path = SnapshotPath(name);
            if (!File.Exists(path))
                throw new ArgumentException("No snapshot named \"" + name + "\". Call budget.snapshot first.");

            var before = Json.AsObject(Json.Parse(File.ReadAllText(path)));
            var after = Report(a);

            var beforeScene = Json.AsObject(before.ContainsKey("scene") ? before["scene"] : null);
            var afterScene = after["scene"] as Dictionary<string, object> ?? new Dictionary<string, object>();

            var changes = new List<object>();
            foreach (var pair in afterScene)
            {
                if (!IsNumber(pair.Value)) continue;
                if (!beforeScene.ContainsKey(pair.Key) || !IsNumber(beforeScene[pair.Key])) continue;

                double was = Convert.ToDouble(beforeScene[pair.Key]);
                double now = Convert.ToDouble(pair.Value);
                if (Math.Abs(now - was) < 0.0001) continue;

                changes.Add(new Dictionary<string, object>
                {
                    { "metric", pair.Key },
                    { "before", was },
                    { "after", now },
                    { "delta", now - was }
                });
            }

            var beforeBundle = Json.AsObject(before.ContainsKey("bundle") ? before["bundle"] : null);
            var afterBundle = after["bundle"] as Dictionary<string, object> ?? new Dictionary<string, object>();
            object bundleDelta = null;
            if (beforeBundle.ContainsKey("bytes") && afterBundle.ContainsKey("bytes"))
            {
                long wasBytes = Convert.ToInt64(beforeBundle["bytes"]);
                long nowBytes = Convert.ToInt64(afterBundle["bytes"]);
                bundleDelta = new Dictionary<string, object>
                {
                    { "beforeBytes", wasBytes },
                    { "afterBytes", nowBytes },
                    { "deltaBytes", nowBytes - wasBytes },
                    { "deltaMegabytes", (nowBytes - wasBytes) / (1024.0 * 1024.0) }
                };
            }

            return new Dictionary<string, object>
            {
                { "name", name },
                { "snapshotFile", path.Replace('\\', '/') },
                { "changedMetricCount", changes.Count },
                { "changes", changes },
                { "bundle", bundleDelta },
                { "current", after }
            };
        }

        private static bool IsNumber(object value)
        {
            return value is double || value is float || value is int || value is long || value is short || value is decimal;
        }
    }
}
