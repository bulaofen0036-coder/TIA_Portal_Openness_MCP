using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 把「参数写错了」变成一句能照着改的话。
    ///
    /// WHY：用错的参数名调用一个已注册工具，调用方拿到的全部信息就是
    /// "An error occurred invoking 'GetSoftwareTree'." —— 没说少了谁、没说正确的名字叫什么。
    /// SDK 在参数绑定阶段就失败了，工具方法体根本没执行，所以写在方法内部的任何校验都来不及。
    /// 对 AI 客户端而言这等于什么都没说，只能反复试错猜参数名。
    ///
    /// 同时报告**会被静默丢弃**的参数：调用方以为某个选项生效了、实际被无声忽略，
    /// 比直接拒绝这次调用更危险。
    ///
    /// 刻意做成零依赖（不引 MCP 类型、不做 I/O），这样不起服务器也能单测。
    /// </summary>
    public static class ArgDiagnostics
    {
        /// <summary>
        /// 没什么可抱怨的就返回 ""；否则返回一句话，说清问题 + 正确签名。
        /// </summary>
        /// <param name="known">工具接受的全部参数名。空 = 「schema 读不出来」，此时这层完全让路
        /// —— 判不了的调用绝不拦。</param>
        /// <param name="required">必须提供的参数名。</param>
        /// <param name="supplied">调用方实际送来的参数名。</param>
        /// <param name="typeOf">可选的 名字 -> JSON 类型，只用来渲染签名。</param>
        public static string Check(
            string toolName,
            IReadOnlyList<string>? known,
            IReadOnlyList<string>? required,
            IReadOnlyList<string>? supplied,
            IReadOnlyDictionary<string, string>? typeOf = null)
        {
            if (known == null || known.Count == 0) return "";

            var req = required ?? Array.Empty<string>();
            var got = supplied ?? Array.Empty<string>();

            // 区分大小写：SDK 按参数名**精确**绑定（实测 0.3.0-preview.4：FindTools 收到
            // {"Query":…} 不报错，query 被静默丢掉，按空查询列出整张表）。这里若放过大小写
            // 不同的名字，正好把「会被静默忽略」的那一种放了过去。
            var unknown = got
                .Where(g => !string.IsNullOrEmpty(g))
                .Where(g => !known.Any(k => string.Equals(k, g, StringComparison.Ordinal)))
                .ToList();

            var missing = req
                .Where(r => !got.Any(g => string.Equals(g, r, StringComparison.Ordinal)))
                .ToList();

            if (unknown.Count == 0 && missing.Count == 0) return "";

            var sb = new StringBuilder();
            sb.Append(toolName).Append(": ");

            if (missing.Count > 0)
            {
                sb.Append("missing required argument(s): ").Append(string.Join(", ", missing)).Append('.');
                if (unknown.Count > 0) sb.Append(' ');
            }

            if (unknown.Count > 0)
            {
                sb.Append("unknown argument(s) that would have been SILENTLY IGNORED: ")
                  .Append(string.Join(", ", unknown)).Append('.');
                var hints = unknown
                    .Select(u => new { u, near = NearestName(u, known) })
                    .Where(x => x.near != null)
                    .Select(x => x.u + " -> " + x.near)
                    .ToList();
                if (hints.Count > 0)
                    sb.Append(" Did you mean: ").Append(string.Join(", ", hints)).Append('?');
                if (unknown.Any(u => known.Any(k => string.Equals(k, u, StringComparison.OrdinalIgnoreCase))))
                    sb.Append(" Argument names are case-sensitive.");
            }

            sb.Append(" Expected signature: ").Append(RenderSignature(toolName, known, req, typeOf));
            // 「什么都没执行」这句是给调用方的副作用保证：诊断发生在工具体之前，
            // 调用方据此知道不必担心已经写盘/改工程，可以放心重试。
            sb.Append(" (nothing was executed).");
            return sb.ToString();
        }

        /// <summary>'Tool(a: string, b?: integer)' —— 改法直接写在报错里，别让人再去翻文档。</summary>
        public static string RenderSignature(
            string toolName,
            IReadOnlyList<string> known,
            IReadOnlyList<string> required,
            IReadOnlyDictionary<string, string>? typeOf = null)
        {
            var parts = new List<string>(known.Count);
            foreach (var k in known)
            {
                bool isReq = required.Any(r => string.Equals(r, k, StringComparison.OrdinalIgnoreCase));
                string type = "";
                if (typeOf != null && typeOf.TryGetValue(k, out var t) && !string.IsNullOrEmpty(t)) type = t;
                parts.Add(k + (isReq ? "" : "?") + (type.Length > 0 ? ": " + type : ""));
            }
            return toolName + "(" + string.Join(", ", parts) + ")";
        }

        /// <summary>给拼错的名字找最接近的已知名，够不着就返回 null。
        /// 故意保守：一个自信但错误的建议会把调用方带进另一条死胡同，比不给建议更糟。</summary>
        public static string? NearestName(string candidate, IReadOnlyList<string> known)
        {
            if (string.IsNullOrEmpty(candidate) || known == null || known.Count == 0) return null;

            string best = "";
            int bestScore = int.MaxValue;
            foreach (var k in known)
            {
                int d = Distance(candidate.ToLowerInvariant(), (k ?? "").ToLowerInvariant());
                if (d < bestScore) { bestScore = d; best = k ?? ""; }
            }
            if (best.Length == 0) return null;

            // 大致允许每三个字符一次编辑，下限两次。
            int limit = Math.Max(2, candidate.Length / 3);
            return bestScore <= limit ? best : null;
        }

        /// <summary>Levenshtein 编辑距离。</summary>
        public static int Distance(string a, string b)
        {
            a ??= "";
            b ??= "";
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                Array.Copy(cur, prev, cur.Length);
            }
            return prev[b.Length];
        }
    }
}
