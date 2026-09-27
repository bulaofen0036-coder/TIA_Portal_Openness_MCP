using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer
{
    /// <summary>
    /// HTTP host for the MCP server. Implements a pragmatic subset of the
    /// MCP Streamable HTTP transport spec aimed at local / internal use:
    /// <list type="bullet">
    ///   <item><description>POST /mcp — JSON-RPC, returns JSON or SSE based on Accept header.</description></item>
    ///   <item><description>GET /mcp/health — liveness/info probe.</description></item>
    ///   <item><description>DELETE /mcp — terminate session (best-effort).</description></item>
    ///   <item><description>GET / — server identity (kept for backward compat).</description></item>
    /// </list>
    /// Auth: a single shared secret (<c>--http-api-key</c>, or the <c>TIA_MCP_HTTP_API_KEY</c>
    /// environment variable) supplied via either
    /// <c>Authorization: Bearer &lt;secret&gt;</c> or <c>X-API-Key: &lt;secret&gt;</c>.
    /// A prefix that listens beyond loopback is refused without one, and browser requests from
    /// any non-local Origin are rejected.
    /// Mcp-Session-Id is generated on first request and correlated on subsequent requests;
    /// state isolation between sessions is intentionally not implemented because the
    /// underlying TIA Portal handle is process-wide.
    /// </summary>
    internal static class HttpMcpServer
    {
        private const string ServerName = "TIA Portal MCP";
        private const string SessionHeader = "Mcp-Session-Id";
        private const string ProtocolHeader = "MCP-Protocol-Version";

        // Upper bound on how long a POST waits for the MCP host to produce a matching
        // response before returning 504, so a stalled pipe can't hang the request forever.
        // It has to outlast real work: a cold TIA start, a project open, a compile or a
        // download all routinely take minutes. Override with --http-timeout-seconds.
        public const int DefaultResponseTimeoutSeconds = 600;

        private sealed class Session
        {
            public string Id = "";
            public DateTime LastSeenUtc;
            public string? ProtocolVersion;
        }

        private static readonly ConcurrentDictionary<string, Session> _sessions
            = new ConcurrentDictionary<string, Session>(StringComparer.OrdinalIgnoreCase);

        public static async Task Run(
            CliOptions? options,
            McpBlockingStream httpToMcp,
            McpBlockingStream mcpToHttp,
            Action<string> log)
        {
            string prefix = options?.HttpPrefix ?? "http://127.0.0.1:8765/";
            if (!prefix.EndsWith("/")) prefix += "/";
            string? secret = HttpSecurity.ResolveApiKey(options?.HttpApiKey);
            var timeout = TimeSpan.FromSeconds(options?.HttpTimeoutSeconds is int t && t > 0 ? t : DefaultResponseTimeoutSeconds);

            // Program.RunHttpHost checks this before starting anything; kept here so no other
            // caller can bring the listener up on a LAN prefix without a key.
            var refusal = StartupRefusal(options);
            if (refusal != null)
            {
                log(refusal);
                throw new InvalidOperationException(refusal);
            }

            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            Console.Error.WriteLine($"TIA Portal MCP Server (HTTP) listening at {prefix}");
            log($"HTTP transport started at {prefix} (response timeout {timeout.TotalSeconds:0}s)");
            if (secret == null)
                Console.Error.WriteLine("WARNING: no API key set; the loopback endpoint is unauthenticated.");

            // Requests are written concurrently; StreamWriter is not thread-safe, so writes take
            // a short lock. Responses are matched by id on a single dedicated reader - no lock is
            // held while a tool runs, so ping and tools/list are not stuck behind a compile.
            var writeLock = new object();
            var mcpWriter = new StreamWriter(httpToMcp, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var mcpReader = new StreamReader(mcpToHttp, new UTF8Encoding(false));
            var router = new JsonRpcResponseRouter();
            var pump = new Thread(() => router.Pump(mcpReader)) { IsBackground = true, Name = "mcp-http-response-pump" };
            pump.Start();

            void Send(string line)
            {
                lock (writeLock) mcpWriter.WriteLine(line);
            }

            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Dispatch(ctx, secret, timeout, router, Send, log).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log("HTTP handler error: " + ex.Message);
                        try { ctx.Response.Abort(); } catch { }
                    }
                });
            }

            listener.Stop();
        }

        /// <summary>Why the HTTP transport must not start with these options, or null when it may.
        /// This endpoint can download to a PLC and delete blocks: listening beyond loopback with no
        /// key would hand that to anyone on the network.</summary>
        public static string? StartupRefusal(CliOptions? options)
        {
            string prefix = options?.HttpPrefix ?? "http://127.0.0.1:8765/";
            if (HttpSecurity.ResolveApiKey(options?.HttpApiKey) != null || HttpSecurity.IsLoopbackPrefix(prefix))
                return null;
            return $"Refusing to start: --http-prefix {prefix} accepts connections from other machines, "
                + $"and no API key is set. Pass --http-api-key <secret> or set {HttpSecurity.ApiKeyEnvironmentVariable}, "
                + "or listen on http://127.0.0.1:<port>/ only.";
        }

        private static async Task Dispatch(
            HttpListenerContext ctx,
            string? secret,
            TimeSpan timeout,
            JsonRpcResponseRouter router,
            Action<string> send,
            Action<string> log)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var method = req.HttpMethod ?? "";
            var rawUrl = req.RawUrl ?? "";

            // Health probe — unauthenticated by design so monitoring can hit it cheaply.
            if (HttpMethod("GET", method) && rawUrl.StartsWith("/mcp/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(res, 200, BuildHealthJson()).ConfigureAwait(false);
                return;
            }

            // Server identity (kept for backward compatibility with existing tooling).
            if (HttpMethod("GET", method) && rawUrl == "/")
            {
                await WriteJson(res, 200, $"{{\"server\":\"{ServerName}\",\"transport\":\"http\"}}").ConfigureAwait(false);
                return;
            }

            // All /mcp paths require auth (when a secret is configured).
            if (!rawUrl.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            // A web page the user happens to visit must not be able to drive this server: browsers
            // send a text/plain POST cross-site without a CORS preflight, but they always attach Origin.
            if (!HttpSecurity.IsAllowedOrigin(req.Headers["Origin"]))
            {
                log("HTTP request rejected: Origin " + req.Headers["Origin"]);
                res.StatusCode = 403;
                res.Close();
                return;
            }

            if (secret != null && !AuthOk(req, secret))
            {
                res.StatusCode = 401;
                // WWW-Authenticate is a restricted response header on .NET Framework: setting it
                // through the indexer throws, the handler aborted the connection, and a client with
                // a wrong key saw a connection reset instead of 401. The status is what matters.
                try { res.AddHeader("WWW-Authenticate", "Bearer"); }
                catch (ArgumentException) { }
                res.Close();
                return;
            }

            // DELETE /mcp — session termination (best-effort; idempotent).
            if (HttpMethod("DELETE", method))
            {
                var sid = req.Headers[SessionHeader];
                if (!string.IsNullOrEmpty(sid)) _sessions.TryRemove(sid!, out _);
                res.StatusCode = 204;
                res.Close();
                return;
            }

            if (!HttpMethod("POST", method))
            {
                res.StatusCode = 405;
                res.Close();
                return;
            }

            // Body length guard: real MCP requests are tens of KB; cap defensively.
            const long MaxBodyBytes = 10L * 1024 * 1024;
            if (req.ContentLength64 > MaxBodyBytes)
            {
                res.StatusCode = 413;
                res.Close();
                return;
            }

            // Read the raw body, bounded by Content-Length. Async reads against the
            // HttpListener input stream can hang, so read synchronously (this handler
            // already runs on a dedicated task thread) and stop at the declared length.
            string body;
            {
                long declared = req.ContentLength64;
                var ms = new MemoryStream();
                var buf = new byte[8192];
                var input = req.InputStream;
                int read;
                while ((read = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                    if (ms.Length > MaxBodyBytes) { res.StatusCode = 413; res.Close(); return; }
                    if (declared >= 0 && ms.Length >= declared) break;
                }
                body = (req.ContentEncoding ?? Encoding.UTF8).GetString(ms.ToArray());
            }

            if (string.IsNullOrWhiteSpace(body)) { res.StatusCode = 400; res.Close(); return; }

            JsonNode? requestId;
            string? rpcMethod;
            try
            {
                var parsed = JsonNode.Parse(body);
                requestId = parsed?["id"];
                rpcMethod = parsed?["method"]?.GetValue<string>();
            }
            catch
            {
                res.StatusCode = 400;
                res.Close();
                return;
            }

            // Session bookkeeping. We assign on initialize and accept any subsequent header.
            var session = TouchSession(req, rpcMethod);
            res.Headers[SessionHeader] = session.Id;
            var protoVersion = req.Headers[ProtocolHeader];
            if (!string.IsNullOrEmpty(protoVersion)) session.ProtocolVersion = protoVersion;

            bool isNotification = requestId == null;
            bool wantsSse = WantsEventStream(req);

            if (isNotification)
            {
                send(body);
                res.StatusCode = 202;
                res.Close();
                return;
            }

            // Register before writing, or a fast response could arrive with nobody waiting.
            string idKey = JsonRpcResponseRouter.IdKey(requestId);
            var waiter = router.Expect(idKey);
            if (waiter == null)
            {
                // Two in-flight requests with one id cannot be told apart on the way back.
                res.StatusCode = 409;
                res.Close();
                return;
            }

            send(body);

            string responseLine;
            var done = await Task.WhenAny(waiter, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != waiter)
            {
                router.Abandon(idKey);
                // Tell the host too, so a tool that honours cancellation stops instead of running on
                // for a client that has already given up. A late response is dropped by the router.
                var cancel = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/cancelled",
                    ["params"] = new JsonObject
                    {
                        ["requestId"] = requestId!.DeepClone(),
                        ["reason"] = $"HTTP bridge timeout after {timeout.TotalSeconds:0}s",
                    },
                };
                send(cancel.ToJsonString());
                log($"HTTP request {idKey} ({rpcMethod}) timed out after {timeout.TotalSeconds:0}s");
                res.StatusCode = 504;
                res.Close();
                return;
            }

            try { responseLine = await waiter.ConfigureAwait(false); }
            catch (Exception ex)
            {
                // The host stopped: say so now rather than letting every request run into the timeout.
                log("HTTP request " + idKey + " failed: " + ex.Message);
                res.StatusCode = 502;
                res.Close();
                return;
            }

            if (wantsSse)
            {
                await WriteSse(res, responseLine).ConfigureAwait(false);
            }
            else
            {
                await WriteJson(res, 200, responseLine).ConfigureAwait(false);
            }
        }

        // ---------- helpers ----------

        private static bool HttpMethod(string expected, string actual)
            => string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        private static bool AuthOk(HttpListenerRequest req, string secret)
        {
            var apiKey = req.Headers["X-API-Key"];
            if (apiKey != null && HttpSecurity.ConstantTimeEquals(apiKey, secret)) return true;

            var authz = req.Headers["Authorization"];
            if (!string.IsNullOrEmpty(authz)
                && authz!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && HttpSecurity.ConstantTimeEquals(authz.Substring(7).Trim(), secret))
            {
                return true;
            }
            return false;
        }

        private static bool WantsEventStream(HttpListenerRequest req)
        {
            var accept = req.Headers["Accept"];
            return !string.IsNullOrEmpty(accept)
                && accept!.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Session TouchSession(HttpListenerRequest req, string? rpcMethod)
        {
            var sid = req.Headers[SessionHeader];
            if (!string.IsNullOrEmpty(sid) && _sessions.TryGetValue(sid!, out var existing))
            {
                existing.LastSeenUtc = DateTime.UtcNow;
                return existing;
            }
            // Allocate a new session on initialize, or whenever a client omits the header.
            var s = new Session
            {
                Id = Guid.NewGuid().ToString("N"),
                LastSeenUtc = DateTime.UtcNow,
            };
            _sessions[s.Id] = s;
            return s;
        }

        private static string BuildHealthJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"server\":\"").Append(ServerName).Append("\"");
            sb.Append(",\"transport\":\"http\"");
            sb.Append(",\"sessions\":").Append(_sessions.Count);
            sb.Append(",\"build\":\"").Append(typeof(HttpMcpServer).Assembly.GetName().Version).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        private static async Task WriteJson(HttpListenerResponse res, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            res.StatusCode = status;
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            res.Close();
        }

        private static async Task WriteSse(HttpListenerResponse res, string jsonRpcLine)
        {
            // Single-shot SSE: one "message" event carrying the JSON-RPC response body,
            // then end of stream. This keeps spec-compliant clients happy without
            // adding a real long-lived stream that local use does not need.
            var sb = new StringBuilder();
            sb.Append("event: message\n");
            // Split payload on newlines per SSE framing rules.
            foreach (var line in jsonRpcLine.Split('\n'))
            {
                sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
            }
            sb.Append('\n');

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-cache";
            res.SendChunked = true;
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            res.OutputStream.Close();
            res.Close();
        }
    }
}
