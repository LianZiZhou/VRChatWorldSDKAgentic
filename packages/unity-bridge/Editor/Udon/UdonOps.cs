using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// The UdonSharp authoring loop: write a script, compile it, attach it, set its fields, read its state.
    ///
    /// Three SDK behaviours dictate the shape of everything here:
    ///   1. UdonSharpCompilerV1 checks DoesUnityProjectHaveCompileErrors() and then *silently returns*
    ///      (UdonSharpCompilerV1.cs:272). So udon.compile must verify Unity's own C# compile first, or an
    ///      agent sees a compile that reports neither success nor failure.
    ///   2. A fresh UdonSharpProgramAsset has ScriptVersion = Unknown, and CopyProxyToUdon throws until
    ///      the upgrade pass and a compile have run (UdonSharpEditorUtility.cs:1172-1176).
    ///   3. Setting public fields on the proxy MonoBehaviour is not enough. The values that ship live on
    ///      the hidden backing UdonBehaviour, and only CopyProxyToUdon moves them there. Skipping that is
    ///      why generic Unity tooling silently loses UdonSharp field values.
    /// </summary>
    public static class UdonOps
    {
        private const string DefaultScriptFolder = "Assets/Udon";

        public static void Register()
        {
            BridgeRouter.Register("udon.status",
                "UdonSharp program assets, whether any script has an error, and the last compile's diagnostics.",
                delegate (Dictionary<string, object> a) { return Status(); });

            BridgeRouter.Register("udon.create_script",
                "Write an UdonSharpBehaviour .cs and create its program asset. Args: className, code (optional), folder.",
                delegate (Dictionary<string, object> a) { return CreateScript(a); }, defaultTimeoutMs: 120000);

            BridgeRouter.Register("udon.attach",
                "Attach an UdonSharpBehaviour to a GameObject, wiring the backing UdonBehaviour. Args: gameObject, type.",
                delegate (Dictionary<string, object> a) { return Attach(a); }, defaultTimeoutMs: 60000);

            BridgeRouter.Register("udon.set_fields",
                "Set public fields on an attached UdonSharpBehaviour and copy them to the backing UdonBehaviour. " +
                "Args: gameObject, type, fields{}, saveScene.",
                delegate (Dictionary<string, object> a) { return SetFields(a); }, defaultTimeoutMs: 60000);

            BridgeRouter.Register("udon.read_vars",
                "Read an attached behaviour's values from both the proxy and the backing UdonBehaviour, so a " +
                "failed proxy-to-Udon copy is visible. Args: gameObject, type, names[].",
                delegate (Dictionary<string, object> a) { return ReadVars(a); });

            BridgeRouter.Register("udon.list_behaviours",
                "Every UdonSharpBehaviour in the loaded scenes, with its GameObject path and backing state.",
                delegate (Dictionary<string, object> a) { return ListBehaviours(); });

            JobStore.RegisterKind("udon.compile", delegate (Dictionary<string, object> args, JobContext ctx)
            {
                return CompileJob(args, ctx);
            });
        }

        // ------------------------------------------------------------------ status

        private static Dictionary<string, object> Status()
        {
            var programAssetType = UdonReflection.Resolve("UdonSharp.UdonSharpProgramAsset");
            var all = programAssetType.GetMethod("GetAllUdonSharpPrograms", BindingFlags.Static | BindingFlags.Public);
            var anyError = programAssetType.GetMethod("AnyUdonSharpScriptHasError", BindingFlags.Static | BindingFlags.Public);
            var anyDirty = programAssetType.GetMethod("IsAnyProgramAssetSourceDirty", BindingFlags.Static | BindingFlags.Public);

            var assets = all == null ? null : all.Invoke(null, null) as Array;
            var names = new List<object>();
            if (assets != null)
            {
                foreach (UnityEngine.Object asset in assets)
                {
                    if (asset == null) continue;
                    names.Add(new Dictionary<string, object>
                    {
                        { "name", asset.name },
                        { "path", UdonReflection.NormalizePath(AssetDatabase.GetAssetPath(asset)) }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "programAssetCount", names.Count },
                { "programAssets", names },
                { "anyScriptHasError", anyError == null ? (object)null : anyError.Invoke(null, null) },
                { "anySourceDirty", anyDirty == null ? (object)null : anyDirty.Invoke(null, null) },
                { "diagnostics", UdonReflection.ReadCompileDiagnostics() },
                { "unityIsCompiling", EditorApplication.isCompiling }
            };
        }

        // ------------------------------------------------------------------ create

        /// <summary>
        /// Writes the .cs and creates the matching UdonSharpProgramAsset.
        ///
        /// Nothing in the SDK does this automatically: UdonSharpProgramAssetPostprocessor only reacts to
        /// program assets that already exist, and the one helper that creates both
        /// (UdonSharpBehaviourEditor.CreateUSharpScript) is internal and opens a SaveFilePanel, so it is
        /// unusable headlessly. This replicates its body.
        /// </summary>
        private static Dictionary<string, object> CreateScript(Dictionary<string, object> a)
        {
            string className = Json.GetString(a, "className");
            if (string.IsNullOrEmpty(className)) throw new ArgumentException("udon.create_script needs \"className\"");
            if (className.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("className contains characters that are not valid in a filename");

            string folder = UdonReflection.NormalizePath(Json.GetString(a, "folder", DefaultScriptFolder)).TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && folder != "Assets")
                throw new ArgumentException("folder must be under Assets/ (AssetDatabase.CreateAsset cannot write into Packages/)");

            string code = Json.GetString(a, "code");
            if (string.IsNullOrEmpty(code)) code = DefaultTemplate(className);
            if (code.IndexOf("class " + className, StringComparison.Ordinal) < 0)
                throw new ArgumentException(
                    "UdonSharp requires the class name to match the file name, but the supplied code does not declare " +
                    "\"class " + className + "\".");

            EnsureFolder(folder);

            string scriptPath = folder + "/" + className + ".cs";
            string assetPath = folder + "/" + className + ".asset";
            bool scriptExisted = File.Exists(scriptPath);

            File.WriteAllText(scriptPath, code, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceSynchronousImport);

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            bool programAssetCreated = false;

            // The program asset can only be linked once Unity has compiled the script into an assembly,
            // because sourceCsScript resolution goes through MonoScript.GetClass(). On the first write
            // that has not happened yet, so creating it is deferred to the caller's next pass.
            bool typeAvailable = script != null && script.GetClass() != null;

            if (Json.GetBool(a, "createProgramAsset", true) && !File.Exists(assetPath) && typeAvailable)
            {
                CreateProgramAsset(script, assetPath);
                programAssetCreated = true;
            }

            return new Dictionary<string, object>
            {
                { "scriptPath", scriptPath },
                { "assetPath", assetPath },
                { "scriptExisted", scriptExisted },
                { "typeAvailable", typeAvailable },
                { "programAssetCreated", programAssetCreated },
                { "programAssetExists", File.Exists(assetPath) },
                { "nextStep", typeAvailable
                    ? "Run the udon.compile job, then udon.attach."
                    : "Unity must compile the new script first: poll unity.compile_status until idle, then call " +
                      "udon.create_script again (it is idempotent) to create the program asset." }
            };
        }

        private static void CreateProgramAsset(MonoScript script, string assetPath)
        {
            var programAssetType = UdonReflection.Resolve("UdonSharp.UdonSharpProgramAsset");
            var asset = ScriptableObject.CreateInstance(programAssetType);

            var sourceField = programAssetType.GetField("sourceCsScript", BindingFlags.Public | BindingFlags.Instance);
            if (sourceField == null) throw new MissingMemberException("UdonSharp API moved: UdonSharpProgramAsset.sourceCsScript");
            sourceField.SetValue(asset, script);

            AssetDatabase.CreateAsset(asset, assetPath);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string DefaultTemplate(string className)
        {
            var sb = new StringBuilder();
            sb.Append("using UdonSharp;\n");
            sb.Append("using UnityEngine;\n");
            sb.Append("using VRC.SDKBase;\n");
            sb.Append("using VRC.Udon;\n\n");
            sb.Append("public class ").Append(className).Append(" : UdonSharpBehaviour\n");
            sb.Append("{\n");
            sb.Append("    void Start()\n");
            sb.Append("    {\n");
            sb.Append("    }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ compile

        /// <summary>
        /// Compiles all UdonSharp programs, in the order the SDK actually requires.
        ///
        /// Note this job can be reported as `interrupted`: the upgrade pass rewrites assets, which can
        /// trigger a domain reload that destroys the job's enumerator. That is expected - resubmit.
        /// </summary>
        private static IEnumerator CompileJob(Dictionary<string, object> args, JobContext ctx)
        {
            bool isEditorBuild = Json.GetBool(args, "isEditorBuild", true);

            ctx.Report(0.05f, "waiting for Unity to finish compiling");
            int guard = 0;
            while ((EditorApplication.isCompiling || EditorApplication.isUpdating) && guard++ < 60 * 60 * 10)
            {
                if (ctx.CancelRequested) yield break;
                yield return null;
            }

            // Step order matters: a C# error makes CompileSync a silent no-op, so report it as the result
            // rather than letting the agent read an empty success.
            var csharp = CompileWatcher.Read();
            int csharpErrors = Convert.ToInt32(csharp["errorCount"]);
            if (csharpErrors > 0)
            {
                ctx.SetResult(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "stage", "csharp" },
                    { "message", "Unity's C# compile has " + csharpErrors + " error(s). UdonSharp would silently do nothing; fix these first." },
                    { "diagnostics", csharp["diagnostics"] }
                });
                yield break;
            }

            ctx.Report(0.25f, "running UdonSharp upgrade pass");
            bool rewritten = false;
            Exception failure = null;
            try { rewritten = UdonReflection.RunAllUpgrades(); }
            catch (Exception ex) { failure = ex; }
            if (failure != null) throw failure;

            if (rewritten)
            {
                ctx.SetResult(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "stage", "upgrade" },
                    { "message", "The upgrade pass rewrote scripts, so a recompile is pending. Wait for unity.compile_status to go idle and resubmit udon.compile." }
                });
                yield break;
            }

            ctx.Report(0.45f, "compiling UdonSharp programs");
            yield return null; // let the progress update reach disk before the blocking call

            try { UdonReflection.CompileSync(isEditorBuild); }
            catch (Exception ex) { failure = ex; }
            if (failure != null) throw failure;

            ctx.Report(0.9f, "collecting diagnostics");
            yield return null;

            var diagnostics = UdonReflection.ReadCompileDiagnostics();
            int errors = 0, warnings = 0;
            foreach (var entry in diagnostics)
            {
                string severity = Json.GetString((Dictionary<string, object>)entry, "severity", "");
                if (severity.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) errors++;
                else if (severity.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) warnings++;
            }

            var programAssetType = UdonReflection.Resolve("UdonSharp.UdonSharpProgramAsset");
            var anyError = programAssetType.GetMethod("AnyUdonSharpScriptHasError", BindingFlags.Static | BindingFlags.Public);
            bool anyScriptHasError = anyError != null && (bool)anyError.Invoke(null, null);

            ctx.SetResult(new Dictionary<string, object>
            {
                { "ok", !anyScriptHasError && errors == 0 },
                { "stage", "udonsharp" },
                { "errorCount", errors },
                { "warningCount", warnings },
                { "anyScriptHasError", anyScriptHasError },
                { "diagnostics", diagnostics }
            });
        }

        // ------------------------------------------------------------------ attach

        private static Dictionary<string, object> Attach(Dictionary<string, object> a)
        {
            var go = UdonReflection.FindGameObject(Json.GetString(a, "gameObject"));
            var type = UdonReflection.ResolveUdonSharpBehaviourType(Json.GetString(a, "type"));

            var existing = go.GetComponent(type);
            bool alreadyPresent = existing != null;

            object behaviour = existing;
            if (!alreadyPresent)
            {
                // UdonSharpUndo.AddComponent also runs RunBehaviourSetup, which creates the hidden backing
                // UdonBehaviour and links the program asset. ConvertToUdonBehaviours is NOT an alternative:
                // it is an obsolete stub that throws NotImplementedException.
                var undoType = UdonReflection.Resolve("UdonSharpEditor.UdonSharpUndo");
                var add = undoType.GetMethod("AddComponent", BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(GameObject), typeof(Type) }, null);
                if (add == null) throw new MissingMethodException("UdonSharp API moved: UdonSharpUndo.AddComponent(GameObject, Type)");
                try { behaviour = add.Invoke(null, new object[] { go, type }); }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            if (Json.GetBool(a, "saveScene", false)) EditorSceneManager.SaveScene(go.scene);

            return Describe(go, type, behaviour as MonoBehaviour, alreadyPresent);
        }

        private static Dictionary<string, object> Describe(GameObject go, Type type, MonoBehaviour behaviour, bool alreadyPresent)
        {
            var backing = GetBackingUdonBehaviour(behaviour);
            return new Dictionary<string, object>
            {
                { "gameObject", HierarchyPath(go.transform) },
                { "type", type.FullName },
                { "alreadyPresent", alreadyPresent },
                { "hasBackingUdonBehaviour", backing != null },
                { "programAssetLinked", backing != null && GetProgramSource(backing) != null },
                { "publicFields", DescribeFields(type) }
            };
        }

        private static List<object> DescribeFields(Type type)
        {
            var fields = new List<object>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsNotSerialized) continue;
                fields.Add(new Dictionary<string, object>
                {
                    { "name", field.Name },
                    { "type", field.FieldType.FullName },
                    { "synced", IsSynced(field) }
                });
            }
            return fields;
        }

        private static bool IsSynced(FieldInfo field)
        {
            foreach (var attr in field.GetCustomAttributes(false))
            {
                if (attr.GetType().Name == "UdonSyncedAttribute") return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ fields

        private static Dictionary<string, object> SetFields(Dictionary<string, object> a)
        {
            var go = UdonReflection.FindGameObject(Json.GetString(a, "gameObject"));
            var type = UdonReflection.ResolveUdonSharpBehaviourType(Json.GetString(a, "type"));
            var behaviour = go.GetComponent(type) as MonoBehaviour;
            if (behaviour == null)
                throw new ArgumentException("GameObject \"" + go.name + "\" has no " + type.Name + ". Call udon.attach first.");

            object rawFields;
            a.TryGetValue("fields", out rawFields);
            var fields = Json.AsObject(rawFields);
            if (fields.Count == 0) throw new ArgumentException("udon.set_fields needs a non-empty \"fields\" object");

            var applied = new List<object>();
            foreach (var pair in fields)
            {
                var field = type.GetField(pair.Key, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    var available = new List<string>();
                    foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) available.Add(f.Name);
                    throw new ArgumentException(
                        "\"" + pair.Key + "\" is not a public field of " + type.Name +
                        ". Public fields: " + string.Join(", ", available.ToArray()));
                }
                object converted = UdonValues.Convert(pair.Value, field.FieldType, pair.Key);
                field.SetValue(behaviour, converted);
                applied.Add(new Dictionary<string, object> { { "name", field.Name }, { "type", field.FieldType.FullName } });
            }

            EditorUtility.SetDirty(behaviour);

            // Without this the values live only on the proxy MonoBehaviour and never reach the backing
            // UdonBehaviour, so they are silently absent at runtime and in the uploaded world.
            string copyError = null;
            try { CopyProxyToUdon(behaviour); }
            catch (Exception ex) { copyError = ex.Message; }

            EditorSceneManager.MarkSceneDirty(go.scene);
            if (Json.GetBool(a, "saveScene", true)) EditorSceneManager.SaveScene(go.scene);

            var result = new Dictionary<string, object>
            {
                { "gameObject", HierarchyPath(go.transform) },
                { "type", type.FullName },
                { "applied", applied },
                { "copiedToUdon", copyError == null }
            };
            if (copyError != null)
            {
                result["copyError"] = copyError;
                result["hint"] =
                    "CopyProxyToUdon throws while a program asset is still at ScriptVersion.Unknown. Run the " +
                    "udon.compile job (it performs the upgrade pass) and call udon.set_fields again.";
            }
            return result;
        }

        private static void CopyProxyToUdon(MonoBehaviour proxy)
        {
            var utility = UdonReflection.Resolve("UdonSharpEditor.UdonSharpEditorUtility");
            var policyType = UdonReflection.Resolve("UdonSharpEditor.ProxySerializationPolicy");
            var allPolicy = policyType.GetProperty("All", BindingFlags.Static | BindingFlags.Public);

            var proxyType = UdonReflection.Resolve("UdonSharp.UdonSharpBehaviour");
            var withPolicy = utility.GetMethod("CopyProxyToUdon", BindingFlags.Static | BindingFlags.Public,
                null, new[] { proxyType, policyType }, null);

            try
            {
                if (withPolicy != null && allPolicy != null)
                {
                    withPolicy.Invoke(null, new[] { proxy, allPolicy.GetValue(null, null) });
                    return;
                }
                var simple = utility.GetMethod("CopyProxyToUdon", BindingFlags.Static | BindingFlags.Public,
                    null, new[] { proxyType }, null);
                if (simple == null) throw new MissingMethodException("UdonSharp API moved: UdonSharpEditorUtility.CopyProxyToUdon");
                simple.Invoke(null, new object[] { proxy });
            }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        // ------------------------------------------------------------------ read

        /// <summary>
        /// Reads each requested field from the proxy *and* from the backing UdonBehaviour's public
        /// variable table. Reporting both is the point: a mismatch is exactly the symptom of a missing
        /// CopyProxyToUdon, which is otherwise invisible until the world misbehaves at runtime.
        /// </summary>
        private static Dictionary<string, object> ReadVars(Dictionary<string, object> a)
        {
            var go = UdonReflection.FindGameObject(Json.GetString(a, "gameObject"));
            var type = UdonReflection.ResolveUdonSharpBehaviourType(Json.GetString(a, "type"));
            var behaviour = go.GetComponent(type) as MonoBehaviour;
            if (behaviour == null)
                throw new ArgumentException("GameObject \"" + go.name + "\" has no " + type.Name);

            var requested = Json.GetStringList(a, "names");
            var fields = new List<FieldInfo>();
            if (requested.Count == 0)
            {
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) fields.Add(f);
            }
            else
            {
                foreach (string name in requested)
                {
                    var f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) throw new ArgumentException("\"" + name + "\" is not a public field of " + type.Name);
                    fields.Add(f);
                }
            }

            var backing = GetBackingUdonBehaviour(behaviour);
            var variables = new List<object>();
            int mismatches = 0;

            foreach (var field in fields)
            {
                object proxyValue = field.GetValue(behaviour);
                bool foundOnUdon = false;
                object udonValue = null;
                if (backing != null) foundOnUdon = TryGetPublicVariable(backing, field.Name, out udonValue);

                bool agrees = !foundOnUdon || ValuesAgree(proxyValue, udonValue);
                if (!agrees) mismatches++;

                variables.Add(new Dictionary<string, object>
                {
                    { "name", field.Name },
                    { "type", field.FieldType.FullName },
                    { "synced", IsSynced(field) },
                    { "proxyValue", proxyValue },
                    { "presentOnUdonBehaviour", foundOnUdon },
                    { "udonValue", udonValue },
                    { "agrees", agrees }
                });
            }

            var result = new Dictionary<string, object>
            {
                { "gameObject", HierarchyPath(go.transform) },
                { "type", type.FullName },
                { "hasBackingUdonBehaviour", backing != null },
                { "mismatchCount", mismatches },
                { "variables", variables }
            };
            if (mismatches > 0)
                result["hint"] = "Proxy and UdonBehaviour disagree, which means the values were never copied. " +
                                 "Call udon.set_fields (it runs CopyProxyToUdon) rather than writing the component directly.";
            return result;
        }

        private static bool ValuesAgree(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a is UnityEngine.Object || b is UnityEngine.Object) return Equals(a, b);
            if (a is float && b is float) return Mathf.Approximately((float)a, (float)b);
            return Equals(a, b) || string.Equals(Convert.ToString(a), Convert.ToString(b), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ listing

        private static Dictionary<string, object> ListBehaviours()
        {
            var baseType = UdonReflection.Resolve("UdonSharp.UdonSharpBehaviour");
            var found = new List<object>();

            for (int s = 0; s < EditorSceneManager.sceneCount; s++)
            {
                var scene = EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var component in root.GetComponentsInChildren(baseType, true))
                    {
                        var mb = component as MonoBehaviour;
                        if (mb == null) continue;
                        var backing = GetBackingUdonBehaviour(mb);
                        found.Add(new Dictionary<string, object>
                        {
                            { "gameObject", HierarchyPath(mb.transform) },
                            { "scene", scene.name },
                            { "type", mb.GetType().FullName },
                            { "enabled", mb.enabled },
                            { "hasBackingUdonBehaviour", backing != null },
                            { "programAssetLinked", backing != null && GetProgramSource(backing) != null }
                        });
                    }
                }
            }

            return new Dictionary<string, object> { { "count", found.Count }, { "behaviours", found } };
        }

        // ------------------------------------------------------------------ udon plumbing

        private static Component GetBackingUdonBehaviour(MonoBehaviour proxy)
        {
            if (proxy == null) return null;
            var utility = UdonReflection.Resolve("UdonSharpEditor.UdonSharpEditorUtility");
            var proxyType = UdonReflection.Resolve("UdonSharp.UdonSharpBehaviour");
            var get = utility.GetMethod("GetBackingUdonBehaviour", BindingFlags.Static | BindingFlags.Public,
                null, new[] { proxyType }, null);
            if (get == null) return null;
            try { return get.Invoke(null, new object[] { proxy }) as Component; }
            catch (TargetInvocationException) { return null; }
        }

        private static object GetProgramSource(Component udonBehaviour)
        {
            var field = udonBehaviour.GetType().GetField("programSource",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(udonBehaviour);
            var property = udonBehaviour.GetType().GetProperty("programSource",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property == null ? null : property.GetValue(udonBehaviour, null);
        }

        /// <summary>
        /// Reads a value from the backing UdonBehaviour's publicVariables table. At edit time this is the
        /// authoritative store - GetProgramVariable only works once the program has been initialised.
        /// </summary>
        private static bool TryGetPublicVariable(Component udonBehaviour, string symbol, out object value)
        {
            value = null;
            var table = GetMemberValue(udonBehaviour, "publicVariables");
            if (table == null) return false;

            var tryGet = table.GetType().GetMethod("TryGetVariableValue", new[] { typeof(string), typeof(object).MakeByRefType() });
            if (tryGet == null)
            {
                foreach (var candidate in table.GetType().GetMethods())
                {
                    if (candidate.Name != "TryGetVariableValue" || candidate.IsGenericMethod) continue;
                    var ps = candidate.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].IsOut) { tryGet = candidate; break; }
                }
            }
            if (tryGet == null) return false;

            var args = new object[] { symbol, null };
            try
            {
                bool ok = (bool)tryGet.Invoke(table, args);
                value = args[1];
                return ok;
            }
            catch (Exception) { return false; }
        }

        private static object GetMemberValue(object instance, string name)
        {
            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                try { return property.GetValue(instance, null); }
                catch (Exception) { return null; }
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field == null ? null : field.GetValue(instance);
        }

        public static string HierarchyPath(Transform tr)
        {
            var parts = new List<string>();
            while (tr != null) { parts.Add(tr.name); tr = tr.parent; }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
