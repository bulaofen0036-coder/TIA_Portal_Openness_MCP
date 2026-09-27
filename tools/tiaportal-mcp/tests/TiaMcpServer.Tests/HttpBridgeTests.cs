using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// The HTTP transport's response routing and security decisions.
    ///
    /// The routing bug this pins: a request that timed out left its reader running on the shared
    /// stream, so the next response went to the orphan and every later request timed out as well.
    /// </summary>
    internal static class HttpBridgeTests
    {
        internal static void Run(Action<bool, string> check)
        {
            RunRouter(check);
            RunSecurity(check);
        }

        private static void RunRouter(Action<bool, string> check)
        {
            var r = new JsonRpcResponseRouter();
            var one = r.Expect("1")!;
            var two = r.Expect("2")!;
            check(r.Expect("1") == null, "a second in-flight request with the same id is rejected");

            check(r.Route("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}"), "a response completes its own waiter");
            check(two.IsCompleted && !one.IsCompleted, "out-of-order responses reach the right request");

            check(!r.Route("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}"),
                "a notification is not a response");
            check(!r.Route("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"),
                "a server-to-client request with a colliding id is not a response");
            check(!one.IsCompleted, "the waiter is untouched by non-responses");
            check(!r.Route("not json") && !r.Route(""), "garbage lines are ignored");

            // The desync: abandon on timeout, then the late response must be dropped, not kept
            // around for the next request that happens to reuse the id.
            r.Abandon("1");
            check(one.IsCanceled, "abandoning cancels the waiter");
            check(!r.Route("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"), "a late response after a timeout is dropped");
            check(r.PendingCount == 0, "nothing is left pending");
            var again = r.Expect("1")!;
            check(r.Route("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"n\":2}}") && again.Result.Contains("\"n\":2"),
                "the id can be reused once the old request is gone, and gets its own response");

            // 7 and "7" are different ids.
            var str = r.Expect(JsonRpcResponseRouter.IdKey(JsonValue.Create("7")))!;
            check(!r.Route("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}") && !str.IsCompleted,
                "numeric id 7 does not answer string id \"7\"");
            check(r.Route("{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"result\":{}}") && str.IsCompleted,
                "string id \"7\" answers itself");

            // The host's stream ends: everything waiting fails at once, and new requests fail fast.
            var p = new JsonRpcResponseRouter();
            var waiting = p.Expect("9")!;
            var lines = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":8,\"result\":{}}\n");
            p.Pump(lines);
            check(waiting.IsFaulted, "when the host's output ends, waiting requests fail instead of timing out");
            var late = p.Expect("10");
            check(late != null && late.IsFaulted, "after the host is gone new requests fail immediately");
        }

        private static void RunSecurity(Action<bool, string> check)
        {
            foreach (var ok in new[] { "http://127.0.0.1:8765/", "http://localhost:8765/", "http://[::1]:8765/", "http://127.0.0.2:9/mcp/" })
                check(HttpSecurity.IsLoopbackPrefix(ok), "loopback prefix: " + ok);
            foreach (var bad in new[] { "http://+:8765/", "http://*:8765/", "http://0.0.0.0:8765/", "http://192.168.0.10:8765/", "http://myhost:8765/", "", "127.0.0.1:8765" })
                check(!HttpSecurity.IsLoopbackPrefix(bad), "not a loopback prefix: '" + bad + "'");

            check(HttpSecurity.IsAllowedOrigin(null) && HttpSecurity.IsAllowedOrigin(""), "no Origin (a non-browser client) is allowed");
            check(HttpSecurity.IsAllowedOrigin("http://localhost:3000") && HttpSecurity.IsAllowedOrigin("http://127.0.0.1")
                  && HttpSecurity.IsAllowedOrigin("http://[::1]:8080"), "local origins are allowed");
            check(!HttpSecurity.IsAllowedOrigin("https://evil.example") && !HttpSecurity.IsAllowedOrigin("http://192.168.0.5"),
                "a page on another host cannot drive the server");
            check(!HttpSecurity.IsAllowedOrigin("null") && !HttpSecurity.IsAllowedOrigin("file:///c:/x.html"),
                "sandboxed and file:// pages are rejected");

            check(HttpSecurity.ConstantTimeEquals("s3cret", "s3cret"), "equal secrets match");
            check(!HttpSecurity.ConstantTimeEquals("s3cret", "s3creT") && !HttpSecurity.ConstantTimeEquals("s3cret", "s3cre")
                  && !HttpSecurity.ConstantTimeEquals("s3cre", "s3cret") && !HttpSecurity.ConstantTimeEquals(null, "x"),
                "different, shorter, longer and missing secrets do not match");

            var redacted = HttpSecurity.RedactArgs(new[] { "--transport", "http", "--http-api-key", "s3cret", "--http-api-key=abc", "--profile", "full" });
            check(!redacted.Contains("s3cret") && !redacted.Contains("abc") && redacted.Contains("--profile full"),
                "the startup log never contains the API key: " + redacted);

            check(HttpSecurity.ResolveApiKey(null, _ => "fromEnv") == "fromEnv", "the key can come from the environment");
            check(HttpSecurity.ResolveApiKey("cli", _ => "fromEnv") == "cli", "the command line wins over the environment");
            check(HttpSecurity.ResolveApiKey(null, _ => "") == null, "an empty environment variable means no key");
        }
    }
}
