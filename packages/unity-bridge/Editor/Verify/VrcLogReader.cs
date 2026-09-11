using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Reads the real VRChat client's logs.
    ///
    /// This is the only observability channel into a world actually running in VRChat. Udon cannot phone
    /// home: VRCUrl is not constructible at runtime, so a world cannot POST its state anywhere. What it can
    /// do is Debug.Log, and those lines reach
    ///   %USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt
    /// when the client is launched with --enable-udon-debug-logging.
    ///
    /// So the contract for automated verification against a real client is: the world logs tagged lines,
    /// and this parses them back out. [AGENTTEL]{json} is that tag.
    /// </summary>
    public static class VrcLogReader
    {
        public const string TelemetryTag = "[AGENTTEL]";

        /// <summary>Launch flags a client needs before any of this produces data.</summary>
        public static readonly string[] RequiredLaunchFlags =
        {
            "--enable-udon-debug-logging",
            "--enable-sdk-log-levels",
            "--enable-debug-gui",
        };

        private static readonly Regex UdonExceptionPattern = new Regex(
            @"\[UdonBehaviour\].*exception", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TelemetryPattern = new Regex(
            @"\[AGENTTEL\]\s*(\{.*\})", RegexOptions.Compiled);

        public static void Register()
        {
            BridgeRouter.Register("verify.vrc_log_dir",
                "Where the VRChat client writes its logs, and which log files exist.",
                delegate (Dictionary<string, object> a) { return Describe(); }, requiresMainThread: false);

            BridgeRouter.Register("verify.read_vrc_logs",
                "Parse VRChat client logs for Udon exceptions, Debug.Log output and [AGENTTEL] telemetry. " +
                "Args: maxFiles, maxLines, sinceUtc, grep.",
                delegate (Dictionary<string, object> a) { return Read(a); }, requiresMainThread: false,
                defaultTimeoutMs: 120000);

            BridgeRouter.Register("verify.udon_exceptions",
                "Udon runtime exceptions from both the Unity console (ClientSim / UdonSharp's exception " +
                "watcher, which maps back to C# file and line) and the VRChat client logs.",
                delegate (Dictionary<string, object> a) { return UdonExceptions(a); }, requiresMainThread: false);
        }

        public static string LogDirectory
        {
            get
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(profile, Path.Combine("AppData", Path.Combine("LocalLow",
                    Path.Combine("VRChat", "VRChat"))));
            }
        }

        private static Dictionary<string, object> Describe()
        {
            string dir = LogDirectory;
            var files = new List<object>();
            bool exists = Directory.Exists(dir);

            if (exists)
            {
                foreach (string path in Directory.GetFiles(dir, "output_log_*.txt"))
                {
                    var info = new FileInfo(path);
                    files.Add(new Dictionary<string, object>
                    {
                        { "file", path.Replace('\\', '/') },
                        { "bytes", info.Length },
                        { "modifiedUtc", info.LastWriteTimeUtc.ToString("o") }
                    });
                }
            }

            files.Sort(delegate (object a, object b)
            {
                var da = (Dictionary<string, object>)a;
                var db = (Dictionary<string, object>)b;
                return string.CompareOrdinal((string)db["modifiedUtc"], (string)da["modifiedUtc"]);
            });

            return new Dictionary<string, object>
            {
                { "directory", dir.Replace('\\', '/') },
                { "exists", exists },
                { "logCount", files.Count },
                { "logs", files },
                { "requiredLaunchFlags", RequiredLaunchFlags },
                { "note", exists
                    ? "Udon Debug.Log lines only appear when the client was launched with --enable-udon-debug-logging."
                    : "No VRChat log directory, so the client has never run on this machine under this user." }
            };
        }

        private static List<FileInfo> NewestLogs(int maxFiles)
        {
            var result = new List<FileInfo>();
            string dir = LogDirectory;
            if (!Directory.Exists(dir)) return result;

            foreach (string path in Directory.GetFiles(dir, "output_log_*.txt")) result.Add(new FileInfo(path));
            result.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
            if (maxFiles > 0 && result.Count > maxFiles) result.RemoveRange(maxFiles, result.Count - maxFiles);
            return result;
        }

        private static Dictionary<string, object> Read(Dictionary<string, object> a)
        {
            int maxFiles = Json.GetInt(a, "maxFiles", 2);
            int maxLines = Json.GetInt(a, "maxLines", 4000);
            string grep = Json.GetString(a, "grep");

            DateTime? since = null;
            string sinceUtc = Json.GetString(a, "sinceUtc");
            if (!string.IsNullOrEmpty(sinceUtc))
            {
                DateTime parsed;
                if (DateTime.TryParse(sinceUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out parsed))
                    since = parsed;
            }

            var exceptions = new List<object>();
            var telemetry = new List<object>();
            var matched = new List<object>();
            var files = new List<object>();
            int scannedLines = 0;

            foreach (var file in NewestLogs(maxFiles))
            {
                if (since.HasValue && file.LastWriteTimeUtc < since.Value) continue;
                files.Add(file.FullName.Replace('\\', '/'));

                foreach (string line in ReadTailLines(file, maxLines))
                {
                    scannedLines++;

                    if (UdonExceptionPattern.IsMatch(line))
                        exceptions.Add(new Dictionary<string, object> { { "file", file.Name }, { "line", Trim(line) } });

                    var telemetryMatch = TelemetryPattern.Match(line);
                    if (telemetryMatch.Success)
                    {
                        var entry = new Dictionary<string, object> { { "file", file.Name }, { "raw", telemetryMatch.Groups[1].Value } };
                        try { entry["json"] = Json.Parse(telemetryMatch.Groups[1].Value); }
                        catch (Exception ex) { entry["parseError"] = ex.Message; }
                        telemetry.Add(entry);
                    }

                    if (!string.IsNullOrEmpty(grep) && line.IndexOf(grep, StringComparison.OrdinalIgnoreCase) >= 0)
                        matched.Add(new Dictionary<string, object> { { "file", file.Name }, { "line", Trim(line) } });
                }
            }

            return new Dictionary<string, object>
            {
                { "directory", LogDirectory.Replace('\\', '/') },
                { "filesScanned", files },
                { "linesScanned", scannedLines },
                { "udonExceptions", exceptions },
                { "telemetry", telemetry },
                { "grepMatches", matched },
                { "telemetryTag", TelemetryTag }
            };
        }

        private static Dictionary<string, object> UdonExceptions(Dictionary<string, object> a)
        {
            // The editor side is the more useful of the two: UdonSharp's runtime exception watcher rewrites
            // an Udon fault into the originating C# file, line and character.
            var editorSide = LogCapture.Read(Json.GetLong(a, "sinceSeq", 0), 200, "error", null);
            var fromEditor = new List<object>();
            var entries = editorSide["entries"] as List<LogCapture.Entry>;
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry.msg == null) continue;
                    if (entry.msg.IndexOf("Udon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    fromEditor.Add(new Dictionary<string, object>
                    {
                        { "seq", entry.seq },
                        { "type", entry.type },
                        { "message", Trim(entry.msg) }
                    });
                }
            }

            var clientSide = Read(new Dictionary<string, object> { { "maxFiles", 2 }, { "maxLines", 4000 } });

            return new Dictionary<string, object>
            {
                { "fromEditor", fromEditor },
                { "fromClientLogs", clientSide["udonExceptions"] },
                { "editorLatestSeq", editorSide["latestSeq"] },
                { "note", "Editor-side entries come from ClientSim or play mode and carry C# line numbers. " +
                          "Client-side entries come from a real VRChat session and need --enable-udon-debug-logging." }
            };
        }

        /// <summary>
        /// Reads at most the last <paramref name="maxLines"/> lines. Logs from a long session reach tens of
        /// megabytes, so the whole file is never pulled into memory.
        /// </summary>
        private static List<string> ReadTailLines(FileInfo file, int maxLines)
        {
            var lines = new List<string>();
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    const int WindowBytes = 4 * 1024 * 1024;
                    if (stream.Length > WindowBytes) stream.Seek(-WindowBytes, SeekOrigin.End);

                    using (var reader = new StreamReader(stream))
                    {
                        // If we seeked, the first line is probably a fragment.
                        if (stream.Position > 0) reader.ReadLine();

                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lines.Add(line);
                            if (lines.Count > maxLines) lines.RemoveAt(0);
                        }
                    }
                }
            }
            catch (Exception) { /* the client holds the file open; partial reads are expected */ }
            return lines;
        }

        private static string Trim(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= 1500 ? value : value.Substring(0, 1500) + "...";
        }
    }
}
