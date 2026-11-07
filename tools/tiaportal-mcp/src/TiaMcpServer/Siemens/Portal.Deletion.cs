using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial: 删除程序块 / PLC 变量表 / 用户数据类型（UDT）。
    ///
    /// 为什么删除单独成一族、而不是散在各自的 Portal.Blocks.cs / Portal.Software.cs 里：
    /// 删除是这个服务器里唯一**不可逆**的一类写操作，护栏（精确路径、dryRun、删后回读）
    /// 必须三个入口一模一样。放在一起，改一条规矩就三处一起改，不会漏。
    ///
    /// 三条共同规矩：
    /// 1) 路径必须精确 —— 叶子名含正则元字符一律拒绝。理由见 <see cref="ResolveSingleByName"/>：
    ///    定位是「先字面、再锚定正则」，模式仍可能命中到一个**同样合法但不是你要的**对象；
    ///    读错了顶多返回错东西，删错了工程就没了。所以删除口不吃模式，一个字符都不行。
    /// 2) dryRun=true 时**一行工程都不动**，只解析目标并把代价（引用它的是谁）摆出来。
    /// 3) dryRun=false 时删完必须重新取句柄回读；对象还在 = 失败，抛异常，绝不报成功。
    ///
    /// 为什么删变量表比删块更危险：块被删了，调用方编译立刻报错；
    /// 一张变量表被删，表里所有变量的**符号**同时消失，而 HMI 是按符号名绑定 PLC 变量的，
    /// PLC 侧编译不一定报错，故障要到画面上才暴露。所以这里的预览必须把
    /// 「这张表里有多少变量、都叫什么」摆给调用方，而不是只回一句「找到了，可以删」。
    /// </summary>
    public partial class Portal
    {
        #region delete blocks / tag tables / types

        /// <summary>
        /// 预览或删除**一个** PLC 程序块（FB / FC / OB / 全局 DB / 背景 DB 都是 PlcBlock）。
        /// dryRun=true（默认）只解析目标，不做任何改动。
        /// </summary>
        public JsonObject DeletePlcBlock(string softwarePath, string blockPath, bool dryRun)
        {
            // 参数校验放在连接检查之前：它不需要工程，放后面就只有连着 TIA 时才走得到，
            // 等于离线永远测不到这条兜底（那样它是不是死代码根本无从判断）。
            if (string.IsNullOrWhiteSpace(blockPath))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcBlock: blockPath is empty");
            }

            var leaf = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;
            if (leaf.IndexOfAny(_regexChars) >= 0)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "DeletePlcBlock requires one exact block path; regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DeletePlcBlock: no project is open. Connect / AttachToOpenProject first.");
            }

            var block = GetBlock(softwarePath, blockPath);
            if (block == null)
            {
                // 打错名字和「块确实不存在」对调用方是两件事，把可选项列出来才好改。
                var known = GetBlocks(softwarePath);
                var names = known?.Select(GetBlockPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                  .Take(50).ToList() ?? new List<string>();
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcBlock: block '{blockPath}' not found in '{softwarePath}'" +
                    (names.Count > 0
                        ? ". Available (first 50): " + string.Join(", ", names)
                        : " (this PLC has no blocks, or the block list could not be read)") +
                    AvailablePlcPathsSuffix(),
                    names);
            }

            string resolvedPath = GetBlockPath(block);
            string blockType = block.GetType().Name;

            // 🔴 显式块号是**在删除这一刻**丢的，事后再提醒已经晚了：
            // 把 FB 钉成 103（AutoNumber=false）→ 删掉 → 从同一份外部源重建 →
            // 新块拿到自动分配的号，103 一去不返，依赖它的实例 DB 关联随之断裂，
            // 而整个过程**没有任何报错**。所以要在 dryRun 阶段就把号读出来交给工具层去警告。
            // （注意「不删、直接对已有块重新生成」是安全的，编号原样保留。）
            int? pinnedNumber = null;
            try
            {
                if (!block.AutoNumber) pinnedNumber = block.Number;
            }
            catch
            {
                // 某些块类型不给 Number/AutoNumber。读不到就不警告，但绝不因此让删除失败。
            }

            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["requestedBlockPath"] = blockPath,
                ["resolvedBlockPath"] = resolvedPath,
                ["blockName"] = block.Name,
                ["blockType"] = blockType,
                ["pinnedBlockNumber"] = pinnedNumber,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            var warnings = new JsonArray
            {
                "删除只删这一个块。它的背景 DB、以及调用它的块都不会被一起删，"
                + "调用方会变成悬空引用 —— 删后必须 CompileSoftware 才看得出影响面。"
            };

            // 交叉引用：能查就查，查不到必须说清楚是「查不了」，不是「没人用」。
            var refs = GetCrossReferences(softwarePath, resolvedPath, "Block");
            result["crossReferenceAvailable"] = refs != null;
            if (refs != null)
            {
                result["crossReferenceCount"] = refs.Count;
                result["crossReferences"] = ToCrossReferenceArray(refs);
            }
            else
            {
                result["crossReferenceCount"] = null;
                result["crossReferences"] = null;
                warnings.Add("⚠️ 取不到这个块的交叉引用，这**不等于**没人调用它。"
                    + "删前请先 ExportAsDocuments 备份，删后必须 CompileSoftware 看错误。");
            }
            result["warnings"] = warnings;

            if (dryRun) return result;

            block.Delete();

            // Delete() 之后原来的代理对象已死，回读必须从 PlcSoftware 重新解析一遍路径。
            bool absent = GetBlock(softwarePath, resolvedPath) == null;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcBlock: Delete() returned but '{resolvedPath}' is still present");
            }
            return result;
        }

        /// <summary>
        /// 预览或删除一张 PLC 变量表。<paramref name="tagTableName"/> 接受裸表名或
        /// GetPlcTagTables 返回的组限定名（"驱动/变频器变量表"），反斜杠也当分隔符。
        /// dryRun=true（默认）只解析并清点内容，不做任何改动。
        /// </summary>
        public JsonObject DeletePlcTagTable(string softwarePath, string tagTableName, bool dryRun)
        {
            // 同上：参数校验先于连接检查，否则离线走不到，无法判断它是不是死代码。
            if (string.IsNullOrWhiteSpace(tagTableName))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcTagTable: tagTableName is empty");
            }

            // 变量表名允许带 '/' 做组分隔，所以只校验叶子名里的元字符。
            var tableLeaf = tagTableName.Replace('\\', '/').Trim('/');
            tableLeaf = tableLeaf.Contains("/") ? tableLeaf.Substring(tableLeaf.LastIndexOf("/") + 1) : tableLeaf;
            if (tableLeaf.IndexOfAny(_regexChars) >= 0)
            {
                // 变量表的定位走字面比对，本来不会把名字当模式；但删除口的规矩三处一致比
                // 「这一处恰好安全」更重要 —— 以后谁把定位换成模式匹配，这道闸仍然在。
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "DeletePlcTagTable requires one exact tag table name or group-qualified path; "
                    + "regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DeletePlcTagTable: no project is open. Connect / AttachToOpenProject first.");
            }

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTagTable: PLC software not found at '{softwarePath}'." + AvailablePlcPathsSuffix());
            }

            var group = ResolvePlcTagTableGroup(plc);
            if (group == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTagTable: tag table group not found on '{softwarePath}' (plcType={plc.GetType().FullName})");
            }

            var wanted = tagTableName.Replace('\\', '/').Trim('/');
            var table = FindTagTableWithPath(group, string.Empty, wanted,
                new HashSet<object>(ReferenceEqualityComparer.Instance), out var resolvedPath);
            if (table == null)
            {
                // 打错名字和「表确实不存在」对调用方是两件事，把可选项列出来才好改。
                var known = GetPlcTagTables(softwarePath) ?? new List<string>();
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTagTable: no tag table named '{tagTableName}' in '{softwarePath}'" +
                    (known.Count > 0 ? ". Available: " + string.Join(", ", known) : " (this PLC has no tag tables)"),
                    known);
            }

            var name = TryGetPropertyValue(table, "Name")?.ToString() ?? wanted;
            bool isDefault = TryGetPropertyValue(table, "IsDefault") is bool b && b;
            if (isDefault)
            {
                // PLC 必须留一张默认变量表，删掉它 TIA 侧行为未定义。宁可挡住也不试。
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"DeletePlcTagTable: '{resolvedPath}' is the DEFAULT tag table (IsDefault=true) and is refused. " +
                    "A PLC always needs one default table. To empty it, delete its tags individually in TIA.");
            }

            var tags = CollectTagSummary(table, out int tagCount);
            int userConstants = CountOf(TryGetPropertyValue(table, "UserConstants"));
            int systemConstants = CountOf(TryGetPropertyValue(table, "SystemConstants"));

            var warnings = new JsonArray
            {
                "删除后这张表里全部 " + tagCount + " 个变量的符号一并消失。"
                + "HMI 按符号名绑定 PLC 变量，PLC 侧编译不一定报错，断链要到画面上才暴露。",
                "被删的变量若在程序里以绝对地址（%I/%Q/%M）使用，程序仍能编译通过，"
                + "但注释和符号全丢，事后无法还原。"
            };

            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["requestedTagTableName"] = tagTableName,
                ["resolvedTagTablePath"] = resolvedPath,
                ["tagTableName"] = name,
                ["isDefaultTable"] = isDefault,
                ["tagCount"] = tagCount,
                ["tags"] = tags,
                ["userConstantCount"] = userConstants,
                ["systemConstantCount"] = systemConstants,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            // 交叉引用：能查就查，查不到必须说清楚是「查不了」，不是「没人用」。
            var refs = TryGetTagTableCrossReferences(table, resolvedPath, out var crossRefReason);
            result["crossReferenceAvailable"] = refs != null;
            result["crossReferenceUnavailableReason"] = crossRefReason;
            if (refs != null)
            {
                result["crossReferences"] = ToCrossReferenceArray(refs);
                result["crossReferenceCount"] = refs.Count;
            }
            else
            {
                result["crossReferences"] = null;
                result["crossReferenceCount"] = null;
                warnings.Add("⚠️ 没有查到这张表的交叉引用（原因见 crossReferenceUnavailableReason）。"
                    + "这**不等于**没人引用它。要确认，请先 ExportPlcTagTable 备份，"
                    + "再对可能用到这些符号的块逐个 GetCrossReferences，或在 TIA 里手工看交叉引用。");
            }

            result["warnings"] = warnings;

            if (dryRun) return result;

            // 反射调用 Delete()：本文件里变量表一路都是 object（PlcSoftware / HMI 软件两种形态共用
            // 同一套遍历），保持一致，不为一个调用把整条链改成强类型。
            var invoked = TryInvokeVoidMethod(table, "Delete");
            if (!invoked)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcTagTable: '{resolvedPath}' has no callable Delete() (type={table.GetType().FullName})");
            }

            // Delete() 之后原来的代理对象已死，读回必须从 PlcSoftware 重新取一遍句柄。
            var plc2 = GetPlcSoftware(softwarePath);
            var group2 = plc2 == null ? null : ResolvePlcTagTableGroup(plc2);
            bool absent = group2 == null || FindTagTableWithPath(group2, string.Empty, resolvedPath,
                new HashSet<object>(ReferenceEqualityComparer.Instance), out _) == null;

            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcTagTable: Delete() returned but '{resolvedPath}' is still present");
            }
            return result;
        }

        /// <summary>
        /// 预览或删除一个 PLC 用户数据类型（UDT）。dryRun=true（默认）只解析并查引用。
        /// </summary>
        public JsonObject DeletePlcType(string softwarePath, string typePath, bool dryRun)
        {
            // 同上：参数校验先于连接检查，否则离线走不到，无法判断它是不是死代码。
            if (string.IsNullOrWhiteSpace(typePath))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcType: typePath is empty");
            }

            var leaf = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;
            if (leaf.IndexOfAny(_regexChars) >= 0)
            {
                // GetType() 在没有字面同名时会把名字当锚定模式匹配，删除工具绝不能吃这一口。
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "DeletePlcType requires one exact type path; regular expressions and wildcards are not allowed");
            }

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DeletePlcType: no project is open. Connect / AttachToOpenProject first.");
            }

            PlcType? type = GetType(softwarePath, typePath);
            if (type == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcType: type '{typePath}' not found in '{softwarePath}'." + AvailablePlcPathsSuffix());
            }

            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["requestedTypePath"] = typePath,
                ["typeName"] = type.Name,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false
            };

            var warnings = new JsonArray
            {
                "UDT 被删后，所有以它为数据类型的 DB / 块接口全部失去类型定义，"
                + "必须重新编译整个 PLC 才能看出影响面。"
            };

            var refs = GetCrossReferences(softwarePath, typePath, "Type");
            result["crossReferenceAvailable"] = refs != null;
            if (refs != null)
            {
                result["crossReferenceCount"] = refs.Count;
                result["crossReferences"] = ToCrossReferenceArray(refs);
            }
            else
            {
                result["crossReferenceCount"] = null;
                result["crossReferences"] = null;
                warnings.Add("⚠️ 取不到这个 UDT 的交叉引用，这**不等于**没人用它。"
                    + "删前请先 ExportType 备份，删后必须 CompileSoftware 看错误。");
            }
            result["warnings"] = warnings;

            if (dryRun) return result;

            type.Delete();
            bool absent = GetType(softwarePath, typePath) == null;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"DeletePlcType: Delete() returned but '{typePath}' is still present");
            }
            return result;
        }

        private static JsonArray ToCrossReferenceArray(List<ModelContextProtocol.CrossReferenceEntry> refs)
        {
            var arr = new JsonArray();
            foreach (var r in refs)
            {
                arr.Add(new JsonObject
                {
                    ["sourceName"] = r.SourceName,
                    ["sourcePath"] = r.SourcePath,
                    ["referenceName"] = r.ReferenceName,
                    ["referencePath"] = r.ReferencePath,
                    ["referenceType"] = r.ReferenceType,
                    ["access"] = r.Access
                });
            }
            return arr;
        }

        /// <summary>
        /// 和 Portal.Software.cs 里的 FindTagTable 走同一棵树、同一套匹配规则，
        /// 但额外回传组限定路径 —— 删除要报「到底删的是哪一张」，裸表名在多组重名时不够用。
        /// </summary>
        private static object? FindTagTableWithPath(
            object group, string prefix, string wanted, HashSet<object> visited, out string resolvedPath)
        {
            resolvedPath = wanted;
            if (!visited.Add(group)) return null;

            var tables = TryGetPropertyValue(group, "TagTables");
            if (tables is IEnumerable tEnum and not string)
            {
                foreach (var t in tEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    if (name.Length == 0) continue;
                    var qualified = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(qualified, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedPath = qualified;
                        return t;
                    }
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = TryGetPropertyValue(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    var hit = FindTagTableWithPath(sub, next, wanted, visited, out resolvedPath);
                    if (hit != null) return hit;
                }
            }
            resolvedPath = wanted;
            return null;
        }

        /// <summary>清点表里的变量。列表封顶，免得一张几千行的表把响应撑爆。</summary>
        private const int TagPreviewLimit = 200;

        private static JsonArray CollectTagSummary(object table, out int tagCount)
        {
            tagCount = 0;
            var arr = new JsonArray();
            var tags = TryGetPropertyValue(table, "Tags");
            if (tags is not IEnumerable e || tags is string) return arr;

            foreach (var tag in e)
            {
                if (tag == null) continue;
                tagCount++;
                if (arr.Count >= TagPreviewLimit) continue;
                arr.Add(new JsonObject
                {
                    ["name"] = TryGetPropertyValue(tag, "Name")?.ToString(),
                    ["dataType"] = TryGetPropertyValue(tag, "DataTypeName")?.ToString(),
                    ["address"] = TryGetPropertyValue(tag, "LogicalAddress")?.ToString()
                });
            }
            return arr;
        }

        private static int CountOf(object? collection)
        {
            if (collection == null) return 0;
            if (TryGetPropertyValue(collection, "Count") is int n) return n;
            if (collection is IEnumerable e and not string) return e.Cast<object?>().Count(x => x != null);
            return 0;
        }

        /// <summary>
        /// 试着从变量表对象自己取 CrossReferenceService。
        /// 已知事实（V21 真机实测）：Software / Device / DeviceItem 三层都回 "service not available"，
        /// 只有 Block 层给得出。变量表属于哪一层没有实测数据，
        /// 所以这里**试一次**，拿不到就如实回 null + 原因，绝不假装查过。
        /// </summary>
        private static List<ModelContextProtocol.CrossReferenceEntry>? TryGetTagTableCrossReferences(
            object table, string resolvedPath, out string? reason)
        {
            var svc = TryGetServiceByTypeSuffix(table, "CrossReferenceService");
            if (svc == null)
            {
                reason = "PlcTagTable 给不出 CrossReferenceService（V21 上已知 Software/Device/DeviceItem "
                       + "三层都不给，只有 Block 层给得出）。所以这张表有没有人引用，本工具查不到。";
                return null;
            }

            var raw = TryInvokeGetCrossReferences(svc, "AllObjects");
            if (raw == null)
            {
                reason = "拿到了 CrossReferenceService，但 GetCrossReferences(AllObjects) 调不动。";
                return null;
            }

            reason = null;
            return TryFlattenCrossReferenceResult(raw, resolvedPath);
        }

        private static bool TryInvokeVoidMethod(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
            if (method == null) return false;
            method.Invoke(target, Array.Empty<object>());
            return true;
        }

        #endregion
    }
}
