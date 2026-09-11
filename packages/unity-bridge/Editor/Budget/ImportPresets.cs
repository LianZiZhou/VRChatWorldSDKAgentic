using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Per-platform asset import settings.
    ///
    /// Import settings are where a dual-platform world is actually won or lost. Textures dominate bundle
    /// size, and on Android the SDK does not merely advise: its project check reports an *error* for
    /// non-ASTC Android textures and refuses the build. Audio is the other common surprise — a few long
    /// clips left on DecompressOnLoad will blow the memory budget on Quest.
    ///
    /// Auditing is separate from applying because applying reimports assets, which is slow and
    /// irreversible-feeling; an agent should be able to see what would change first.
    /// </summary>
    public static class ImportPresets
    {
        public static void Register()
        {
            BridgeRouter.Register("budget.import_audit",
                "Report which assets do not match the platform's recommended import settings, without " +
                "changing anything. Args: platform(current|Windows|Android), folder, maxAssets.",
                delegate (Dictionary<string, object> a) { return Audit(a, false); }, defaultTimeoutMs: 300000);

            JobStore.RegisterKind("budget.apply_import_presets", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return ApplyJob(args, ctx);
            });
        }

        private sealed class Profile
        {
            public bool mobile;
            public string platformName;
            public int maxTextureSize;
            public TextureImporterFormat textureFormat;
            public int textureCompressionQuality;
            public ModelImporterMeshCompression meshCompression;
            /// <summary>Clips longer than this stream rather than living in memory.</summary>
            public float streamingSeconds;
        }

        private static Profile ResolveProfile(Dictionary<string, object> a)
        {
            string requested = (Json.GetString(a, "platform", "current") ?? "current").ToLowerInvariant();
            bool mobile;
            if (requested == "current" || requested.Length == 0)
                mobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ||
                         EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
            else if (requested == "android" || requested == "quest") mobile = true;
            else if (requested == "windows" || requested == "pc") mobile = false;
            else throw new ArgumentException("platform must be current, Windows or Android");

            var profile = new Profile
            {
                mobile = mobile,
                platformName = mobile ? "Android" : "Standalone",
                // 1K is VRChat's stated Android maximum; PC tolerates 2K comfortably.
                maxTextureSize = Json.GetInt(a, "maxTextureSize", mobile ? 1024 : 2048),
                // ASTC is required on Android: the SDK errors on anything else and blocks the build.
                textureFormat = mobile ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.DXT5,
                textureCompressionQuality = 50,
                meshCompression = mobile ? ModelImporterMeshCompression.Medium : ModelImporterMeshCompression.Low,
                streamingSeconds = 10f
            };

            string format = Json.GetString(a, "textureFormat");
            if (!string.IsNullOrEmpty(format))
                profile.textureFormat = (TextureImporterFormat)Enum.Parse(typeof(TextureImporterFormat), format, true);

            return profile;
        }

        // ------------------------------------------------------------------ audit / apply

        private static Dictionary<string, object> Audit(Dictionary<string, object> a, bool apply)
        {
            var profile = ResolveProfile(a);
            string folder = UdonReflection.NormalizePath(Json.GetString(a, "folder", "Assets")).TrimEnd('/');
            if (!folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("folder must be under Assets");
            int maxAssets = Mathf.Clamp(Json.GetInt(a, "maxAssets", 2000), 1, 50000);

            var textureChanges = new List<object>();
            var audioChanges = new List<object>();
            var meshChanges = new List<object>();
            int examined = 0;
            int applied = 0;

            if (apply) AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
                {
                    if (examined++ >= maxAssets) break;
                    var change = InspectTexture(AssetDatabase.GUIDToAssetPath(guid), profile, apply);
                    if (change != null) { textureChanges.Add(change); if (apply) applied++; }
                }
                foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
                {
                    if (examined++ >= maxAssets) break;
                    var change = InspectAudio(AssetDatabase.GUIDToAssetPath(guid), profile, apply);
                    if (change != null) { audioChanges.Add(change); if (apply) applied++; }
                }
                foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { folder }))
                {
                    if (examined++ >= maxAssets) break;
                    var change = InspectModel(AssetDatabase.GUIDToAssetPath(guid), profile, apply);
                    if (change != null) { meshChanges.Add(change); if (apply) applied++; }
                }
            }
            finally
            {
                if (apply)
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }
            }

            return new Dictionary<string, object>
            {
                { "platform", profile.platformName },
                { "mobileProfile", profile.mobile },
                { "applied", apply },
                { "assetsExamined", examined },
                { "assetsChanged", applied },
                { "profile", new Dictionary<string, object>
                    {
                        { "maxTextureSize", profile.maxTextureSize },
                        { "textureFormat", profile.textureFormat.ToString() },
                        { "meshCompression", profile.meshCompression.ToString() },
                        { "streamingSeconds", profile.streamingSeconds }
                    }
                },
                { "textures", textureChanges },
                { "audio", audioChanges },
                { "models", meshChanges },
                { "note", profile.mobile
                    ? "Android textures must be ASTC: the SDK reports an error and blocks the build otherwise."
                    : "PC has no hard import requirements; these settings trade size against quality." }
            };
        }

        private static Dictionary<string, object> InspectTexture(string path, Profile profile, bool apply)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return null;

            var settings = importer.GetPlatformTextureSettings(profile.platformName);
            var reasons = new List<string>();

            bool wantsOverride = !settings.overridden;
            bool wantsSize = settings.maxTextureSize > profile.maxTextureSize;
            bool wantsFormat = settings.format != profile.textureFormat;

            if (wantsOverride) reasons.Add("no " + profile.platformName + " override");
            if (wantsSize) reasons.Add("maxTextureSize " + settings.maxTextureSize + " > " + profile.maxTextureSize);
            if (wantsFormat) reasons.Add("format " + settings.format + " != " + profile.textureFormat);
            if (reasons.Count == 0) return null;

            if (apply)
            {
                settings.overridden = true;
                settings.name = profile.platformName;
                if (wantsSize) settings.maxTextureSize = profile.maxTextureSize;
                settings.format = profile.textureFormat;
                settings.compressionQuality = profile.textureCompressionQuality;
                importer.SetPlatformTextureSettings(settings);
                importer.SaveAndReimport();
            }

            return new Dictionary<string, object>
            {
                { "path", UdonReflection.NormalizePath(path) },
                { "reasons", reasons },
                { "changed", apply }
            };
        }

        private static Dictionary<string, object> InspectAudio(string path, Profile profile, bool apply)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) return null;

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            float length = clip == null ? 0f : clip.length;

            // Long clips stream; short ones stay compressed in memory. DecompressOnLoad is the expensive
            // default that catches people out.
            var wantedLoadType = length >= profile.streamingSeconds
                ? AudioClipLoadType.Streaming
                : AudioClipLoadType.CompressedInMemory;

            var settings = importer.GetOverrideSampleSettings(profile.platformName);
            var reasons = new List<string>();
            if (settings.loadType != wantedLoadType)
                reasons.Add("loadType " + settings.loadType + " != " + wantedLoadType + " (clip is " + length.ToString("0.0") + "s)");
            if (settings.compressionFormat != AudioCompressionFormat.Vorbis)
                reasons.Add("compression " + settings.compressionFormat + " != Vorbis");
            if (reasons.Count == 0) return null;

            if (apply)
            {
                settings.loadType = wantedLoadType;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                importer.SetOverrideSampleSettings(profile.platformName, settings);
                importer.SaveAndReimport();
            }

            return new Dictionary<string, object>
            {
                { "path", UdonReflection.NormalizePath(path) },
                { "lengthSeconds", length },
                { "reasons", reasons },
                { "changed", apply }
            };
        }

        private static Dictionary<string, object> InspectModel(string path, Profile profile, bool apply)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) return null;

            var reasons = new List<string>();
            if (importer.meshCompression != profile.meshCompression)
                reasons.Add("meshCompression " + importer.meshCompression + " != " + profile.meshCompression);
            // Read/write costs a second copy of every mesh in memory and is rarely needed in a world.
            if (importer.isReadable) reasons.Add("isReadable keeps a CPU-side copy of the mesh");
            if (reasons.Count == 0) return null;

            if (apply)
            {
                importer.meshCompression = profile.meshCompression;
                importer.isReadable = false;
                importer.SaveAndReimport();
            }

            return new Dictionary<string, object>
            {
                { "path", UdonReflection.NormalizePath(path) },
                { "reasons", reasons },
                { "changed", apply }
            };
        }

        // ------------------------------------------------------------------ job

        /// <summary>
        /// Applying presets reimports every asset it touches, which on a real project runs for minutes, so
        /// it is a job rather than a blocking call.
        /// </summary>
        private static IEnumerator ApplyJob(Dictionary<string, object> args, JobContext ctx)
        {
            ctx.Report(0.05f, "auditing");
            yield return null;

            var audit = Audit(args, false);
            int pending = Convert.ToInt32(audit["assetsExamined"]);
            ctx.Report(0.2f, "applying presets to assets that need them");
            yield return null;

            var result = Audit(args, true);
            ctx.Report(0.95f, "reimport finished");
            yield return null;

            result["auditedBefore"] = pending;
            ctx.SetResult(result);
        }
    }
}
