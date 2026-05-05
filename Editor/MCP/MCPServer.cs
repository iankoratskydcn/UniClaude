using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UniClaude.Editor.MCP
{
    /// <summary>
    /// Coordinates MCP server lifecycle: starts transport, routes requests
    /// through dispatcher on Unity's main thread, manages domain reload strategy.
    /// </summary>
    [InitializeOnLoad]
    public class MCPServer : IDisposable
    {
        const string SessionKeyAuthToken = "UniClaude_MCP_AuthToken";

        static MCPServer _instance;

        IMCPTransport _transport;
        MCPDispatcher _dispatcher;
        IDomainReloadStrategy _reloadStrategy;
        MCPSettings _settings;
        ConcurrentQueue<WorkItem> _workQueue;
        string _authToken;
        bool _disposed;

        /// <summary>Singleton for access from MCP tools and other editor components.</summary>
        public static MCPServer Instance => _instance;

        /// <summary>Whether the server is currently running.</summary>
        public bool IsRunning => _transport?.IsRunning ?? false;

        /// <summary>The endpoint address for the MCP server.</summary>
        public string Endpoint => _transport?.Endpoint;

        /// <summary>Port the MCP HTTP server is listening on. Returns 0 if not running.</summary>
        public int Port => (_transport as HttpTransport)?.Port ?? 0;

        /// <summary>
        /// Per-session authentication token shared with the Node.js sidecar.
        /// Required on every MCP HTTP request and on every Sidecar HTTP request.
        /// Rotated on every Editor launch (and reused across domain reloads via SessionState).
        /// </summary>
        public string AuthToken => _authToken;

        /// <summary>The active domain reload strategy.</summary>
        public IDomainReloadStrategy ActiveReloadStrategy => _reloadStrategy;

        /// <summary>
        /// Fired when a tool executes. Parameters: tool name, args summary, result.
        /// </summary>
        public event Action<string, string, MCPToolResult> OnToolExecuted;

        static MCPServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;

            if (SessionState.GetBool("UniClaude_MCP_WasRunning", false))
            {
                EditorApplication.delayCall += () =>
                {
                    if (_instance == null || !_instance.IsRunning)
                    {
                        var server = new MCPServer();
                        var settings = new MCPSettings();
                        var savedPort = SessionState.GetInt("UniClaude_MCP_Port", 0);
                        if (savedPort > 0) settings.Port = savedPort;
                        server.Start(settings);
                    }
                };
            }
        }

        static void OnBeforeReload()
        {
            if (_instance != null && _instance.IsRunning)
            {
                SessionState.SetBool("UniClaude_MCP_WasRunning", true);
                if (_instance._transport?.Endpoint != null)
                {
                    var uri = new Uri(_instance._transport.Endpoint);
                    SessionState.SetInt("UniClaude_MCP_Port", uri.Port);
                }
            }
            else
            {
                SessionState.SetBool("UniClaude_MCP_WasRunning", false);
            }
        }

        /// <summary>Starts the server with the given settings.</summary>
        /// <param name="settings">MCP settings controlling port, strategy, logging.</param>
        public void Start(MCPSettings settings)
        {
            if (IsRunning) return;

            _settings = settings;
            _dispatcher = new MCPDispatcher();
            _workQueue = new ConcurrentQueue<WorkItem>();

            _authToken = SessionState.GetString(SessionKeyAuthToken, "");
            if (string.IsNullOrEmpty(_authToken))
            {
                _authToken = GenerateAuthToken();
                SessionState.SetString(SessionKeyAuthToken, _authToken);
            }

            _reloadStrategy = settings.DomainReloadStrategy == ReloadStrategy.Manual
                ? (IDomainReloadStrategy)new ManualReloadStrategy()
                : new AutoReloadStrategy(settings.ReloadTimeoutSeconds);

            _transport = new HttpTransport();
            _transport.SetRequestHandler(EnqueueAndWait);
            _transport.SetAuthToken(_authToken);
            _transport.Start(settings.Port);

            EditorApplication.update += ProcessMainThreadQueue;
            EditorApplication.quitting += Stop;

            _instance = this;

            if (_settings.LogLevel >= 1)
                Debug.Log($"[UniClaude MCP] Server running on {_transport.Endpoint}");
        }

        static string GenerateAuthToken()
        {
            // 32 bytes = 256 bits of entropy. Hex-encoded for ASCII-safe transport
            // through env vars, query strings, and HTTP headers.
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Rotates the auth token. Caller is responsible for restarting the sidecar so it
        /// receives the new value. Useful when the user clicks "Restart Sidecar" — we don't
        /// want a previously-spawned process to keep working with stale credentials.
        /// </summary>
        public void RotateAuthToken()
        {
            _authToken = GenerateAuthToken();
            SessionState.SetString(SessionKeyAuthToken, _authToken);
            _transport?.SetAuthToken(_authToken);
        }

        /// <summary>Stops the server and cleans up resources.</summary>
        public void Stop()
        {
            if (!IsRunning && _transport == null) return;

            EditorApplication.update -= ProcessMainThreadQueue;
            EditorApplication.quitting -= Stop;

            _transport?.Stop();
            _reloadStrategy?.Dispose();
            _reloadStrategy = null;

            while (_workQueue != null && _workQueue.TryDequeue(out var item))
                item.Completion.TrySetCanceled();

            if (_settings?.LogLevel >= 1)
                Debug.Log("[UniClaude MCP] Server stopped.");
        }

        /// <summary>Notifies the reload strategy that the current turn is complete.</summary>
        public void NotifyTurnComplete()
        {
            _reloadStrategy?.OnTurnComplete();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _transport?.Dispose();

            if (_instance == this)
                _instance = null;
        }

        Task<string> EnqueueAndWait(string json)
        {
            var tcs = new TaskCompletionSource<string>();
            _workQueue.Enqueue(new WorkItem(json, tcs));
            return tcs.Task;
        }

        void ProcessMainThreadQueue()
        {
            while (_workQueue.TryDequeue(out var item))
            {
                try
                {
                    var toolName = ExtractToolName(item.Json);

                    if (toolName != null)
                        _reloadStrategy?.OnToolCallStart(toolName);

                    var response = _dispatcher.HandleRequest(item.Json);

                    if (toolName != null)
                    {
                        _reloadStrategy?.OnToolCallEnd(toolName);

                        var result = ExtractToolResult(response);
                        if (result != null)
                            OnToolExecuted?.Invoke(toolName, item.Json, result);
                    }

                    item.Completion.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }

        static string ExtractToolName(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                if (obj["method"]?.ToString() == "tools/call")
                    return obj["params"]?["name"]?.ToString();
            }
            catch { }
            return null;
        }

        static MCPToolResult ExtractToolResult(string responseJson)
        {
            if (string.IsNullOrEmpty(responseJson)) return null;
            try
            {
                var obj = JObject.Parse(responseJson);
                var text = obj["result"]?["content"]?[0]?["text"]?.ToString();
                var isError = obj["result"]?["isError"]?.Value<bool>() ?? false;
                if (text != null)
                    return isError ? MCPToolResult.Error(text) : MCPToolResult.Success(text);
            }
            catch { }
            return null;
        }

        class WorkItem
        {
            public string Json { get; }
            public TaskCompletionSource<string> Completion { get; }

            public WorkItem(string json, TaskCompletionSource<string> completion)
            {
                Json = json;
                Completion = completion;
            }
        }
    }
}
