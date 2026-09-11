using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Procedural architecture: rooms, corridors and stairs built from correctly-placed primitives.
    ///
    /// This fills the gap between "the agent has a library of props" and "the agent needs a building". The
    /// work it saves is not the primitives — scene.apply_spec can already place those — it is the arithmetic.
    /// A wall with a doorway in it is three boxes whose sizes and offsets all depend on each other, and an
    /// agent computing those from coordinates gets them subtly wrong in ways that only show up as a gap a
    /// player can walk through.
    ///
    /// Primitives rather than generated meshes, deliberately: they carry correct colliders and UVs for free,
    /// which matters more for a blockout than triangle efficiency does.
    /// </summary>
    public static class GeoGenerate
    {
        public static void Register()
        {
            BridgeRouter.Register("geo.generate",
                "Generate architecture from parameters. kinds: room (walls, floor, ceiling, doorways), " +
                "corridor (between two points), stairs. Args: kind, path, plus per-kind parameters. " +
                "Idempotent: regenerating replaces the previous children.",
                delegate (Dictionary<string, object> a) { return Generate(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("geo.kinds",
                "The shapes geo.generate can build, with their parameters and an example.",
                delegate (Dictionary<string, object> a) { return Kinds(); }, requiresMainThread: false);
        }

        private static Dictionary<string, object> Kinds()
        {
            return new Dictionary<string, object>
            {
                { "kinds", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "kind", "room" },
                            { "parameters", "size[x,y,z], thickness, floor, ceiling, walls[north|south|east|west], " +
                                            "doors[{wall,width,height,offset}], material" },
                            { "example", "{ \"kind\":\"room\", \"path\":\"World/Hall\", \"size\":[10,3,8], " +
                                         "\"doors\":[{\"wall\":\"north\",\"width\":1.6,\"height\":2.2}] }" }
                        },
                        new Dictionary<string, object>
                        {
                            { "kind", "corridor" },
                            { "parameters", "from[x,y,z], to[x,y,z], width, height, thickness, ceiling, material" },
                            { "example", "{ \"kind\":\"corridor\", \"path\":\"World/Hall2\", \"from\":[0,0,0], \"to\":[0,0,20], \"width\":2, \"height\":3 }" }
                        },
                        new Dictionary<string, object>
                        {
                            { "kind", "stairs" },
                            { "parameters", "steps, stepHeight, stepDepth, width, material" },
                            { "example", "{ \"kind\":\"stairs\", \"path\":\"World/Stairs\", \"steps\":12, \"stepHeight\":0.2, \"stepDepth\":0.3, \"width\":1.6 }" }
                        }
                    }
                },
                { "note", "Built from primitive cubes, so colliders and UVs come for free. A doorway is cut by " +
                          "splitting the wall into side and lintel segments rather than by boolean geometry." }
            };
        }

        // ------------------------------------------------------------------ entry

        private static Dictionary<string, object> Generate(Dictionary<string, object> a)
        {
            string kind = (Json.GetString(a, "kind") ?? "").ToLowerInvariant();
            string path = Json.GetString(a, "path");
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("geo.generate needs \"path\"");

            var parent = EnsureParent(path);

            // Regenerating replaces what was there, so repeated calls converge instead of stacking geometry.
            int removed = parent.transform.childCount;
            for (int i = parent.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(parent.transform.GetChild(i).gameObject);

            Material material = null;
            string materialPath = Json.GetString(a, "material");
            if (!string.IsNullOrEmpty(materialPath))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(UdonReflection.NormalizePath(materialPath));
                if (material == null) throw new ArgumentException("No material at \"" + materialPath + "\"");
            }

            var pieces = new List<object>();
            switch (kind)
            {
                case "room": BuildRoom(a, parent, material, pieces); break;
                case "corridor": BuildCorridor(a, parent, material, pieces); break;
                case "stairs": BuildStairs(a, parent, material, pieces); break;
                default: throw new ArgumentException("kind must be room, corridor or stairs (got \"" + kind + "\"). Call geo.kinds.");
            }

            EditorSceneManager.MarkSceneDirty(parent.scene);
            if (Json.GetBool(a, "saveScene", true)) EditorSceneManager.SaveScene(parent.scene);

            // Each cube is 12 triangles; worth stating so a budget decision can be made immediately.
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "path", UdonOps.HierarchyPath(parent.transform) },
                { "childrenReplaced", removed },
                { "pieceCount", pieces.Count },
                { "approximateTriangles", pieces.Count * 12 },
                { "pieces", pieces }
            };
        }

        private static GameObject EnsureParent(string path)
        {
            string[] segments = path.Split('/');
            Transform parent = null;
            string walked = null;

            for (int i = 0; i < segments.Length; i++)
            {
                walked = walked == null ? segments[i] : walked + "/" + segments[i];
                GameObject node;
                try { node = UdonReflection.FindGameObject(walked); }
                catch (ArgumentException)
                {
                    node = new GameObject(segments[i]);
                    node.transform.SetParent(parent, false);
                }
                parent = node.transform;
            }
            return parent.gameObject;
        }

        // ------------------------------------------------------------------ room

        private static void BuildRoom(Dictionary<string, object> a, GameObject parent, Material material, List<object> pieces)
        {
            Vector3 size = ReadVector(a, "size", new Vector3(8f, 3f, 8f));
            float thickness = ReadFloat(a, "thickness", 0.2f);
            bool floor = Json.GetBool(a, "floor", true);
            bool ceiling = Json.GetBool(a, "ceiling", false);

            var walls = Json.GetStringList(a, "walls");
            if (walls.Count == 0) walls.AddRange(new[] { "north", "south", "east", "west" });

            var doors = new List<Dictionary<string, object>>();
            object rawDoors;
            if (a.TryGetValue("doors", out rawDoors))
            {
                var list = rawDoors as List<object>;
                if (list != null)
                {
                    foreach (object entry in list)
                    {
                        var door = entry as Dictionary<string, object>;
                        if (door != null) doors.Add(door);
                    }
                }
            }

            // `size` is the *interior* of the room, so the floor and ceiling are extended to pass underneath
            // the walls. Sizing them to the interior instead leaves every wall standing just beyond the
            // floor's edge with nothing below it — geometry that looks right in the inspector and drops a
            // player through the seam.
            float outerX = size.x + thickness * 2f;
            float outerZ = size.z + thickness * 2f;

            if (floor)
                AddBox(parent, material, pieces, "Floor",
                    new Vector3(0f, -thickness * 0.5f, 0f), new Vector3(outerX, thickness, outerZ));
            if (ceiling)
                AddBox(parent, material, pieces, "Ceiling",
                    new Vector3(0f, size.y + thickness * 0.5f, 0f), new Vector3(outerX, thickness, outerZ));

            foreach (string wall in walls)
            {
                string name = wall.Trim().ToLowerInvariant();
                bool northSouth = name == "north" || name == "south";
                float sign = (name == "north" || name == "east") ? 1f : -1f;

                // North/south walls span the full exterior width so the corners close; east/west walls span
                // only the interior depth and butt into them.
                float length = northSouth ? outerX : size.z;
                float offsetAxis = northSouth ? size.z : size.x;

                Vector3 centre = northSouth
                    ? new Vector3(0f, size.y * 0.5f, sign * (offsetAxis * 0.5f + thickness * 0.5f))
                    : new Vector3(sign * (offsetAxis * 0.5f + thickness * 0.5f), size.y * 0.5f, 0f);

                var wallDoors = new List<Dictionary<string, object>>();
                foreach (var door in doors)
                {
                    if (string.Equals(Json.GetString(door, "wall"), name, StringComparison.OrdinalIgnoreCase))
                        wallDoors.Add(door);
                }

                if (wallDoors.Count == 0)
                {
                    Vector3 scale = northSouth
                        ? new Vector3(length, size.y, thickness)
                        : new Vector3(thickness, size.y, length);
                    AddBox(parent, material, pieces, Capitalise(name) + "Wall", centre, scale);
                    continue;
                }

                BuildWallWithDoors(parent, material, pieces, name, northSouth, centre, length, size.y, thickness, wallDoors);
            }
        }

        /// <summary>
        /// Splits a wall into the segments left and right of each doorway, plus a lintel above it.
        ///
        /// Doorways are sorted and clamped so overlapping or out-of-range specs cannot produce inverted
        /// segments — a negative-width box is invisible in the inspector and leaves a hole a player falls
        /// through, which is exactly the class of bug this function exists to prevent.
        /// </summary>
        private static void BuildWallWithDoors(GameObject parent, Material material, List<object> pieces,
            string wallName, bool northSouth, Vector3 centre, float length, float height, float thickness,
            List<Dictionary<string, object>> doors)
        {
            var openings = new List<float[]>(); // {start, end, height}
            foreach (var door in doors)
            {
                float width = Mathf.Max(0.1f, ReadFloat(door, "width", 1.6f));
                float doorHeight = Mathf.Clamp(ReadFloat(door, "height", 2.2f), 0.1f, height);
                float offset = ReadFloat(door, "offset", 0f);

                float start = Mathf.Clamp(offset - width * 0.5f, -length * 0.5f, length * 0.5f);
                float end = Mathf.Clamp(offset + width * 0.5f, -length * 0.5f, length * 0.5f);
                if (end - start < 0.05f) continue;
                openings.Add(new[] { start, end, doorHeight });
            }
            openings.Sort(delegate (float[] x, float[] y) { return x[0].CompareTo(y[0]); });

            float cursor = -length * 0.5f;
            int index = 0;

            foreach (var opening in openings)
            {
                float segmentWidth = opening[0] - cursor;
                if (segmentWidth > 0.01f)
                {
                    float segmentCentre = cursor + segmentWidth * 0.5f;
                    AddSegment(parent, material, pieces, wallName + "Wall_" + index++, northSouth, centre,
                        segmentCentre, segmentWidth, height, thickness, height * 0.5f);
                }

                // Lintel above the opening.
                float lintelHeight = height - opening[2];
                if (lintelHeight > 0.01f)
                {
                    float doorWidth = opening[1] - opening[0];
                    float doorCentre = opening[0] + doorWidth * 0.5f;
                    AddSegment(parent, material, pieces, wallName + "Lintel_" + index++, northSouth, centre,
                        doorCentre, doorWidth, lintelHeight, thickness, opening[2] + lintelHeight * 0.5f);
                }

                cursor = Mathf.Max(cursor, opening[1]);
            }

            float tailWidth = (length * 0.5f) - cursor;
            if (tailWidth > 0.01f)
            {
                float segmentCentre = cursor + tailWidth * 0.5f;
                AddSegment(parent, material, pieces, wallName + "Wall_" + index, northSouth, centre,
                    segmentCentre, tailWidth, height, thickness, height * 0.5f);
            }
        }

        private static void AddSegment(GameObject parent, Material material, List<object> pieces, string name,
            bool northSouth, Vector3 wallCentre, float alongOffset, float alongWidth, float segmentHeight,
            float thickness, float verticalCentre)
        {
            Vector3 position = northSouth
                ? new Vector3(alongOffset, verticalCentre, wallCentre.z)
                : new Vector3(wallCentre.x, verticalCentre, alongOffset);
            Vector3 scale = northSouth
                ? new Vector3(alongWidth, segmentHeight, thickness)
                : new Vector3(thickness, segmentHeight, alongWidth);
            AddBox(parent, material, pieces, name, position, scale);
        }

        // ------------------------------------------------------------------ corridor

        private static void BuildCorridor(Dictionary<string, object> a, GameObject parent, Material material, List<object> pieces)
        {
            Vector3 from = ReadVector(a, "from", Vector3.zero);
            Vector3 to = ReadVector(a, "to", new Vector3(0f, 0f, 10f));
            float width = ReadFloat(a, "width", 2f);
            float height = ReadFloat(a, "height", 3f);
            float thickness = ReadFloat(a, "thickness", 0.2f);
            bool ceiling = Json.GetBool(a, "ceiling", true);

            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.01f) throw new ArgumentException("corridor \"from\" and \"to\" are the same point");

            // Built along +Z then rotated, so the arithmetic stays one-dimensional.
            var pivot = new GameObject("Corridor");
            pivot.transform.SetParent(parent.transform, false);
            pivot.transform.localPosition = from - parent.transform.position;
            pivot.transform.localRotation = Quaternion.LookRotation(delta.normalized, Vector3.up);

            AddBox(pivot, material, pieces, "Floor",
                new Vector3(0f, -thickness * 0.5f, length * 0.5f), new Vector3(width, thickness, length));
            if (ceiling)
                AddBox(pivot, material, pieces, "Ceiling",
                    new Vector3(0f, height + thickness * 0.5f, length * 0.5f), new Vector3(width, thickness, length));

            AddBox(pivot, material, pieces, "WallLeft",
                new Vector3(-(width * 0.5f + thickness * 0.5f), height * 0.5f, length * 0.5f),
                new Vector3(thickness, height, length));
            AddBox(pivot, material, pieces, "WallRight",
                new Vector3(width * 0.5f + thickness * 0.5f, height * 0.5f, length * 0.5f),
                new Vector3(thickness, height, length));
        }

        // ------------------------------------------------------------------ stairs

        private static void BuildStairs(Dictionary<string, object> a, GameObject parent, Material material, List<object> pieces)
        {
            int steps = Mathf.Clamp(Json.GetInt(a, "steps", 10), 1, 400);
            float stepHeight = ReadFloat(a, "stepHeight", 0.2f);
            float stepDepth = ReadFloat(a, "stepDepth", 0.3f);
            float width = ReadFloat(a, "width", 1.6f);

            for (int i = 0; i < steps; i++)
            {
                // Each tread is a full-height riser from the ground, so there is no gap under the stairs for
                // a player to fall into.
                float height = stepHeight * (i + 1);
                AddBox(parent, material, pieces, "Step_" + i,
                    new Vector3(0f, height * 0.5f, stepDepth * (i + 0.5f)),
                    new Vector3(width, height, stepDepth));
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void AddBox(GameObject parent, Material material, List<object> pieces,
            string name, Vector3 localPosition, Vector3 localScale)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = localPosition;
            box.transform.localScale = localScale;

            if (material != null)
            {
                var renderer = box.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;
            }

            pieces.Add(new Dictionary<string, object>
            {
                { "name", name },
                { "localPosition", localPosition },
                { "localScale", localScale }
            });
        }

        private static Vector3 ReadVector(Dictionary<string, object> a, string key, Vector3 fallback)
        {
            object value;
            if (a == null || !a.TryGetValue(key, out value) || value == null) return fallback;
            return (Vector3)UdonValues.Convert(value, typeof(Vector3), key);
        }

        private static float ReadFloat(Dictionary<string, object> a, string key, float fallback)
        {
            object value;
            if (a == null || !a.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        private static string Capitalise(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }
    }
}
