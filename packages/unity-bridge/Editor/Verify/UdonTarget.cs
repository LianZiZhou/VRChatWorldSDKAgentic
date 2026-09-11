using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Resolves a scenario's "gameObject + type" pair to the live UdonBehaviour that actually runs.
    ///
    /// Verification must read the UdonBehaviour, not the UdonSharpBehaviour proxy. The proxy is an editor
    /// convenience; at runtime the program's state lives on the backing UdonBehaviour, and
    /// GetProgramVariable there is the authoritative view. Resolving through the program asset's class
    /// also keeps this working when several behaviours share a GameObject.
    /// </summary>
    public static class UdonTarget
    {
        public sealed class Resolved
        {
            public GameObject gameObject;
            public Component udonBehaviour;
            public string typeName;
        }

        public static Resolved Resolve(string gameObjectPath, string typeName)
        {
            var go = UdonReflection.FindGameObject(gameObjectPath);
            var udonBehaviourType = UdonReflection.Resolve("VRC.Udon.UdonBehaviour");
            var behaviours = go.GetComponents(udonBehaviourType);

            if (behaviours.Length == 0)
                throw new ArgumentException("\"" + gameObjectPath + "\" has no UdonBehaviour. Attach the script with udon.attach first.");

            if (string.IsNullOrEmpty(typeName))
            {
                if (behaviours.Length > 1)
                    throw new ArgumentException("\"" + gameObjectPath + "\" has " + behaviours.Length +
                        " UdonBehaviours; pass \"type\" to say which one.");
                return new Resolved { gameObject = go, udonBehaviour = behaviours[0], typeName = DescribeProgram(behaviours[0]) };
            }

            var matches = new List<Component>();
            foreach (var behaviour in behaviours)
            {
                string programClass = DescribeProgram(behaviour);
                if (programClass == null) continue;
                if (programClass == typeName || EndsWithType(programClass, typeName)) matches.Add(behaviour);
            }

            if (matches.Count == 1) return new Resolved { gameObject = go, udonBehaviour = matches[0], typeName = typeName };
            if (matches.Count > 1)
                throw new ArgumentException("\"" + gameObjectPath + "\" has several UdonBehaviours running " + typeName + ".");

            var available = new List<string>();
            foreach (var behaviour in behaviours) available.Add(DescribeProgram(behaviour) ?? "<no program>");
            throw new ArgumentException("No UdonBehaviour running \"" + typeName + "\" on \"" + gameObjectPath +
                "\". Present: " + string.Join(", ", available.ToArray()));
        }

        private static bool EndsWithType(string fullName, string shortName)
        {
            return fullName.EndsWith("." + shortName, StringComparison.Ordinal);
        }

        /// <summary>Class name of the UdonSharp program driving this behaviour, or null.</summary>
        public static string DescribeProgram(Component udonBehaviour)
        {
            try
            {
                var field = udonBehaviour.GetType().GetField("programSource",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object programAsset = field == null ? null : field.GetValue(udonBehaviour);
                if (programAsset == null) return null;

                var getClass = programAsset.GetType().GetMethod("GetClass", BindingFlags.Instance | BindingFlags.Public);
                var type = getClass == null ? null : getClass.Invoke(programAsset, null) as Type;
                return type == null ? null : type.FullName;
            }
            catch (Exception) { return null; }
        }

        public static object GetVariable(Component udonBehaviour, string name)
        {
            var method = FindStringMethod(udonBehaviour, "GetProgramVariable", typeof(object));
            if (method == null) throw new MissingMethodException("VRChat SDK API moved: UdonBehaviour.GetProgramVariable(string)");
            try { return method.Invoke(udonBehaviour, new object[] { name }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        public static void SetVariable(Component udonBehaviour, string name, object value)
        {
            foreach (var method in udonBehaviour.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != "SetProgramVariable" || method.IsGenericMethod) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(string)) continue;
                if (parameters[1].ParameterType != typeof(object)) continue;
                try { method.Invoke(udonBehaviour, new object[] { name, value }); return; }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
            }
            throw new MissingMethodException("VRChat SDK API moved: UdonBehaviour.SetProgramVariable(string, object)");
        }

        public static void SendEvent(Component udonBehaviour, string eventName)
        {
            var method = udonBehaviour.GetType().GetMethod("SendCustomEvent",
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (method == null) throw new MissingMethodException("VRChat SDK API moved: UdonBehaviour.SendCustomEvent(string)");
            try { method.Invoke(udonBehaviour, new object[] { eventName }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        private static MethodInfo FindStringMethod(Component instance, string name, Type returnType)
        {
            foreach (var method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != name || method.IsGenericMethod) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string)) continue;
                if (returnType != null && method.ReturnType != returnType) continue;
                return method;
            }
            return null;
        }
    }
}
