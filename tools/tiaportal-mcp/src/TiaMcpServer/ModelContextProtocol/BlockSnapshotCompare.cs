using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 导入后读回校验的三种结局。公开线的 ResponseMessage 只有 Message + Meta，
    /// 没有三态 Outcome 契约，所以三态在这一层表达，由调用方翻译成两态：
    ///   Mismatch → 计入 failed[]（真失败，不能当成功返回）
    ///   Unknown  → 正常返回，但 Message 以 "⚠ 未验证：" 开头 + Meta["verified"]=false
    /// 绝不能把 Unknown 折叠成 Verified —— "没验成" 冒充 "验过了" 比报错更糟。
    /// </summary>
    internal enum PlcBlockVerificationState
    {
        Verified,
        Mismatch,
        Unknown
    }

    internal sealed class PlcBlockVerificationOutcome
    {
        public PlcBlockVerificationOutcome(PlcBlockVerificationState state, string detail, PlcBlockAttributeSnapshot? actual = null)
        {
            State = state;
            Detail = detail;
            Actual = actual;
        }

        public PlcBlockVerificationState State { get; }
        public string Detail { get; }

        /// <summary>读回到的块（没读到时为 null），调用方据此把 name/number/布局原样回给模型。</summary>
        public PlcBlockAttributeSnapshot? Actual { get; }
    }

    /// <summary>
    /// 导入前后用来比对的块属性快照。
    /// PriorityNumber：SimaticML 的 OB 导出里根本不带这一项，读回侧也常常不暴露，
    /// 所以它读不到属于 "unavailable"，不是 "不相等"。
    /// </summary>
    internal sealed class PlcBlockAttributeSnapshot
    {
        public string Name { get; set; } = "";
        public int? Number { get; set; }

        /// <summary>OB 的类型（ProgramCycle / Startup / CyclicInterrupt …）。非 OB 块为 null。</summary>
        public string? SecondaryType { get; set; }
        public int? PriorityNumber { get; set; }
        public string? BlockKind { get; set; }

        /// <summary>Standard（非优化）/ Optimized。PUT/GET 与 S7 绝对地址读取只认 Standard。</summary>
        public string? MemoryLayout { get; set; }

        /// <summary>循环中断 OB 的周期（µs）。其它块为 null。</summary>
        public int? CyclicTimeUs { get; set; }
    }

    /// <summary>
    /// 读回校验里不碰 Openness 的那一半：从 SimaticML 读出"打算导入什么"，再和读回的快照逐项比对。
    /// 单独成文件是为了能在离线测试里直接跑（McpServer.BlockImportVerification.cs 依赖 Siemens.Engineering）。
    ///
    /// 判据是 **XML 里声明的块名 + 块编号**，不是 XML 文件名。两个原因：
    /// 1. 文件名 ≠ 块名。OB100 在 TIA 里默认叫 Startup，把它的 XML 存成 OB100.xml
    ///    导进去，按文件名去比就会把**一次成功的导入**报成「NOT found after import」。
    /// 2. 反过来，块若被静默降级（.s7dcl 导 OB 会全变成 OB1），光比名字也可能"对上"，
    ///    只有块编号抓得住。
    /// </summary>
    internal static class BlockSnapshotCompare
    {
        /// <summary>
        /// 从 SimaticML 文档里读出 "我打算导入的到底是什么"。
        /// 块名取 AttributeList/Name（不是文件名），编号取 Number，OB 再多带一个 SecondaryType，
        /// 布局取 MemoryLayout。
        /// </summary>
        internal static PlcBlockAttributeSnapshot? ReadExpectedBlockFromXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            var doc = XDocument.Parse(xml);
            var obj = doc.Root?.Elements().FirstOrDefault(e =>
                e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
            var attrs = obj?.Element("AttributeList");
            if (attrs == null)
                return null;

            var name = attrs.Element("Name")?.Value?.Trim() ?? "";
            if (string.IsNullOrEmpty(name))
                return null;

            return new PlcBlockAttributeSnapshot
            {
                Name = name,
                Number = ParseIntOrNull(attrs.Element("Number")?.Value),
                SecondaryType = NullIfBlank(attrs.Element("SecondaryType")?.Value),
                // SimaticML 的 OB 导出里没有 PriorityNumber —— 真实导出对拍过，这里读不到是正常的。
                PriorityNumber = ParseIntOrNull(attrs.Element("PriorityNumber")?.Value),
                BlockKind = obj!.Name.LocalName,
                MemoryLayout = NullIfBlank(attrs.Element("MemoryLayout")?.Value),
                CyclicTimeUs = ParseIntOrNull(attrs.Element("CyclicTime")?.Value)
            };
        }

        /// <summary>
        /// XML 的块种类（SW.Blocks.OB）与读回对象的 .NET 类型名（OB）是否是同一种块。
        /// 任何一边不知道就不下结论（返回 true）—— 只拿它排除明确不同的块。
        /// DB100、FC100、OB100 编号相同，按编号兜底时不看种类会认错块。
        /// </summary>
        internal static bool KindMatches(string? expectedKind, string? actualKind)
        {
            if (string.IsNullOrWhiteSpace(expectedKind) || string.IsNullOrWhiteSpace(actualKind)) return true;
            static string Norm(string k)
            {
                var s = k.Trim();
                var dot = s.LastIndexOf('.');
                return (dot >= 0 ? s.Substring(dot + 1) : s).ToLowerInvariant();
            }
            return Norm(expectedKind!) == Norm(actualKind!);
        }

        /// <summary>
        /// 逐属性比对。任何一项对不上都要点名，不能只给一个总的布尔值。
        /// 读不到的属性（比如 Openness 不暴露 PriorityNumber）不算不相等，
        /// 但要在说明里出现，否则调用方会把 "没验" 当成 "验过了"。
        /// </summary>
        internal static PlcBlockVerificationOutcome CompareBlockSnapshots(
            PlcBlockAttributeSnapshot expected,
            PlcBlockAttributeSnapshot? actual,
            string importFileNameWithoutExtension = "")
        {
            var fileNameHint = BuildFileNameHint(expected, importFileNameWithoutExtension);

            if (actual == null)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Mismatch,
                    $"block '{expected.Name}'"
                    + (expected.Number.HasValue ? $" (number {expected.Number.Value})" : "")
                    + " NOT found after import" + fileNameHint);
            }

            var mismatches = new List<string>();
            var unavailable = new List<string>();

            if (actual.Name == null)
                unavailable.Add("Name");
            else if (!string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"Name expected '{expected.Name}' actual '{actual.Name}'");

            if (!expected.Number.HasValue)
                unavailable.Add("Number (not declared in the imported XML)");
            else if (!actual.Number.HasValue)
                unavailable.Add("Number (not exposed on read-back)");
            else if (expected.Number.Value != actual.Number.Value)
                mismatches.Add($"Number expected '{expected.Number.Value}' actual '{actual.Number.Value}'");

            if (!string.IsNullOrEmpty(expected.SecondaryType))
            {
                if (string.IsNullOrEmpty(actual.SecondaryType))
                    unavailable.Add("SecondaryType (not exposed on read-back)");
                else if (!string.Equals(expected.SecondaryType, actual.SecondaryType, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add($"SecondaryType expected '{expected.SecondaryType}' actual '{actual.SecondaryType}'");
            }

            // 布局写进了 XML 就必须读回一致：TIA 若没照做，一个 "Standard" DB 实际是优化的，
            // PUT/GET 与绝对地址读取会在现场才失败。
            if (!string.IsNullOrEmpty(expected.MemoryLayout))
            {
                if (string.IsNullOrEmpty(actual.MemoryLayout))
                    unavailable.Add("MemoryLayout (not exposed on read-back)");
                else if (!string.Equals(expected.MemoryLayout, actual.MemoryLayout, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add($"MemoryLayout expected '{expected.MemoryLayout}' actual '{actual.MemoryLayout}'");
            }

            if (!KindMatches(expected.BlockKind, actual.BlockKind))
                mismatches.Add($"block kind expected '{expected.BlockKind}' actual '{actual.BlockKind}'");

            // 循环中断的周期写进了 XML 就必须生效：写错单位（ms 当 µs）只有这里抓得住。
            if (expected.CyclicTimeUs.HasValue)
            {
                if (!actual.CyclicTimeUs.HasValue)
                    unavailable.Add("CyclicTime (not exposed on read-back)");
                else if (expected.CyclicTimeUs.Value != actual.CyclicTimeUs.Value)
                    mismatches.Add($"CyclicTime expected '{expected.CyclicTimeUs.Value}' µs actual '{actual.CyclicTimeUs.Value}' µs");
            }

            // PriorityNumber 走 XML 往返必丢：导出文档里没有这一项，读回侧也常常不暴露。
            // 所以只有两边都拿得到值时才真比，否则记 unavailable。
            if (expected.PriorityNumber.HasValue && actual.PriorityNumber.HasValue)
            {
                if (expected.PriorityNumber.Value != actual.PriorityNumber.Value)
                    mismatches.Add($"PriorityNumber expected '{expected.PriorityNumber.Value}' actual '{actual.PriorityNumber.Value}'");
            }
            else
            {
                unavailable.Add("PriorityNumber (SimaticML block export does not carry it; set/check it in TIA)");
            }

            if (mismatches.Count > 0)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Mismatch,
                    $"block '{actual.Name}' found but attribute mismatch: " + string.Join("; ", mismatches) + fileNameHint,
                    actual);
            }

            var detail = $"block '{actual.Name}'"
                + (actual.Number.HasValue ? $" (number {actual.Number.Value})" : "")
                + " present after import; attributes match" + fileNameHint
                + (unavailable.Count > 0 ? $"; not verifiable via XML round-trip: {string.Join(", ", unavailable)}" : "");

            return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Verified, detail, actual);
        }

        private static string BuildFileNameHint(PlcBlockAttributeSnapshot expected, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            if (string.Equals(fileName, expected.Name, StringComparison.OrdinalIgnoreCase)) return "";

            // 文件名叫 OB100、块名叫 Startup，两者本来就不该相等 —— 按文件名比会把成功误判成失败。
            return $" (file name '{fileName}' differs from the block name '{expected.Name}' declared in the XML — "
                 + "normal for OBs, so the block number is the authoritative check)";
        }

        private static int? ParseIntOrNull(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return int.TryParse(value!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}
