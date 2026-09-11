using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Declarative scene-graph apply.
    ///
    /// Generic Unity tooling exposes one-GameObject-at-a-time operations, which for a world of any size
    /// means hundreds of round trips and an enormous amount of agent context spent restating the same
    /// hierarchy. A spec is applied in a single call and is idempotent - objects are keyed by hierarchy
    /// path, so re-applying converges rather than duplicating.
    ///
    /// It also checks the component whitelist. VRChat strips any component not on
    /// WorldValidation.ComponentTypeWhiteList{Common,Sdk3} at build time, silently, so a scene can look
    /// right in the editor and lose behaviour in the uploaded world.
    /// </summary>
    public static class SceneSpec
    {
        private static string[] _whitelistCache;

        public static void Register()
        {
            BridgeRouter.Register("scene.apply_spec",
                "Create or update GameObjects and components from a declarative spec. Idempotent. " +
                "Args: objects[], delete[], dryRun, saveScene.",
                delegate (Dictionary<string, object> a) { return Apply(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("scene.component_whitelist",
                "VRChat's allowed component types. Anything else is stripped from the world at build. " +
                "Args: filter.",
                delegate (Dictionary<string, object> a) { return Whitelist(Json.GetString(a, "filter")); });

            BridgeRouter.Register("scene.describe",
                "Hierarchy of the loaded scenes with components. Args: root, maxDepth, includeInactive.",
                delegate (Dictionary<string, object> a) { return Describe(a); });
        }

        // ------------------------------------------------------------------ apply

        private static Dictionary<string, object> Apply(Dictionary<string, object> a)
        {
            object rawObjects;
            a.TryGetValue("objects", out rawObjects);
            var objects = rawObjects as List<object> ?? new List<object>();
            var toDelete = Json.GetStringList(a, "delete");
            bool dryRun = Json.GetBool(a, "dryRun", false);

            var created = new List<string>();
            var updated = new List<string>();
            var deleted = new List<string>();
            var componentsAdded = new List<object>();
            var warnings = new List<string>();

            Scene? touched = null;

            foreach (object entry in objects)
            {
                var spec = entry as Dictionary<string, object>;
                if (spec == null) continue;

                string path = Json.GetString(spec, "path");
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("Every object needs a \"path\"");

                if (dryRun)
                {
                    if (TryFind(path) == null) created.Add(path); else updated.Add(path);
                    continue;
                }

                bool wasCreated;
                var go = EnsureGameObject(path, spec, out wasCreated);
                if (wasCreated) created.Add(path); else updated.Add(path);
                touched = go.scene;

                ApplyTransform(go, spec);
                ApplyObjectSettings(go, spec, warnings);
                ApplyComponents(go, spec, componentsAdded, warnings);
                EditorUtility.SetDirty(go);
            }

            foreach (string path in toDelete)
            {
                var go = TryFind(path);
                if (go == null) continue;
                deleted.Add(path);
                if (dryRun) continue;
                touched = go.scene;
                UnityEngine.Object.DestroyImmediate(go);
            }

            if (!dryRun && touched.HasValue)
            {
                EditorSceneManager.MarkSceneDirty(touched.Value);
                if (Json.GetBool(a, "saveScene", true)) EditorSceneManager.SaveScene(touched.Value);
            }

            return new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "created", created },
                { "updated", updated },
                { "deleted", deleted },
                { "componentsAdded", componentsAdded },
                { "warnings", warnings }
            };
        }

        /// <summary>Finds or creates the object at <paramref name="path"/>, creating missing ancestors.</summary>
        private static GameObject EnsureGameObject(string path, Dictionary<string, object> spec, out bool created)
        {
            created = false;
            var existing = TryFind(path);
            if (existing != null) return existing;

            string[] segments = path.Split('/');
            Transform parent = null;
            string walked = null;

            for (int i = 0; i < segments.Length; i++)
            {
                walked = walked == null ? segments[i] : walked + "/" + segments[i];
                var node = TryFind(walked);
                if (node == null)
                {
                    bool isLeaf = i == segments.Length - 1;
                    node = isLeaf ? CreateLeaf(spec, segments[i]) : new GameObject(segments[i]);
                    node.transform.SetParent(parent, false);
                    if (isLeaf) created = true;
                }
                parent = node.transform;
            }

            return parent.gameObject;
        }

        /// <summary>Primitive and prefab only apply at creation time; an existing object is never replaced.</summary>
        private static GameObject CreateLeaf(Dictionary<string, object> spec, string name)
        {
            string prefab = Json.GetString(spec, "prefab");
            if (!string.IsNullOrEmpty(prefab))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(UdonReflection.NormalizePath(prefab));
                if (asset == null) throw new ArgumentException("No prefab at \"" + prefab + "\"");
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                instance.name = name;
                return instance;
            }

            string primitive = Json.GetString(spec, "primitive");
            if (!string.IsNullOrEmpty(primitive))
            {
                PrimitiveType kind;
                try { kind = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitive, true); }
                catch (Exception)
                {
                    throw new ArgumentException("Unknown primitive \"" + primitive +
                        "\". Valid: Cube, Sphere, Capsule, Cylinder, Plane, Quad.");
                }
                var go = GameObject.CreatePrimitive(kind);
                go.name = name;
                return go;
            }

            return new GameObject(name);
        }

        private static GameObject TryFind(string path)
        {
            try { return UdonReflection.FindGameObject(path); }
            catch (ArgumentException) { return null; }
        }

        private static void ApplyTransform(GameObject go, Dictionary<string, object> spec)
        {
            object value;
            if (spec.TryGetValue("position", out value) && value != null)
                go.transform.localPosition = ToVector3(value);
            if (spec.TryGetValue("rotation", out value) && value != null)
                go.transform.localRotation = ToRotation(value);
            if (spec.TryGetValue("scale", out value) && value != null)
                go.transform.localScale = ToVector3(value);
        }

        private static void ApplyObjectSettings(GameObject go, Dictionary<string, object> spec, List<string> warnings)
        {
            object value;
            if (spec.TryGetValue("active", out value) && value is bool) go.SetActive((bool)value);

            if (spec.TryGetValue("layer", out value) && value != null)
            {
                int layer = value is string ? LayerMask.NameToLayer((string)value) : Convert.ToInt32(value);
                if (layer < 0) warnings.Add("Unknown layer \"" + value + "\" on " + UdonOps.HierarchyPath(go.transform));
                else go.layer = layer;
            }

            string tag = Json.GetString(spec, "tag");
            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; }
                catch (Exception) { warnings.Add("Tag \"" + tag + "\" is not defined in this project."); }
            }
        }

        private static void ApplyComponents(GameObject go, Dictionary<string, object> spec, List<object> added, List<string> warnings)
        {
            object raw;
            if (!spec.TryGetValue("components", out raw)) return;
            var components = raw as List<object>;
            if (components == null) return;

            foreach (object entry in components)
            {
                var componentSpec = entry as Dictionary<string, object>;
                if (componentSpec == null) continue;

                string typeName = Json.GetString(componentSpec, "type");
                if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("Every component needs a \"type\"");

                var type = Reflect.ResolveType(typeName);

                // An UdonSharpBehaviour added with plain AddComponent has no backing UdonBehaviour and no
                // program asset, so it does nothing at runtime and cannot be repaired by setting fields.
                if (IsUdonSharpBehaviour(type))
                    throw new ArgumentException(
                        typeName + " is an UdonSharpBehaviour. Use udon.attach instead - a plain AddComponent " +
                        "skips the backing UdonBehaviour setup and produces a component that never runs.");

                if (!IsWhitelisted(type))
                    warnings.Add(type.FullName + " on " + UdonOps.HierarchyPath(go.transform) +
                                 " is not on VRChat's component whitelist and will be stripped from the built world.");

                var component = go.GetComponent(type);
                bool isNew = component == null;
                if (isNew)
                {
                    component = go.AddComponent(type);
                    added.Add(new Dictionary<string, object>
                    {
                        { "gameObject", UdonOps.HierarchyPath(go.transform) },
                        { "type", type.FullName }
                    });
                }
                if (component == null)
                    throw new ArgumentException(
                        "Unity refused to add " + typeName + " to " + UdonOps.HierarchyPath(go.transform) +
                        ". The usual cause is an unsatisfied [RequireComponent] dependency (AudioLowPassFilter " +
                        "needs an AudioSource, for instance) - add the prerequisite component first.");

                object fieldsRaw;
                if (componentSpec.TryGetValue("fields", out fieldsRaw))
                {
                    ApplyComponentFields(component, Json.AsObject(fieldsRaw), warnings);
                    EditorUtility.SetDirty(component);
                }
            }
        }

        private static void ApplyComponentFields(Component component, Dictionary<string, object> fields, List<string> warnings)
        {
            var type = component.GetType();
            foreach (var pair in fields)
            {
                var field = type.GetField(pair.Key, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(component, UdonValues.Convert(pair.Value, field.FieldType, pair.Key));
                    continue;
                }

                var property = type.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(component, UdonValues.Convert(pair.Value, property.PropertyType, pair.Key), null);
                    continue;
                }

                // Private fields are writable only when Unity itself serialises them.
                //
                // Several SDK components keep their wiring in private [SerializeField] fields — VRCUnityVideoPlayer
                // holds its UnityEngine.Video.VideoPlayer and its target audio sources that way — so refusing to
                // touch them means a video screen cannot be assembled from a spec at all. The [SerializeField]
                // requirement is the line: it is state the component intends to be set in the inspector, which is
                // exactly what this method is standing in for. Anything else private stays off limits.
                var serialized = type.GetField(pair.Key, BindingFlags.NonPublic | BindingFlags.Instance);
                if (serialized != null && Attribute.IsDefined(serialized, typeof(SerializeField)))
                {
                    serialized.SetValue(component, UdonValues.Convert(pair.Value, serialized.FieldType, pair.Key));
                    continue;
                }

                warnings.Add("\"" + pair.Key + "\" is not a writable member of " + type.Name +
                             (serialized != null ? " (it is private and not [SerializeField])" : "") + "; skipped.");
            }
        }

        private static bool IsUdonSharpBehaviour(Type type)
        {
            try { return UdonReflection.Resolve("UdonSharp.UdonSharpBehaviour").IsAssignableFrom(type); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ whitelist

        private static string[] LoadWhitelist()
        {
            if (_whitelistCache != null) return _whitelistCache;

            var names = new List<string>();
            var validation = Reflect.ResolveType("VRC.SDKBase.Validation.WorldValidation");
            foreach (string fieldName in new[] { "ComponentTypeWhiteListCommon", "ComponentTypeWhiteListSdk3" })
            {
                var field = validation.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var value = field == null ? null : field.GetValue(null) as string[];
                if (value != null) names.AddRange(value);
            }
            _whitelistCache = names.ToArray();
            return _whitelistCache;
        }

        private static bool IsWhitelisted(Type type)
        {
            var whitelist = LoadWhitelist();
            if (whitelist.Length == 0) return true; // cannot check; do not cry wolf

            for (var current = type; current != null; current = current.BaseType)
            {
                for (int i = 0; i < whitelist.Length; i++)
                {
                    if (string.Equals(whitelist[i], current.FullName, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static Dictionary<string, object> Whitelist(string filter)
        {
            var all = LoadWhitelist();
            var matched = new List<object>();
            foreach (string name in all)
            {
                if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched.Add(name);
            }
            return new Dictionary<string, object>
            {
                { "total", all.Length },
                { "matched", matched.Count },
                { "types", matched }
            };
        }

        // ------------------------------------------------------------------ describe

        private static Dictionary<string, object> Describe(Dictionary<string, object> a)
        {
            string rootPath = Json.GetString(a, "root");
            int maxDepth = Json.GetInt(a, "maxDepth", 4);
            bool includeInactive = Json.GetBool(a, "includeInactive", true);

            var nodes = new List<object>();
            if (!string.IsNullOrEmpty(rootPath))
            {
                nodes.Add(DescribeNode(UdonReflection.FindGameObject(rootPath).transform, maxDepth, includeInactive));
            }
            else
            {
                for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                {
                    var scene = EditorSceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (!includeInactive && !root.activeInHierarchy) continue;
                        nodes.Add(DescribeNode(root.transform, maxDepth, includeInactive));
                    }
                }
            }
            return new Dictionary<string, object> { { "nodes", nodes } };
        }

        private static Dictionary<string, object> DescribeNode(Transform tr, int depthLeft, bool includeInactive)
        {
            var componentNames = new List<object>();
            foreach (var c in tr.GetComponents<Component>())
            {
                if (c == null) { componentNames.Add("<missing script>"); continue; }
                if (c is Transform) continue;
                componentNames.Add(c.GetType().FullName);
            }

            var node = new Dictionary<string, object>
            {
                { "name", tr.name },
                { "path", UdonOps.HierarchyPath(tr) },
                { "active", tr.gameObject.activeSelf },
                { "layer", LayerMask.LayerToName(tr.gameObject.layer) },
                { "position", tr.localPosition },
                { "components", componentNames },
                { "childCount", tr.childCount }
            };

            if (depthLeft > 0 && tr.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < tr.childCount; i++)
                {
                    var child = tr.GetChild(i);
                    if (!includeInactive && !child.gameObject.activeInHierarchy) continue;
                    children.Add(DescribeNode(child, depthLeft - 1, includeInactive));
                }
                node["children"] = children;
            }
            return node;
        }

        // ------------------------------------------------------------------ conversions

        private static Vector3 ToVector3(object value)
        {
            return (Vector3)UdonValues.Convert(value, typeof(Vector3), "vector");
        }

        private static Quaternion ToRotation(object value)
        {
            // Three components read as Euler angles, which is what an agent almost always means.
            var list = value as List<object>;
            if (list != null && list.Count == 3)
            {
                return Quaternion.Euler(
                    Convert.ToSingle(list[0], CultureInfo.InvariantCulture),
                    Convert.ToSingle(list[1], CultureInfo.InvariantCulture),
                    Convert.ToSingle(list[2], CultureInfo.InvariantCulture));
            }

            var obj = value as Dictionary<string, object>;
            if (obj != null && !obj.ContainsKey("w")) return Quaternion.Euler(ToVector3(value));

            return (Quaternion)UdonValues.Convert(value, typeof(Quaternion), "rotation");
        }
    }
}
