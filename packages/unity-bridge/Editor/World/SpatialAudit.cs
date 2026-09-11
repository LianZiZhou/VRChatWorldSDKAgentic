using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Quantitative geometry checks, for the problems an agent cannot see.
    ///
    /// An agent assembling a scene from coordinates has no feel for whether a player can stand on the
    /// floor, whether two props are inside each other, or whether something is floating. These are cheap
    /// physics queries that turn "looks plausible in the hierarchy" into numbers. It complements
    /// scene.survey_shots rather than replacing it: this catches geometry, screenshots catch composition.
    ///
    /// The checks are reported with the evidence that produced them, because a flagged object is a
    /// prompt to look, not a verdict - an intentionally floating lamp is not a bug.
    /// </summary>
    public static class SpatialAudit
    {
        private const float PlayerEyeHeight = 1.6f;
        private const int MaxSamplesPerFinding = 12;

        public static void Register()
        {
            BridgeRouter.Register("scene.spatial_audit",
                "Physics-based sanity checks: spawn grounding, floor holes, floating and interpenetrating " +
                "objects, missing colliders, out-of-range positions, scale outliers. " +
                "Args: gridStep, maxObjects, floatThreshold.",
                delegate (Dictionary<string, object> a) { return Audit(a); }, defaultTimeoutMs: 180000);
        }

        private static Dictionary<string, object> Audit(Dictionary<string, object> a)
        {
            float gridStep = Mathf.Clamp(ReadFloat(a, "gridStep", 2f), 0.25f, 20f);
            int maxObjects = Mathf.Clamp(Json.GetInt(a, "maxObjects", 4000), 1, 50000);
            float floatThreshold = ReadFloat(a, "floatThreshold", 0.35f);

            // Edit-mode physics queries read a cached transform state; without this, objects moved in the
            // same call would be raycast at their old positions.
            Physics.SyncTransforms();

            var renderers = CollectRenderers(maxObjects);
            var findings = new List<object>();

            var spawnReport = AuditSpawns(findings);
            var coverage = AuditFloorCoverage(renderers, gridStep, findings);
            AuditFloating(renderers, floatThreshold, findings);
            AuditInterpenetration(renderers, findings);
            AuditMissingColliders(renderers, findings);
            AuditOutOfRange(renderers, findings);
            AuditScaleOutliers(renderers, findings);

            int errors = 0, warnings = 0;
            foreach (Dictionary<string, object> f in findings)
            {
                if ((string)f["severity"] == "error") errors++; else warnings++;
            }

            return new Dictionary<string, object>
            {
                { "rendererCount", renderers.Count },
                { "spawns", spawnReport },
                { "floorCoverage", coverage },
                { "findingCount", findings.Count },
                { "errorCount", errors },
                { "warningCount", warnings },
                { "findings", findings },
                { "note", "Findings are prompts to look, not verdicts. A deliberately floating or overlapping object is fine." }
            };
        }

        // ------------------------------------------------------------------ collection

        private sealed class Sample
        {
            public Renderer renderer;
            public Bounds bounds;
            public string path;
        }

        private static List<Sample> CollectRenderers(int maxObjects)
        {
            var samples = new List<Sample>();
            for (int s = 0; s < EditorSceneManager.sceneCount && samples.Count < maxObjects; s++)
            {
                var scene = EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
                    {
                        if (samples.Count >= maxObjects) break;
                        if (renderer == null || !renderer.enabled) continue;
                        if (renderer is ParticleSystemRenderer) continue;
                        samples.Add(new Sample
                        {
                            renderer = renderer,
                            bounds = renderer.bounds,
                            path = UdonOps.HierarchyPath(renderer.transform)
                        });
                    }
                }
            }
            return samples;
        }

        // ------------------------------------------------------------------ checks

        private static List<object> AuditSpawns(List<object> findings)
        {
            var reports = new List<object>();
            var descriptorType = Reflect.ResolveType("VRC.SDKBase.VRC_SceneDescriptor");

            foreach (var obj in UnityEngine.Object.FindObjectsOfType(descriptorType, true))
            {
                var component = obj as Component;
                if (component == null) continue;

                var spawnsField = descriptorType.GetField("spawns");
                var respawnField = descriptorType.GetField("RespawnHeightY");
                var spawns = spawnsField == null ? null : spawnsField.GetValue(component) as Transform[];
                float respawnY = respawnField == null ? -100f : Convert.ToSingle(respawnField.GetValue(component));

                if (spawns == null) continue;
                for (int i = 0; i < spawns.Length; i++)
                {
                    var spawn = spawns[i];
                    if (spawn == null)
                    {
                        findings.Add(Finding("error", "spawn-null", "Spawn " + i + " is null; the SDK refuses to build with no valid spawn.", null));
                        continue;
                    }

                    Vector3 at = spawn.position;
                    RaycastHit hit;
                    bool grounded = Physics.Raycast(at + Vector3.up * 0.2f, Vector3.down, out hit, 60f);

                    reports.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "path", UdonOps.HierarchyPath(spawn) },
                        { "position", at },
                        { "grounded", grounded },
                        { "groundDistance", grounded ? (object)hit.distance : null },
                        { "groundObject", grounded ? UdonOps.HierarchyPath(hit.transform) : null }
                    });

                    if (!grounded)
                        findings.Add(Finding("error", "spawn-no-ground",
                            "Spawn " + i + " has no collider within 60m below it; players would fall and respawn forever.",
                            UdonOps.HierarchyPath(spawn)));
                    else if (hit.distance > 3f)
                        findings.Add(Finding("warning", "spawn-high",
                            "Spawn " + i + " is " + hit.distance.ToString("0.0") + "m above the ground.",
                            UdonOps.HierarchyPath(spawn)));

                    if (at.y <= respawnY)
                        findings.Add(Finding("error", "spawn-below-respawn",
                            "Spawn " + i + " is at y=" + at.y.ToString("0.0") + ", at or below RespawnHeightY (" + respawnY + ").",
                            UdonOps.HierarchyPath(spawn)));

                    // The SDK itself warns past this distance (WORLD_MAX_SPAWN_OFFSET = 1000).
                    if (Mathf.Abs(at.x) >= 1000f || Mathf.Abs(at.y) >= 1000f || Mathf.Abs(at.z) >= 1000f)
                        findings.Add(Finding("warning", "spawn-far-from-origin",
                            "Spawn " + i + " is more than 1000 units from the origin, which the SDK flags.",
                            UdonOps.HierarchyPath(spawn)));
                }
            }
            return reports;
        }

        /// <summary>
        /// Rays straight down over a grid covering the scene's horizontal extent, counting cells with no
        /// collider beneath. A hole in a floor is invisible in the hierarchy and obvious to a player.
        /// </summary>
        private static Dictionary<string, object> AuditFloorCoverage(List<Sample> samples, float gridStep, List<object> findings)
        {
            if (samples.Count == 0)
                return new Dictionary<string, object> { { "checked", false }, { "reason", "no renderers in the scene" } };

            Bounds total = samples[0].bounds;
            for (int i = 1; i < samples.Count; i++) total.Encapsulate(samples[i].bounds);

            // Only probe a sane area; a skybox-sized scene would otherwise produce a huge grid.
            float sizeX = Mathf.Min(total.size.x, 400f);
            float sizeZ = Mathf.Min(total.size.z, 400f);
            int cellsX = Mathf.Clamp(Mathf.CeilToInt(sizeX / gridStep), 1, 200);
            int cellsZ = Mathf.Clamp(Mathf.CeilToInt(sizeZ / gridStep), 1, 200);

            float startX = total.center.x - sizeX * 0.5f;
            float startZ = total.center.z - sizeZ * 0.5f;
            float fromY = total.max.y + 5f;
            float rayLength = total.size.y + 20f;

            int hits = 0;
            int total_ = 0;
            var holes = new List<object>();

            for (int ix = 0; ix < cellsX; ix++)
            {
                for (int iz = 0; iz < cellsZ; iz++)
                {
                    var origin = new Vector3(startX + (ix + 0.5f) * (sizeX / cellsX), fromY, startZ + (iz + 0.5f) * (sizeZ / cellsZ));
                    total_++;
                    if (Physics.Raycast(origin, Vector3.down, rayLength)) hits++;
                    else if (holes.Count < MaxSamplesPerFinding) holes.Add(new Vector3(origin.x, total.center.y, origin.z));
                }
            }

            float coverage = total_ == 0 ? 0f : (float)hits / total_;
            if (coverage < 0.6f)
                findings.Add(Finding("warning", "sparse-ground",
                    "Only " + (coverage * 100f).ToString("0") + "% of the scene's horizontal extent has a collider beneath it. " +
                    "That is normal for an island layout and a problem for a room.", null));

            return new Dictionary<string, object>
            {
                { "checked", true },
                { "gridCells", total_ },
                { "cellsWithGround", hits },
                { "coverage", coverage },
                { "sampleHoles", holes },
                { "probedArea", new Dictionary<string, object> { { "center", total.center }, { "sizeX", sizeX }, { "sizeZ", sizeZ } } }
            };
        }

        private static void AuditFloating(List<Sample> samples, float threshold, List<object> findings)
        {
            int flagged = 0;
            foreach (var sample in samples)
            {
                if (flagged >= MaxSamplesPerFinding) break;
                if (sample.bounds.size.magnitude < 0.1f) continue;

                // Cast from just under the object's lowest point, ignoring the object itself by starting
                // below it rather than by layer tricks.
                var origin = new Vector3(sample.bounds.center.x, sample.bounds.min.y - 0.02f, sample.bounds.center.z);
                RaycastHit hit;
                bool supported = Physics.Raycast(origin, Vector3.down, out hit, threshold + 0.05f);
                if (supported) continue;

                bool anythingBelow = Physics.Raycast(origin, Vector3.down, out hit, 200f);
                findings.Add(Finding("warning", "floating",
                    anythingBelow
                        ? "Nothing within " + threshold.ToString("0.00") + "m below this object; the nearest surface is " +
                          hit.distance.ToString("0.0") + "m down."
                        : "Nothing at all below this object within 200m.",
                    sample.path));
                flagged++;
            }
        }

        /// <summary>
        /// Flags pairs whose bounds overlap substantially. Bounds are coarse, so the threshold is
        /// deliberately high: the aim is to catch a prop buried inside a wall, not to complain about
        /// every touching surface.
        /// </summary>
        private static void AuditInterpenetration(List<Sample> samples, List<object> findings)
        {
            int flagged = 0;
            int budget = 250_000; // pairwise on a large scene is quadratic; cap the comparisons

            for (int i = 0; i < samples.Count && flagged < MaxSamplesPerFinding && budget > 0; i++)
            {
                for (int j = i + 1; j < samples.Count && flagged < MaxSamplesPerFinding && budget > 0; j++)
                {
                    budget--;
                    var a = samples[i].bounds;
                    var b = samples[j].bounds;
                    if (!a.Intersects(b)) continue;

                    // Parent/child pairs share space by construction.
                    if (samples[j].path.StartsWith(samples[i].path + "/", StringComparison.Ordinal)) continue;
                    if (samples[i].path.StartsWith(samples[j].path + "/", StringComparison.Ordinal)) continue;

                    Vector3 overlap = Vector3.Min(a.max, b.max) - Vector3.Max(a.min, b.min);
                    float overlapVolume = Mathf.Max(0f, overlap.x) * Mathf.Max(0f, overlap.y) * Mathf.Max(0f, overlap.z);
                    float smaller = Mathf.Min(Volume(a), Volume(b));
                    if (smaller <= 0.0001f) continue;

                    float ratio = overlapVolume / smaller;
                    if (ratio < 0.5f) continue;

                    findings.Add(Finding("warning", "interpenetrating",
                        (ratio * 100f).ToString("0") + "% of one object's volume is inside the other.",
                        samples[i].path + "  <->  " + samples[j].path));
                    flagged++;
                }
            }
        }

        private static float Volume(Bounds b)
        {
            return Mathf.Max(0.0001f, b.size.x) * Mathf.Max(0.0001f, b.size.y) * Mathf.Max(0.0001f, b.size.z);
        }

        private static void AuditMissingColliders(List<Sample> samples, List<object> findings)
        {
            int flagged = 0;
            foreach (var sample in samples)
            {
                if (flagged >= MaxSamplesPerFinding) break;
                // Only worth flagging for things big enough to walk into.
                if (sample.bounds.size.magnitude < 1.0f) continue;
                if (sample.renderer.GetComponent<Collider>() != null) continue;
                if (sample.renderer.GetComponentInParent<Collider>() != null) continue;
                if (sample.renderer.GetComponentInChildren<Collider>() != null) continue;

                findings.Add(Finding("warning", "no-collider",
                    "Renderer of size " + sample.bounds.size.magnitude.ToString("0.0") + "m has no collider anywhere on it; " +
                    "players will walk through it.",
                    sample.path));
                flagged++;
            }
        }

        private static void AuditOutOfRange(List<Sample> samples, List<object> findings)
        {
            int flagged = 0;
            foreach (var sample in samples)
            {
                if (flagged >= MaxSamplesPerFinding) break;
                Vector3 p = sample.renderer.transform.position;
                if (Mathf.Abs(p.x) < 5000f && Mathf.Abs(p.y) < 5000f && Mathf.Abs(p.z) < 5000f) continue;
                findings.Add(Finding("warning", "far-from-origin",
                    "Positioned at " + p.ToString("0") + "; floating-point precision degrades badly this far out.",
                    sample.path));
                flagged++;
            }
        }

        private static void AuditScaleOutliers(List<Sample> samples, List<object> findings)
        {
            int flagged = 0;
            foreach (var sample in samples)
            {
                if (flagged >= MaxSamplesPerFinding) break;
                Vector3 scale = sample.renderer.transform.lossyScale;
                float max = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                float min = Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

                if (min <= 0.00001f)
                {
                    findings.Add(Finding("warning", "degenerate-scale", "A scale axis is zero, so the mesh is flattened away.", sample.path));
                    flagged++;
                }
                else if (max / min > 100f)
                {
                    findings.Add(Finding("warning", "extreme-anisotropy",
                        "Scale is wildly non-uniform (" + scale.ToString("0.###") + "), which distorts lighting and colliders.",
                        sample.path));
                    flagged++;
                }
                // Human scale is the reference: a VRChat player is roughly 1.6-1.9m tall.
                else if (sample.bounds.size.magnitude > 2000f)
                {
                    findings.Add(Finding("warning", "enormous",
                        "Bounds diagonal is " + sample.bounds.size.magnitude.ToString("0") + "m; a player is about " +
                        PlayerEyeHeight + "m tall, so check this is intentional.",
                        sample.path));
                    flagged++;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static Dictionary<string, object> Finding(string severity, string code, string message, string subject)
        {
            return new Dictionary<string, object>
            {
                { "severity", severity },
                { "code", code },
                { "message", message },
                { "subject", subject }
            };
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
