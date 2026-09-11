using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using UnityEditor;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Compiles and runs a C# snippet inside the live editor.
    ///
    /// This one method is deliberately the escape hatch for everything the typed tools do not cover.
    /// Hundreds of narrow "set this field" tools would burn far more agent context than a single
    /// general one, and the SDK surface an agent needs is mostly static calls into assemblies that are
    /// already loaded in this domain.
    ///
    /// Uses the Mono CodeDom provider rather than Roslyn: VRChat forces
    /// ApiCompatibilityLevel.NET_4_6 (EnvConfig.cs:241), and UdonSharp's bundled Roslyn DLLs are
    /// marked isExplicitlyReferenced, which would force this assembly into overrideReferences and make
    /// us re-declare every SDK DLL by hand.
    /// </summary>
    public static class CSharpEval
    {
        private const string WrapperType = "__AgenticEval";
        private const int MaxReportedWarnings = 20;

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.IO",
            "System.Reflection",
            "UnityEngine",
            "UnityEngine.SceneManagement",
            "UnityEditor",
            "UnityEditor.SceneManagement"
        };

        public static Dictionary<string, object> Run(string code, List<string> extraUsings, int maxDepth, bool includeReferences = false)
        {
            long logCursor = LogCapture.LatestSeq;

            string source = BuildSource(code, extraUsings);
            var started = DateTime.UtcNow;
            List<string> references = CollectReferences();

            CompilerResults results;
            try
            {
                using (var provider = new CSharpCodeProvider())
                {
                    var parameters = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false,
                        IncludeDebugInformation = false,
                        // /noconfig /nostdlib+ are load-bearing. Left to itself, mcs adds its own
                        // default profile (mscorlib, System, System.Core, System.Xml from
                        // MonoBleedingEdge/lib/mono/4.5) on top of the ones we pass from the editor's
                        // unityjit-win32 profile. Every core type then exists twice and the compile dies
                        // with CS0433 on System.Object, AppDomain, Dictionary and friends. So: no
                        // implicit anything, CollectReferences supplies the complete set.
                        //
                        // 0162 unreachable: the wrapper appends a trailing `return null;`, unreachable
                        // whenever the snippet already returns on every path.
                        CompilerOptions = "/noconfig /nostdlib+ /nowarn:0162,0168,0219,0414,1685"
                    };
                    foreach (string reference in references) parameters.ReferencedAssemblies.Add(reference);
                    results = provider.CompileAssemblyFromSource(parameters, source);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "No usable C# compiler in this editor (" + ex.GetType().Name + ": " + ex.Message + "). " +
                    "Use unity.invoke_static / unity.menu_item instead, which need no compiler.", ex);
            }

            // Errors are always reported; warnings are capped so a noisy compile cannot crowd the real
            // message out of the response.
            var diagnostics = new List<object>();
            bool hasError = false;
            int warningsSuppressed = 0;
            int warningsKept = 0;
            foreach (CompilerError err in results.Errors)
            {
                if (err.IsWarning)
                {
                    if (warningsKept >= MaxReportedWarnings) { warningsSuppressed++; continue; }
                    warningsKept++;
                }
                else hasError = true;

                diagnostics.Add(new Dictionary<string, object>
                {
                    { "severity", err.IsWarning ? "warning" : "error" },
                    { "code", err.ErrorNumber },
                    { "line", TranslateLine(err.Line, extraUsings) },
                    { "column", err.Column },
                    { "message", err.ErrorText }
                });
            }

            var common = new Dictionary<string, object>
            {
                { "diagnostics", diagnostics },
                { "warningsSuppressed", warningsSuppressed },
                { "referenceCount", references.Count },
                { "logs", LogCapture.Read(logCursor, 200, "all", null) }
            };
            if (includeReferences) common["references"] = references;

            if (hasError)
            {
                var failure = new Dictionary<string, object>(common)
                {
                    { "compiled", false },
                    { "source", source }
                };
                return failure;
            }

            var type = results.CompiledAssembly.GetType(WrapperType);
            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);

            object value;
            try { value = method.Invoke(null, null); }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                var threw = new Dictionary<string, object>(common)
                {
                    { "compiled", true },
                    { "threw", true },
                    { "exception", inner.GetType().FullName },
                    { "message", inner.Message },
                    { "stack", inner.StackTrace }
                };
                threw["logs"] = LogCapture.Read(logCursor, 200, "all", null);
                return threw;
            }

            var ok = new Dictionary<string, object>(common)
            {
                { "compiled", true },
                { "threw", false },
                { "value", value },
                { "valueType", value == null ? null : value.GetType().FullName },
                { "elapsedMs", (DateTime.UtcNow - started).TotalMilliseconds }
            };
            // Snippets mostly communicate through Debug.Log, so re-reading the log delta after the call
            // makes a single round trip enough to see what happened.
            ok["logs"] = LogCapture.Read(logCursor, 200, "all", null);
            return ok;
        }

        private static string BuildSource(string code, List<string> extraUsings)
        {
            var sb = new StringBuilder(512);
            foreach (string u in DefaultUsings) sb.Append("using ").Append(u).Append(";\n");
            if (extraUsings != null)
            {
                foreach (string u in extraUsings)
                {
                    if (string.IsNullOrEmpty(u)) continue;
                    string trimmed = u.Trim().TrimEnd(';');
                    if (trimmed.StartsWith("using ")) trimmed = trimmed.Substring(6).Trim();
                    sb.Append("using ").Append(trimmed).Append(";\n");
                }
            }

            sb.Append("public static class ").Append(WrapperType).Append(" {\n");
            sb.Append("  public static object Run() {\n");

            // Bare-expression ergonomics: "Selection.activeGameObject" is far more natural for an agent
            // to write than "return Selection.activeGameObject;".
            string body = code.Trim();
            if (body.IndexOf(';') < 0 && !body.StartsWith("return", StringComparison.Ordinal))
                sb.Append("    return (").Append(body).Append(");\n");
            else
                sb.Append(body).Append('\n');

            sb.Append("    return null;\n");
            sb.Append("  }\n}\n");
            return sb.ToString();
        }

        /// <summary>Maps a compiler line back to a 1-based line of the caller's own snippet.</summary>
        private static int TranslateLine(int generatedLine, List<string> extraUsings)
        {
            int prologue = DefaultUsings.Length + (extraUsings != null ? extraUsings.Count : 0) + 2;
            int translated = generatedLine - prologue;
            return translated > 0 ? translated : generatedLine;
        }

        /// <summary>
        /// Builds the reference set from the live domain, minus the reference-only assemblies.
        ///
        /// Referencing the whole domain naively does not work. Unity's editor domain has both the Mono
        /// BCL (mscorlib, System) and .NET Standard reference assemblies loaded, and the netstandard ref
        /// assembly contains real definitions rather than type forwards. Handing the compiler both makes
        /// every core type ambiguous:
        ///     error CS0433: The imported type `System.AppDomain' is defined multiple times
        /// plus about a hundred CS1685 warnings. So reference-only trees are excluded, and the remainder
        /// is deduplicated by simple assembly name.
        /// </summary>
        private static List<string> CollectReferences()
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            string mscorlibPath = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string location;
                string simpleName;
                try
                {
                    if (asm.IsDynamic) continue;
                    location = asm.Location;
                    simpleName = asm.GetName().Name;
                }
                catch (Exception) { continue; }

                if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(simpleName)) continue;
                if (IsReferenceOnlyAssembly(simpleName, location)) continue;
                if (byName.ContainsKey(simpleName)) continue;

                byName.Add(simpleName, location);
                ordered.Add(location);
                if (string.Equals(simpleName, "mscorlib", StringComparison.OrdinalIgnoreCase)) mscorlibPath = location;
            }

            if (!byName.ContainsKey("netstandard"))
            {
                string facade = FindNetStandardFacade(mscorlibPath);
                if (facade != null)
                {
                    byName.Add("netstandard", facade);
                    ordered.Add(facade);
                }
            }

            return ordered;
        }

        /// <summary>
        /// Finds the type-forwarding netstandard facade that sits beside the BCL profile in use.
        ///
        /// It is needed because VRChat and UdonSharp ship NuGet-built, netstandard-targeting assemblies
        /// (System.Collections.Immutable, System.Reflection.Metadata, Newtonsoft.Json...). Referencing
        /// those without netstandard fails with
        ///     error CS0012: The type `System.Object' is defined in an assembly that is not referenced
        /// The facade is the right answer rather than Data/NetStandard/ref/2.1.0/netstandard.dll: the
        /// facade is ~90KB of TypeForwardedTo entries and so cannot collide with mscorlib, whereas the
        /// 1.5MB reference assembly carries real definitions and reintroduces CS0433.
        /// </summary>
        private static string FindNetStandardFacade(string mscorlibPath)
        {
            if (!string.IsNullOrEmpty(mscorlibPath))
            {
                string sibling = Path.Combine(Path.Combine(Path.GetDirectoryName(mscorlibPath), "Facades"), "netstandard.dll");
                if (File.Exists(sibling)) return sibling;
            }

            // Fall back to the editor's Mono profiles if mscorlib was not among the loaded assemblies.
            try
            {
                string monoLib = Path.Combine(EditorApplication.applicationContentsPath, "MonoBleedingEdge/lib/mono");
                if (Directory.Exists(monoLib))
                {
                    foreach (string profile in new[] { "unityjit-win32", "4.7.1-api", "4.8-api", "4.5" })
                    {
                        string candidate = Path.Combine(monoLib, profile + "/Facades/netstandard.dll");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private static bool IsReferenceOnlyAssembly(string simpleName, string location)
        {
            // Data/NetStandard/ref and /compat hold reference assemblies with real type definitions;
            // including them alongside mscorlib makes every core type ambiguous.
            string normalized = location.Replace('\\', '/');
            return normalized.IndexOf("/NetStandard/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
