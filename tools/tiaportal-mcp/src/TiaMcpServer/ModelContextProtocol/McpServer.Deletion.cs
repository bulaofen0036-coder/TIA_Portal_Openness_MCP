using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Partial: 删除族 —— 程序块（含全局 DB / 背景 DB / FB / FC / OB）、PLC 变量表、用户数据类型。
    /// 三个工具一个规格：dryRun 默认 true、路径必须精确、预览要把代价说清楚、
    /// 实删之后必须读回确认对象真的不在了。
    ///
    /// 关于「成功」的口径（这一族最容易骗人的地方）：
    /// 本线的 ResponseMessage 只有 Message/Meta，没有三态 Outcome，所以「删成功」和
    /// 「删是删了，但关键判据没拿到」只能靠 Message + Warnings 区分。规矩定死：
    ///   · Portal 抛异常 → 这里转成 McpException，**绝不**吞成一条空 Message 报成功；
    ///   · 删完回读没确认对象消失 → Ok=false + 消息里明写「未验证」，不算成功；
    ///   · dryRun 预览里交叉引用取不到 → Ok 仍为 true（预览本身是做成了的），
    ///     但消息与 Warnings 必须写明「查不到 ≠ 没人用」，免得被读成「确认可以删」。
    /// </summary>
    public static partial class McpServer
    {
        #region delete blocks / tag tables / types

        [McpServerTool(Name = "DeletePlcBlock"), Description(
            "[L2][PLC-Software][Destructive] Preview or delete exactly one PLC block by its exact path, including "
            + "blocks inside nested groups. THIS IS ALSO THE TOOL FOR DELETING A DATA BLOCK: global DB, instance DB, "
            + "ARRAY DB, FB, FC and OB are all PLC blocks, so there is no separate DeleteGlobalDb / DeleteDb / "
            + "DeleteFunctionBlock tool - use this one. Defaults to dryRun=true, which changes nothing and reports "
            + "the resolved target plus its cross references. It never deletes instance DBs or callers "
            + "automatically. Before dryRun=false, review the previewed cross references (or run GetCrossReferences) "
            + "and back the block up with ExportAsDocuments; compile with CompileSoftware after deletion and before "
            + "SaveProject. Regex and wildcards are rejected. To delete a tag table use DeletePlcTagTable, a UDT use "
            + "DeletePlcType.")]
        public static ResponseJsonReport DeletePlcBlock(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockPath: exact block path, e.g. 'DB_Test' or 'GroupA/FB_Motor'. Regex and wildcards are rejected.")] string blockPath,
            [Description("dryRun: true (default) only resolves and reports the target; false performs Delete() and verifies the block is absent")] bool dryRun = true)
        {
            try
            {
                var data = Portal.DeletePlcBlock(softwarePath, blockPath, dryRun);
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;
                int? pinned = data["pinnedBlockNumber"]?.GetValue<int>();

                // 🔴 显式块号是**在删除这一刻**丢的，事后再提醒已经晚了：
                // 把 FB 钉成 103（AutoNumber=false）→ 删掉 → 从同一份外部源重建 →
                // 新块拿到自动分配的号，103 一去不返，依赖它的实例 DB 关联随之断裂，全程无报错。
                // 「不删、直接对已有块重新生成」是安全的，编号原样保留 —— 所以这里要说的不是
                // 「别删」，而是「删了就拿不回来，想保号就别删」。
                string? pinnedWarning = pinned == null ? null
                    : $"该块钉着显式块号 {pinned}（AutoNumber=false）。"
                      + (dryRun ? "一旦真的删除，这个号就没了 —— " : "这个号已经随块一起没了 —— ")
                      + "从外部源重建时新块会拿到自动分配的号，依赖原块号的实例 DB 关联会断，且不会有任何报错。"
                      + "若只是想更新块内容，请不要删，直接对已有块 GenerateBlocksFromExternalSource，编号会保留。"
                      + $"确实要删并重建的话，重建后用 InvokeObject 把号改回去：SetAttribute(\"AutoNumber\", false) 然后 SetAttribute(\"Number\", {pinned})。";

                return BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"程序块 '{data["resolvedBlockPath"]}'",
                    dryRunTail: "确认无误后用 dryRun=false 实际删除。",
                    extraWarning: pinnedWarning,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportAsDocuments —— 删之前先把这个块导出备份",
                            "GetCrossReferences —— 逐个看还有谁在调用它",
                            "确认后再 DeletePlcBlock(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompileSoftware —— 看调用方有没有变成悬空引用",
                            "SaveProject —— 确认无误后再存盘"
                        });
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC block '{blockPath}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC block '{blockPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcTagTable"), Description(
            "[L2][PLC-Software][Destructive] Preview or delete ONE PLC tag table (variable table / tag list) "
            + "by name, including tables nested in user groups. Defaults to dryRun=true, which only reports what "
            + "the table contains and deletes nothing. DANGER: deleting a tag table removes the SYMBOLS of every "
            + "tag in it. HMI panels bind PLC tags by symbolic name, so the PLC may still compile clean while the "
            + "HMI silently loses its bindings - always review the previewed tag list and cross references first. "
            + "The default tag table (IsDefault) is refused. Cross references are attempted but may be unavailable "
            + "at tag-table level; the response says explicitly whether they were obtained. Regex and wildcards are "
            + "rejected. Back up first with ExportPlcTagTable.")]
        public static ResponseJsonReport DeletePlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tagTableName: bare table name, or the group-qualified path from GetPlcTagTables (e.g. 'Drives/VFD tags'). Regex and wildcards are rejected.")] string tagTableName,
            [Description("dryRun: true (default) only resolves the table and lists its contents; false performs Delete() and verifies the table is absent")] bool dryRun = true)
        {
            try
            {
                var data = Portal.DeletePlcTagTable(softwarePath, tagTableName, dryRun);
                int tagCount = data["tagCount"]?.GetValue<int>() ?? 0;
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;

                var report = BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"变量表 '{data["resolvedTagTablePath"]}'（{tagCount} 个变量）",
                    dryRunTail: "确认无误后用 dryRun=false 实际删除。",
                    extraWarning: null,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportPlcTagTable —— 删之前先把这张表导出备份",
                            "逐个 GetCrossReferences 相关块 —— 表级交叉引用未必可用，块级可用",
                            "确认后再 DeletePlcTagTable(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompileSoftware —— 看 PLC 侧有没有断链",
                            "⚠️ HMI 侧的符号绑定编译查不出来，请单独核对画面变量",
                            "SaveProject —— 确认无误后再存盘"
                        });

                report.Meta!["tagCount"] = tagCount;
                return report;
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC tag table '{tagTableName}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcType"), Description(
            "[L2][PLC-Software][Destructive] Preview or delete ONE PLC user data type (UDT / PlcType) by its exact "
            + "path. Defaults to dryRun=true. Deleting a UDT breaks every DB and block interface declared with it, "
            + "so the preview reports its cross references (GetCrossReferences works at type level) before you "
            + "commit - review them first. Regex and wildcards are rejected. Export the type first with ExportType, "
            + "and CompileSoftware afterwards.")]
        public static ResponseJsonReport DeletePlcType(
            [Description("softwarePath: path in the project structure to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("typePath: exact UDT path, e.g. 'UDT_Motor' or 'GroupA/UDT_Motor'. Regex and wildcards are rejected.")] string typePath,
            [Description("dryRun: true (default) only resolves the type and reports its cross references; false performs Delete() and verifies the type is absent")] bool dryRun = true)
        {
            try
            {
                var data = Portal.DeletePlcType(softwarePath, typePath, dryRun);
                bool crossRefOk = data["crossReferenceAvailable"]?.GetValue<bool>() ?? false;

                return BuildDeletionReport(
                    data, dryRun, crossRefOk,
                    objectLabel: $"UDT '{typePath}'",
                    dryRunTail: "确认无误后用 dryRun=false 实际删除。",
                    extraWarning: null,
                    nextActions: dryRun
                        ? new JsonArray
                        {
                            "ExportType —— 删之前先把这个 UDT 导出备份",
                            "GetCrossReferences —— 看还有哪些 DB / 块用它做数据类型",
                            "确认后再 DeletePlcType(dryRun=false)"
                        }
                        : new JsonArray
                        {
                            "CompileSoftware —— 失去类型定义的 DB / 块会在这里暴露",
                            "SaveProject —— 确认无误后再存盘"
                        });
            }
            catch (PortalException pex)
            {
                throw new McpException(
                    $"Failed deleting PLC type '{typePath}' [{pex.Code}]: {pex.Message}",
                    pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error deleting PLC type '{typePath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        /// <summary>
        /// 三个删除工具共用的成品报告。抽出来是因为「什么算成功」这条口径必须三处完全一致 ——
        /// 分开写迟早会有一处漏掉「回读未确认」或「交叉引用查不到」的措辞，而那正是这一族的命门。
        /// </summary>
        private static ResponseJsonReport BuildDeletionReport(
            JsonObject data, bool dryRun, bool crossRefOk,
            string objectLabel, string dryRunTail, string? extraWarning, JsonArray nextActions)
        {
            bool deleted = data["deleted"]?.GetValue<bool>() ?? false;
            bool verifiedAbsent = data["verifiedAbsent"]?.GetValue<bool>() ?? false;

            var warnings = new List<string>();
            if (data["warnings"] is JsonArray raw)
            {
                warnings.AddRange(raw.Select(w => w?.GetValue<string>()).Where(w => w != null)!);
            }
            if (extraWarning != null) warnings.Add(extraWarning);

            string message;
            bool ok;
            if (dryRun)
            {
                // 预览路径：工程一行没动。交叉引用是预览的全部价值，取不到就必须明说，
                // 否则「成功」会被读成「确认可以删」—— 删除类工具里这是代价最大的错档。
                ok = true;
                message = $"[dryRun] 未做任何改动。目标 {objectLabel}，"
                        + (crossRefOk
                            ? $"交叉引用 {data["crossReferenceCount"]} 条（见 data.crossReferences）。"
                            : "⚠️ 交叉引用查不到 —— 这不等于没人引用它，请先自行核对。")
                        + dryRunTail;
            }
            else if (deleted && verifiedAbsent)
            {
                ok = true;
                message = $"{objectLabel} 已删除，并已重新读回确认它确实不在了。";
            }
            else
            {
                // 走到这里说明 Delete() 调过但回读没能确认对象消失。绝不当成功报。
                ok = false;
                message = $"⚠️ 未验证：{objectLabel} 的删除结果无法确认（deleted={deleted}, verifiedAbsent={verifiedAbsent}）。"
                        + "请在 TIA 里手工确认该对象是否还在，不要按「已删除」继续操作。";
                warnings.Add("删除后的回读确认没有通过，本次结果不可信。");
            }

            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = data,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok,
                    ["dryRun"] = dryRun,
                    ["deleted"] = deleted,
                    ["verifiedAbsent"] = verifiedAbsent,
                    ["crossReferenceAvailable"] = crossRefOk,
                    ["nextActions"] = nextActions
                }
            };
        }

        #endregion
    }
}
