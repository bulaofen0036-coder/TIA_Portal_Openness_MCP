using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// The table behind tool annotations, the Openness gate and CallTool's refusal list.
    /// A wrong entry here never crashes anything: the host just stops asking before a download,
    /// or two calls overlap on the portal handle. Only a failing case notices.
    /// </summary>
    internal static class ToolSafetyTests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            void Pin(string name, Func<ToolSafetyInfo, bool> expected, string what)
            {
                var s = ToolSafety.Classify(name);
                check(expected(s), name + ": " + what);
            }

            Pin("DownloadToPlc", s => s.Destructive && s.OpenWorld && s.DirectOnly && !s.ReadOnly && s.TouchesPortal,
                "destructive, open-world, direct-only");
            Pin("GoOnline", s => s.OpenWorld && s.DirectOnly && !s.ReadOnly, "open-world, direct-only");
            Pin("SetWatchTableModifyValue", s => s.Destructive && s.DirectOnly, "destructive, direct-only");
            foreach (var del in new[] { "DeletePlcBlock", "DeletePlcType", "DeletePlcTagTable", "DeletePlcExternalSource" })
                Pin(del, s => s.Destructive && s.DirectOnly && !s.Idempotent, "destructive, direct-only");
            Pin("CallTool", s => s.Destructive && s.TouchesPortal && !s.DirectOnly, "inherits the worst case but stays callable");
            Pin("InvokeObject", s => s.Destructive, "generic reflection bridge is destructive");
            Pin("ImportBlock", s => s.Destructive, "imports use Override semantics");
            Pin("SetUnifiedHmiScreenEventScriptCode", s => s.Destructive && s.Idempotent && s.TouchesPortal && !s.DirectOnly,
                "writes a screen script; same input, same result (issue #38)");
            Pin("ImportUnifiedHmiScriptModule", s => s.Destructive && !s.Idempotent && s.TouchesPortal && !s.DirectOnly,
                "imports can overwrite an existing module (issue #38)");
            Pin("CloseProject", s => s.Destructive, "closing loses unsaved edits");
            Pin("GoOfflineAll", s => s.Destructive && s.OpenWorld, "drops live sessions, including the user's");
            Pin("GetBlocks", s => s.ReadOnly && !s.Destructive && s.Idempotent && s.TouchesPortal && !s.OpenWorld,
                "read-only but still needs the portal");
            Pin("ReadPlcLiveValuesS7", s => s.ReadOnly && s.OpenWorld, "reads a CPU over the network");
            Pin("FindTools", s => s.ReadOnly && !s.TouchesPortal, "tool search needs no portal");
            Pin("GetExport", s => s.ReadOnly && !s.TouchesPortal, "export store needs no portal");
            Pin("SaveExport", s => !s.ReadOnly && !s.TouchesPortal && !s.Destructive, "writes a file, not the project");
            Pin("CompileSoftware", s => !s.ReadOnly && !s.Destructive && s.Idempotent, "mutating but idempotent");
            Pin("ConnectIsolated", s => !s.Idempotent, "starts one more TIA instance per call");
            Pin("Bootstrap", s => !s.ReadOnly, "may add the user to the Openness group");
            // Verb prefixes only count at a word boundary.
            Pin("Setup", s => !s.Destructive, "'Setup' is not the verb 'Set'");
            Pin("Settle", s => !s.Destructive, "'Settle' is not the verb 'Set'");
            // Unknown tools get the conservative default.
            Pin("SomethingNew", s => !s.ReadOnly && !s.Destructive && s.TouchesPortal && !s.DirectOnly,
                "unknown tools are treated as mutating calls that need the portal");

            var roster = FindRepoFile(Path.Combine("manifest", "tools-list.json"));
            if (roster == null)
            {
                skip("ToolSafety vs manifest/tools-list.json", "manifest not found above the test binary");
            }
            else
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(roster));
                var names = new HashSet<string>(
                    doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!),
                    StringComparer.Ordinal);

                var unknownDirect = ToolSafety.DirectOnlyTools.Where(n => !names.Contains(n)).ToList();
                check(unknownDirect.Count == 0,
                    "every direct-only tool is a real tool (unknown: " + string.Join(",", unknownDirect) + ")");

                var both = names.Where(n => { var s = ToolSafety.Classify(n); return s.ReadOnly && s.Destructive; }).ToList();
                check(both.Count == 0, "no tool is both read-only and destructive (" + string.Join(",", both) + ")");

                var portalFree = names.Where(n => !ToolSafety.Classify(n).TouchesPortal).OrderBy(n => n).ToList();
                check(portalFree.SequenceEqual(new[] { "ClearExports", "DeleteExport", "FindTools", "GetAuthoringGuide", "GetExport", "ListExports", "SaveExport" }),
                    "only the tool search, the guide and the export store skip the Openness gate (got: " + string.Join(",", portalFree) + ")");

                int readOnly = names.Count(n => ToolSafety.Classify(n).ReadOnly);
                check(readOnly > 80 && readOnly < names.Count - 60,
                    "a plausible share of the roster is read-only (" + readOnly + " of " + names.Count + ")");
            }

            // CallTool refuses direct-only tools, so the lite roster has to list every one of them
            // or they are unreachable in the default profile. Pinned against the source text:
            // the Profile file needs the MCP SDK and cannot be compiled into this suite.
            var profile = FindRepoFile(Path.Combine("tools", "tiaportal-mcp", "src", "TiaMcpServer", "ModelContextProtocol", "McpServer.Profile.cs"));
            if (profile == null)
            {
                skip("lite roster contains every direct-only tool", "McpServer.Profile.cs not found");
            }
            else
            {
                var text = File.ReadAllText(profile);
                int start = text.IndexOf("LiteToolNames = new HashSet<string>", StringComparison.Ordinal);
                int end = start < 0 ? -1 : text.IndexOf("};", start, StringComparison.Ordinal);
                if (start < 0 || end < 0)
                {
                    check(false, "LiteToolNames initializer not found in McpServer.Profile.cs");
                }
                else
                {
                    var body = string.Join("\n", text.Substring(start, end - start)
                        .Split('\n').Select(l => { int c = l.IndexOf("//", StringComparison.Ordinal); return c < 0 ? l : l.Substring(0, c); }));
                    var lite = new HashSet<string>(Regex.Matches(body, "\"([A-Za-z0-9]+)\"").Cast<Match>().Select(m => m.Groups[1].Value));
                    var missing = ToolSafety.DirectOnlyTools.Where(n => !lite.Contains(n)).ToList();
                    check(missing.Count == 0,
                        "lite roster lists every direct-only tool (missing: " + string.Join(",", missing) + ")");
                }
            }
        }

        internal static string? FindRepoFile(string relative)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 12 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, relative);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }
    }
}
