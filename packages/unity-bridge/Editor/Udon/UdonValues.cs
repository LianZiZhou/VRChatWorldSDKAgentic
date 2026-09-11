using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Converts JSON values into the field types an UdonSharpBehaviour actually declares.
    ///
    /// Object references are the interesting case: an agent cannot send a C# reference over JSON, so they
    /// are named explicitly:
    ///   { "$gameObject": "VRCWorld/Door" }                      -> GameObject in a loaded scene
    ///   { "$component": { "path": "VRCWorld/Door", "type": "Animator" } }
    ///   { "$asset": "Assets/Materials/Glass.mat" }              -> any asset by project path
    ///   { "$null": true }                                        -> explicit null
    /// Vectors and colours accept either an object ({x,y,z}) or an array ([x,y,z]).
    /// </summary>
    public static class UdonValues
    {
        public static object Convert(object json, Type target, string fieldName)
        {
            try { return ConvertCore(json, target); }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "Cannot convert the value for field \"" + fieldName + "\" to " + target.FullName + ": " + ex.Message);
            }
        }

        private static object ConvertCore(object json, Type target)
        {
            if (json == null) return DefaultOf(target);

            var obj = json as Dictionary<string, object>;
            if (obj != null)
            {
                object marker;
                if (obj.TryGetValue("$null", out marker)) return DefaultOf(target);
                if (obj.TryGetValue("$gameObject", out marker)) return ResolveGameObject(marker, target);
                if (obj.TryGetValue("$component", out marker)) return ResolveComponent(marker, target);
                if (obj.TryGetValue("$asset", out marker)) return ResolveAsset(marker, target);
            }

            if (target == typeof(string)) return json as string ?? System.Convert.ToString(json, CultureInfo.InvariantCulture);
            if (target == typeof(bool)) return ToBool(json);
            if (target.IsEnum)
            {
                if (json is string) return Enum.Parse(target, (string)json, true);
                return Enum.ToObject(target, System.Convert.ToInt64(json, CultureInfo.InvariantCulture));
            }
            if (target.IsPrimitive || target == typeof(decimal))
                return System.Convert.ChangeType(json, target, CultureInfo.InvariantCulture);

            if (target == typeof(Vector2)) { var c = Components(json, 2); return new Vector2(c[0], c[1]); }
            if (target == typeof(Vector3)) { var c = Components(json, 3); return new Vector3(c[0], c[1], c[2]); }
            if (target == typeof(Vector4)) { var c = Components(json, 4); return new Vector4(c[0], c[1], c[2], c[3]); }
            if (target == typeof(Quaternion)) { var c = Components(json, 4); return new Quaternion(c[0], c[1], c[2], c[3]); }
            if (target == typeof(Color)) { var c = Components(json, 4); return new Color(c[0], c[1], c[2], c[3]); }
            if (target == typeof(Color32)) { var c = Components(json, 4); return (Color32)new Color(c[0], c[1], c[2], c[3]); }

            if (target.IsArray)
            {
                var list = json as List<object>;
                if (list == null) throw new ArgumentException("expected an array");
                var elementType = target.GetElementType();
                var array = Array.CreateInstance(elementType, list.Count);
                for (int i = 0; i < list.Count; i++) array.SetValue(ConvertCore(list[i], elementType), i);
                return array;
            }

            // A bare string targeting a UnityEngine.Object is treated as a scene path or asset path,
            // which is what an agent will reach for first.
            if (typeof(UnityEngine.Object).IsAssignableFrom(target) && json is string)
            {
                string s = (string)json;
                return s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? ResolveAsset(s, target)
                    : (target == typeof(GameObject) ? ResolveGameObject(s, target) : ResolveComponentByPath(s, target));
            }

            throw new ArgumentException("no conversion defined for this JSON shape");
        }

        private static object DefaultOf(Type target)
        {
            return target.IsValueType ? Activator.CreateInstance(target) : null;
        }

        private static bool ToBool(object json)
        {
            if (json is bool) return (bool)json;
            if (json is double) return (double)json != 0;
            return System.Convert.ToBoolean(json, CultureInfo.InvariantCulture);
        }

        private static float[] Components(object json, int count)
        {
            var result = new float[count];
            // Colours default alpha to 1 so { "r":1,"g":0,"b":0 } behaves as expected.
            if (count == 4) result[3] = 1f;

            var list = json as List<object>;
            if (list != null)
            {
                for (int i = 0; i < count && i < list.Count; i++)
                    result[i] = System.Convert.ToSingle(list[i], CultureInfo.InvariantCulture);
                return result;
            }

            var obj = json as Dictionary<string, object>;
            if (obj == null) throw new ArgumentException("expected an object or array of components");

            string[] xyzw = { "x", "y", "z", "w" };
            string[] rgba = { "r", "g", "b", "a" };
            for (int i = 0; i < count; i++)
            {
                object value;
                if (obj.TryGetValue(xyzw[i], out value) || obj.TryGetValue(rgba[i], out value))
                    result[i] = System.Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static object ResolveGameObject(object marker, Type target)
        {
            var go = UdonReflection.FindGameObject(System.Convert.ToString(marker, CultureInfo.InvariantCulture));
            if (target == typeof(GameObject) || target == typeof(UnityEngine.Object)) return go;
            if (target == typeof(Transform)) return go.transform;
            if (typeof(Component).IsAssignableFrom(target))
            {
                var component = go.GetComponent(target);
                if (component == null)
                    throw new ArgumentException("GameObject \"" + go.name + "\" has no " + target.Name + " component");
                return component;
            }
            throw new ArgumentException("$gameObject cannot be assigned to " + target.FullName);
        }

        private static object ResolveComponent(object marker, Type target)
        {
            var spec = marker as Dictionary<string, object>;
            if (spec == null) throw new ArgumentException("$component needs { path, type }");
            string path = Json.GetString(spec, "path");
            string typeName = Json.GetString(spec, "type");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(typeName))
                throw new ArgumentException("$component needs both \"path\" and \"type\"");

            var go = UdonReflection.FindGameObject(path);
            var componentType = Reflect.ResolveType(typeName);
            var component = go.GetComponent(componentType);
            if (component == null)
                throw new ArgumentException("GameObject \"" + path + "\" has no " + typeName + " component");
            if (!target.IsInstanceOfType(component))
                throw new ArgumentException(typeName + " is not assignable to " + target.FullName);
            return component;
        }

        private static object ResolveComponentByPath(string path, Type target)
        {
            var go = UdonReflection.FindGameObject(path);
            var component = go.GetComponent(target);
            if (component == null)
                throw new ArgumentException("GameObject \"" + path + "\" has no " + target.Name + " component");
            return component;
        }

        private static object ResolveAsset(object marker, Type target)
        {
            string path = UdonReflection.NormalizePath(System.Convert.ToString(marker, CultureInfo.InvariantCulture));
            var asset = AssetDatabase.LoadAssetAtPath(path, target);
            if (asset == null) asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) throw new ArgumentException("No asset at \"" + path + "\"");
            if (!target.IsInstanceOfType(asset))
                throw new ArgumentException("Asset at \"" + path + "\" is " + asset.GetType().Name + ", not " + target.Name);
            return asset;
        }
    }
}
