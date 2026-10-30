using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// CLI 动词的参数整理（issue #51 顺带报的 prewarm）。
    ///
    /// 用法里写着「GLOBAL FLAGS (also accepted)」，可原来只认 args[0] 是不是动词：
    /// `TiaMcpServer.exe --tia-major-version 21 prewarm` 一个字不报，直接当 MCP 服务器起来，
    /// stdin 一关就 exit 0 —— 和「执行成功」长得一模一样。
    ///
    /// 独立成零依赖文件，是为了能在离线自检套件里被喂输入。
    /// </summary>
    internal static class CliVerbArgs
    {
        /// <summary>后面跟一个取值的参数（全局的 + 动词自己的）。找位置参数时要连值一起跳过。</summary>
        private static readonly string[] ValueFlags =
        {
            "--tia-major-version", "-tia-major-version",
            "--tia-portal-location", "-tia-portal-location",
            "--profile", "-profile",
            "--logging", "-logging",
            "--plc", "--out", "--block", "--from", "--host",
        };

        internal static bool TakesValue(string arg) => Array.IndexOf(ValueFlags, arg.ToLowerInvariant()) >= 0;

        /// <summary>
        /// 动词前面只有参数时，把动词挪到最前（前置参数放到末尾，照样被各动词按名取到）。
        /// 碰到既不是参数也不是动词的词就返回 null —— 那不是 CLI 调用，照旧走 MCP 服务器。
        /// </summary>
        internal static string[]? ExtractVerbArgs(string[] args, Func<string, bool> isVerb)
        {
            int i = 0;
            while (i < args.Length)
            {
                var a = args[i];
                if (isVerb(a))
                {
                    if (i == 0) return args;
                    return new[] { a }.Concat(args.Skip(i + 1)).Concat(args.Take(i)).ToArray();
                }
                if (!a.StartsWith("-")) return null;
                i += TakesValue(a) ? 2 : 1;
            }
            return null;
        }

        /// <summary>动词后第一个位置参数；跳过参数和它们的取值（`gen --tia-major-version 21 x.json` 取 x.json，不取 21）。</summary>
        internal static string? Positional(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("-"))
                {
                    if (TakesValue(args[i])) i++;
                    continue;
                }
                return args[i];
            }
            return null;
        }
    }
}
