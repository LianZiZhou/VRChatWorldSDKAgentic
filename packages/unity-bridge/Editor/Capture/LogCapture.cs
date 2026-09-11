using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Console capture for the agent.
    ///
    /// Unity's own console is only readable through internal UnityEditor.LogEntries reflection, and it
    /// is cleared by domain reloads and by "Clear on Play". An agent needs a stable, monotonically
    /// numbered stream it can poll with "give me everything after seq N", so the bridge keeps its own:
    /// an in-memory ring for fast reads plus a JSONL file that survives reloads and crashes.
    /// </summary>
    [InitializeOnLoad]
    public static class LogCapture
    {
        private const int RingCapacity = 4000;
        private const long MaxFileBytes = 16L * 1024 * 1024;
        private const int MaxMessageChars = 8000;

        private static readonly object Gate = new object();
        private static readonly List<Entry> Ring = new List<Entry>(RingCapacity);
        private static readonly List<Entry> PendingFlush = new List<Entry>(256);
        private static long _seq;
        private static bool _seqLoaded;

        static LogCapture()
        {
            LoadSeq();
            Subscribe();

            EditorApplication.update -= Flush;
            EditorApplication.update += Flush;
            AssemblyReloadEvents.beforeAssemblyReload -= Flush;
            AssemblyReloadEvents.beforeAssemblyReload += Flush;

            // Re-subscribe around play mode. Unity clears Application.logMessageReceivedThreaded handlers
            // when play mode ends, and with domain reload disabled — which ClientSim verification requires —
            // this static constructor never runs again to restore them. Without this, console capture dies
            // silently after the first play-mode session and stays dead, which is the worst kind of
            // observability failure: the logs simply stop and nothing says so.
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void Subscribe()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            Flush();
            Subscribe();
        }

        /// <summary>Highest sequence number issued so far. Callers snapshot this, do work, then ask for
        /// everything after it — that is how eval_csharp returns just its own log output.</summary>
        public static long LatestSeq
        {
            get { lock (Gate) return _seq; }
        }

        public sealed class Entry
        {
            public long seq;
            public string t;
            public string type;
            public string msg;
            public string stack;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            // Called from arbitrary threads, so this does nothing but append under a lock. File IO
            // happens on the main thread in Flush.
            var entry = new Entry
            {
                t = DateTime.UtcNow.ToString("o"),
                type = type.ToString(),
                msg = Truncate(condition, MaxMessageChars),
                stack = type == LogType.Log ? null : Truncate(stackTrace, MaxMessageChars)
            };

            lock (Gate)
            {
                entry.seq = ++_seq;
                Ring.Add(entry);
                if (Ring.Count > RingCapacity) Ring.RemoveRange(0, Ring.Count - RingCapacity);
                PendingFlush.Add(entry);
            }
        }

        private static void Flush()
        {
            Entry[] batch;
            long highestSeq;
            lock (Gate)
            {
                if (PendingFlush.Count == 0) return;
                batch = PendingFlush.ToArray();
                PendingFlush.Clear();
                highestSeq = _seq;
            }

            try
            {
                RotateIfNeeded();
                var sb = new StringBuilder(batch.Length * 160);
                for (int i = 0; i < batch.Length; i++) sb.Append(Json.Write(batch[i], 3)).Append('\n');
                File.AppendAllText(BridgePaths.EditorLogJsonl, sb.ToString(), new UTF8Encoding(false));
                File.WriteAllText(SeqFile, highestSeq.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Losing log persistence must never break the editor or re-enter Debug.Log (which
                // would recurse straight back into OnLog).
            }
        }

        private static string SeqFile { get { return Path.Combine(BridgePaths.LogDir, "editor.seq"); } }

        private static void LoadSeq()
        {
            if (_seqLoaded) return;
            _seqLoaded = true;
            try
            {
                if (File.Exists(SeqFile))
                {
                    long parsed;
                    if (long.TryParse(File.ReadAllText(SeqFile).Trim(), out parsed)) _seq = parsed;
                }
            }
            catch (Exception) { }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(BridgePaths.EditorLogJsonl);
                if (!fi.Exists || fi.Length < MaxFileBytes) return;
                string rotated = Path.Combine(BridgePaths.LogDir, "editor.1.jsonl");
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(BridgePaths.EditorLogJsonl, rotated);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Entries with seq &gt; sinceSeq, newest-biased: when more than <paramref name="limit"/> match,
        /// the most recent ones win, because that is what a debugging agent actually needs.
        /// </summary>
        public static Dictionary<string, object> Read(long sinceSeq, int limit, string levelFilter, string grep)
        {
            Flush();

            List<Entry> source;
            bool fromRing;
            lock (Gate)
            {
                fromRing = Ring.Count == 0 || Ring[0].seq <= sinceSeq + 1;
                source = fromRing ? new List<Entry>(Ring) : null;
            }
            if (source == null) source = ReadFromFile();

            var matched = new List<Entry>();
            for (int i = 0; i < source.Count; i++)
            {
                var e = source[i];
                if (e.seq <= sinceSeq) continue;
                if (!LevelMatches(e.type, levelFilter)) continue;
                if (!string.IsNullOrEmpty(grep) &&
                    (e.msg == null || e.msg.IndexOf(grep, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                matched.Add(e);
            }

            int dropped = 0;
            if (limit > 0 && matched.Count > limit)
            {
                dropped = matched.Count - limit;
                matched.RemoveRange(0, dropped);
            }

            long latest;
            lock (Gate) latest = _seq;

            return new Dictionary<string, object>
            {
                { "entries", matched },
                { "latestSeq", latest },
                { "returned", matched.Count },
                { "droppedOlder", dropped },
                { "source", fromRing ? "ring" : "file" }
            };
        }

        private static bool LevelMatches(string type, string filter)
        {
            if (string.IsNullOrEmpty(filter) || filter == "all") return true;
            if (filter == "error") return type == "Error" || type == "Exception" || type == "Assert";
            if (filter == "warning") return type == "Warning" || type == "Error" || type == "Exception" || type == "Assert";
            return string.Equals(type, filter, StringComparison.OrdinalIgnoreCase);
        }

        private static List<Entry> ReadFromFile()
        {
            var result = new List<Entry>();
            foreach (string path in new[] { Path.Combine(BridgePaths.LogDir, "editor.1.jsonl"), BridgePaths.EditorLogJsonl })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (line.Length == 0) continue;
                        var o = Json.AsObject(Json.Parse(line));
                        result.Add(new Entry
                        {
                            seq = Json.GetLong(o, "seq", 0),
                            t = Json.GetString(o, "t"),
                            type = Json.GetString(o, "type"),
                            msg = Json.GetString(o, "msg"),
                            stack = Json.GetString(o, "stack")
                        });
                    }
                }
                catch (Exception) { }
            }
            return result;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...[truncated " + (s.Length - max) + " chars]";
        }
    }
}
