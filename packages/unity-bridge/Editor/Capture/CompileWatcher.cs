using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Structured C# compile diagnostics.
    ///
    /// This is a prerequisite for UdonSharp work, not a convenience: UdonSharpCompilerV1 checks
    /// DoesUnityProjectHaveCompileErrors() and then *silently returns doing nothing*
    /// (UdonSharpCompilerV1.cs:272). An agent that cannot see plain C# errors would see a U# compile
    /// that reports neither success nor failure.
    ///
    /// Results are persisted because compilation ends with a domain reload, which wipes the in-memory
    /// collection right when the answer becomes interesting.
    /// </summary>
    [InitializeOnLoad]
    public static class CompileWatcher
    {
        private static readonly List<Diagnostic> Collected = new List<Diagnostic>();
        private static readonly object Gate = new object();

        static CompileWatcher()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public sealed class Diagnostic
        {
            public string severity;
            public string assembly;
            public string file;
            public int line;
            public int column;
            public string message;
        }

        private static void OnCompilationStarted(object _)
        {
            lock (Gate) Collected.Clear();
            Persist("compiling", null);
        }

        private static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

            lock (Gate)
            {
                for (int i = 0; i < messages.Length; i++)
                {
                    var m = messages[i];
                    // Info-level compiler messages are noise for an agent; keep errors and warnings.
                    if (m.type != CompilerMessageType.Error && m.type != CompilerMessageType.Warning) continue;
                    Collected.Add(new Diagnostic
                    {
                        severity = m.type == CompilerMessageType.Error ? "error" : "warning",
                        assembly = assemblyName,
                        file = NormalizePath(m.file),
                        line = m.line,
                        column = m.column,
                        message = m.message
                    });
                }
            }
        }

        private static void OnCompilationFinished(object _)
        {
            Persist("idle", DateTime.UtcNow);
        }

        private static void Persist(string phase, DateTime? finishedUtc)
        {
            try
            {
                Diagnostic[] snapshot;
                lock (Gate) snapshot = Collected.ToArray();

                int errors = 0, warnings = 0;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (snapshot[i].severity == "error") errors++; else warnings++;
                }

                var payload = new Dictionary<string, object>
                {
                    { "phase", phase },
                    { "errorCount", errors },
                    { "warningCount", warnings },
                    { "diagnostics", snapshot },
                    { "updatedUtc", DateTime.UtcNow.ToString("o") }
                };
                if (finishedUtc.HasValue) payload["finishedUtc"] = finishedUtc.Value.ToString("o");

                File.WriteAllText(BridgePaths.CompileStateJson, Json.Write(payload, 5), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// <summary>Live editor state plus the diagnostics from the most recent compile.</summary>
        public static Dictionary<string, object> Read()
        {
            var result = new Dictionary<string, object>
            {
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdatingAssets", EditorApplication.isUpdating },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode },
                { "scriptCompilationFailed", EditorUtility.scriptCompilationFailed }
            };

            Diagnostic[] live;
            lock (Gate) live = Collected.ToArray();

            if (live.Length > 0)
            {
                int errors = 0, warnings = 0;
                for (int i = 0; i < live.Length; i++) { if (live[i].severity == "error") errors++; else warnings++; }
                result["errorCount"] = errors;
                result["warningCount"] = warnings;
                result["diagnostics"] = live;
                result["source"] = "live";
                return result;
            }

            // Nothing in memory means this domain has not compiled anything yet — the interesting
            // diagnostics are from the compile that caused this very reload.
            try
            {
                if (File.Exists(BridgePaths.CompileStateJson))
                {
                    var persisted = Json.AsObject(Json.Parse(File.ReadAllText(BridgePaths.CompileStateJson)));
                    result["errorCount"] = Json.GetInt(persisted, "errorCount", 0);
                    result["warningCount"] = Json.GetInt(persisted, "warningCount", 0);
                    object diags;
                    result["diagnostics"] = persisted.TryGetValue("diagnostics", out diags) ? diags : new List<object>();
                    result["finishedUtc"] = Json.GetString(persisted, "finishedUtc");
                    result["source"] = "persisted";
                    return result;
                }
            }
            catch (Exception) { }

            result["errorCount"] = 0;
            result["warningCount"] = 0;
            result["diagnostics"] = new List<object>();
            result["source"] = "empty";
            return result;
        }

        private static string NormalizePath(string p)
        {
            return string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/');
        }
    }
}
