using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Partial：WinCC Unified 离散报警。
    //
    // 补的洞（2026-09-16 真机发现）：全仓只有 Export/ImportAlarmClasses 和
    // ImportAlarmTextLists，**没有任何工具能建一条报警**。自动生成的工程因此
    // 画面上有 HmiAlarmControl、变量表里有 Alm* 位，报警表却是空的 ——
    // 每一步都 Success，而报警画面在运行时永远不出一条。
    public static partial class McpServer
    {
        [McpServerTool(Name = "EnsureUnifiedHmiDiscreteAlarm"), Description("[L2][HMI-Unified]Create or update ONE WinCC Unified discrete alarm (HmiSoftware.DiscreteAlarms) and bind it to a bit of an existing HMI tag. Without this, a generated project ends up with an HmiAlarmControl on screen and Alm* bits in the tag table but an EMPTY alarm list - every step reports success and the alarm screen simply never shows anything at runtime. triggerTag must be an EXISTING HMI tag: a name that does not exist is REFUSED up front (with a sample of the real tag names) instead of being written as a dangling reference that only fails at compile or runtime. Also REFUSED before anything is written (meta.success=false, project untouched), because Openness accepts them and only the HMI compile fails: an alarmClass that does not exist, a trigger tag whose type is not Bool or a bit string (TIA V21 only accepts Bool / Byte / Word / DWord / LWord here: every integer type, signed or unsigned, and Real/String fail the HMI compile), a bit number outside the tag's width, and a tag+bit already used by another discrete alarm. Every property written is read back and compared; any item that cannot be read back, or reads back different, fails the call and says which one - the project may then be half-changed, which is reported rather than hidden. triggerMode accepts only the enum names this TIA version defines (typically OnRisingEdge / OnFallingEdge); an unknown value is rejected with the list of valid ones, never coerced. Requires: Connect + OpenProject; run CompileAndDiagnoseHmi and SaveProject afterwards.")]
        public static ResponseMessage EnsureUnifiedHmiDiscreteAlarm(
            [Description("hmiSoftwarePath: Unified HMI software path, e.g. 'HMI_RT_1'")] string hmiSoftwarePath,
            [Description("alarmName: alarm name. An existing alarm with this name is updated, not duplicated.")] string alarmName,
            [Description("eventText: the alarm text shown in the alarm control. Empty = leave unchanged.")] string eventText = "",
            [Description("triggerTag: name of an EXISTING HMI tag that carries the trigger bit; its data type must be Bool, Byte, Word, DWord or LWord (integer types such as Int/UInt/DInt are refused by the TIA compile).")] string triggerTag = "",
            [Description("bitNumber: which bit of triggerTag raises the alarm (0-based). Bool tag: 0 only; Byte 0..7, Word 0..15, DWord 0..31, LWord 0..63. -1 (default) = leave unchanged (a new alarm gets bit 0).")] int bitNumber = -1,
            [Description("alarmClass: name of an EXISTING HMI alarm class, e.g. 'Alarm' / 'Warning' (GetUnifiedHmiDiscreteAlarms lists them). Empty = leave the default.")] string alarmClass = "",
            [Description("triggerMode: OnRisingEdge or OnFallingEdge. Empty = leave the default.")] string triggerMode = "")
        {
            try
            {
                return Portal.EnsureUnifiedHmiDiscreteAlarm(
                    hmiSoftwarePath, alarmName, eventText, triggerTag, bitNumber, alarmClass, triggerMode);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring discrete alarm '{alarmName}' on '{hmiSoftwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetUnifiedHmiDiscreteAlarms"), Description("[L2][HMI-Unified]READ-ONLY. List every WinCC Unified discrete alarm with its trigger tag (RaisedStateTag), trigger bit (RaisedStateTagBitNumber), the absolute TriggerBitAddress TIA derived from them, alarm class, trigger mode, priority and the event text per language, plus the names of the alarm classes that actually exist in this HMI. Use it to read back what EnsureUnifiedHmiDiscreteAlarm wrote, and before adding alarms to see which tag/bit combinations are already taken (TIA refuses two alarms on the same tag+bit at compile time). A property that cannot be read is reported as '<read threw: ...>' and makes meta.success false, never an empty value. Nothing is written.")]
        public static ResponseMessage GetUnifiedHmiDiscreteAlarms(
            [Description("hmiSoftwarePath: Unified HMI software path, e.g. 'HMI_RT_1'")] string hmiSoftwarePath)
        {
            try
            {
                return Portal.GetUnifiedHmiDiscreteAlarms(hmiSoftwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing discrete alarms on '{hmiSoftwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
