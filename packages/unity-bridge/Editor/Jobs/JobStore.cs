using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Long-running work as pollable jobs.
    ///
    /// The operations this toolchain exists to drive — UdonSharp compiles, world builds, lightmap
    /// bakes, multi-client tests — run for minutes. A synchronous RPC would blow every client timeout,
    /// and worse, blocking the main thread would stall the very <see cref="EditorApplication.update"/>
    /// loop that the SDK's async build needs in order to make progress. So jobs are coroutines stepped
    /// by the update pump, and clients poll.
    ///
    /// Every state change is journalled to disk. That is what makes a job legible after a domain reload
    /// or an editor crash: the record still says what was running and how far it got.
    ///
    /// Job bodies are IEnumerators stepped once per editor update, so <c>yield return null</c> means
    /// "resume next frame" exactly as in a Unity coroutine.
    /// </summary>
    [InitializeOnLoad]
    public static class JobStore
    {
        private const double MaxMillisecondsPerTick = 12.0;
        private const int MaxTrackedJobs = 200;

        private static readonly Dictionary<string, JobRecord> Jobs = new Dictionary<string, JobRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Func<Dictionary<string, object>, JobContext, IEnumerator>> Kinds =
            new Dictionary<string, Func<Dictionary<string, object>, JobContext, IEnumerator>>(StringComparer.Ordinal);
        private static readonly List<RunningJob> Running = new List<RunningJob>();
        private static readonly object Gate = new object();

        static JobStore()
        {
            ReconcileInterruptedJobs();
            EditorApplication.update -= Step;
            EditorApplication.update += Step;
        }

        // ---------- kinds ----------

        public static void RegisterKind(string kind, Func<Dictionary<string, object>, JobContext, IEnumerator> factory)
        {
            lock (Gate) Kinds[kind] = factory;
        }

        public static List<string> KindNames
        {
            get
            {
                lock (Gate)
                {
                    var names = new List<string>(Kinds.Keys);
                    names.Sort(StringComparer.Ordinal);
                    return names;
                }
            }
        }

        // ---------- api ----------

        public static JobRecord Submit(string kind, Dictionary<string, object> args)
        {
            Func<Dictionary<string, object>, JobContext, IEnumerator> factory;
            lock (Gate)
            {
                if (!Kinds.TryGetValue(kind, out factory))
                    throw new ArgumentException("Unknown job kind: " + kind + ". Known kinds: " + string.Join(", ", KindNames.ToArray()));
            }

            var record = new JobRecord
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 12),
                kind = kind,
                state = "queued",
                progress = 0f,
                status = "queued",
                createdUtc = DateTime.UtcNow.ToString("o"),
                updatedUtc = DateTime.UtcNow.ToString("o")
            };

            var context = new JobContext(record);
            var pending = new RunningJob { Record = record, Context = context, Args = args, Factory = factory };

            lock (Gate)
            {
                Jobs[record.id] = record;
                Running.Add(pending);
                TrimTrackedLocked();
            }

            Persist(record);
            return record;
        }

        public static JobRecord Get(string id)
        {
            lock (Gate)
            {
                JobRecord record;
                if (Jobs.TryGetValue(id, out record)) return record;
            }
            return LoadFromDisk(id);
        }

        public static List<JobRecord> List(int limit)
        {
            var all = new List<JobRecord>();
            lock (Gate) all.AddRange(Jobs.Values);

            // Jobs from before the last domain reload only exist on disk.
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < all.Count; i++) seen.Add(all[i].id);
                foreach (string path in Directory.GetFiles(BridgePaths.JobDir, "*.json"))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    if (seen.Contains(id)) continue;
                    var record = LoadFromDisk(id);
                    if (record != null) all.Add(record);
                }
            }
            catch (Exception) { }

            all.Sort(delegate (JobRecord a, JobRecord b) { return string.CompareOrdinal(b.createdUtc, a.createdUtc); });
            if (limit > 0 && all.Count > limit) all.RemoveRange(limit, all.Count - limit);
            return all;
        }

        public static bool Cancel(string id)
        {
            lock (Gate)
            {
                for (int i = 0; i < Running.Count; i++)
                {
                    if (Running[i].Record.id != id) continue;
                    Running[i].Context.RequestCancel();
                    return true;
                }
            }
            return false;
        }

        // ---------- pump ----------

        /// <summary>
        /// Advances every running job by exactly one step per editor update.
        ///
        /// One step per frame is the contract, so `yield return null` inside a job means "resume next
        /// editor frame", matching Unity's coroutine convention. An earlier version drained each job as
        /// many times as fit in a time budget, which broke that contract badly: a job whose whole
        /// purpose is to wait for EditorApplication.isCompiling to go false would busy-spin through
        /// thousands of iterations per frame instead of waiting. CPU-bound jobs should do more work per
        /// step rather than relying on being stepped repeatedly.
        /// </summary>
        private static void Step()
        {
            RunningJob[] snapshot;
            lock (Gate)
            {
                if (Running.Count == 0) return;
                snapshot = Running.ToArray();
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].Finished) continue;
                StepOne(snapshot[i]);

                // With many concurrent jobs, still yield back to the editor rather than letting one
                // frame's worth of job work stall the update loop the SDK's async build depends on.
                if (sw.Elapsed.TotalMilliseconds > MaxMillisecondsPerTick) break;
            }

            lock (Gate) Running.RemoveAll(delegate (RunningJob r) { return r.Finished; });
        }

        private static void StepOne(RunningJob job)
        {
            try
            {
                if (job.Enumerator == null)
                {
                    job.Record.state = "running";
                    job.Record.startedUtc = DateTime.UtcNow.ToString("o");
                    job.Enumerator = job.Factory(job.Args, job.Context);
                    Touch(job.Record);
                }

                if (job.Context.CancelRequested)
                {
                    Finish(job, "cancelled", null, "cancelled by client");
                    return;
                }

                if (!job.Enumerator.MoveNext())
                {
                    Finish(job, "succeeded", job.Context.Result, null);
                    return;
                }

                // A job that only needs a progress ping still yields; persist at most once a second so
                // a chatty job does not turn into a write storm.
                if (job.Context.ConsumeDirty()) Persist(job.Record);
            }
            catch (Exception ex)
            {
                Finish(job, "failed", null, ex.GetType().Name + ": " + ex.Message, ex.StackTrace);
            }
        }

        private static void Finish(RunningJob job, string state, object result, string error, string stack = null)
        {
            job.Finished = true;
            job.Record.state = state;
            job.Record.result = result;
            job.Record.error = error;
            job.Record.errorStack = stack;
            job.Record.progress = state == "succeeded" ? 1f : job.Record.progress;
            job.Record.finishedUtc = DateTime.UtcNow.ToString("o");
            Touch(job.Record);
        }

        internal static void Touch(JobRecord record)
        {
            record.updatedUtc = DateTime.UtcNow.ToString("o");
            Persist(record);
        }

        // ---------- persistence ----------

        private static void Persist(JobRecord record)
        {
            try
            {
                string path = Path.Combine(BridgePaths.JobDir, record.id + ".json");
                File.WriteAllText(path, Json.Write(record, 8), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static JobRecord LoadFromDisk(string id)
        {
            try
            {
                string path = Path.Combine(BridgePaths.JobDir, id + ".json");
                if (!File.Exists(path)) return null;
                var o = Json.AsObject(Json.Parse(File.ReadAllText(path)));
                object result, progress;
                o.TryGetValue("result", out result);
                o.TryGetValue("progress", out progress);
                return new JobRecord
                {
                    id = Json.GetString(o, "id", id),
                    kind = Json.GetString(o, "kind"),
                    state = Json.GetString(o, "state"),
                    status = Json.GetString(o, "status"),
                    progress = progress is double ? (float)(double)progress : 0f,
                    result = result,
                    error = Json.GetString(o, "error"),
                    errorStack = Json.GetString(o, "errorStack"),
                    createdUtc = Json.GetString(o, "createdUtc"),
                    startedUtc = Json.GetString(o, "startedUtc"),
                    finishedUtc = Json.GetString(o, "finishedUtc"),
                    updatedUtc = Json.GetString(o, "updatedUtc")
                };
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// A job that was mid-flight when the domain reloaded has lost its enumerator; nothing will
        /// ever advance it. Mark it so explicitly instead of leaving a record that claims to be
        /// running forever — the client can then decide whether to resubmit.
        /// </summary>
        private static void ReconcileInterruptedJobs()
        {
            try
            {
                foreach (string path in Directory.GetFiles(BridgePaths.JobDir, "*.json"))
                {
                    var record = LoadFromDisk(Path.GetFileNameWithoutExtension(path));
                    if (record == null) continue;
                    if (record.state != "running" && record.state != "queued") continue;
                    record.state = "interrupted";
                    record.error = "Editor domain reloaded or restarted while this job was " + record.state + "; it cannot resume.";
                    record.finishedUtc = DateTime.UtcNow.ToString("o");
                    record.updatedUtc = record.finishedUtc;
                    Persist(record);
                }
            }
            catch (Exception) { }
        }

        private static void TrimTrackedLocked()
        {
            if (Jobs.Count <= MaxTrackedJobs) return;
            var finished = new List<JobRecord>();
            foreach (var record in Jobs.Values)
            {
                if (record.state != "running" && record.state != "queued") finished.Add(record);
            }
            finished.Sort(delegate (JobRecord a, JobRecord b) { return string.CompareOrdinal(a.createdUtc, b.createdUtc); });
            int toDrop = Jobs.Count - MaxTrackedJobs;
            for (int i = 0; i < finished.Count && i < toDrop; i++) Jobs.Remove(finished[i].id);
        }

        private sealed class RunningJob
        {
            public JobRecord Record;
            public JobContext Context;
            public Dictionary<string, object> Args;
            public Func<Dictionary<string, object>, JobContext, IEnumerator> Factory;
            public IEnumerator Enumerator;
            public bool Finished;
        }
    }

    public sealed class JobRecord
    {
        public string id;
        public string kind;
        public string state;
        public string status;
        public float progress;
        public object result;
        public string error;
        public string errorStack;
        public string createdUtc;
        public string startedUtc;
        public string finishedUtc;
        public string updatedUtc;
    }

    /// <summary>Handed to a job body so it can report progress, check for cancellation, and set a result.</summary>
    public sealed class JobContext
    {
        private readonly JobRecord _record;
        private volatile bool _cancelRequested;
        private DateTime _lastPersistUtc = DateTime.MinValue;
        private bool _dirty;

        internal JobContext(JobRecord record) { _record = record; }

        public object Result { get; private set; }
        public bool CancelRequested { get { return _cancelRequested; } }

        internal void RequestCancel() { _cancelRequested = true; }

        public void Report(float progress, string status)
        {
            _record.progress = Mathf.Clamp01(progress);
            if (!string.IsNullOrEmpty(status)) _record.status = status;
            _dirty = true;
        }

        public void SetResult(object result) { Result = result; }

        /// <summary>True at most once a second, so progress reporting cannot turn into a write storm.</summary>
        internal bool ConsumeDirty()
        {
            if (!_dirty) return false;
            if ((DateTime.UtcNow - _lastPersistUtc).TotalSeconds < 1.0) return false;
            _dirty = false;
            _lastPersistUtc = DateTime.UtcNow;
            return true;
        }
    }
}
