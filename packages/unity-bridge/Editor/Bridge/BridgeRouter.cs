using System;
using System.Collections.Generic;

namespace Agentic.Vrcw.Bridge
{
    public delegate object BridgeHandler(Dictionary<string, object> args);

    public sealed class BridgeMethod
    {
        public string Name;
        public string Summary;
        public BridgeHandler Handler;
        /// <summary>False only for handlers that touch no Unity API (health, job polling).</summary>
        public bool RequiresMainThread = true;
        public int DefaultTimeoutMs = 30000;
    }

    /// <summary>
    /// Method table for the bridge. Registration is explicit rather than attribute-scanned because
    /// [InitializeOnLoad] ordering across types is not defined, and a half-registered router would
    /// surface as intermittent "unknown method" errors.
    /// </summary>
    public static class BridgeRouter
    {
        private static readonly Dictionary<string, BridgeMethod> Methods =
            new Dictionary<string, BridgeMethod>(StringComparer.Ordinal);

        public static void Register(BridgeMethod method)
        {
            if (method == null || string.IsNullOrEmpty(method.Name) || method.Handler == null)
                throw new ArgumentException("BridgeMethod needs a name and a handler");
            Methods[method.Name] = method;
        }

        public static void Register(string name, string summary, BridgeHandler handler,
            bool requiresMainThread = true, int defaultTimeoutMs = 30000)
        {
            Register(new BridgeMethod
            {
                Name = name,
                Summary = summary,
                Handler = handler,
                RequiresMainThread = requiresMainThread,
                DefaultTimeoutMs = defaultTimeoutMs
            });
        }

        public static bool TryGet(string name, out BridgeMethod method)
        {
            return Methods.TryGetValue(name, out method);
        }

        public static List<object> Describe()
        {
            var list = new List<object>();
            foreach (var kv in Methods)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "name", kv.Key },
                    { "summary", kv.Value.Summary },
                    { "requiresMainThread", kv.Value.RequiresMainThread },
                    { "defaultTimeoutMs", kv.Value.DefaultTimeoutMs }
                });
            }
            list.Sort(delegate (object a, object b)
            {
                var da = (Dictionary<string, object>)a;
                var db = (Dictionary<string, object>)b;
                return string.CompareOrdinal((string)da["name"], (string)db["name"]);
            });
            return list;
        }

        public static int Count { get { return Methods.Count; } }

        /// <summary>
        /// Executes a method, marshalling onto the main thread when the method needs it.
        /// Error shaping happens here so every transport gets identical error codes.
        /// </summary>
        public static Dictionary<string, object> Invoke(string name, Dictionary<string, object> args, int timeoutMsOverride)
        {
            BridgeMethod method;
            if (!TryGet(name, out method))
                return Error("UNKNOWN_METHOD", "No such bridge method: " + name);

            int timeoutMs = timeoutMsOverride > 0 ? timeoutMsOverride : method.DefaultTimeoutMs;

            try
            {
                object result = method.RequiresMainThread
                    ? MainThread.Run(delegate { return method.Handler(args); }, timeoutMs)
                    : method.Handler(args);

                return new Dictionary<string, object> { { "ok", true }, { "result", result } };
            }
            catch (MainThreadBusyException ex)
            {
                return Error("EDITOR_BUSY", ex.Message);
            }
            catch (BridgeHandlerException ex)
            {
                var inner = ex.InnerException ?? ex;
                return Error("HANDLER_ERROR", inner.Message, inner.StackTrace);
            }
            catch (Exception ex)
            {
                return Error("HANDLER_ERROR", ex.GetType().Name + ": " + ex.Message, ex.StackTrace);
            }
        }

        public static Dictionary<string, object> Error(string code, string message, string stack = null)
        {
            var err = new Dictionary<string, object> { { "code", code }, { "message", message } };
            if (!string.IsNullOrEmpty(stack)) err["stack"] = stack;
            return new Dictionary<string, object> { { "ok", false }, { "error", err } };
        }
    }
}
