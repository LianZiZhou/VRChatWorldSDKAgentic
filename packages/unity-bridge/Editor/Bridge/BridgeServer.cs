using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// The always-on HTTP endpoint inside the Unity Editor.
    ///
    /// Why HTTP rather than a persistent socket: the editor domain-reloads on every script change,
    /// which tears down all managed state. A request/response protocol with no long-lived connection
    /// survives that — the client simply retries and the reloaded domain serves the next call.
    ///
    /// Security: bound to 127.0.0.1 only, and every /rpc call must present the token written into the
    /// registry file. The bridge can compile and run arbitrary C# in the editor, so an unauthenticated
    /// local port would be a straightforward code-execution hole for any other process on the machine.
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeServer
    {
        private const int PortAttempts = 12;
        private const string SessionPortKey = "Agentic.Vrcw.BridgePort";
        private const string SessionTokenKey = "Agentic.Vrcw.BridgeToken";
        private const int MaxConcurrentRequests = 64;

        private static HttpListener _listener;
        private static Thread _acceptThread;
        private static int _port;
        private static string _token;
        private static volatile bool _running;
        private static int _inFlight;
        private static readonly DateTime StartedUtc = DateTime.UtcNow;

        static BridgeServer()
        {
            // A second editor opening the same project, or a domain reload, must not leave a stale
            // listener bound to the port.
            // A reload only closes the listener — the registry entry stays, because the port and token
            // in it are still the ones the reloaded domain will rebind to. Deleting it on every
            // recompile would make the editor look gone to any client that happened to look then.
            AssemblyReloadEvents.beforeAssemblyReload -= StopListener;
            AssemblyReloadEvents.beforeAssemblyReload += StopListener;
            EditorApplication.quitting -= Shutdown;
            EditorApplication.quitting += Shutdown;

            BridgeMethods.RegisterAll();

            // Bind immediately rather than deferring to EditorApplication.delayCall.
            //
            // An earlier version used delayCall, and after one domain reload it simply never fired: the
            // assembly compiled and loaded, yet the bridge never rebound and the editor became
            // unreachable with nothing in the log. Binding an HttpListener needs no Unity main loop, so
            // there is no reason to make startup depend on one.
            Start();

            // Self-healing second chance. If the bind above lost a port race, or ran before the editor
            // could answer Application.dataPath, this retries until it works. An unreachable bridge is
            // indistinguishable from a dead editor from outside, so it must not be a one-shot.
            EditorApplication.update -= EnsureStarted;
            EditorApplication.update += EnsureStarted;
        }

        private static double _nextRetryAt;
        private static int _failedStarts;

        private static void EnsureStarted()
        {
            if (_running) return;
            if (EditorApplication.timeSinceStartup < _nextRetryAt) return;
            _nextRetryAt = EditorApplication.timeSinceStartup + 2.0;

            Start();
            if (_running)
            {
                _failedStarts = 0;
                return;
            }

            // Report sparsely: a per-tick error would bury the console.
            if (++_failedStarts % 30 == 1)
                Debug.LogWarning("[agentic-vrcw] Bridge is not listening; retrying. Attempt " + _failedStarts + ".");
        }

        public static int Port { get { return _port; } }
        public static bool Running { get { return _running; } }

        public static void Start()
        {
            if (_running) return;

            try
            {
                // Port and token are kept in SessionState so they survive domain reloads. Without this
                // every script edit would move the endpoint and rotate the credential, and a client
                // mid-conversation would have to rediscover the editor after each recompile.
                int preferredPort = SessionState.GetInt(SessionPortKey, 0);

                for (int attempt = 0; attempt < PortAttempts && _listener == null; attempt++)
                {
                    int candidate = attempt == 0 && preferredPort > 0 ? preferredPort : FindFreePort();
                    var listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + candidate + "/");
                    try
                    {
                        listener.Start();
                        _listener = listener;
                        _port = candidate;
                    }
                    catch (HttpListenerException)
                    {
                        // Either we lost the race for that port, or the previous domain's listener has
                        // not released it yet. Either way, try another.
                        try { listener.Close(); } catch (Exception) { }
                    }
                }

                if (_listener == null)
                {
                    Debug.LogError("[agentic-vrcw] Could not bind a local port for the bridge after " + PortAttempts + " attempts.");
                    return;
                }

                SessionState.SetInt(SessionPortKey, _port);

                _token = SessionState.GetString(SessionTokenKey, "");
                if (string.IsNullOrEmpty(_token))
                {
                    _token = Guid.NewGuid().ToString("N");
                    SessionState.SetString(SessionTokenKey, _token);
                }

                _running = true;

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "agentic-vrcw-bridge";
                _acceptThread.Start();

                WriteRegistry();
                Debug.Log("[agentic-vrcw] Bridge listening on http://127.0.0.1:" + _port + "/ (" + BridgeRouter.Count + " methods)");
            }
            catch (Exception ex)
            {
                Debug.LogError("[agentic-vrcw] Bridge failed to start: " + ex);
                _running = false;
            }
        }

        /// <summary>Releases the port. Used on domain reload, where a new domain will rebind it.</summary>
        public static void StopListener()
        {
            _running = false;
            try { if (_listener != null) _listener.Close(); } catch (Exception) { }
            _listener = null;
        }

        /// <summary>Editor is going away for good: release the port and withdraw the registry entry.</summary>
        public static void Shutdown()
        {
            StopListener();
            DeleteRegistry();
        }

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { return; } // listener closed (reload/quit)

                // Each request is handled on the thread pool, never inline on the accept thread.
                //
                // Handling inline looks harmless because main-thread methods marshal onto the pump
                // anyway, but it breaks the one guarantee that matters most: a single /rpc waiting on a
                // blocked main thread would occupy the accept thread for its whole timeout, so /ping
                // could not be accepted either. That was observed with a real modal dialog - the editor
                // looked *dead* rather than *blocked*, which is the opposite of what /ping exists for.
                if (Interlocked.Increment(ref _inFlight) > MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref _inFlight);
                    try { Respond(ctx, 503, BridgeRouter.Error("TOO_MANY_REQUESTS", "Bridge has " + MaxConcurrentRequests + " requests in flight.")); }
                    catch (Exception) { }
                    continue;
                }

                var captured = ctx;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Handle(captured); }
                    catch (Exception ex)
                    {
                        try { Respond(captured, 500, BridgeRouter.Error("TRANSPORT_ERROR", ex.Message)); }
                        catch (Exception) { }
                    }
                    finally { Interlocked.Decrement(ref _inFlight); }
                });
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');

            // /ping is answered straight from the listener thread, deliberately without touching the
            // main thread. That is what lets the supervisor tell "process alive but editor wedged"
            // apart from "process gone" — /ping succeeds while /rpc returns EDITOR_BUSY.
            if (path == "/ping")
            {
                Respond(ctx, 200, new Dictionary<string, object>
                {
                    { "ok", true },
                    { "result", new Dictionary<string, object>
                        {
                            { "alive", true },
                            { "bridgeVersion", BridgePaths.BridgeVersion },
                            { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                            { "port", _port },
                            { "pumpTicks", MainThread.TickCount },
                            { "lastPumpUtc", MainThread.LastTickUtc.ToString("o") },
                            { "queueDepth", MainThread.QueueDepth },
                            { "startedUtc", StartedUtc.ToString("o") }
                        }
                    }
                });
                return;
            }

            if (path != "/rpc")
            {
                Respond(ctx, 404, BridgeRouter.Error("NOT_FOUND", "Unknown path: " + path));
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                Respond(ctx, 405, BridgeRouter.Error("METHOD_NOT_ALLOWED", "/rpc requires POST"));
                return;
            }

            string presented = ctx.Request.Headers["X-Agentic-Token"];
            if (string.IsNullOrEmpty(_token) || presented != _token)
            {
                Respond(ctx, 401, BridgeRouter.Error("UNAUTHORIZED",
                    "Missing or wrong X-Agentic-Token. Read the token from " + BridgePaths.RegistryFile));
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            Dictionary<string, object> request;
            try { request = Json.AsObject(Json.Parse(body)); }
            catch (Exception ex)
            {
                Respond(ctx, 400, BridgeRouter.Error("BAD_REQUEST", "Malformed JSON: " + ex.Message));
                return;
            }

            string method = Json.GetString(request, "method");
            if (string.IsNullOrEmpty(method))
            {
                Respond(ctx, 400, BridgeRouter.Error("BAD_REQUEST", "Missing \"method\""));
                return;
            }

            object rawParams;
            request.TryGetValue("params", out rawParams);
            var args = Json.AsObject(rawParams);
            int timeoutMs = Json.GetInt(request, "timeoutMs", 0);

            object id;
            bool hasId = request.TryGetValue("id", out id);

            // Already-serialised JSON: the router does it on the main thread so live Unity objects in a
            // result can be read safely. Protocol-level errors ride a 200 with ok:false.
            string json = BridgeRouter.InvokeToJson(method, args, timeoutMs, id, hasId);
            RespondJson(ctx, 200, json);
        }

        private static void Respond(HttpListenerContext ctx, int status, object payload)
        {
            RespondJson(ctx, status, Json.Write(payload, 12));
        }

        private static void RespondJson(HttpListenerContext ctx, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void WriteRegistry()
        {
            var info = new Dictionary<string, object>
            {
                { "projectPath", BridgePaths.ProjectRoot.Replace('\\', '/') },
                { "projectName", BridgePaths.ProjectName },
                { "port", _port },
                { "token", _token },
                { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                { "unityVersion", Application.unityVersion },
                { "bridgeVersion", BridgePaths.BridgeVersion },
                { "startedUtc", StartedUtc.ToString("o") }
            };
            string json = Json.Write(info, 4);
            TryWrite(BridgePaths.RegistryFile, json);
            TryWrite(BridgePaths.LocalBridgeInfo, json);
        }

        private static void DeleteRegistry()
        {
            try { if (File.Exists(BridgePaths.RegistryFile)) File.Delete(BridgePaths.RegistryFile); }
            catch (Exception) { }
        }

        private static void TryWrite(string path, string contents)
        {
            try { File.WriteAllText(path, contents, new UTF8Encoding(false)); }
            catch (Exception ex) { Debug.LogWarning("[agentic-vrcw] Could not write " + path + ": " + ex.Message); }
        }
    }
}
