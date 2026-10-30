using ModelContextProtocol;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

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
        public PlcBlockVerificationOutcome(PlcBlockVerificationState state, string detail)
        {
            State = state;
            Detail = detail;
        }

        public PlcBlockVerificationState State { get; }
        public string Detail { get; }
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
    }

    /// <summary>
    /// Partial: 导入后的读回校验。
    ///
    /// 判据是 **XML 里声明的块名 + 块编号**，不是 XML 文件名。两个原因：
    /// 1. 文件名 ≠ 块名。OB100 在 TIA 里默认叫 Startup，把它的 XML 存成 OB100.xml
    ///    导进去，按文件名去比就会把**一次成功的导入**报成「NOT found after import」，
    ///    调用方于是去重导、去排查一个根本不存在的问题。
    /// 2. 反过来，块若被静默降级（.s7dcl 导 OB 会全变成 OB1），光比名字也可能"对上"，
    ///    只有块编号抓得住。
    /// </summary>
    public static partial class McpServer
    {
        #region block import verification

        /// 导入后校验：判据是 XML 里声明的块名 + 块编号，而不是 XML 文件名。
        /// 校验过程本身出错（没连接、代理失效、XML 读不出块名）一律记 Unknown，
        /// 既不冒充导入失败，也不冒充校验通过。
        /// </summary>
        internal static PlcBlockVerificationOutcome VerifyImportedBlock(string softwarePath, string xmlPath)
        {
            PlcBlockAttributeSnapshot? expected;
            try
            {
                expected = ReadExpectedBlockFromXml(System.IO.File.ReadAllText(xmlPath));
            }
            catch (Exception ex)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Unknown,
                    $"could not read the generated XML back for verification ({ex.Message})");
            }

            if (expected == null)
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Unknown,
                    "the generated XML declares no AttributeList/Name, so there is nothing to verify against");

            PlcBlockAttributeSnapshot? actual;
            try
            {
                actual = ReadBackPlcBlockSnapshot(softwarePath, expected);
            }
            catch (Exception ex)
            {
                return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Unknown,
                    $"read-back of block '{expected.Name}' failed ({ex.Message})");
            }

            return CompareBlockSnapshots(expected, actual, System.IO.Path.GetFileNameWithoutExtension(xmlPath));
        }

        /// <summary>
        /// 导入后按 "XML 里声明的块名 + 块编号" 读回一个块。
        /// 名字找不到就按编号在全量块里兜底 —— OB 的名字可以被工程改掉，编号不会。
        /// </summary>
        internal static PlcBlockAttributeSnapshot? ReadBackPlcBlockSnapshot(string softwarePath, PlcBlockAttributeSnapshot expected)
        {
            var escaped = Regex.Escape(expected.Name);
            var found = Portal.GetBlocks(softwarePath, $"^{escaped}$");
            if (found == null || found.Count == 0)
                found = Portal.GetBlocks(softwarePath, escaped);

            PlcBlock? hit = found?.FirstOrDefault();

            if (hit == null && expected.Number.HasValue)
            {
                var all = Portal.GetBlocks(softwarePath, "");
                hit = all?.FirstOrDefault(b => SafeNumber(b) == expected.Number.Value);
            }

            return hit == null ? null : SnapshotOf(hit);
        }

        /// <summary>
        /// 从 SimaticML 文档里读出 "我打算导入的到底是什么"。
        /// 块名取 AttributeList/Name（不是文件名），编号取 Number，OB 再多带一个 SecondaryType。
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
                BlockKind = obj!.Name.LocalName
            };
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
                    $"block '{actual.Name}' found but attribute mismatch: " + string.Join("; ", mismatches) + fileNameHint);
            }

            var detail = $"block '{actual.Name}'"
                + (actual.Number.HasValue ? $" (number {actual.Number.Value})" : "")
                + " present after import; attributes match" + fileNameHint
                + (unavailable.Count > 0 ? $"; not verifiable via XML round-trip: {string.Join(", ", unavailable)}" : "");

            return new PlcBlockVerificationOutcome(PlcBlockVerificationState.Verified, detail);
        }

        private static string BuildFileNameHint(PlcBlockAttributeSnapshot expected, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            if (string.Equals(fileName, expected.Name, StringComparison.OrdinalIgnoreCase)) return "";

            // 文件名叫 OB100、块名叫 Startup，两者本来就不该相等 —— 按文件名比会把成功误判成失败。
            return $" (file name '{fileName}' differs from the block name '{expected.Name}' declared in the XML — "
                 + "normal for OBs, so the block number is the authoritative check)";
        }

        private static PlcBlockAttributeSnapshot SnapshotOf(PlcBlock block)
        {
            var snapshot = new PlcBlockAttributeSnapshot
            {
                Name = block.Name,
                Number = SafeNumber(block),
                BlockKind = block.GetType().Name
            };

            if (block is OB ob)
            {
                try { snapshot.SecondaryType = ob.SecondaryType; } catch { /* 代理失效时宁可标 unavailable，也不谎报不相等 */ }
            }

            snapshot.PriorityNumber = TryReadPriority(block);
            return snapshot;
        }

        /// <summary>
        /// 优先级在 .NET API 上没有属性（V21 反射确认：OB 只有 Name/Number/SecondaryType 等），
        /// 只可能作为动态属性出现。所以按属性名去问，问不到就返回 null（记 unavailable），不猜。
        /// </summary>
        private static int? TryReadPriority(PlcBlock block)
        {
            try
            {
                if (block is not IEngineeringObject eo) return null;
                var info = eo.GetAttributeInfos()
                    .FirstOrDefault(a => a.Name.IndexOf("Priority", StringComparison.OrdinalIgnoreCase) >= 0);
                if (info == null) return null;
                var value = eo.GetAttribute(info.Name);
                return value == null ? null : Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private static int? SafeNumber(PlcBlock block)
        {
            try { return block.Number; } catch { return null; }
        }

        private static int? ParseIntOrNull(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return int.TryParse(value!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        }

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        #endregion
    }
}
