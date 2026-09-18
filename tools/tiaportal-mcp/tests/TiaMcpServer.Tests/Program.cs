using System;
using System.Collections.Generic;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 最小离线自检套件的入口。不连 TIA Portal，不碰注册表，跑得起 dotnet 就跑得起它。
    ///
    /// 它盯的是一类特定的缺陷：**从上线起就没生效过的检查**。
    /// 这类东西的共同点是「不响的报警器和没装的报警器长得一模一样」——
    /// 靠人读代码发现不了，只有会失败的用例盯得住。所以每组用例里都放了反向哨兵：
    /// 正常输入必须照旧通过，否则说明这次是把功能改坏了而不是把检查修好了。
    ///
    /// ⚠️ 用 `dotnet run` 驱动，别用 `dotnet test`（见 csproj 里的说明）。
    /// </summary>
    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static int _skip;

        private static void Check(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
                Console.WriteLine("  FAIL: " + what);
            }
        }

        /// <summary>
        /// 用例依赖仓外/仓内布局才拿得到的东西（如引擎源码）。拿不到就明确跳过并计数：
        /// 「少跑了一批」和「全过了」必须在汇总行里长得不一样，否则换台机器跑
        /// 就是个永远不响的报警器。
        /// </summary>
        private static void Skip(string what, string why)
        {
            _skip++;
            Console.WriteLine("  SKIP: " + what + "  <- " + why);
        }

        private static int Main()
        {
            Console.WriteLine("== 「执行 JSON 检查」不许是复述已知事实的同义反复 ==");
            HmiTemplateLayoutExecutionCheckTests.Run(Check);

            Console.WriteLine("== .s7res 的 en-US 扫描（原实现对每个真实文件都抛异常又被吞掉）==");
            RunS7ResScannerTests();

            Console.WriteLine("== 参数诊断 + 大响应寄存分页（坏了也悄无声息的两块）==");
            ExportsAndArgDiagnosticsTests.Run(Check);

            Console.WriteLine("== Unified JS 脚本的 SyntaxCheck 默认关闭 + 进程级致命错不许被吞（issue #36）==");
            UnifiedScriptSyntaxCheckTests.Run(Check, Skip);

            Console.WriteLine("== 画面分组里的画面不许隐形（PR #41）==");
            HmiScreenWalkTests.Run(Check);

            Console.WriteLine("== HMI 标签/画面元件的属性读取：缺属性不许拖垮整次调用，读不到的项不许消失 ==");
            HmiDetailReadTests.Run(Check);

            Console.WriteLine("== 反射桥：GetAttributes 批量读要放行，SetAttributes 仍要挡住 ==");
            ReflectionInvokeSafetyTests.Run(Check);

            Console.WriteLine("== DescribeBlockLogic 的 SCL 回读逐行还原：下标 / 调用 / 常量 / 引号（issue #42）==");
            SymbolQuotingReadbackTests.Run(Check);

            Console.WriteLine(_fail == 0
                ? $"{_pass} passed, {_fail} failed, {_skip} skipped."
                : $"{_pass} passed, {_fail} failed, {_skip} skipped.  <<< 有失败");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// .s7res 是 YAML，不是 XML。原实现拿 XML 解析器去读，对**每一个真实文件**都抛异常，
        /// 异常又被外面的 catch 吞掉 —— 于是「没有缺失的 en-US 条目」这个结论，
        /// 是在一次都没真正扫过的情况下得出的。这里用真实形态的行喂它。
        /// </summary>
        private static void RunS7ResScannerTests()
        {
            // 真实形态：MultiLingualTexts 容器 + 「- id: MLC_xxx」列表项 + 各语言行。
            var missing = new List<string>
            {
                "MultiLingualTexts:",
                "  - id: MLC_Comment",
                "    de-DE: 'Start'",
                "  - id: MLC_Title",
                "    en-US: 'Stop'",
            };
            var ids = TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(missing);
            Check(ids.Contains("MLC_Comment"), "缺 en-US 的条目要被点名（MLC_Comment）");
            Check(!ids.Contains("MLC_Title"), "有 en-US 的条目不许被误报（MLC_Title）");

            // 反向哨兵：全都有 en-US 时必须一条都不报。
            // 少了这条，「永远返回空列表」的坏实现也能通过上面那两条里的第二条。
            var complete = new List<string>
            {
                "MultiLingualTexts:",
                "  - id: MLC_Comment",
                "    en-US: 'Start'",
                "  - id: MLC_Title",
                "    en-US: 'Stop'",
            };
            Check(TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(complete).Count == 0,
                "[反向哨兵] 全都有 en-US 时不许报缺失");

            // [哨兵] 空输入不许崩：预检坏掉本身就该看得见，但不该把调用方一起带走。
            Check(TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(new List<string>()).Count == 0,
                "[哨兵] 空输入返回空列表且不抛");
        }
    }
}
