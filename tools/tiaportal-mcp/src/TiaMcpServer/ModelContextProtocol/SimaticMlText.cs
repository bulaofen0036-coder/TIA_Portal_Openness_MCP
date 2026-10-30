using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// SimaticML 导出里「一个操作数」和「一段 SCL/STL 正文」怎么读回成文本。
    ///
    /// 单独抽成零依赖文件有两个原因：
    /// 1) 原来 LadTextRenderer 里 LAD 操作数和 SCL 正文各写了一份读法，同一个缺陷两个副本。
    /// 2) 回读造假是**静默**的 —— 编不出错、看不出假，只有会失败的用例盯得住，
    ///    所以它必须待在离线测试工程够得着的地方（不依赖 MCP SDK / Openness）。
    ///
    /// 曾经的缺陷：Access 的文本用 <c>Descendants("Component")</c> 拼所有后代分量。
    /// 这只对「普通符号访问」成立，碰上函数调用就会凭空造出一个不存在的符号：
    /// <c>ABS(#A - #B)</c> 读成 <c>#A.B</c>、<c>MAX(IN1:=#A,IN2:=#B)</c> 读成 <c>#A.B</c>、
    /// <c>SQRT(#R1)</c> 读成 <c>#R1</c>（函数整个消失）；带常量参数的调用
    /// （<c>TON(IN:=#x, PT:=T#2S)</c>）更会因为 Descendants("ConstantValue")
    /// 整条被读成 <c>T#2S</c>。拿这种回读去审程序，会看到根本不存在的逻辑。
    /// 修法：只认**直接子节点**判定形态，调用按 token 流原样重放。
    /// </summary>
    internal static class SimaticMlText
    {
        /// <summary>
        /// 读一个 &lt;Access&gt;：常量返回字面量（literal=true），符号返回 #local / "Global"，
        /// 调用等复合形态按 token 流重放（ABS(#x)、#inst(IN:=#a, PT:=T#2S)）。
        /// </summary>
        public static (string text, bool literal) ReadAccess(XElement acc)
        {
            var scope = acc.Attribute("Scope")?.Value ?? "";

            // 常量：真实导出是 <Constant><ConstantValue>，也见过 ConstantValue 直接挂在 Access 下。
            // 两种都收，但**只往下看一层** —— 用 Descendants 会把调用参数里的常量当成本 Access 自己的值。
            var cvNode = acc.Element("Constant")?.Element("ConstantValue") ?? acc.Element("ConstantValue");
            if (cvNode != null)
            {
                var cv = cvNode.Value?.Trim();
                return (string.IsNullOrEmpty(cv) ? "?" : cv!, true);
            }
            if (Has(scope, "Constant"))
            {
                // 具名常量（块内 CONSTANT / 全局用户常量）没有值、只有名字：<Constant Name="RUN_FWD"/>。
                // 名字就是导出给出的事实，按 SCL 写法 #名 / "名" 回读 —— 原来一律读成 "?"，
                // 于是 MOVE 正转控制字 / 反转控制字 两个网络回读成一模一样的 MOVE ?。
                var constName = acc.Element("Constant")?.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(constName))
                    return (RootName(constName!, Has(scope, "Global")), true);
                // 既无值也无名：说不知道，别编一个。
                return ("?", true);
            }

            // 普通符号：分量是 <Symbol> 的**直接**子节点；数组下标是分量自己的子 Access。
            var symbol = acc.Element("Symbol");
            if (symbol != null)
            {
                var name = RenderSymbol(symbol, Has(scope, "Global"));
                return (string.IsNullOrEmpty(name) ? "?" : name, false);
            }

            // 调用 / 其它复合形态：按 token 流重放（Instruction 名 + 括号 + 参数都在子节点里）。
            var sb = new StringBuilder();
            AppendTokens(acc, sb);
            var text = sb.ToString().Trim();
            return (text.Length == 0 ? "?" : text, false);
        }

        /// <summary>一个 CompileUnit 里的 SCL/STL 正文，按导出的 token 流还原（含缩进与换行）。</summary>
        public static string RenderStructuredText(XElement unit)
        {
            var st = unit.Descendants("StructuredText").FirstOrDefault();
            if (st == null) return "";
            var sb = new StringBuilder();
            AppendTokens(st, sb);
            return sb.ToString();
        }

        /// <summary>
        /// 引号只包**单个**分量，不包整条路径（issue #42）：<c>"HMI".Axis.Speed</c>，
        /// 不是 <c>"HMI.Axis.Speed"</c> —— 后者在 SCL 里是另一个（通常不存在的）全局名。
        /// 根分量：全局一律带引号；局部是 #名，名字本身要引号时写 #"名"。
        /// 后续分量：纯标识符不加引号，含空格或 / 等字符的（M/A、my var）必须加，否则 M/A 会被读成除法。
        /// V21 真实导出只给全局根分量挂 HasQuotes=true，M/A、my var 都**不带**这个属性 ——
        /// 所以不能只看 HasQuotes，要按名字本身判断；HasQuotes=true 时照它的意思加。
        /// </summary>
        private static string RenderSymbol(XElement symbol, bool global)
        {
            var sb = new StringBuilder();
            foreach (var comp in symbol.Elements("Component"))
            {
                var name = comp.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                bool hasQuotes = comp.Elements("BooleanAttribute").Any(b =>
                    b.Attribute("Name")?.Value == "HasQuotes" && string.Equals(b.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
                if (sb.Length == 0) sb.Append(RootName(name!, global, hasQuotes));
                else sb.Append('.').Append(hasQuotes ? Quote(name!) : MemberName(name!));
                // arr[#i] / arr[1,2]：下标是分量下面的 Access，不是符号的下一段。
                var idx = comp.Elements("Access").Select(a => ReadAccess(a).text).ToList();
                if (idx.Count > 0) sb.Append('[').Append(string.Join(", ", idx)).Append(']');
            }
            return sb.ToString();
        }

        // 标识符判据与 SymbolPath 保持一致（字母/下划线开头，字母数字下划线组成）。
        private static bool IsIdentifier(string value) => Regex.IsMatch(value, @"^[\p{L}_][\p{L}\p{N}_]*$");
        private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        private static string MemberName(string value) => IsIdentifier(value) ? value : Quote(value);
        private static string RootName(string value, bool global, bool hasQuotes = false)
            => global ? Quote(value) : "#" + (hasQuotes ? Quote(value) : MemberName(value));

        private static void AppendTokens(XElement parent, StringBuilder sb)
        {
            foreach (var node in parent.Elements())
            {
                switch (node.Name.LocalName)
                {
                    case "Text": sb.Append(node.Value); break;
                    case "Token": sb.Append(node.Attribute("Text")?.Value ?? ""); break;
                    case "Blank": sb.Append(new string(' ', ParseNum(node, 1))); break;
                    case "NewLine": sb.Append('\n', ParseNum(node, 1)); break;
                    case "Access": sb.Append(ReadAccess(node).text); break;
                    // 指令名在属性上（实例调用没有 Name，实例名由前一个 Access 给出），
                    // 括号和参数都在子节点里 —— 名字漏了就成了「参数裸奔」。
                    case "Instruction": sb.Append(node.Attribute("Name")?.Value ?? ""); AppendTokens(node, sb); break;
                    // 块调用（FC/FB）：真实导出是 <CallInfo Name BlockType><Instance Scope><Component/></Instance>( … )。
                    // 有实例写实例（"DB"( … ) / #inst( … )），没有实例写块名（"FC"( … )）。
                    // 原来不认它，整条调用读成 "?"。
                    case "CallInfo":
                        var inst = node.Element("Instance");
                        var instName = inst?.Element("Component")?.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(instName))
                            sb.Append(RootName(instName!, !Has(inst!.Attribute("Scope")?.Value ?? "", "Local")));
                        else if (!string.IsNullOrEmpty(node.Attribute("Name")?.Value))
                            sb.Append(Quote(node.Attribute("Name")!.Value));
                        AppendTokens(node, sb);
                        break;
                    case "Parameter": sb.Append(node.Attribute("Name")?.Value ?? ""); AppendTokens(node, sb); break;
                    case "NamelessParameter": AppendTokens(node, sb); break;
                    case "Comment":
                    case "LineComment":
                        var ct = node.Descendants("Text").FirstOrDefault()?.Value;
                        if (!string.IsNullOrEmpty(ct)) sb.Append("//" + ct);
                        break;
                }
            }
        }

        private static bool Has(string scope, string word)
            => scope.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        private static int ParseNum(XElement e, int def)
            => int.TryParse(e.Attribute("Num")?.Value, out var n) ? n : def;
    }
}
