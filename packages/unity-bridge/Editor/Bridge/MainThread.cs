using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEditor;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Marshals work from HttpListener worker threads onto Unity's main thread.
    ///
    /// Nearly every Unity and VRChat SDK API is main-thread-only, and the SDK's build pipeline is
    /// async/UniTask-based so it additionally needs <see cref="EditorApplication.update"/> to keep
    /// ticking. Pumping through update is therefore not an optimisation, it is the only way the
    /// bridge can call into the editor at all.
    ///
    /// A queued item that is never drained means the main thread is wedged — a modal dialog, an
    /// import lock, or a hang. That shows up here as <see cref="MainThreadBusyException"/>, which the
    /// supervisor treats as a distinct signal from "the process died".
    /// </summary>
    [InitializeOnLoad]
    public static class MainThread
    {
        private const int MaxQueueDepth = 256;
        private const double MaxMillisecondsPerTick = 30.0;

        private static readonly Queue<WorkItem> Pending = new Queue<WorkItem>();
        private static readonly object Gate = new object();
        private static int _mainThreadId;
        private static long _tickCount;
        private static DateTime _lastTickUtc;

        static MainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _lastTickUtc = DateTime.UtcNow;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        /// <summary>Ticks since load, and when the pump last ran. Used by bridge.health.</summary>
        public static long TickCount { get { return Interlocked.Read(ref _tickCount); } }

        public static DateTime LastTickUtc { get { lock (Gate) return _lastTickUtc; } }

        public static int QueueDepth { get { lock (Gate) return Pending.Count; } }

        public static bool IsMainThread
        {
            get { return Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and returns its value. Throws
        /// <see cref="MainThreadBusyException"/> if the pump does not get to it within the timeout,
        /// and rethrows whatever <paramref name="work"/> threw otherwise.
        /// </summary>
        public static T Run<T>(Func<T> work, int timeoutMs)
        {
            if (work == null) throw new ArgumentNullException("work");

            // Handlers that are already on the main thread (menu items, tests) must not deadlock
            // waiting for a pump that cannot run until they return.
            if (IsMainThread) return work();

            var item = new WorkItem { Work = delegate { return work(); } };

            lock (Gate)
            {
                if (Pending.Count >= MaxQueueDepth)
                    throw new MainThreadBusyException("Bridge main-thread queue is full (" + Pending.Count + " pending); the editor is not draining work.");
                Pending.Enqueue(item);
            }

            if (!item.Done.Wait(timeoutMs))
            {
                item.Abandoned = true;
                throw new MainThreadBusyException(
                    "Editor did not process the request within " + timeoutMs + "ms. It is most likely compiling, importing, " +
                    "or blocked on a modal dialog. Last pump tick was " + LastTickUtc.ToString("o") + "Z.");
            }

            if (item.Error != null) throw new BridgeHandlerException(item.Error);
            return (T)item.Result;
        }

        public static void Run(Action work, int timeoutMs)
        {
            Run<object>(delegate { work(); return null; }, timeoutMs);
        }

        private static void Pump()
        {
            Interlocked.Increment(ref _tickCount);
            lock (Gate) _lastTickUtc = DateTime.UtcNow;

            var sw = Stopwatch.StartNew();
            while (true)
            {
                WorkItem item;
                lock (Gate)
                {
                    if (Pending.Count == 0) return;
                    item = Pending.Dequeue();
                }

                if (item.Abandoned) continue;

                try { item.Result = item.Work(); }
                catch (Exception ex) { item.Error = ex; }
                finally { item.Done.Set(); }

                // Yield back to the editor regularly. Starving the update loop would block the very
                // async SDK operations the bridge exists to drive.
                if (sw.Elapsed.TotalMilliseconds > MaxMillisecondsPerTick) return;
            }
        }

        private sealed class WorkItem
        {
            public Func<object> Work;
            public object Result;
            public Exception Error;
            public volatile bool Abandoned;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }
    }

    /// <summary>The editor is alive but not draining bridge work.</summary>
    public class MainThreadBusyException : Exception
    {
        public MainThreadBusyException(string message) : base(message) { }
    }

    /// <summary>Wraps an exception thrown by handler code so the original stack survives marshalling.</summary>
    public class BridgeHandlerException : Exception
    {
        public BridgeHandlerException(Exception inner)
            : base(inner.GetType().Name + ": " + inner.Message, inner) { }
    }
}
