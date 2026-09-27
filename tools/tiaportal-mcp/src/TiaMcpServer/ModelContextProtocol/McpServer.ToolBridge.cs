using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    // The escape hatch that lets the lite roster be the default without losing anything.
    //
    // Shipping the whole roster costs ~40k tokens of JSON schema in every single turn and
    // exceeds what Copilot (128) and Windsurf (100) will even load. Shipping only the lite
    // tools fixes that but used to be a dead end: a model in lite could not reach
    // ExportPlcWatchTable at all, and had no way to find out it existed.
    //
    // FindTools + CallTool close that gap: two tools (~700 tokens) buy on-demand access to
    // the entire roster. The model searches when it needs something the roster lacks, reads
    // just that one signature, and calls it. This is the progressive-disclosure / tool-search
    // pattern that Anthropic, VS Code and the agent gateways all converged on during 2025-26.
    public static partial class McpServer
    {
        // name -> the static method carrying [McpServerTool]. Built once.
        private static Dictionary<string, MethodInfo>? _allToolMethods;

        private static Dictionary<string, MethodInfo> AllToolMethods()
        {
            if (_allToolMethods != null) return _allToolMethods;
            var map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                map[attr.Name ?? m.Name] = m;
            }
            _allToolMethods = map;
            return map;
        }

        private static string ToolDescription(MethodInfo m)
        {
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            return d == null ? "" : d.Description;
        }

        /// <summary>The SDK-owned parameters a tool method may declare. They are never part of the
        /// arguments the model sends, so FindTools does not show them and CallTool fills them.</summary>
        private static bool IsInjectedParameterType(Type t) =>
            t == typeof(IMcpServer)
            || t == typeof(RequestContext<CallToolRequestParams>)
            || t == typeof(CancellationToken);

        [McpServerTool(Name = "FindTools"), Description(
            "[L0][Meta] Search the FULL tool roster, including tools not listed in this session. " +
            "By default the server lists only a core 'lite' roster so the tool list stays small and every host can load it; " +
            "everything else is reached through this tool plus CallTool. " +
            "USE THIS whenever the visible tools do not cover what you need, before concluding the server cannot do something. " +
            "Search by capability words, not exact names: 'watch table', 'HMI screen', 'download', 'cross reference', 'GSD'. " +
            "Returns each match's exact name, parameter signature with defaults, and full description; then invoke it with CallTool.")]
        public static ResponseStringList FindTools(
            [Description("query: space-separated words matched against tool names and descriptions, e.g. 'export watch table'. Empty lists the whole roster.")] string query = "",
            [Description("limit: max tools to return (default 12). Raise it for a broad survey.")] int limit = 12)
        {
            try
            {
                var all = AllToolMethods();
                if (limit <= 0) limit = 12;

                var terms = (query ?? "")
                    .Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .ToArray();

                var scored = new List<KeyValuePair<int, string>>();
                foreach (var kv in all)
                {
                    string lname = kv.Key.ToLowerInvariant();
                    string desc = ToolDescription(kv.Value).ToLowerInvariant();
                    int score = 0;
                    if (terms.Length == 0) score = 1;
                    foreach (var t in terms)
                    {
                        // Name hits outrank description hits: a model searching "watch table"
                        // wants ExportPlcWatchTable ahead of every tool that merely mentions it.
                        if (lname == t) score += 100;
                        else if (lname.Contains(t)) score += 20;
                        if (desc.Contains(t)) score += 3;
                    }
                    if (score > 0) scored.Add(new KeyValuePair<int, string>(score, kv.Key));
                }

                if (scored.Count == 0)
                {
                    return new ResponseStringList
                    {
                        Message = "No tool matches '" + query + "'. Try fewer or more general words " +
                                  "(e.g. 'watch table' instead of 'ExportPlcWatchTableToCsv'), " +
                                  "or call FindTools with an empty query to list everything.",
                        Meta = BridgeMeta(true),
                    };
                }

                var hits = scored
                    .OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal)
                    .Take(limit).ToList();

                bool lite = IsLiteProfile();
                var lines = new List<string>();
                foreach (var h in hits)
                {
                    var m = all[h.Value];
                    bool listed = !lite || LiteToolNames.Contains(h.Value);
                    lines.Add(ReflectiveToolInvoker.RenderSignature(h.Value, m, Injector(null, null, default))
                              + (listed ? "  [already listed - call it directly]" : "  [call via CallTool]"));
                    lines.Add("    " + ToolDescription(m));
                }

                return new ResponseStringList
                {
                    Message = hits.Count + " of " + scored.Count + " matching tools (roster: " + all.Count + " total). " +
                              "Tools marked [call via CallTool] are not in this session's tool list - " +
                              "invoke them with CallTool(name, argumentsJson).",
                    Items = lines,
                    Meta = BridgeMeta(true),
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList { Message = "FindTools failed: " + ex.Message, Meta = BridgeMeta(false) };
            }
        }

        [McpServerTool(Name = "CallTool"), Description(
            "[L0][Meta] Invoke a tool from the full roster by name, including ones not listed in this session. " +
            "Use FindTools first to get the exact name and parameter signature. " +
            "Returns exactly what calling the tool directly returns, and unknown argument names are rejected before anything runs. " +
            "Tools that download to a CPU, go online, write watch tables or delete blocks/types/tag tables/external sources " +
            "are refused here: call those by their own name so the user sees what will run. " +
            "Example: name='ExportPlcWatchTable', argumentsJson='{\"softwarePath\":\"PLC_1\",\"watchTableName\":\"WT1\"}'.")]
        public static async Task<object?> CallTool(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("name: exact tool name from FindTools, e.g. 'ExportPlcWatchTable'.")] string name,
            [Description("argumentsJson: JSON object of the tool's arguments, e.g. '{\"softwarePath\":\"PLC_1\"}'. Omit or '{}' for a no-argument tool.")] string argumentsJson = "",
            CancellationToken cancellationToken = default)
        {
            string target = (name ?? "").Trim();
            if (target.Length == 0)
                throw new McpException("CallTool: 'name' is required. Call FindTools to look up a tool name.", McpErrorCode.InvalidParams);

            // Self-recursion would be a loop with no purpose; refuse it explicitly.
            if (string.Equals(target, "CallTool", StringComparison.OrdinalIgnoreCase))
                throw new McpException("CallTool cannot invoke itself. Pass the target tool's own name.", McpErrorCode.InvalidParams);

            var all = AllToolMethods();
            if (!all.TryGetValue(target, out var method))
            {
                // A wrong name is the likeliest failure, so spend the message on the fix
                // rather than on restating the problem.
                var near = all.Keys
                    .Where(k => k.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0
                             || target.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(k => k, StringComparer.Ordinal).Take(8).ToList();
                // Containment misses the commonest case of all - a typo in the middle of an
                // otherwise correct name ("ExportPlcWatchTabel"). Fall back to shared prefix.
                if (near.Count == 0)
                    near = all.Keys
                        .Select(k => new KeyValuePair<int, string>(CommonPrefixLength(k, target), k))
                        .Where(x => x.Key >= 6)
                        .OrderByDescending(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal)
                        .Take(5).Select(x => x.Value).ToList();
                throw new McpException("No tool named '" + target + "'." + (near.Count > 0
                    ? " Did you mean: " + string.Join(", ", near) + "?"
                    : " Call FindTools with a capability keyword to find the right name."), McpErrorCode.InvalidParams);
            }

            // Canonical casing from here on, so the safety table and the error messages agree.
            target = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;

            if (ToolSafety.Classify(target).DirectOnly)
                throw new McpException(
                    "CallTool refuses '" + target + "': it changes a running CPU or deletes engineering data, so it must be " +
                    "called by its own name - the host then shows the user exactly what will run. It is in this session's " +
                    "tool list; call " + target + " directly.", McpErrorCode.InvalidRequest);

            JsonObject args;
            if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}")
            {
                args = new JsonObject();
            }
            else
            {
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(argumentsJson); }
                catch (JsonException jx)
                {
                    throw new McpException(
                        "argumentsJson is not valid JSON (" + jx.Message + "). It must be a JSON OBJECT of the " +
                        "tool's parameters, e.g. {\"softwarePath\":\"PLC_1\"} - not a bare value, not the tool name.",
                        McpErrorCode.InvalidParams);
                }
                args = parsed as JsonObject
                    ?? throw new McpException(
                        "argumentsJson must be a JSON object, e.g. {\"softwarePath\":\"PLC_1\"}. " +
                        "Expected signature: " + ReflectiveToolInvoker.RenderSignature(target, method, Injector(server, context, cancellationToken)),
                        McpErrorCode.InvalidParams);
            }

            // The generic reflection bridges run any public method once allowWrite is set. Through
            // CallTool the host would only ever see "CallTool"; make the write visible instead.
            if ((target == "InvokeObject" || target == "InvokeService") && IsTrue(args, "allowWrite"))
                throw new McpException(
                    target + " with allowWrite=true is refused through CallTool: it can invoke any public Openness method, " +
                    "including ones that close or delete. Start the server with --profile full so " + target +
                    " is listed and the host can ask the user before it runs.", McpErrorCode.InvalidRequest);

            var binding = ReflectiveToolInvoker.Bind(target, method, args, Injector(server, context, cancellationToken), BridgeJson);
            if (binding.Error != null)
                throw new McpException(binding.Error, McpErrorCode.InvalidParams);

            // The target's own response object goes back unchanged, so the SDK serializes it with
            // the same options as a direct call: same field names, same shape, same error handling.
            // A target that throws propagates as-is (McpException or not), exactly like a direct call.
            return await ReflectiveToolInvoker.InvokeAsync(method, binding.Arguments).ConfigureAwait(false);
        }

        private static ReflectiveToolInvoker.Injector Injector(
            IMcpServer? server, RequestContext<CallToolRequestParams>? context, CancellationToken cancellationToken)
        {
            return (Type t, out object? value) =>
            {
                value = null;
                if (!IsInjectedParameterType(t)) return false;
                if (t == typeof(IMcpServer)) value = server;
                else if (t == typeof(RequestContext<CallToolRequestParams>)) value = context;
                else value = cancellationToken;
                return true;
            };
        }

        private static bool IsTrue(JsonObject args, string key)
        {
            foreach (var kv in args)
            {
                if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Value is JsonValue v)
                {
                    if (v.TryGetValue<bool>(out var b)) return b;
                    if (v.TryGetValue<string>(out var s)) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        private static JsonObject BridgeMeta(bool success)
        {
            return new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = success };
        }

        private static readonly JsonSerializerOptions BridgeJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // Chinese project/block names must survive the round trip unescaped.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
