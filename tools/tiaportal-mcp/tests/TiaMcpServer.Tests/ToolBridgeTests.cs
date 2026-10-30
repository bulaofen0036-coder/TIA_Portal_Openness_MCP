using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// CallTool's binding and invocation. The two shapes that used to fail silently:
    /// an async tool came back as a serialized Task object instead of its result, and a tool
    /// taking a server-injected parameter always failed with "missing required argument(s)".
    /// </summary>
    internal static class ToolBridgeTests
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        internal static void Run(Action<bool, string> check)
        {
            var server = new FakeServer("srv");
            ReflectiveToolInvoker.Injector inject = (Type t, out object? v) =>
            {
                v = t == typeof(FakeServer) ? server : null;
                return t == typeof(FakeServer);
            };

            object? Call(string method, string json, ReflectiveToolInvoker.Injector? inj = null)
            {
                var m = typeof(SampleTools).GetMethod(method, BindingFlags.Public | BindingFlags.Static)!;
                var b = ReflectiveToolInvoker.Bind(method, m, JsonNode.Parse(json) as JsonObject, inj ?? inject, Json);
                if (b.Error != null) return "ERR:" + b.Error;
                return ReflectiveToolInvoker.InvokeAsync(m, b.Arguments).GetAwaiter().GetResult();
            }

            check(Equals(Call("Echo", "{\"text\":\"hi\"}"), "hi"), "sync tool: optional parameter takes its default");
            check(Equals(Call("Echo", "{\"text\":\"hi\",\"times\":3}"), "hihihi"), "sync tool: supplied optional parameter is used");
            check(Equals(Call("Echo", "{\"Text\":\"hi\"}"), "hi"), "bridge binding stays case-insensitive (it binds by itself)");

            var unknown = Call("Echo", "{\"text\":\"hi\",\"bogus\":1}") as string ?? "";
            check(unknown.StartsWith("ERR:") && unknown.Contains("bogus") && unknown.Contains("SILENTLY IGNORED")
                  && unknown.Contains("nothing was executed"),
                "an argument nobody reads is refused before anything runs: " + unknown);
            var typo = Call("Echo", "{\"text\":\"hi\",\"tims\":2}") as string ?? "";
            check(typo.Contains("tims -> times"), "a near-miss name gets a suggestion: " + typo);

            var missing = Call("Echo", "{}") as string ?? "";
            check(missing.StartsWith("ERR:") && missing.Contains("missing required argument(s): text"),
                "a missing required argument is named: " + missing);

            var badType = Call("Echo", "{\"text\":\"hi\",\"times\":\"many\"}") as string ?? "";
            check(badType.StartsWith("ERR:") && badType.Contains("could not be read as integer"),
                "a wrongly typed argument says which type was expected: " + badType);

            // The regression: Task<T> must come back as T, not as {Result, Id, Status, ...}.
            check(Equals(Call("AddAsync", "{\"a\":2,\"b\":3}"), 5), "async tool: Task<int> is awaited and unwrapped");
            check(Call("DoAsync", "{}") == null, "async tool: plain Task yields null, not VoidTaskResult");
            check(Equals(Call("EchoValueTask", "{\"s\":\"x\"}"), "x!"), "ValueTask<T> is awaited and unwrapped");

            // The other regression: server-injected parameters are filled, not demanded.
            check(Equals(Call("WithInjected", "{\"name\":\"n\"}"), "srv:n"), "injected parameter is supplied by the bridge");
            var injectedAsArg = Call("WithInjected", "{\"name\":\"n\",\"server\":\"x\"}") as string ?? "";
            check(injectedAsArg.StartsWith("ERR:") && injectedAsArg.Contains("server"),
                "the model cannot pass an injected parameter itself");

            var sig = ReflectiveToolInvoker.RenderSignature("WithInjected",
                typeof(SampleTools).GetMethod("WithInjected")!, inject);
            check(sig == "WithInjected(name: string)", "signature hides injected and CancellationToken parameters: " + sig);
            var sig2 = ReflectiveToolInvoker.RenderSignature("Echo", typeof(SampleTools).GetMethod("Echo")!, inject);
            check(sig2 == "Echo(text: string, times?: integer = 1)", "signature shows defaults: " + sig2);

            // A tool's own exception surfaces unwrapped, like a direct call.
            try
            {
                Call("Throws", "{}");
                check(false, "a throwing tool must throw through the bridge");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message == "boom", "the tool's own exception type and message come through");
            }
            catch (Exception ex)
            {
                check(false, "expected the tool's InvalidOperationException, got " + ex.GetType().Name);
            }
        }

        internal sealed class FakeServer
        {
            public FakeServer(string id) { Id = id; }
            public string Id { get; }
        }

        internal static class SampleTools
        {
            public static string Echo(string text, int times = 1)
            {
                var s = "";
                for (int i = 0; i < times; i++) s += text;
                return s;
            }

            public static async Task<int> AddAsync(int a, int b)
            {
                await Task.Yield();
                return a + b;
            }

            public static async Task DoAsync()
            {
                await Task.Yield();
            }

            public static ValueTask<string> EchoValueTask(string s) => new ValueTask<string>(s + "!");

            public static string WithInjected(FakeServer server, string name, CancellationToken cancellationToken = default)
                => server.Id + ":" + name;

            public static int Throws() => throw new InvalidOperationException("boom");
        }
    }
}
