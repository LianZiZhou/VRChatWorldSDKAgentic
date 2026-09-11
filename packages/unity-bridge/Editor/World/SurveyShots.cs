using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Renders the scene from chosen viewpoints to PNG files.
    ///
    /// This is the agent's only way to judge composition - whether a room reads as a room, whether a
    /// prop faces the right way, whether the lighting is black. Numbers from scene.spatial_audit cannot
    /// answer any of that. Shots are taken from player eye height at the spawn points by default, because
    /// that is the only viewpoint that actually matters for a VRChat world.
    ///
    /// Files land under &lt;project&gt;/.agentic/shots and the absolute paths are returned, so the caller can
    /// read them back as images.
    /// </summary>
    public static class SurveyShots
    {
        private const float EyeHeight = 1.6f;

        public static void Register()
        {
            BridgeRouter.Register("scene.survey_shots",
                "Render the scene to PNGs from spawn points, an orbit, and/or top-down. " +
                "Args: mode(spawns|orbit|top|all), width, height, yawSteps, orbitCount, fieldOfView, label.",
                delegate (Dictionary<string, object> a) { return Capture(a); }, defaultTimeoutMs: 180000);
        }

        private static Dictionary<string, object> Capture(Dictionary<string, object> a)
        {
            string mode = (Json.GetString(a, "mode", "all") ?? "all").ToLowerInvariant();
            int width = Mathf.Clamp(Json.GetInt(a, "width", 960), 128, 3840);
            int height = Mathf.Clamp(Json.GetInt(a, "height", 540), 128, 2160);
            int yawSteps = Mathf.Clamp(Json.GetInt(a, "yawSteps", 4), 1, 12);
            int orbitCount = Mathf.Clamp(Json.GetInt(a, "orbitCount", 4), 1, 16);
            float fov = Mathf.Clamp(ReadFloat(a, "fieldOfView", 70f), 20f, 140f);
            string label = Sanitize(Json.GetString(a, "label", "survey"));

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(BridgePaths.StateDir, "shots");
            Directory.CreateDirectory(dir);

            var views = new List<ViewPoint>();
            if (mode == "spawns" || mode == "all") views.AddRange(SpawnViews(yawSteps));
            if (mode == "orbit" || mode == "all") views.AddRange(OrbitViews(orbitCount));
            if (mode == "top" || mode == "all") views.AddRange(TopViews());

            if (views.Count == 0)
                return new Dictionary<string, object>
                {
                    { "shots", new List<object>() },
                    { "reason", "Nothing to photograph: no spawn points and no renderers with bounds." }
                };

            var shots = new List<object>();
            var cameraHolder = new GameObject("__agentic_survey_camera");
            cameraHolder.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                var camera = cameraHolder.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = fov;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 2000f;
                camera.enabled = false;

                foreach (var view in views)
                {
                    cameraHolder.transform.position = view.position;
                    cameraHolder.transform.rotation = view.rotation;

                    string file = Path.Combine(dir, stamp + "-" + label + "-" + Sanitize(view.name) + ".png");
                    RenderToFile(camera, width, height, file);

                    shots.Add(new Dictionary<string, object>
                    {
                        { "name", view.name },
                        { "file", file.Replace('\\', '/') },
                        { "position", view.position },
                        { "eulerAngles", view.rotation.eulerAngles },
                        { "description", view.description }
                    });
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraHolder);
            }

            return new Dictionary<string, object>
            {
                { "shotCount", shots.Count },
                { "directory", dir.Replace('\\', '/') },
                { "shots", shots },
                { "hint", "Read the files by absolute path to look at them." }
            };
        }

        private static void RenderToFile(Camera camera, int width, int height, string file)
        {
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            target.antiAliasing = 2;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;

            try
            {
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false);
                File.WriteAllBytes(file, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        // ------------------------------------------------------------------ viewpoints

        private struct ViewPoint
        {
            public string name;
            public string description;
            public Vector3 position;
            public Quaternion rotation;
        }

        private static List<ViewPoint> SpawnViews(int yawSteps)
        {
            var views = new List<ViewPoint>();
            var descriptorType = Reflect.ResolveType("VRC.SDKBase.VRC_SceneDescriptor");

            foreach (var obj in UnityEngine.Object.FindObjectsOfType(descriptorType, true))
            {
                var component = obj as Component;
                if (component == null) continue;
                var field = descriptorType.GetField("spawns");
                var spawns = field == null ? null : field.GetValue(component) as Transform[];
                if (spawns == null) continue;

                for (int i = 0; i < spawns.Length; i++)
                {
                    if (spawns[i] == null) continue;
                    Vector3 eye = spawns[i].position + Vector3.up * EyeHeight;
                    for (int step = 0; step < yawSteps; step++)
                    {
                        float yaw = spawns[i].eulerAngles.y + step * (360f / yawSteps);
                        views.Add(new ViewPoint
                        {
                            name = "spawn" + i + "-yaw" + Mathf.RoundToInt(yaw),
                            description = "From spawn " + i + " at " + EyeHeight + "m eye height, facing " + Mathf.RoundToInt(yaw) + " degrees.",
                            position = eye,
                            rotation = Quaternion.Euler(0f, yaw, 0f)
                        });
                    }
                }
            }
            return views;
        }

        private static List<ViewPoint> OrbitViews(int count)
        {
            var views = new List<ViewPoint>();
            Bounds bounds;
            if (!TryGetSceneBounds(out bounds)) return views;

            float radius = Mathf.Max(bounds.extents.magnitude * 1.6f, 5f);
            for (int i = 0; i < count; i++)
            {
                float angle = i * (360f / count);
                var offset = Quaternion.Euler(0f, angle, 0f) * new Vector3(0f, 0f, -radius);
                Vector3 position = bounds.center + offset + Vector3.up * bounds.extents.y * 0.8f;
                views.Add(new ViewPoint
                {
                    name = "orbit" + Mathf.RoundToInt(angle),
                    description = "Looking at the scene centre from " + Mathf.RoundToInt(angle) + " degrees, " + radius.ToString("0") + "m out.",
                    position = position,
                    rotation = Quaternion.LookRotation(bounds.center - position, Vector3.up)
                });
            }
            return views;
        }

        private static List<ViewPoint> TopViews()
        {
            var views = new List<ViewPoint>();
            Bounds bounds;
            if (!TryGetSceneBounds(out bounds)) return views;

            float altitude = bounds.max.y + Mathf.Max(bounds.size.x, bounds.size.z) * 0.9f + 5f;
            views.Add(new ViewPoint
            {
                name = "top",
                description = "Top-down plan view of the whole scene.",
                position = new Vector3(bounds.center.x, altitude, bounds.center.z),
                rotation = Quaternion.Euler(90f, 0f, 0f)
            });
            return views;
        }

        private static bool TryGetSceneBounds(out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;

            for (int s = 0; s < EditorSceneManager.sceneCount; s++)
            {
                var scene = EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
                    {
                        if (renderer == null || !renderer.enabled) continue;
                        if (renderer is ParticleSystemRenderer) continue;
                        if (!any) { bounds = renderer.bounds; any = true; }
                        else bounds.Encapsulate(renderer.bounds);
                    }
                }
            }
            return any;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "shot";
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') chars[i] = '_';
            }
            return new string(chars);
        }

        private static float ReadFloat(Dictionary<string, object> a, string key, float fallback)
        {
            object value;
            if (a == null || !a.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }
    }
}
