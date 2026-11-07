using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 2.7.11: #60 follow-up (TIA's non-throwing cross-reference diagnostic node), #67 (server-side read-only
    /// mode), and the wiring of #66 (bulk export by software unit) and #68 (software unit tag tables), whose
    /// behaviour needs TIA and is verified on the real engine.
    /// </summary>
    internal static class Release2711Tests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            RunDiagnosticNode(check);
            RunReadOnlyPolicy(check);
            RunReadOnlyRosterSweep(check, skip);
            RunWiring(check, skip);
        }

        // ---------------- #60 diagnostic node ----------------

        private sealed class Loc { public string Name => "NW1"; public string ReferenceLocation => "NW1"; public string ReferenceType => "UsedBy"; public string Access => "Read"; }
        private sealed class Ref { public string Name => "Caller"; public string Path => "Caller"; public IEnumerable Locations => new[] { new Loc() }; }

        /// <summary>Records which branch getters were touched, like the reporter's repro.</summary>
        private sealed class Node
        {
            public Node(string name, string path, params Node[] children) { Name = name; Path = path; ChildItems = children; }
            public string Name { get; }
            public string Path { get; }
            public object[] RefItems { get; set; } = new object[] { new Ref() };
            public Node[] ChildItems { get; }
            public List<string> Reads { get; } = new List<string>();
            public IEnumerable References { get { Reads.Add("References"); return RefItems; } }
            public IEnumerable Children { get { Reads.Add("Children"); return ChildItems; } }
        }
        private sealed class Result { public IEnumerable Sources { get; set; } = Array.Empty<object>(); }

        private const string ZhDiagnostic = "发生意外结果。请在“设置”中重新创建交叉引用信息。在“选项”中，转至“设置 > 常规 > 交叉引用”。再次编译所有相关的块。";

        private static void RunDiagnosticNode(Action<bool, string> check)
        {
            // The reporter's shape: a parent without references whose only child is TIA's message, empty Path,
            // empty References / Children, no exception. Before 2.7.11 this came back as "0 entries, complete".
            var diag = new Node(ZhDiagnostic, "") { RefItems = Array.Empty<object>() };
            var parent = new Node("SyntheticParent", "PLC_1", diag) { RefItems = Array.Empty<object>() };
            var items = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { parent } }, "fb", out var t, out var unread);
            check(items.Count == 0 && t != null && unread.Count == 1 && unread[0].StartsWith("SyntheticParent [TIA returned a cross-reference diagnostic"),
                "#60 补充：博途返回诊断文案节点（不抛错）时标不完整、点名所在分支（截断=" + (t ?? "null") + "）");
            check(diag.Reads.Count == 0, "#60 补充：认出诊断节点后不再读它的 References / Children（实际读了：" + string.Join(",", diag.Reads) + "）");
            check(t != null && t.Contains("INCOMPLETE"), "#60 补充：截断说明写明清单不完整、不能据此删除");

            var en = new Node("An unexpected result occurred. Re-create the cross-reference information in the settings.", "") { RefItems = Array.Empty<object>() };
            CrossReferenceFlattener.Flatten(new Result { Sources = new[] { new Node("P", "PLC_1", en) { RefItems = Array.Empty<object>() } } }, "fb", out var tEn, out _);
            check(tEn != null && en.Reads.Count == 0, "#60 补充：英文界面的同一提示也认得出");
            check(CrossReferenceFlattener.IsDiagnosticNode("Unexpected result. Recreate the cross reference information.", ""), "#60 补充：英文写成 cross reference（不带连字符）也认得出");

            // reverse sentinels: neither condition alone is enough.
            var emptyPath = new Node("Motor", "");
            var okItems = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { new Node("DB", "PLC_1", emptyPath) { RefItems = Array.Empty<object>() } } }, "fb", out var t1, out _);
            check(t1 == null && okItems.Count == 1 && emptyPath.Reads.Contains("References"), "#60 补充·反向哨兵：普通节点 Path 为空照常读，不误判成诊断");
            var namedLikeDiag = new Node(ZhDiagnostic, "PLC_1\\Program blocks");
            var okItems2 = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { namedLikeDiag } }, "fb", out var t2, out _);
            check(t2 == null && okItems2.Count == 1, "#60 补充·反向哨兵：名字像提示但有 Path 的节点照常读");
            check(!CrossReferenceFlattener.IsDiagnosticNode("", "") && !CrossReferenceFlattener.IsDiagnosticNode("交叉引用表", ""),
                "#60 补充·反向哨兵：空名字、只含「交叉引用」的普通名字不算诊断");
        }

        // ---------------- #67 read-only policy ----------------

        private static void RunReadOnlyPolicy(Action<bool, string> check)
        {
            Func<string, bool?> none = _ => null;
            string[] allowed = { "GetBlocks", "GetPlcTags", "GetCrossReferences", "DescribeBlockLogic", "ExportBlocks", "ExportBlock",
                                 "ExportPlcTagTable", "ExportBlocksAsDocuments", "GetExport", "SaveExport", "FindTools", "CallTool",
                                 "Bootstrap", "Connect", "ConnectIsolated", "AttachToOpenProject", "OpenProject", "CloseProject", "Disconnect",
                                 "ReadPlcLiveValuesS7" };
            var wronglyRefused = allowed.Where(n => ReadOnlyPolicy.Refusal(n, none) != null).ToList();
            check(wronglyRefused.Count == 0, "#67 只读模式放行读取、导出、会话类工具（被误拒：" + string.Join(", ", wronglyRefused) + "）");

            string[] refused = { "SetPlcTag", "SetPlcTags", "CompileSoftware", "CompileAndDiagnosePlc", "SaveProject", "DownloadToPlc", "GoOnline",
                                 "ImportBlock", "ImportFromDocuments", "DeletePlcBlock", "CreatePlcTagTable", "RenamePlcObject",
                                 "GenerateBlocksFromExternalSource", "PlcSimWriteTags", "SetWatchTableModifyValue", "InvokeObject", "InvokeService",
                                 "ScaffoldProject", "CreateProject", "SaveAsProject", "ArchiveProject", "NoSuchToolXyz", "" };
            var wronglyAllowed = refused.Where(n => ReadOnlyPolicy.Refusal(n, none) == null).ToList();
            check(wronglyAllowed.Count == 0, "#67 只读模式拒绝改工程、编译、保存、下载、写在线值、反射和未知工具（被放行：" + string.Join(", ", wronglyAllowed) + "）");

            var r = ReadOnlyPolicy.Refusal("SetPlcTag", none) ?? "";
            check(r.StartsWith("READ-ONLY MODE") && r.Contains("Nothing was executed"), "#67 拒绝文案说清是只读模式、什么都没执行");

            check(ReadOnlyPolicy.Refusal("OpenProject", k => k == "closeForeignProject" ? true : (bool?)null) != null
                  && ReadOnlyPolicy.Refusal("CloseProject", k => k == "closeForeignProject" ? true : (bool?)null) != null,
                "#67 会话类工具带 closeForeignProject=true（会关掉用户的工程）被拒");
            check(ReadOnlyPolicy.Refusal("Doctor", none) != null && ReadOnlyPolicy.Refusal("Doctor", k => k == "fix" ? false : (bool?)null) == null,
                "#67 Doctor 默认会改 Windows 用户组所以被拒，fix=false 只诊断才放行");

            var before = Environment.GetEnvironmentVariable("TIA_MCP_READ_ONLY");
            try
            {
                Environment.SetEnvironmentVariable("TIA_MCP_READ_ONLY", null);
                var off = ReadOnlyPolicy.Enabled;
                Environment.SetEnvironmentVariable("TIA_MCP_READ_ONLY", "1");
                var on = ReadOnlyPolicy.Enabled;
                Environment.SetEnvironmentVariable("TIA_MCP_READ_ONLY", "0");
                var zero = ReadOnlyPolicy.Enabled;
                check(!off && on && !zero, "#67 环境变量 TIA_MCP_READ_ONLY=1 开启，不设或 0 不开（默认不影响现有用户）");
            }
            finally { Environment.SetEnvironmentVariable("TIA_MCP_READ_ONLY", before); }
        }

        // Every tool the server registers: whatever read-only mode lets through must not be a writer by name.
        private static readonly string[] WriterVerbs =
        {
            "Set", "Delete", "Import", "Create", "Ensure", "Compile", "Save", "Download", "Apply", "Bind", "Move", "Rename",
            "Repair", "Seed", "Sync", "Scaffold", "Write", "Go", "Invoke", "Generate", "Add", "Plug", "Change", "Clear", "Copy",
            "Capture", "Archive", "Assign", "Detach", "Rebuild", "Plc", "Run", "Fix", "Workspace",
        };

        private static void RunReadOnlyRosterSweep(Action<bool, string> check, Action<string, string> skip)
        {
            var dir = FindSourceDir();
            if (dir == null) { skip("#67 全工具表扫描", "找不到引擎源码目录（只在仓内跑得了）"); return; }
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(Path.Combine(dir, "ModelContextProtocol"), "McpServer*.cs"))
                foreach (Match m in Regex.Matches(File.ReadAllText(f), "McpServerTool\\(Name = \"([A-Za-z0-9]+)\""))
                    names.Add(m.Groups[1].Value);
            check(names.Count > 200 && names.Contains("SetPlcTag") && names.Contains("GetBlocks"), "#67 扫描本身有效：从源码找到 " + names.Count + " 个工具（哨兵）");

            var allowed = names.Where(n => ReadOnlyPolicy.Refusal(n, _ => null) == null).ToList();
            // SaveExport writes an export into a file; the two Run*SafetySelfTest tools are offline self-tests that
            // ToolSafety lists as read-only one by one.
            var exempt = new HashSet<string> { "SaveExport", "RunHmiActionScriptRecipeSafetySelfTest", "RunOnlineMonitoringSafetySelfTest" };
            var suspicious = allowed.Where(n => !exempt.Contains(n) && WriterVerbs.Any(v => n.StartsWith(v, StringComparison.Ordinal) && n.Length > v.Length && char.IsUpper(n[v.Length]))).ToList();
            check(suspicious.Count == 0, "#67 只读模式放行的 " + allowed.Count + " 个工具里没有按名字看就是写操作的（可疑：" + string.Join(", ", suspicious) + "）");
            check(!allowed.Contains("SetPlcTag") && allowed.Contains("GetPlcTags"), "#67 扫描哨兵：SetPlcTag 必拒、GetPlcTags 必放");
        }

        // ---------------- wiring ----------------

        private static void RunWiring(Action<bool, string> check, Action<string, string> skip)
        {
            var dir = FindSourceDir();
            if (dir == null) { skip("#66/#67/#68 接线（源码形状）", "找不到引擎源码目录（只在仓内跑得了）"); return; }
            string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { dir }.Concat(p).ToArray()));

            var arg = Read("ModelContextProtocol", "McpServer.ArgDiagnostics.cs");
            check(arg.Contains("WrapWithReadOnlyPolicy(WrapWithArgDiagnostics("), "#67 只读策略包在工具注册的最外层（参数绑定前就拦）");
            var bridge = Read("ModelContextProtocol", "McpServer.ToolBridge.cs");
            check(bridge.Contains("ReadOnlyPolicy.Refusal(target"), "#67 CallTool 对真正的目标工具再判一次");
            check(Read("Program.cs").Contains("ReadOnlyPolicy.EnableFromCli(options.ReadOnly)") && Read("CliOptions.cs").Contains("\"--read-only\""), "#67 命令行 --read-only 接上");
            check(bridge.Contains("ReadOnlyPolicy.Refusal(target, key => ArgBool(args, key))") && bridge.Contains("TryGetValue<bool>(out var b)) return b;"),
                "#67 CallTool 里 JSON null 当作没给（绑定器会用默认值 fix=true），不当成 false 放行");
            var plcSw = Read("ModelContextProtocol", "McpServer.PlcSoftware.cs");
            var ao = plcSw.IndexOf("internal static T WithAutoOffline<T>", StringComparison.Ordinal);
            var aoBody = ao > 0 ? plcSw.Substring(ao, Math.Min(900, plcSw.Length - ao)) : "";
            var roAt = aoBody.IndexOf("if (ReadOnlyPolicy.Enabled)", StringComparison.Ordinal);
            check(roAt > 0 && roAt < aoBody.IndexOf("GoOfflineAll", StringComparison.Ordinal),
                "#67 只读模式下导出碰到在线不自动 GoOfflineAll");
            check(Read("Siemens", "Portal.cs").Replace("\r\n", "\n").Contains("ReadOnlyPolicy.Enabled\n                            ? _portal?.Projects.Open(fi)"),
                "#67 只读模式下打开工程不升级");

            var blocks = Read("ModelContextProtocol", "McpServer.Blocks.cs");
            check(blocks.Contains("Portal.GetBlocksForExport(softwarePath, regexName, softwareUnit, \"ExportBlocks\")") && blocks.Contains("Portal.LastExportBlocksFailures"),
                "#66 ExportBlocks 按单元取块，并把逐块失败带回结果");
            check(Read("ModelContextProtocol", "McpServer.Documents.cs").Contains("Portal.GetBlocksForExport(softwarePath, regexName, softwareUnit, \"ExportBlocksAsDocuments\")"),
                "#66 ExportBlocksAsDocuments 同样按单元取块");

            var edit = Read("Siemens", "Portal.EverydayEdit.cs");
            check(edit.Contains("SelectSoftwareUnits(plc, unitScope, \"GetPlcTags\"") && edit.Contains("result[\"complete\"] = unitFailure == null"),
                "#68 GetPlcTags 读软件单元的变量表，读不出单元时标不完整");
            var sw = Read("Siemens", "Portal.Software.cs");
            check(sw.Contains("ResolveUnitScopedPath(plc, wanted, \"ExportPlcTagTable\")") && sw.Contains("diagnostics.SoftwareUnitsFailure = unitFailure"),
                "#68 GetPlcTagTables 列出单元变量表，ExportPlcTagTable 认单元路径");
        }

        private static string? FindSourceDir()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var k = 0; k < 10 && dir != null; k++)
            {
                var candidate = Path.Combine(dir, "src", "TiaMcpServer");
                if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }
    }
}
