using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// How DownloadToPlc answers TIA's download prompts. Every answer must be a member of the
    /// selection enum the V21 Openness manual documents for that prompt: a non-member never applies,
    /// the prompt stays unanswered and the download fails without a stated reason - which is exactly
    /// what "KeepActualValues"/"Reinitialize" did for DataBlockReinitialization.
    /// </summary>
    internal static class DownloadPromptPolicyTests
    {
        // Selection enums as documented in "TIA Portal Openness: API for automation of engineering
        // workflows" V21 (03/2026), download configuration table.
        private static readonly Dictionary<string, string[]> Documented = new Dictionary<string, string[]>
        {
            ["StopModules"] = new[] { "NoAction", "StopAll" },
            ["StartModules"] = new[] { "NoAction", "StartModule" },
            ["AllBlocksDownload"] = new[] { "DownloadAllBlocks" },
            ["ConsistentBlocksDownload"] = new[] { "ConsistentDownload" },
            ["DataBlockReinitialization"] = new[] { "StopPlcAndReinitialize", "NoAction" },
            ["ActiveTestCanBeAborted"] = new[] { "NoAction", "AcceptAll" },
            ["ActiveTestCanPreventDownload"] = new[] { "NoAction", "AcceptAll" },
            ["DifferentTargetConfiguration"] = new[] { "NoAction", "AcceptAll" },
        };

        internal static void Run(Action<bool, string> check)
        {
            var defaults = new DownloadPromptOptions();
            var all = new List<DownloadPromptOptions>
            {
                defaults,
                new DownloadPromptOptions { KeepActualValues = false, StartAfterDownload = false, StopBeforeDownload = false, ConsistentBlocksOnly = false, AbortActiveTests = true },
            };

            foreach (var kv in Documented)
                foreach (var o in all)
                {
                    var d = DownloadPromptPolicy.Decide(kv.Key, o);
                    check(d.Selection == null || kv.Value.Contains(d.Selection),
                        kv.Key + ": answer '" + d.Selection + "' is a documented " + kv.Key + "Selections member");
                }

            // Active tests are somebody's live session on the machine: never cancelled by default.
            foreach (var t in new[] { "ActiveTestCanBeAborted", "ActiveTestCanPreventDownload" })
            {
                check(DownloadPromptPolicy.Decide(t, defaults).Selection == "NoAction", t + ": default declines to cancel active tests");
                check(DownloadPromptPolicy.Decide(t, new DownloadPromptOptions { AbortActiveTests = true }).Selection == "AcceptAll",
                    t + ": cancelled only when abortActiveTests=true");
            }

            check(DownloadPromptPolicy.Decide("StopModules", defaults).Selection == "StopAll", "StopModules: stopBeforeDownload answers StopAll (not StopModule)");
            check(DownloadPromptPolicy.Decide("StartModules", defaults).Selection == "StartModule", "StartModules: startAfterDownload answers StartModule");
            check(DownloadPromptPolicy.Decide("StartModules", new DownloadPromptOptions { StartAfterDownload = false }).Selection == "NoAction",
                "StartModules: startAfterDownload=false leaves the CPU in STOP");

            check(DownloadPromptPolicy.Decide("DataBlockReinitialization", defaults).Selection == "NoAction",
                "DataBlockReinitialization: keepActualValues=true declines, so values are never wiped by default");
            check(DownloadPromptPolicy.Decide("DataBlockReinitialization", new DownloadPromptOptions { KeepActualValues = false }).Selection == "StopPlcAndReinitialize",
                "DataBlockReinitialization: keepActualValues=false reinitializes");
            check(DownloadPromptPolicy.Decide("DataBlockReinitializationOrKeepActualValues", defaults).Selection == "KeepActualValues",
                "DataBlockReinitializationOrKeepActualValues: keeps actual values by default (manual example)");

            check(!DownloadPromptPolicy.Decide("AllBlocksDownload", defaults).Answers, "AllBlocksDownload: unanswered with consistentBlocksOnly=true");
            var check1 = DownloadPromptPolicy.Decide("CheckBeforeDownload", defaults);
            check(check1.Checked == true && check1.Selection == null, "CheckBeforeDownload is a check, not a selection");

            var unknown = DownloadPromptPolicy.Decide("ProtectionLevelChanged", defaults);
            check(!unknown.Answers && unknown.Why.Contains("not handled"),
                "an unhandled prompt is left alone and says so (lowering CPU protection is never auto-accepted)");
        }
    }
}
