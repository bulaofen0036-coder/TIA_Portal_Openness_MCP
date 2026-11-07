using System;
using System.Collections.Generic;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// ConnectPortal 的失败文案：把「附加已开博途」那一段的经过带进最终报错（issue #51）。
    ///
    /// ConnectPortal 先逐个附加正在运行的博途，全都附加不上才新起一个实例。原来新起那一步
    /// 失败时只报它自己的异常（栈里是 TiaPortal..ctor），前面每个进程为什么附加不上只进了日志 ——
    /// 用户看到的是「新起实例失败」，于是以为引擎根本没走 GetProcesses → Attach。
    ///
    /// 独立成零依赖文件，是为了能在离线自检套件里被喂输入。
    /// </summary>
    internal static class ConnectFailureText
    {
        internal static string AttachTimedOut(int pid, int timeoutMs)
            => $"PID={pid}: Attach did not return within {timeoutMs / 1000}s";

        internal static string AttachFailed(int pid, Exception ex)
            => $"PID={pid}: {ex.GetType().Name}: {FirstLine(ex.Message)}";

        internal static string AttachThenLaunchFailed(IReadOnlyList<string> attachNotes, string launchMode, string launchDetail)
            => $"ConnectPortal failed: {attachNotes.Count} running TIA Portal process(es) found, none could be attached — "
               + string.Join("; ", attachNotes)
               + $". Starting a new TIA Portal instance ({launchMode}) then also failed: {launchDetail}"
               + " | Hint: a running TIA Portal whose Attach times out is usually busy or showing a modal dialog"
               + " (for example the Openness access confirmation for this exe) — answer it in that TIA window and call Connect again.";

        private static string FirstLine(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(no message)";
            var t = s!.Trim();
            var i = t.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? t : t.Substring(0, i).Trim();
        }
    }
}
