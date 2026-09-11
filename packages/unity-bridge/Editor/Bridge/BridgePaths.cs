using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Every path the bridge writes to. Two homes on purpose:
    ///   - per-project state lives in &lt;project&gt;/.agentic/ so it survives domain reloads and is easy to inspect
    ///   - the instance registry lives in LOCALAPPDATA so an MCP client can discover editors without
    ///     being told the project path up front
    /// </summary>
    public static class BridgePaths
    {
        public const string BridgeVersion = "0.1.0";

        /// <summary>Absolute project root (the folder containing Assets/, Packages/, ProjectSettings/).</summary>
        public static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)); }
        }

        public static string ProjectName
        {
            get { return new DirectoryInfo(ProjectRoot).Name; }
        }

        public static string StateDir { get { return EnsureDir(Path.Combine(ProjectRoot, ".agentic")); } }
        public static string LogDir { get { return EnsureDir(Path.Combine(StateDir, "logs")); } }
        public static string JobDir { get { return EnsureDir(Path.Combine(StateDir, "jobs")); } }
        public static string TempDir { get { return EnsureDir(Path.Combine(StateDir, "tmp")); } }

        public static string EditorLogJsonl { get { return Path.Combine(LogDir, "editor.jsonl"); } }
        public static string CompileStateJson { get { return Path.Combine(StateDir, "compile.json"); } }
        public static string LocalBridgeInfo { get { return Path.Combine(StateDir, "bridge.json"); } }

        public static string RegistryDir
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return EnsureDir(Path.Combine(Path.Combine(localAppData, "AgenticVrcw"), "instances"));
            }
        }

        /// <summary>
        /// Registry filename is derived from the project path so that reopening the same project
        /// reuses (and overwrites) its own entry instead of leaking a new file every launch.
        /// </summary>
        public static string RegistryFile
        {
            get { return Path.Combine(RegistryDir, ProjectName + "-" + StableHash(ProjectRoot) + ".json"); }
        }

        public static string StableHash(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(s.Replace('\\', '/').ToLowerInvariant()));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string EnsureDir(string path)
        {
            try { if (!Directory.Exists(path)) Directory.CreateDirectory(path); }
            catch (Exception) { /* a read-only location must not take the bridge down */ }
            return path;
        }
    }
}
