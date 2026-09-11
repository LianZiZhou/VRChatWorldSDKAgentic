using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Self-contained JSON writer/reader.
    ///
    /// Newtonsoft is in fact reachable here — com.unity.nuget.newtonsoft-json arrives as a transitive
    /// dependency of the VRChat packages — but it is deliberately not used. It is transitive, so it can
    /// vanish on an SDK dependency change, and the bridge must keep working in any project that installs
    /// the package. The reflective writer also needs behaviour a general serializer does not give for
    /// free: hard depth caps, cycle breaking, and swallowing exceptions from Unity property getters, all
    /// of which matter when serialising arbitrary editor objects.
    /// </summary>
    public static class Json
    {
        public const int DefaultMaxDepth = 6;
        private const int MaxEnumerableItems = 2000;

        // ---------- writing ----------

        public static string Write(object value, int maxDepth = DefaultMaxDepth)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, maxDepth, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int depth, HashSet<object> seen)
        {
            if (v == null) { sb.Append("null"); return; }

            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is char) { WriteString(sb, v.ToString()); return; }
            if (v is float) { WriteNumber(sb, (float)v); return; }
            if (v is double) { WriteNumber(sb, (double)v); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }

            var t = v.GetType();
            if (t.IsPrimitive) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }

            if (WriteUnityValueType(sb, v, depth, seen)) return;

            // UnityEngine.Object is a graph with cycles and throwing getters; never recurse into one.
            var uo = v as UnityEngine.Object;
            if (uo != null)
            {
                sb.Append('{');
                sb.Append("\"__unity\":"); WriteString(sb, t.FullName);
                try
                {
                    bool alive = uo;
                    sb.Append(",\"name\":"); WriteString(sb, alive ? uo.name : "<destroyed>");
                    sb.Append(",\"instanceId\":").Append(alive ? uo.GetInstanceID() : 0);
                    var go = v as GameObject;
                    var comp = v as Component;
                    if (alive && go != null) { sb.Append(",\"path\":"); WriteString(sb, HierarchyPath(go.transform)); }
                    else if (alive && comp != null) { sb.Append(",\"path\":"); WriteString(sb, HierarchyPath(comp.transform)); }
                }
                catch (Exception ex)
                {
                    // Reading even `name` off Unity's main thread throws. The router serialises
                    // main-thread results on the main thread precisely to avoid this; this guard keeps a
                    // stray off-thread serialisation from failing an otherwise good response.
                    sb.Append(",\"error\":"); WriteString(sb, ex.GetType().Name + ": " + ex.Message);
                }
                sb.Append('}');
                return;
            }

            var dict = v as IDictionary;
            if (dict != null)
            {
                if (depth <= 0) { WriteString(sb, "<max-depth>"); return; }
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, kv.Value, depth - 1, seen);
                }
                sb.Append('}');
                return;
            }

            var en = v as IEnumerable;
            if (en != null)
            {
                if (depth <= 0) { WriteString(sb, "<max-depth>"); return; }
                sb.Append('[');
                bool first = true;
                int n = 0;
                foreach (var item in en)
                {
                    if (n++ >= MaxEnumerableItems)
                    {
                        if (!first) sb.Append(',');
                        WriteString(sb, "<truncated>");
                        break;
                    }
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth - 1, seen);
                }
                sb.Append(']');
                return;
            }

            if (depth <= 0) { WriteString(sb, v.ToString()); return; }
            if (!t.IsValueType && !seen.Add(v)) { WriteString(sb, "<cycle>"); return; }

            sb.Append('{');
            sb.Append("\"__type\":"); WriteString(sb, t.FullName);

            // A type that keeps its content private, but says what it is when asked.
            //
            // VRCUrl is the case that matters: it has no public members at all, so reflection alone emits
            // {"__type":"VRC.SDKBase.VRCUrl"} and an agent reading back a video URL it just set learns
            // nothing. An overridden ToString is the type's own answer to "what are you", so it is reported
            // as "toString" — a type that has not overridden it is left alone, since Type.FullName twice is
            // noise.
            if (HasCustomToString(t))
            {
                string described = null;
                try { described = v.ToString(); } catch (Exception) { }
                if (!string.IsNullOrEmpty(described))
                {
                    sb.Append(",\"toString\":");
                    WriteString(sb, described);
                }
            }

            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                var fi = fields[i];
                sb.Append(',');
                WriteString(sb, fi.Name);
                sb.Append(':');
                WriteMember(sb, fi, null, v, depth - 1, seen);
            }
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < props.Length; i++)
            {
                var pi = props[i];
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                sb.Append(',');
                WriteString(sb, pi.Name);
                sb.Append(':');
                WriteMember(sb, null, pi, v, depth - 1, seen);
            }
            sb.Append('}');
            if (!t.IsValueType) seen.Remove(v);
        }


        /// <summary>Whether this type overrides ToString(), rather than inheriting object's type-name one.</summary>
        private static bool HasCustomToString(Type t)
        {
            try
            {
                var method = t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method != null && method.DeclaringType != typeof(object) && method.DeclaringType != typeof(ValueType);
            }
            catch (Exception) { return false; }
        }

        private static bool WriteUnityValueType(StringBuilder sb, object v, int depth, HashSet<object> seen)
        {
            if (v is Vector2)
            {
                var a = (Vector2)v;
                sb.Append("{\"x\":"); WriteNumber(sb, a.x);
                sb.Append(",\"y\":"); WriteNumber(sb, a.y); sb.Append('}');
                return true;
            }
            if (v is Vector3)
            {
                var a = (Vector3)v;
                sb.Append("{\"x\":"); WriteNumber(sb, a.x);
                sb.Append(",\"y\":"); WriteNumber(sb, a.y);
                sb.Append(",\"z\":"); WriteNumber(sb, a.z); sb.Append('}');
                return true;
            }
            if (v is Vector4)
            {
                var a = (Vector4)v;
                sb.Append("{\"x\":"); WriteNumber(sb, a.x);
                sb.Append(",\"y\":"); WriteNumber(sb, a.y);
                sb.Append(",\"z\":"); WriteNumber(sb, a.z);
                sb.Append(",\"w\":"); WriteNumber(sb, a.w); sb.Append('}');
                return true;
            }
            if (v is Quaternion)
            {
                var a = (Quaternion)v;
                sb.Append("{\"x\":"); WriteNumber(sb, a.x);
                sb.Append(",\"y\":"); WriteNumber(sb, a.y);
                sb.Append(",\"z\":"); WriteNumber(sb, a.z);
                sb.Append(",\"w\":"); WriteNumber(sb, a.w); sb.Append('}');
                return true;
            }
            if (v is Color)
            {
                var a = (Color)v;
                sb.Append("{\"r\":"); WriteNumber(sb, a.r);
                sb.Append(",\"g\":"); WriteNumber(sb, a.g);
                sb.Append(",\"b\":"); WriteNumber(sb, a.b);
                sb.Append(",\"a\":"); WriteNumber(sb, a.a); sb.Append('}');
                return true;
            }
            if (v is Color32) { return WriteUnityValueType(sb, (Color)(Color32)v, depth, seen); }
            if (v is Bounds)
            {
                var a = (Bounds)v;
                sb.Append("{\"center\":"); WriteValue(sb, a.center, depth, seen);
                sb.Append(",\"size\":"); WriteValue(sb, a.size, depth, seen); sb.Append('}');
                return true;
            }
            return false;
        }

        private static void WriteMember(StringBuilder sb, FieldInfo fi, PropertyInfo pi, object owner, int depth, HashSet<object> seen)
        {
            // Unity getters throw freely (destroyed objects, editor-only state). A bridge must never
            // fail an entire response because one member misbehaved.
            object mv;
            try { mv = fi != null ? fi.GetValue(owner) : pi.GetValue(owner, null); }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                WriteString(sb, "<error: " + inner.GetType().Name + ">");
                return;
            }
            WriteValue(sb, mv, depth, seen);
        }

        private static string HierarchyPath(Transform tr)
        {
            if (tr == null) return "";
            var parts = new List<string>();
            while (tr != null) { parts.Add(tr.name); tr = tr.parent; }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            // JSON has no NaN/Infinity literal; emit null so clients stay on the happy path.
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        // ---------- reading ----------

        public static object Parse(string json)
        {
            int i = 0;
            var v = ParseValue(json, ref i);
            SkipWs(json, ref i);
            if (i < json.Length) throw new FormatException("Trailing content at offset " + i);
            return v;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected colon at offset " + i);
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("Expected comma or close-brace at offset " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("Expected comma or close-bracket at offset " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected string at offset " + i);
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("Unterminated escape");
                char e = s[i++];
                if (e == '"') sb.Append('"');
                else if (e == '\\') sb.Append('\\');
                else if (e == '/') sb.Append('/');
                else if (e == 'b') sb.Append('\b');
                else if (e == 'f') sb.Append('\f');
                else if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u')
                {
                    if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                }
                else throw new FormatException("Bad escape at offset " + (i - 1));
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            if (start == i) throw new FormatException("Expected number at offset " + start);
            string tok = s.Substring(start, i - start);
            return double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new FormatException("Expected literal " + lit + " at offset " + i);
            i += lit.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        // ---------- typed param access ----------

        public static Dictionary<string, object> AsObject(object v)
        {
            return v as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public static string GetString(Dictionary<string, object> o, string key, string fallback = null)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> o, string key, int fallback)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (int)(double)v;
            int parsed;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static long GetLong(Dictionary<string, object> o, string key, long fallback)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double) return (long)(double)v;
            long parsed;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool fallback)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            bool parsed;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static List<string> GetStringList(Dictionary<string, object> o, string key)
        {
            var result = new List<string>();
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return result;
            var list = v as List<object>;
            if (list == null) return result;
            foreach (var item in list)
            {
                if (item != null) result.Add(item as string ?? Convert.ToString(item, CultureInfo.InvariantCulture));
            }
            return result;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
    }
}
