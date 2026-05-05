using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniClaude.Editor.MCP
{
    /// <summary>
    /// HTTP transport for MCP using HttpListener on localhost.
    /// Handles /rpc (POST) and /sse (GET) endpoints.
    /// </summary>
    public class HttpTransport : IMCPTransport
    {
        HttpListener _listener;
        CancellationTokenSource _cts;
        Func<string, Task<string>> _requestHandler;
        string _authToken;
        bool _running;
        int _port;
        int _connectedClients;

        /// <inheritdoc />
        public bool IsRunning => _running;

        /// <inheritdoc />
        public string Endpoint => _running ? $"http://127.0.0.1:{_port}/" : null;

        /// <summary>Port the transport is listening on. Returns 0 if not running.</summary>
        public int Port => _running ? _port : 0;

        /// <summary>Gets the number of currently connected SSE clients.</summary>
        public int ConnectedClients => _connectedClients;

        /// <inheritdoc />
        public void SetRequestHandler(Func<string, Task<string>> handler)
        {
            _requestHandler = handler;
        }

        /// <inheritdoc />
        public void SetAuthToken(string token)
        {
            _authToken = token;
        }

        /// <summary>
        /// Constant-time string compare. Avoids timing side-channels when comparing the
        /// expected auth token against an attacker-supplied value.
        /// </summary>
        static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>
        /// Validates that the request is authentic and originates from loopback. Returns true
        /// when the request should proceed; false when it has been rejected (response already
        /// written and closed by this method).
        /// </summary>
        bool AuthorizeRequest(HttpListenerContext context)
        {
            // Loopback-only: HttpListener is bound to 127.0.0.1, but we still confirm the
            // remote endpoint is loopback in case a future config change drops that prefix.
            var remote = context.Request.RemoteEndPoint?.Address;
            if (remote == null || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return false;
            }

            // Reject Host headers other than 127.0.0.1[:port] / localhost[:port] / [::1].
            // This thwarts DNS-rebinding attacks where a malicious page resolves a domain
            // it controls to 127.0.0.1 and then issues authenticated requests on the user's
            // behalf. We also accept a missing Host header (HttpListener forwards on raw URI).
            var host = context.Request.Headers["Host"];
            if (!string.IsNullOrEmpty(host) && !IsLoopbackHost(host))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return false;
            }

            if (string.IsNullOrEmpty(_authToken))
            {
                // Server started without a token — reject every request to avoid an
                // accidentally-open MCP server in dev configurations.
                context.Response.StatusCode = 503;
                context.Response.Close();
                return false;
            }

            var presented = ExtractToken(context.Request);
            if (presented == null || !ConstantTimeEquals(_authToken, presented))
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.Add("WWW-Authenticate", "Bearer realm=\"uniclaude-mcp\"");
                context.Response.Close();
                return false;
            }

            return true;
        }

        static bool IsLoopbackHost(string hostHeader)
        {
            // Strip an optional :port suffix — but only the last one, so IPv6 [::1]:1234 still works.
            var trimmed = hostHeader.Trim();
            if (trimmed.StartsWith("["))
            {
                var end = trimmed.IndexOf(']');
                if (end < 0) return false;
                var ip = trimmed.Substring(1, end - 1);
                return IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr);
            }
            var colon = trimmed.LastIndexOf(':');
            var hostPart = colon > 0 ? trimmed.Substring(0, colon) : trimmed;
            if (string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(hostPart, out var parsed) && IPAddress.IsLoopback(parsed);
        }

        static string ExtractToken(HttpListenerRequest request)
        {
            var authHeader = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authHeader))
            {
                const string bearer = "Bearer ";
                if (authHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                    return authHeader.Substring(bearer.Length).Trim();
            }

            // Fallback: query string `?token=...`. Required because the MCP HTTP client
            // inside the Claude Agent SDK does not always forward custom headers.
            var fromQuery = request.QueryString["token"];
            if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;

            return null;
        }

        /// <inheritdoc />
        public void Start(int port = 0)
        {
            if (_running) return;
            if (_requestHandler == null)
                throw new InvalidOperationException("Request handler must be set before starting transport.");

            _cts = new CancellationTokenSource();

            if (port == 0)
            {
                var tempListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                tempListener.Start();
                port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
                tempListener.Stop();
            }

            _port = port;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                if (_port != 0)
                {
                    Debug.LogWarning($"[UniClaude MCP] Port {_port} in use, falling back to auto-assign: {ex.Message}");
                    var tempListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                    tempListener.Start();
                    _port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
                    tempListener.Stop();

                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Start();
                }
                else
                {
                    Debug.LogError($"[UniClaude MCP] Failed to start: {ex.Message}");
                    return;
                }
            }

            _running = true;
            Task.Run(() => ListenLoop(_cts.Token));
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (ObjectDisposedException) { }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleContext(context, ct));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        Debug.LogError($"[UniClaude MCP] Listen error: {ex.Message}");
                }
            }
        }

        async Task HandleContext(HttpListenerContext context, CancellationToken ct)
        {
            var path = context.Request.Url.AbsolutePath;

            try
            {
                if (!AuthorizeRequest(context))
                    return;

                if (context.Request.HttpMethod == "POST" && path == "/rpc")
                    await HandleRPC(context);
                else if (context.Request.HttpMethod == "GET" && path == "/sse")
                    await HandleSSE(context, ct);
                else if (context.Request.HttpMethod == "GET" && path == "/rpc")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            catch (Exception)
            {
                try { context.Response.Close(); }
                catch { }
            }
        }

        async Task HandleRPC(HttpListenerContext context)
        {
            string json;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                json = await reader.ReadToEndAsync();

            var tcs = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _requestHandler(json);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            if (await Task.WhenAny(tcs.Task, Task.Delay(30000)) != tcs.Task)
            {
                WriteResponse(context.Response,
                    @"{""jsonrpc"":""2.0"",""id"":null,""error"":{""code"":-32000,""message"":""Timeout: main thread did not respond within 30 seconds""}}");
                context.Response.Close();
                return;
            }

            var response = await tcs.Task;
            if (response != null)
                WriteResponse(context.Response, response);
            else
                context.Response.StatusCode = 204;

            context.Response.Close();
        }

        async Task HandleSSE(HttpListenerContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _connectedClients);

            try
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.Add("Cache-Control", "no-cache");
                context.Response.Headers.Add("Connection", "keep-alive");
                context.Response.SendChunked = true;

                // Use UTF8 without BOM — a BOM prefix corrupts the first SSE event.
                // AutoFlush ensures each write is pushed through to the HTTP chunk immediately.
                var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
                writer.AutoFlush = true;
                var rpcUrl = $"http://127.0.0.1:{_port}/rpc";
                await writer.WriteAsync($"event: endpoint\ndata: {rpcUrl}\n\n");
                await writer.FlushAsync();

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(30000, ct);
                    await writer.WriteAsync(":keepalive\n\n");
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            finally
            {
                Interlocked.Decrement(ref _connectedClients);
                try { context.Response.Close(); }
                catch { }
            }
        }

        static void WriteResponse(HttpListenerResponse response, string body)
        {
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
