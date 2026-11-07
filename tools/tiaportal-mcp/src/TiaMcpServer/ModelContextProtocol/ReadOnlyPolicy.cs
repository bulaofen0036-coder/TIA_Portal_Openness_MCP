using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Server-side read-only mode (issue #67). Started with <c>--read-only</c> (or TIA_MCP_READ_ONLY=1),
    /// the server refuses every call that could change the project, the CPU or the session's open project
    /// before the tool runs, whichever way the call arrives: listed tool, CallTool, HTTP bridge.
    ///
    /// The lite/full profile only decides which tools are LISTED; it is not a write protection. Hiding tools
    /// or asking the agent to behave does not stop another client, or CallTool, from reaching them.
    ///
    /// Allowed: tools ToolSafety classifies read-only (Get/Describe/List/Read/...), exports (they write — and
    /// overwrite — files at the path the caller gives, never the project itself), the large-response store, and
    /// the session tools that only
    /// choose what is open (Connect, ConnectIsolated, AttachToOpenProject, OpenProject, CloseProject,
    /// Disconnect) — but not their "close the user's project" option. In this mode OpenProject does not upgrade an
    /// older project and the export tools do not take PLCs offline (both are enforced at their call sites).
    /// Everything else is refused, including
    /// compile, save, download, every Set/Import/Delete, the reflection bridges and any tool this table does
    /// not know (deny by default).
    ///
    /// Zero dependencies so the offline suite can pin the table.
    /// </summary>
    public static class ReadOnlyPolicy
    {
        private static bool _cliOn;

        /// <summary>Applies the CLI --read-only flag. Call before building the host.</summary>
        public static void EnableFromCli(bool on) { if (on) _cliOn = true; }

        public static bool Enabled => _cliOn || IsTrueText(Environment.GetEnvironmentVariable("TIA_MCP_READ_ONLY"));

        // Only change which project / instance is open, never its content. Their option to close a project
        // the user opened is refused separately below.
        private static readonly HashSet<string> SessionTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "Connect", "ConnectIsolated", "AttachToOpenProject", "OpenProject", "CloseProject", "Disconnect",
        };

        // Read-only despite a verb the classifier does not know; checked one by one.
        private static readonly HashSet<string> ExtraAllowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Bootstrap",            // environment / connection orientation
            "FindTools",            // roster search
            "CallTool",             // the target is checked again inside CallTool
            "SaveExport",           // writes an already-read export into a file
        };

        /// <summary>
        /// null = allowed; otherwise the refusal text. <paramref name="argBool"/> returns a boolean argument
        /// of the call, or null when it was not given.
        /// </summary>
        public static string? Refusal(string? toolName, Func<string, bool?> argBool)
        {
            string name = toolName ?? "";
            if (name == "Doctor")
            {
                // fix defaults to true and adds the Windows user to the Openness group.
                return argBool("fix") == false ? null
                    : Refused(name, "it changes Windows group membership unless fix=false is passed");
            }
            if (SessionTools.Contains(name))
            {
                return argBool("closeForeignProject") == true
                    ? Refused(name, "closeForeignProject=true would close a project the user opened")
                    : null;
            }
            if (ExtraAllowed.Contains(name)) return null;
            if (IsExport(name)) return null;
            if (ToolSafety.Classify(name).ReadOnly) return null;
            return Refused(name, "it is not on the read-only list (project changes, compile, save, download, online writes and unknown tools are refused)");
        }

        private static bool IsExport(string name)
            => name.Length > "Export".Length && name.StartsWith("Export", StringComparison.Ordinal) && char.IsUpper(name["Export".Length]);

        private static string Refused(string name, string why)
            => $"READ-ONLY MODE: '{name}' was refused because {why}. Nothing was executed. "
             + "The server was started with --read-only (or TIA_MCP_READ_ONLY=1); restart it without that to make changes.";

        private static bool IsTrueText(string? s)
        {
            s = s?.Trim();
            return string.Equals(s, "1", StringComparison.Ordinal)
                || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
