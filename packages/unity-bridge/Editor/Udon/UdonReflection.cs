using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Typed access to the parts of UdonSharp an agent needs that are not public API.
    ///
    /// UdonSharp grants InternalsVisibleTo only to VRChat's own assemblies — there is no third-party
    /// friend hook — so reflection is the supported route, not a workaround. Resolution is cached and
    /// every accessor reports a clear message when a member has moved, so an SDK upgrade surfaces as
    /// "UdonSharp API moved: ..." rather than a NullReferenceException somewhere downstream.
    ///
    /// Verified against SDK 3.10.5:
    ///   UdonSharp.Compiler.UdonSharpCompilerV1     public  - CompileSync(UdonSharpCompileOptions)
    ///   UdonSharp.UdonSharpEditorCache             internal - static public Instance, internal LastCompileDiagnostics
    ///   UdonSharpEditor.UdonSharpEditorManager     internal - static public RunAllUpgrades()
    ///   UdonSharp.Compiler.Udon.CompilerUdonInterface internal - static public IsExposedToUdon(string)
    /// </summary>
    public static class UdonReflection
    {
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        public const string CompilerV1 = "UdonSharp.Compiler.UdonSharpCompilerV1";
        public const string CompileOptions = "UdonSharp.Compiler.UdonSharpCompileOptions";
        public const string EditorCache = "UdonSharp.UdonSharpEditorCache";
        public const string EditorManager = "UdonSharpEditor.UdonSharpEditorManager";
        public const string CompilerUdonInterface = "UdonSharp.Compiler.Udon.CompilerUdonInterface";

        public static Type Resolve(string fullName)
        {
            Type cached;
            if (TypeCache.TryGetValue(fullName, out cached)) return cached;

            Type found = Type.GetType(fullName, false);
            if (found == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // Skip UdonSharp's in-memory compile output: it shadow-copies user types, and a
                    // shadow copy is not the type Unity's component system knows about.
                    if (IsUdonSharpShadowAssembly(asm)) continue;
                    found = asm.GetType(fullName, false);
                    if (found != null) break;
                }
            }
            if (found == null) throw new TypeLoadException("UdonSharp API moved: type " + fullName + " not found in any loaded assembly.");

            TypeCache[fullName] = found;
            return found;
        }

        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static object InvokeStatic(string typeName, string method, params object[] args)
        {
            var type = Resolve(typeName);
            var types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i] == null ? typeof(object) : args[i].GetType();

            var info = type.GetMethod(method, AnyStatic, null, types, null)
                       ?? type.GetMethod(method, AnyStatic);
            if (info == null) throw new MissingMethodException("UdonSharp API moved: no static " + typeName + "." + method);

            try { return info.Invoke(null, args); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        public static object GetStaticProperty(string typeName, string property)
        {
            var type = Resolve(typeName);
            var info = type.GetProperty(property, AnyStatic);
            if (info == null) throw new MissingMemberException("UdonSharp API moved: no static property " + typeName + "." + property);
            try { return info.GetValue(null, null); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        public static object GetInstanceProperty(object instance, string property)
        {
            if (instance == null) throw new ArgumentNullException("instance");
            var info = instance.GetType().GetProperty(property, AnyInstance);
            if (info == null) throw new MissingMemberException("UdonSharp API moved: no property " + instance.GetType().FullName + "." + property);
            try { return info.GetValue(instance, null); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        // ---------- the specific things M1 needs ----------

        /// <summary>
        /// Structured compile diagnostics, the only way to get file/line/column/message out of UdonSharp.
        /// The public surface offers just AnyUdonSharpScriptHasError() (a bool), which is not actionable.
        /// </summary>
        public static List<object> ReadCompileDiagnostics()
        {
            var result = new List<object>();
            object cache = GetStaticProperty(EditorCache, "Instance");
            if (cache == null) return result;

            var array = GetInstanceProperty(cache, "LastCompileDiagnostics") as Array;
            if (array == null) return result;

            foreach (object entry in array)
            {
                if (entry == null) continue;
                var type = entry.GetType();
                result.Add(new Dictionary<string, object>
                {
                    { "severity", ReadFieldString(type, entry, "severity") },
                    { "file", NormalizePath(ReadFieldString(type, entry, "file")) },
                    { "line", ReadFieldInt(type, entry, "line") },
                    { "character", ReadFieldInt(type, entry, "character") },
                    { "message", ReadFieldString(type, entry, "message") }
                });
            }
            return result;
        }

        /// <summary>
        /// Runs the upgrade pass. Required before CopyProxyToUdon can touch a freshly created program
        /// asset: a new asset has ScriptVersion = Unknown and CopyProxyToUdon throws until the upgrade
        /// pass plus a compile have bumped it (UdonSharpEditorUtility.cs:1172-1176).
        /// Returns true when scripts were rewritten, which means a recompile is pending.
        /// </summary>
        public static bool RunAllUpgrades()
        {
            object rewritten = InvokeStatic(EditorManager, "RunAllUpgrades");
            return rewritten is bool && (bool)rewritten;
        }

        /// <summary>Blocking UdonSharp compile. Caller must ensure the project has no C# errors first.</summary>
        public static void CompileSync(bool isEditorBuild)
        {
            var optionsType = Resolve(CompileOptions);
            object options = Activator.CreateInstance(optionsType);
            var flag = optionsType.GetProperty("IsEditorBuild", AnyInstance);
            if (flag != null && flag.CanWrite) flag.SetValue(options, isEditorBuild, null);

            var type = Resolve(CompilerV1);
            var method = type.GetMethod("CompileSync", BindingFlags.Static | BindingFlags.Public, null, new[] { optionsType }, null);
            if (method == null) throw new MissingMethodException("UdonSharp API moved: no UdonSharpCompilerV1.CompileSync(UdonSharpCompileOptions)");

            try { method.Invoke(null, new[] { options }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        /// <summary>Whether a C#-style signature is callable from Udon at all.</summary>
        public static bool IsExposedToUdon(string signature)
        {
            object exposed = InvokeStatic(CompilerUdonInterface, "IsExposedToUdon", signature);
            return exposed is bool && (bool)exposed;
        }

        // ---------- small helpers ----------

        private static string ReadFieldString(Type type, object instance, string field)
        {
            var info = type.GetField(field, AnyInstance);
            if (info == null) return null;
            object value = info.GetValue(instance);
            return value == null ? null : value.ToString();
        }

        private static int ReadFieldInt(Type type, object instance, string field)
        {
            var info = type.GetField(field, AnyInstance);
            if (info == null) return 0;
            object value = info.GetValue(instance);
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch (Exception) { return 0; }
        }

        public static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        /// <summary>
        /// Finds a GameObject by its full hierarchy path ("Parent/Child"), searching every loaded scene.
        /// Paths are what an agent naturally has; instance ids do not survive a domain reload.
        /// </summary>
        public static GameObject FindGameObject(string hierarchyPath)
        {
            if (string.IsNullOrEmpty(hierarchyPath)) throw new ArgumentException("A GameObject path is required");

            var direct = GameObject.Find(hierarchyPath);
            if (direct != null) return direct;

            // GameObject.Find skips inactive objects, which an agent will certainly have.
            string[] segments = hierarchyPath.Split('/');
            for (int s = 0; s < UnityEditor.SceneManagement.EditorSceneManager.sceneCount; s++)
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != segments[0]) continue;
                    var current = root.transform;
                    bool ok = true;
                    for (int i = 1; i < segments.Length && ok; i++)
                    {
                        var next = current.Find(segments[i]);
                        if (next == null) ok = false;
                        else current = next;
                    }
                    if (ok) return current.gameObject;
                }
            }

            throw new ArgumentException("No GameObject at path \"" + hierarchyPath + "\" in any loaded scene.");
        }

        /// <summary>
        /// True for UdonSharp's own in-memory compile output.
        ///
        /// The U# compiler Roslyn-compiles user scripts into assemblies named
        /// UdonSharpRoslynCompileAssembly0, 1, ... Those contain *shadow copies* of every
        /// UdonSharpBehaviour, so after one compile a plain domain-wide type search finds
        /// "AgenticTestDoor" three times with three different module ids. Only the copy in
        /// Assembly-CSharp is the type Unity's component system uses; attaching one of the shadows
        /// would produce a component that GetComponent could never find again.
        /// </summary>
        private static bool IsUdonSharpShadowAssembly(Assembly assembly)
        {
            try
            {
                return assembly.IsDynamic ||
                       assembly.GetName().Name.StartsWith("UdonSharpRoslynCompileAssembly", StringComparison.Ordinal);
            }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// Resolves an UdonSharpBehaviour subclass to the type Unity itself uses.
        ///
        /// Program assets are consulted first because their <c>GetClass()</c> is authoritative — it goes
        /// through the MonoScript, so it always yields the Assembly-CSharp type — and because any
        /// behaviour worth attaching must have a program asset anyway.
        /// </summary>
        public static Type ResolveUdonSharpBehaviourType(string name)
        {
            var programAssetType = Resolve("UdonSharp.UdonSharpProgramAsset");
            var getAll = programAssetType.GetMethod("GetAllUdonSharpPrograms", BindingFlags.Static | BindingFlags.Public);
            var getClass = programAssetType.GetMethod("GetClass", BindingFlags.Instance | BindingFlags.Public);

            if (getAll != null && getClass != null)
            {
                var assets = getAll.Invoke(null, null) as Array;
                if (assets != null)
                {
                    foreach (object asset in assets)
                    {
                        if (asset == null) continue;
                        Type candidate;
                        try { candidate = getClass.Invoke(asset, null) as Type; }
                        catch (Exception) { continue; }
                        if (candidate == null || candidate.IsAbstract) continue;
                        if (candidate.FullName == name || candidate.Name == name) return candidate;
                    }
                }
            }

            // Fallback for behaviours that genuinely have no program asset yet: scan, but never the
            // shadow assemblies, and collapse same-FullName hits.
            var baseType = Resolve("UdonSharp.UdonSharpBehaviour");
            var byFullName = new Dictionary<string, Type>(StringComparer.Ordinal);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsUdonSharpShadowAssembly(asm)) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types ?? new Type[0]; }
                catch (Exception) { continue; }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !baseType.IsAssignableFrom(type)) continue;
                    if (type.FullName != name && type.Name != name) continue;
                    if (!byFullName.ContainsKey(type.FullName)) byFullName.Add(type.FullName, type);
                }
            }

            if (byFullName.Count == 1)
            {
                foreach (var only in byFullName.Values) return only;
            }
            if (byFullName.Count > 1)
            {
                var names = new List<string>(byFullName.Keys);
                throw new ArgumentException("\"" + name + "\" is ambiguous; use the full name. Candidates: " + string.Join(", ", names.ToArray()));
            }

            throw new ArgumentException(
                "No non-abstract UdonSharpBehaviour named \"" + name + "\". If the script was just written, " +
                "Unity must compile it first - call unity.refresh_assets and wait for compile_status to go idle.");
        }
    }
}
