using System;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 判断一个异常的含义是「这次调用失败了」还是「TIA Portal 进程已经没了」。
    ///
    /// 这个区别不是措辞问题：进程没了意味着所有 Openness 句柄作废、未保存的改动全部丢失。
    /// 把它当成普通失败处理，调用方会以为「重试这一步就行」，而实际上要重连、重做、重存。
    ///
    /// 独立成零依赖文件，是为了能在离线自检套件里被真输入喂过 —— 它埋在依赖
    /// Siemens.Engineering 的大文件里时，本机没装 TIA 就一行都测不到，而这条判断
    /// 恰恰属于「判错了也悄无声息」的那类：崩溃路径本来就少见，测不到就等于没写。
    /// </summary>
    internal static class PortalFailureClassifier
    {
        /// <summary>
        /// 一律按类型名（含 InnerException 链）判断，不硬引用具体异常类型，原因有两条：
        /// 1) Openness 程序集是运行时按 TIA 版本解析进来的，异常类型随大版本变；
        /// 2) 引擎编译到 net48、自检套件编译到 net8.0，RemotingException 只存在于前者，
        ///    硬引用会让这个文件没法被两边同时编译，也就没法被测到。
        /// </summary>
        internal static bool IsPortalProcessLost(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var name = e.GetType().FullName ?? string.Empty;

                // Openness 的 NonRecoverableException：进程级致命错，博途已经退出。
                if (Has(name, "NonRecoverable"))
                {
                    return true;
                }

                // 反射调用外面包着 TargetInvocation 层时，真正的类型名有时只出现在消息里。
                if (Has(e.Message, "NonRecoverableException"))
                {
                    return true;
                }

                // 进程没了以后再碰任何 Openness 对象，拿到的是 RPC / 远程调用层的错。
                if (Has(name, "System.Runtime.InteropServices.COMException") ||
                    Has(name, "RemotingException"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Has(string? haystack, string needle)
        {
            return (haystack ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
