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

        private static HttpListener _listener;
        private static Thread _acceptThread;
        private static int _port;
        private static string _token;
        private static volatile bool _running;
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

            // Defer the actual bind by one tick: during the static constructor the editor is still
            // mid-reload and Application.dataPath-derived paths are unreliable.
            EditorApplication.delayCall += Start;
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

                // Handled inline rather than on the thread pool: requests marshal onto the main
                // thread anyway, so concurrency here buys nothing and would reorder calls.
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    try { Respond(ctx, 500, BridgeRouter.Error("TRANSPORT_ERROR", ex.Message)); }
                    catch (Exception) { }
                }
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

            var response = BridgeRouter.Invoke(method, args, timeoutMs);
            object id;
            if (request.TryGetValue("id", out id)) response["id"] = id;

            bool ok = response.ContainsKey("ok") && response["ok"] is bool && (bool)response["ok"];
            Respond(ctx, ok ? 200 : 200, response); // protocol-level errors ride a 200 with ok:false
        }

        private static void Respond(HttpListenerContext ctx, int status, object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(payload, 12));
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
