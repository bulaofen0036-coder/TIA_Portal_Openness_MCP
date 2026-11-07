using System;
using System.IO;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// issue #36：写 Unified HMI 的 JS 事件脚本后强制调 SyntaxCheck()，在 TIA V21 上会偶发
    /// 抛 NonRecoverableException 并带走整个 Portal 进程，刚写进去的脚本随之丢失。
    ///
    /// 修法有两半，这里两半都盯：
    /// 1) SyntaxCheck 改成默认不跑 —— 默认值一旦被改回 true，崩溃就原样回来，而且
    ///    离线用例一条都不会红（真崩要有博途才复现），所以只能盯源码里的签名。
    /// 2) 进程级致命错不许再被当成普通失败吞掉 —— 这条有真实现可以喂输入。
    /// </summary>
    internal static class UnifiedScriptSyntaxCheckTests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            RunClassifierTests(check);
            RunDefaultOffContractTests(check, skip);
        }

        private static void RunClassifierTests(Action<bool, string> check)
        {
            // 进程已经没了的几种形状，都必须认出来。
            check(PortalFailureClassifier.IsPortalProcessLost(
                    new FakeNonRecoverableException("TIA Portal terminated unexpectedly.")),
                "Openness 的 NonRecoverableException 要认成「进程没了」");

            check(PortalFailureClassifier.IsPortalProcessLost(
                    new InvalidOperationException("outer",
                        new FakeNonRecoverableException("inner"))),
                "包在 InnerException 里的 NonRecoverable 也要认出来");

            // 反射路径上真正的类型名常常只出现在消息里，类型本身还是 TargetInvocationException。
            check(PortalFailureClassifier.IsPortalProcessLost(
                    new InvalidOperationException(
                        "Siemens.Engineering.NonRecoverableException: the engineering process has exited")),
                "类型名只出现在消息里时也要认出来");

            check(PortalFailureClassifier.IsPortalProcessLost(
                    new System.Runtime.InteropServices.COMException("RPC server unavailable")),
                "进程没了以后的 COMException 要认成「进程没了」");

            // [反向哨兵] 普通业务失败绝不能被误判成「进程没了」——误判的代价是让调用方
            // 以为整个会话作废、去做重连重做，而其实只要修一处参数重试即可。
            // 少了这两条，「无脑 return true」的坏实现能通过上面全部用例。
            check(!PortalFailureClassifier.IsPortalProcessLost(
                    new InvalidOperationException("Screen item 'Btn_Start' not found on screen 'Main'.")),
                "[反向哨兵] 找不到画面元素只是普通失败，不是进程没了");

            check(!PortalFailureClassifier.IsPortalProcessLost(
                    new ArgumentException("eventType 'Pressed' is not a valid HmiButtonEventType value.")),
                "[反向哨兵] 参数错只是普通失败，不是进程没了");

            // [哨兵] null 不许崩：分类器本身坏掉不该把调用方一起带走。
            check(!PortalFailureClassifier.IsPortalProcessLost(null),
                "[哨兵] null 返回 false 且不抛");
        }

        private static void RunDefaultOffContractTests(Action<bool, string> check, Action<string, string> skip)
        {
            // 这两个文件依赖 Siemens.Engineering / MCP SDK，链不进这个套件，只能读源码盯形状。
            var portalSource = FindSource(Path.Combine("Siemens", "Portal.Software.cs"));
            var toolSource = FindSource(Path.Combine("ModelContextProtocol", "McpServer.PlcSoftware.cs"));

            if (portalSource == null || toolSource == null)
            {
                // 找不到源码就明确跳过并计数，不冒充通过 —— 「少跑了一批」和「全过了」
                // 必须长得不一样，否则换台机器跑就是个永远不响的报警器。
                skip("SyntaxCheck 默认关闭的源码形状", "找不到引擎源码目录（只在仓内跑得了）");
                return;
            }

            var portal = File.ReadAllText(portalSource);
            var tool = File.ReadAllText(toolSource);

            check(portal.Contains("bool async = false, bool syntaxCheck = false)"),
                "Portal.SetUnifiedHmiButtonEventScriptCode 的 syntaxCheck 必须默认 false（issue #36）");

            check(tool.Contains("bool syntaxCheck = false)"),
                "MCP 工具暴露的 syntaxCheck 必须默认 false（issue #36）");

            // 默认关闭时绝不能发 syntaxErrorCount：调用方读到「没有这个键」必须理解成
            // 「没查」。发一个 0 出去，等于把「没做检查」谎报成「检查通过」。
            check(portal.Contains("meta[\"syntaxCheckStatus\"] = \"skipped\"") &&
                  portal.Contains("syntaxCheckSkippedReason"),
                "跳过 SyntaxCheck 时必须显式标注 skipped 与原因");

            // ScriptCode 没写进去就该直接失败，不该先拿一个会弄崩 V21 的调用去检查
            // 一份根本不存在的脚本。这里盯的是两段代码的先后顺序。
            var guardAt = portal.IndexOf("ScriptCode property could not be written on", StringComparison.Ordinal);
            var checkAt = portal.IndexOf("meta[\"syntaxCheckRequested\"]", StringComparison.Ordinal);
            check(guardAt > 0 && checkAt > 0 && guardAt < checkAt,
                "ScriptCode 写失败的判断必须排在 SyntaxCheck 之前");
        }

        /// <summary>从测试程序集所在目录往上爬找引擎源码；找不到返回 null，由调用方明确跳过。</summary>
        private static string? FindSource(string relative)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 10 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "src", "TiaMcpServer", relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            return null;
        }

        /// <summary>
        /// 冒充 Openness 的致命异常：这个套件不引用 Siemens.Engineering（引用了就得装博途才编得动），
        /// 而分类器本来就按类型名判断，所以类名必须和真家伙一致。
        /// </summary>
        private sealed class FakeNonRecoverableException : Exception
        {
            internal FakeNonRecoverableException(string message) : base(message)
            {
            }
        }
    }
}
