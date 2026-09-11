using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Reflection-driven static calls.
    ///
    /// Much of the VRChat SDK an agent must reach is internal to VRC.SDK3.Editor, and
    /// InternalsVisibleTo is granted only to VRChat's own assemblies — there is no third-party friend
    /// hook. Reflection is therefore not a shortcut here, it is the supported way in for things like
    /// UdonSharpEditorManager.RunAllUpgrades() and UdonSharpEditorCache.LastCompileDiagnostics.
    ///
    /// Unlike eval_csharp this needs no compiler, so it keeps working in editors where the Mono CodeDom
    /// provider is missing.
    /// </summary>
    public static class Reflect
    {
        public static Dictionary<string, object> InvokeStatic(Dictionary<string, object> a)
        {
            string typeName = Json.GetString(a, "type");
            string methodName = Json.GetString(a, "method");
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("invoke_static needs \"type\"");
            if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("invoke_static needs \"method\"");

            bool nonPublic = Json.GetBool(a, "nonPublic", true);
            var type = ResolveType(typeName);

            object rawArgs;
            a.TryGetValue("args", out rawArgs);
            var jsonArgs = rawArgs as List<object> ?? new List<object>();

            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            if (nonPublic) flags |= BindingFlags.NonPublic;

            var candidates = new List<MethodInfo>();
            foreach (var m in type.GetMethods(flags))
            {
                if (m.Name != methodName) continue;
                if (m.GetParameters().Length != jsonArgs.Count) continue;
                candidates.Add(m);
            }

            if (candidates.Count == 0)
            {
                throw new MissingMethodException("No static method " + type.FullName + "." + methodName +
                    " taking " + jsonArgs.Count + " argument(s). Available: " + string.Join(", ", DescribeMethods(type, methodName, flags)));
            }

            Exception lastFailure = null;
            foreach (var method in candidates)
            {
                object[] converted;
                try { converted = ConvertArgs(jsonArgs, method.GetParameters()); }
                catch (Exception ex) { lastFailure = ex; continue; }

                object returned;
                try { returned = method.Invoke(null, converted); }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

                return new Dictionary<string, object>
                {
                    { "type", type.FullName },
                    { "method", method.Name },
                    { "signature", Describe(method) },
                    { "returned", returned },
                    { "returnType", method.ReturnType == typeof(void) ? "void" : method.ReturnType.FullName }
                };
            }

            throw new ArgumentException("Could not bind arguments to any overload of " + type.FullName + "." + methodName +
                (lastFailure != null ? " (" + lastFailure.Message + ")" : ""));
        }

        public static Dictionary<string, object> GetStatic(Dictionary<string, object> a)
        {
            string typeName = Json.GetString(a, "type");
            string memberName = Json.GetString(a, "member");
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("get_static needs \"type\"");
            if (string.IsNullOrEmpty(memberName)) throw new ArgumentException("get_static needs \"member\"");

            var type = ResolveType(typeName);
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            if (Json.GetBool(a, "nonPublic", true)) flags |= BindingFlags.NonPublic;

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return new Dictionary<string, object>
                {
                    { "type", type.FullName }, { "member", memberName }, { "kind", "field" },
                    { "value", field.GetValue(null) },
                    { "valueType", field.FieldType.FullName }
                };
            }

            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanRead)
            {
                object value;
                try { value = property.GetValue(null, null); }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
                return new Dictionary<string, object>
                {
                    { "type", type.FullName }, { "member", memberName }, { "kind", "property" },
                    { "value", value },
                    { "valueType", property.PropertyType.FullName }
                };
            }

            throw new MissingMemberException("No static field or readable property " + type.FullName + "." + memberName);
        }

        /// <summary>
        /// Accepts an assembly-qualified name, or a bare full name which is then searched across every
        /// loaded assembly — agents rarely know which DLL a VRChat type lives in.
        /// </summary>
        public static Type ResolveType(string name)
        {
            var direct = Type.GetType(name, false);
            if (direct != null) return direct;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var found = assemblies[i].GetType(name, false);
                if (found != null) return found;
            }

            // Last resort: match on the short name, which is how SDK types are usually referred to.
            var matches = new List<Type>();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try { types = assemblies[i].GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }
                catch (Exception) { continue; }

                for (int j = 0; j < types.Length; j++)
                {
                    if (types[j] != null && types[j].Name == name) matches.Add(types[j]);
                }
            }

            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                var names = new List<string>();
                for (int i = 0; i < matches.Count && i < 10; i++) names.Add(matches[i].FullName);
                throw new ArgumentException("Type name \"" + name + "\" is ambiguous; use the full name. Candidates: " + string.Join(", ", names.ToArray()));
            }

            throw new TypeLoadException("Could not resolve type \"" + name + "\" in any loaded assembly.");
        }

        private static object[] ConvertArgs(List<object> jsonArgs, ParameterInfo[] parameters)
        {
            var converted = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                converted[i] = ConvertValue(jsonArgs[i], parameters[i].ParameterType);
            return converted;
        }

        private static object ConvertValue(object value, Type target)
        {
            if (value == null)
            {
                if (target.IsValueType && Nullable.GetUnderlyingType(target) == null)
                    throw new ArgumentException("null is not assignable to value type " + target.FullName);
                return null;
            }

            if (target == typeof(object)) return value;
            if (target.IsInstanceOfType(value)) return value;

            if (target.IsEnum)
            {
                if (value is string) return Enum.Parse(target, (string)value, true);
                return Enum.ToObject(target, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            if (target == typeof(Type)) return ResolveType(Convert.ToString(value, CultureInfo.InvariantCulture));
            if (target == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (target.IsArray)
            {
                var list = value as List<object>;
                if (list == null) throw new ArgumentException("Expected an array for parameter of type " + target.FullName);
                var elementType = target.GetElementType();
                var array = Array.CreateInstance(elementType, list.Count);
                for (int i = 0; i < list.Count; i++) array.SetValue(ConvertValue(list[i], elementType), i);
                return array;
            }

            var nullableInner = Nullable.GetUnderlyingType(target);
            if (nullableInner != null) return ConvertValue(value, nullableInner);

            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        private static List<string> DescribeMethods(Type type, string methodName, BindingFlags flags)
        {
            var described = new List<string>();
            foreach (var m in type.GetMethods(flags))
            {
                if (m.Name == methodName) described.Add(Describe(m));
            }
            if (described.Count == 0) described.Add("(no method named " + methodName + ")");
            return described;
        }

        private static string Describe(MethodInfo m)
        {
            var parameters = m.GetParameters();
            var parts = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                parts[i] = parameters[i].ParameterType.Name + " " + parameters[i].Name;
            return (m.ReturnType == typeof(void) ? "void" : m.ReturnType.Name) + " " + m.Name + "(" + string.Join(", ", parts) + ")";
        }
    }
}
