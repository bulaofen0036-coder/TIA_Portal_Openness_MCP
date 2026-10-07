using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    // Tool roster size. DEFAULT = lite: the core tools instead of the full roster, so a small /
    // non-expert model is not drowned in choices, hosts with a tool cap (Copilot 128,
    // Windsurf 100) can load the server at all, and every turn carries ~8k instead of
    // ~40k tokens of schema. Opt out per session with --profile full / TIA_MCP_PROFILE=full;
    // reach any individual non-lite tool without opting out via FindTools + CallTool.
    // All tools are static so no DI target is needed.
    public static partial class McpServer
    {
        // Explicit allowlist (tool Name, not method name). Kept explicit on purpose:
        // membership must not silently change when a [Lx] description prefix is edited.
        // = the everyday [L0]/[L1] tools + the golden-path tools ServerInstructions/GetAuthoringGuide
        // tell the model to call (previously [L2] and thus missing from lite — a weak
        // model in lite was instructed to call ImportFromDocuments and couldn't see it)
        // + every ToolSafety.DirectOnlyTools entry. Some [L1] tools (the VCI and online-state
        // readers, GetProjectTopology, …) are deliberately left to FindTools/CallTool.
        private static readonly HashSet<string> LiteToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // L0 — the bridge to everything not listed here. Without these two, lite is a
            // dead end: the model cannot even discover that the other tools exist.
            "FindTools", "CallTool",
            // L0 — orientation / diagnostics
            "Bootstrap", "Doctor", "GetState", "GetAuthoringGuide",
            "GenerateAcceptanceReport", "GenerateErrorReport",
            "RunCapabilitySelfTest", "RunOnlineMonitoringSafetySelfTest",
            // L1 — session / project lifecycle
            "Connect", "Disconnect", "ListPortalProcessProjects", "EnsureOpennessUserGroup",
            // 用户在博途界面里开着工程时，Connect 会接管那个实例、OpenProject 又拒绝动它，
            // 整台服务器就用不了了。这条出口必须在默认档里看得见 —— 挡掉它等于让
            // 「用户正在用博途」变成一个无解的死局。
            "ConnectIsolated",
            "OpenProject", "AttachToOpenProject", "CreateProject", "SaveProject", "CloseProject",
            "GetProject", "GetProjectTree", "ValidateAutomationContext",
            // L1 — read / understand
            "GetSoftwareInfo", "GetSoftwareTree", "GetDevices", "DescribeBlockLogic",
            // L1 — build / import / compile
            "ScaffoldProject", "PlcBuildAndImport", "ImportBlock", "ImportType",
            "ImportPlcTagTable", "WritePlcSclSourceFile",
            "CompileSoftware", "CompileAndDiagnosePlc",
            // The HMI counterpart. Without it a lite session can generate Unified screens but
            // cannot read its own HMI compile errors, so it has to hand the project back to the
            // engineer to compile in the UI (#24).
            "CompileAndDiagnoseHmi",
            // L1 — hardware
            "AddDeviceWithFallback", "SearchHardwareCatalog", "ConnectDeviceNodesToProfinetSubnet",
            // Golden-path tools referenced by ServerInstructions / GetAuthoringGuide
            // (previously [L2]; without them the lite roster contradicts the instructions)
            "ImportFromDocuments", "GenerateBlocksFromExternalSource",
            // Batch SD import/export are the "PREFERRED on V21+" batch path in the same
            // instructions; tag tables and cross-references are what a model needs to read a
            // project it did not write.
            "ImportBlocksFromDocuments", "ExportBlocksAsDocuments",
            "GetPlcTagTables", "GetCrossReferences",
            "GetBlocks", "GetBlocksWithHierarchy", "GetBlockInfo",
            "ExportAsDocuments", "GoOffline",
            // 大响应寄存与分页。**任何档都必须能翻页** —— 超过阈值的响应会被寄存，
            // 挡掉这几个出口等于内容直接丢：真实工程上 GetBlocks 的首页只装得下十几个块，
            // 剩下的拿不回来。它们只碰引擎自己内存里的那份副本，一个都不动 TIA 工程。
            "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports",
            // 会动 CPU 或删工程数据的工具必须以本名出现在工具表里（CallTool 拒绝转发它们）：
            // 宿主逐个工具请求批准时，用户看到的才是「DownloadToPlc」而不是一个语焉不详的「CallTool」。
            // 这一组必须与 ToolSafety.DirectOnlyTools 完全一致 —— 离线用例钉着。
            "DownloadToPlc", "GoOnline", "SetWatchTableModifyValue",
            "DeletePlcBlock", "DeletePlcType", "DeletePlcTagTable", "DeletePlcExternalSource",
        };

        /// <summary>The lite roster as data, for the offline suite and the self-test.</summary>
        public static IReadOnlyCollection<string> LiteRoster => LiteToolNames;

        public static IList<McpServerTool> GetLiteTools()
        {
            var tools = new List<McpServerTool>();
            foreach (var method in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                var name = attr.Name ?? method.Name;
                if (LiteToolNames.Contains(name))
                {
                    tools.Add(McpServerTool.Create(method));
                }
            }
            return tools;
        }

        /// <summary>
        /// 全量工具表。存在的理由是**能拿到列表才能包装它** —— 注册时原来走
        /// `WithToolsFromAssembly()`，那条路直接把工具塞进容器，中间没有一个可以插手的地方，
        /// 于是参数诊断、大响应分页这类「每个工具都该有」的能力根本接不上去。
        /// </summary>
        public static IList<McpServerTool> GetAllTools()
        {
            var tools = new List<McpServerTool>();
            foreach (var method in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() == null) continue;
                tools.Add(McpServerTool.Create(method));
            }
            return tools;
        }

        // ---- Profile resolution -----------------------------------------------------------------
        // LITE IS THE DEFAULT. Measured on the V21 engine: the full roster is ~160 KB of JSON
        // schema (~40k tokens) that every host re-sends to the model on EVERY turn, before any
        // work happens. Lite is roughly a quarter of the tools and of the tokens.
        // It is also a hard compatibility wall, not just a cost: VS Code / Copilot refuse to
        // run agent mode above 128 tools and Windsurf is capped at 100, so the full roster
        // simply does not load there. Nothing is lost by defaulting to lite — FindTools /
        // CallTool (McpServer.ToolBridge.cs) reach every one of the other tools on demand.
        // Precedence: --profile flag > TIA_MCP_PROFILE env > lite.
        private static string? _profileOverride;

        /// <summary>Applies the CLI --profile flag. Wins over TIA_MCP_PROFILE. Call before building the host.</summary>
        public static void SetProfileOverride(string? profile)
        {
            _profileOverride = string.IsNullOrWhiteSpace(profile) ? null : profile!.Trim();
        }

        /// <summary>Resolved profile name, always lowercase: "lite" or "full".</summary>
        public static string ResolvedProfile()
        {
            string? p = _profileOverride;
            if (string.IsNullOrEmpty(p)) p = Environment.GetEnvironmentVariable("TIA_MCP_PROFILE");
            p = p?.Trim();
            if (string.IsNullOrEmpty(p)) return "lite";
            // Only "full" (and the historical "all") opts out; anything else — including a
            // typo — stays on the safe, host-compatible lite roster rather than silently
            // blowing past a host's tool cap.
            if (string.Equals(p, "full", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, "all", StringComparison.OrdinalIgnoreCase)) return "full";
            return "lite";
        }

        public static bool IsLiteProfile()
        {
            return ResolvedProfile() == "lite";
        }
    }
}
