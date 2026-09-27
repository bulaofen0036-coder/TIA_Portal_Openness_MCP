using ModelContextProtocol;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Partial: 导入后的读回校验中碰 Openness 的那一半（按名/按号读回块、做快照）。
    /// 判据与比对规则在 BlockSnapshotCompare.cs（不依赖 Siemens.Engineering，离线可测）。
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
                expected = BlockSnapshotCompare.ReadExpectedBlockFromXml(System.IO.File.ReadAllText(xmlPath));
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

            return BlockSnapshotCompare.CompareBlockSnapshots(expected, actual, System.IO.Path.GetFileNameWithoutExtension(xmlPath));
        }

        /// <summary>
        /// 导入后按 "XML 里声明的块名 + 块编号" 读回一个块，顺序：
        /// 1. 名字精确匹配；
        /// 2. 编号 + 块种类（OB 的名字可以被工程改掉，编号不会；但 DB100/FC100/OB100 编号相同，必须同时比种类）；
        /// 3. 名字模糊匹配 —— 只在恰好命中一个块时才算，免得 "Startup_1" 顶替 "Startup"。
        /// </summary>
        internal static PlcBlockAttributeSnapshot? ReadBackPlcBlockSnapshot(string softwarePath, PlcBlockAttributeSnapshot expected)
        {
            var escaped = Regex.Escape(expected.Name);
            var exact = Portal.GetBlocks(softwarePath, $"^{escaped}$");
            PlcBlock? hit = exact?.FirstOrDefault(b => BlockSnapshotCompare.KindMatches(expected.BlockKind, b.GetType().Name))
                            ?? exact?.FirstOrDefault();

            if (hit == null && expected.Number.HasValue)
            {
                var all = Portal.GetBlocks(softwarePath, "");
                hit = all?.FirstOrDefault(b => SafeNumber(b) == expected.Number.Value
                                               && BlockSnapshotCompare.KindMatches(expected.BlockKind, b.GetType().Name));
            }

            if (hit == null)
            {
                var loose = Portal.GetBlocks(softwarePath, escaped);
                if (loose != null && loose.Count == 1)
                    hit = loose[0];
            }

            return hit == null ? null : SnapshotOf(hit);
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

            // 与 GetBlockInfo 读的是同一个属性；读不到记 unavailable，不猜。
            try { snapshot.MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout); } catch { }

            // 循环中断 OB 的周期只是动态属性（V21 实测 GetBlockInfo 列在 Attributes 里）。
            if (block is OB && block is IEngineeringObject eo)
            {
                try
                {
                    if (eo.GetAttributeInfos().Any(a => a.Name == "CyclicTime"))
                        snapshot.CyclicTimeUs = Convert.ToInt32(eo.GetAttribute("CyclicTime"));
                }
                catch { /* 读不到记 unavailable */ }
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

        #endregion
    }
}
