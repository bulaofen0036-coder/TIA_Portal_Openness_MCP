using System;

namespace TiaMcpServer.Siemens
{
    /// <summary>How DownloadToPlc was asked to answer the download dialog's questions.</summary>
    public sealed class DownloadPromptOptions
    {
        public bool KeepActualValues { get; set; } = true;
        public bool StartAfterDownload { get; set; } = true;
        public bool StopBeforeDownload { get; set; } = true;
        public bool ConsistentBlocksOnly { get; set; } = true;
        /// <summary>Accept cancelling active test and commissioning functions (a running trace,
        /// a forced value, a commissioning session) so the download can go ahead.</summary>
        public bool AbortActiveTests { get; set; }
    }

    /// <summary>One answer to one download prompt. <see cref="Selection"/> is a CurrentSelection enum
    /// member name, <see cref="Checked"/> a DownloadCheckConfiguration value; both null = leave the
    /// prompt unanswered, which makes TIA refuse the download for prompts that need an answer.</summary>
    public sealed class DownloadPromptDecision
    {
        public string TypeName { get; set; } = "";
        public string? Selection { get; set; }
        public bool? Checked { get; set; }
        public string Why { get; set; } = "";
        public bool Answers => Selection != null || Checked != null;
    }

    /// <summary>
    /// The answers DownloadToPlc gives to the pre-download prompts, by configuration type name.
    /// Selection names are the enum members documented in the V21 Openness manual
    /// ("Download configurations"). Kept free of Siemens.Engineering so the offline suite can pin it.
    /// </summary>
    public static class DownloadPromptPolicy
    {
        public static DownloadPromptDecision Decide(string typeName, DownloadPromptOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            var d = new DownloadPromptDecision { TypeName = typeName ?? "" };
            switch (typeName)
            {
                case "StopModules":
                    // StopModulesSelections = { NoAction, StopAll } — not "StopModule".
                    return Select(d, o.StopBeforeDownload ? "StopAll" : "NoAction", "stopBeforeDownload=" + o.StopBeforeDownload);
                case "StopHSystemOrModule":
                    return Select(d, o.StopBeforeDownload ? "StopModule" : "NoAction", "stopBeforeDownload=" + o.StopBeforeDownload);
                case "StopHSystem":
                    return Select(d, o.StopBeforeDownload ? "StopHSystem" : "NoAction", "stopBeforeDownload=" + o.StopBeforeDownload);
                case "StartModules":
                case "StartBackupModules":
                    return Select(d, o.StartAfterDownload ? "StartModule" : "NoAction", "startAfterDownload=" + o.StartAfterDownload);
                case "DataBlockReinitialization":
                    // DataBlockReinitializationSelections = { StopPlcAndReinitialize, NoAction } (V21 manual).
                    // It is asked when the program cannot be loaded WITHOUT reinitializing, so keeping
                    // actual values means declining - the download is then refused, not the values lost.
                    // The old answers "KeepActualValues"/"Reinitialize" are not members of this enum, so
                    // they never applied and the download failed without saying why.
                    return o.KeepActualValues
                        ? Select(d, "NoAction", "keepActualValues=true: do not reinitialize; TIA refuses the download instead")
                        : Select(d, "StopPlcAndReinitialize", "keepActualValues=false: stop the PLC and reset all values, including retain data");
                case "DataBlockReinitializationOrKeepActualValues":
                    return Select(d, o.KeepActualValues ? "KeepActualValues" : "StopPlcAndReinitialize", "keepActualValues=" + o.KeepActualValues);
                case "ConsistentBlocksDownload":
                    return Select(d, "ConsistentDownload", "download the consistent program");
                case "AllBlocksDownload":
                    if (o.ConsistentBlocksOnly)
                    {
                        d.Why = "consistentBlocksOnly=true: left unanswered";
                        return d;
                    }
                    return Select(d, "DownloadAllBlocks", "consistentBlocksOnly=false");
                case "CheckBeforeDownload":
                    // The only one of these that is a check box (DownloadCheckConfiguration).
                    d.Checked = true;
                    d.Why = "include in the download";
                    return d;
                case "AlarmTextLibrariesDownload":
                    // AlarmTextLibrariesDownloadSelections = { ConsistentDownload, NoAction }. A
                    // selection, not a check box: answering Checked never applied.
                    return Select(d, "ConsistentDownload", "download the alarm text libraries with the program");
                case "UserManagementDownload":
                    // UserManagementPreDownloadSelections = { KeepOnlineUserManagementData,
                    // UpdateUserManagementDataButKeepOnlinePassword, DownloadAllUserManagementDataResetToProject }.
                    // Users and passwords on the CPU are not the program's to overwrite.
                    return Select(d, "KeepOnlineUserManagementData", "keep the user management data already on the CPU");
                case "DownloadCertificate":
                    // Only Parent and Message: an information prompt, there is nothing to answer.
                    d.Why = "information only, nothing to answer";
                    return d;
                case "DifferentTargetConfiguration":
                    return Select(d, "AcceptAll", "download although the online modules differ from the configured ones");
                case "ActiveTestCanBeAborted":
                case "ActiveTestCanPreventDownload":
                    // The manual: AcceptAll cancels active test and commissioning functions during the
                    // load. That is somebody else's live session on the machine, so it is opt-in.
                    return o.AbortActiveTests
                        ? Select(d, "AcceptAll", "abortActiveTests=true: active tests/commissioning functions are cancelled")
                        : Select(d, "NoAction", "abortActiveTests=false: the download stops rather than cancel active tests");
                default:
                    d.Why = "not handled by the server: TIA's default applies (the download may be refused)";
                    return d;
            }
        }

        private static DownloadPromptDecision Select(DownloadPromptDecision d, string selection, string why)
        {
            d.Selection = selection;
            d.Why = why;
            return d;
        }
    }
}
