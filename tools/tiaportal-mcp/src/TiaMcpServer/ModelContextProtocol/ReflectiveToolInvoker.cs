using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// How CallTool runs a tool the session does not list: bind a JSON object to the method's
    /// parameters, invoke it, and await whatever it returns.
    ///
    /// Kept free of MCP SDK types so the offline suite can drive it. The SDK-only parameters
    /// (IMcpServer, RequestContext, CancellationToken) come in through <see cref="Injector"/>.
    ///
    /// The two failures this replaced: async tools came back as a serialized Task object
    /// ({Result, Id, Status, ...}) instead of their result, and tools that take an injected
    /// parameter always failed with "missing required argument(s): server, context".
    /// </summary>
    public static class ReflectiveToolInvoker
    {
        /// <summary>Fills a parameter the caller never sends. Returns false for types it does not own.</summary>
        public delegate bool Injector(Type parameterType, out object? value);

        public sealed class Binding
        {
            public object?[] Arguments = Array.Empty<object?>();
            /// <summary>Null when every argument bound; otherwise a sentence the model can act on.</summary>
            public string? Error;
        }

        public static Binding Bind(
            string toolName,
            MethodInfo method,
            JsonObject? args,
            Injector? inject,
            JsonSerializerOptions options)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            args ??= new JsonObject();

            var ps = method.GetParameters();
            var call = new object?[ps.Length];
            var matchedKeys = new HashSet<string>(StringComparer.Ordinal);
            var missing = new List<string>();

            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (inject != null && inject(p.ParameterType, out var injected))
                {
                    call[i] = injected;
                    continue;
                }
                if (p.ParameterType == typeof(CancellationToken))
                {
                    call[i] = CancellationToken.None;
                    continue;
                }

                // Case-insensitive on purpose: models routinely send PascalCase for a camelCase
                // parameter, and this path binds by itself rather than through the SDK.
                JsonNode? value = null;
                bool found = false;
                foreach (var kv in args)
                {
                    if (!string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    matchedKeys.Add(kv.Key);
                    value = kv.Value;
                    found = kv.Value != null;
                    break;
                }

                if (!found)
                {
                    if (p.HasDefaultValue) { call[i] = p.DefaultValue; continue; }
                    missing.Add(p.Name!);
                    continue;
                }

                try
                {
                    call[i] = value!.Deserialize(p.ParameterType, options);
                }
                catch (Exception ex)
                {
                    return new Binding
                    {
                        Error = "Argument '" + p.Name + "' of " + toolName + " could not be read as " +
                                FriendlyTypeName(p.ParameterType) + ": " + ex.Message +
                                ". Expected signature: " + RenderSignature(toolName, method, inject),
                    };
                }
            }

            // An argument nobody reads would be dropped without a word, and the caller would
            // believe the option took effect. Refuse instead, before anything runs.
            var unknown = args.Select(kv => kv.Key).Where(k => !matchedKeys.Contains(k)).ToList();
            if (missing.Count > 0 || unknown.Count > 0)
            {
                var names = BindableParameters(method, inject).Select(p => p.Name!).ToList();
                var parts = new List<string>();
                if (missing.Count > 0)
                    parts.Add("missing required argument(s): " + string.Join(", ", missing) + ".");
                if (unknown.Count > 0)
                {
                    var hints = unknown
                        .Select(u => new { u, near = ArgDiagnostics.NearestName(u, names) })
                        .Where(x => x.near != null)
                        .Select(x => x.u + " -> " + x.near)
                        .ToList();
                    parts.Add("unknown argument(s) that would have been SILENTLY IGNORED: " + string.Join(", ", unknown) + "."
                              + (hints.Count > 0 ? " Did you mean: " + string.Join(", ", hints) + "?" : ""));
                }
                return new Binding
                {
                    Error = toolName + ": " + string.Join(" ", parts) +
                            " Expected signature: " + RenderSignature(toolName, method, inject) +
                            " (nothing was executed).",
                };
            }

            return new Binding { Arguments = call };
        }

        /// <summary>Invokes a static tool method and returns what a direct call would have produced:
        /// the awaited value of Task&lt;T&gt; / ValueTask&lt;T&gt;, null for Task / ValueTask / void.
        /// The tool's own exception is rethrown unwrapped, with its original stack.</summary>
        public static async Task<object?> InvokeAsync(MethodInfo method, object?[] arguments)
        {
            object? raw;
            try
            {
                raw = method.Invoke(null, arguments);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
            return await UnwrapAsync(raw, method.ReturnType).ConfigureAwait(false);
        }

        public static async Task<object?> UnwrapAsync(object? raw, Type declaredReturnType)
        {
            if (raw == null) return null;

            if (raw is Task task)
            {
                await task.ConfigureAwait(false);
                // Decide by the declared type, not the runtime one: an `async Task` method's task
                // is a Task<VoidTaskResult> internally, which has a Result nobody should see.
                if (declaredReturnType.IsGenericType && declaredReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                    return declaredReturnType.GetProperty("Result")!.GetValue(task);
                return null;
            }

            if (raw is ValueTask vt)
            {
                await vt.ConfigureAwait(false);
                return null;
            }

            var rt = raw.GetType();
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var asTask = (Task)rt.GetMethod("AsTask")!.Invoke(raw, null)!;
                return await UnwrapAsync(asTask, typeof(Task<>).MakeGenericType(rt.GetGenericArguments()[0])).ConfigureAwait(false);
            }

            return raw;
        }

        /// <summary>'Tool(a: string, b?: integer = 12)', leaving out what the server injects.</summary>
        public static string RenderSignature(string name, MethodInfo method, Injector? inject = null)
        {
            var parts = new List<string>();
            foreach (var p in BindableParameters(method, inject))
            {
                string t = FriendlyTypeName(p.ParameterType);
                // Optional params are what a model most often gets wrong, so show the actual
                // default rather than a bare "?".
                if (!p.HasDefaultValue) { parts.Add(p.Name + ": " + t); continue; }
                string def;
                if (p.DefaultValue == null) def = "null";
                else if (p.DefaultValue is bool b) def = b ? "true" : "false";
                else if (p.DefaultValue is string s) def = "\"" + s + "\"";
                else def = Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                parts.Add(p.Name + "?: " + t + " = " + def);
            }
            return name + "(" + string.Join(", ", parts) + ")";
        }

        public static string FriendlyTypeName(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return "string";
            if (u == typeof(bool)) return "boolean";
            if (u == typeof(int) || u == typeof(long)) return "integer";
            if (u == typeof(double) || u == typeof(float) || u == typeof(decimal)) return "number";
            if (u.IsArray) return FriendlyTypeName(u.GetElementType()!) + "[]";
            return u.Name;
        }

        private static IEnumerable<ParameterInfo> BindableParameters(MethodInfo method, Injector? inject)
        {
            foreach (var p in method.GetParameters())
            {
                if (p.ParameterType == typeof(CancellationToken)) continue;
                if (inject != null && inject(p.ParameterType, out _)) continue;
                yield return p;
            }
        }
    }
}
