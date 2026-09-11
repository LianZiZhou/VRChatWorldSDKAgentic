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
        /// Executes a method and returns the complete JSON response.
        ///
        /// Serialisation deliberately happens *inside* the main-thread work item for main-thread methods.
        /// Results routinely contain live UnityEngine.Objects, and reading even their name off the main
        /// thread throws "GetName can only be called from the main thread" — which would turn a perfectly
        /// good result into a transport error after the handler had already succeeded.
        /// </summary>
        public static string InvokeToJson(string name, Dictionary<string, object> args, int timeoutMsOverride, object id, bool hasId)
        {
            BridgeMethod method;
            if (!TryGet(name, out method))
                return Serialize(Error("UNKNOWN_METHOD", "No such bridge method: " + name), id, hasId);

            int timeoutMs = timeoutMsOverride > 0 ? timeoutMsOverride : method.DefaultTimeoutMs;

            try
            {
                if (method.RequiresMainThread)
                {
                    return MainThread.Run(delegate
                    {
                        object result = method.Handler(args);
                        return Serialize(new Dictionary<string, object> { { "ok", true }, { "result", result } }, id, hasId);
                    }, timeoutMs);
                }

                // Off-main-thread methods return plain data by contract (health counters, job records),
                // so serialising here is safe.
                object offThread = method.Handler(args);
                return Serialize(new Dictionary<string, object> { { "ok", true }, { "result", offThread } }, id, hasId);
            }
            catch (MainThreadBusyException ex)
            {
                return Serialize(Error("EDITOR_BUSY", ex.Message), id, hasId);
            }
            catch (BridgeHandlerException ex)
            {
                var inner = ex.InnerException ?? ex;
                return Serialize(Error("HANDLER_ERROR", inner.Message, inner.StackTrace), id, hasId);
            }
            catch (Exception ex)
            {
                return Serialize(Error("HANDLER_ERROR", ex.GetType().Name + ": " + ex.Message, ex.StackTrace), id, hasId);
            }
        }

        private static string Serialize(Dictionary<string, object> response, object id, bool hasId)
        {
            if (hasId) response["id"] = id;
            return Json.Write(response, 12);
        }

        public static Dictionary<string, object> Error(string code, string message, string stack = null)
        {
            var err = new Dictionary<string, object> { { "code", code }, { "message", message } };
            if (!string.IsNullOrEmpty(stack)) err["stack"] = stack;
            return new Dictionary<string, object> { { "ok", false }, { "error", err } };
        }
    }
}
