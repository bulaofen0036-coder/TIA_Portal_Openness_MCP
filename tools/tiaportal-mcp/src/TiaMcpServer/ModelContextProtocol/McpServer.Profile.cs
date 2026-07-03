using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    // TIA_MCP_PROFILE=lite: expose only [L0]/[L1] tools (~40 essentials) instead of
    // ~200, so a small / non-expert model is not drowned in choices and hosts with a
    // tool cap (VS Code: 128) can enable everything. Opt-in via env var; full profile
    // (WithToolsFromAssembly) stays the default. All tools are static so no DI target
    // is needed.
    public static partial class McpServer
    {
        public static IList<McpServerTool> GetLiteTools()
        {
            var tools = new List<McpServerTool>();
            foreach (var method in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() == null) continue;
                var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
                if (desc.StartsWith("[L0]", StringComparison.Ordinal) || desc.StartsWith("[L1]", StringComparison.Ordinal))
                {
                    tools.Add(McpServerTool.Create(method));
                }
            }
            return tools;
        }

        public static bool IsLiteProfile()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("TIA_MCP_PROFILE")?.Trim(),
                "lite", StringComparison.OrdinalIgnoreCase);
        }
    }
}
