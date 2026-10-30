using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Partial: 往已有 CPU / 机架上插入子模块（信号板 SB、信号模块 SM、通信模块 CM）的工具。
    ///
    /// 和整机添加（AddDevice / AddDeviceWithFallback）走的是两套 Openness API，失败模式也不同，
    /// 所以单独成域。插完要改起始地址的话，用 SetDeviceItemIoAddress，本域不碰地址。
    ///
    /// 本线的 ResponseMessage 只有 Message + Meta，没有三态 Outcome：
    /// 可判定的失败一律抛 McpException（失败类别与 attempts 一并写进异常正文，否则排障信息就丢了）；
    /// 只有 Reason=VerifyFailed 这一档是「插没插上答不上来」，见下面的注释。
    /// </summary>
    public static partial class McpServer
    {
        #region plug submodule

        [McpServerTool(Name = "GetDevicePlugLocations"), Description(
            "[L2][Hardware] READ-ONLY. List the plug slots of one device item: which slot numbers are FREE "
            + "(as reported by TIA at runtime) and which are already OCCUPIED (by name / order number / built-in). "
            + "Call this BEFORE plugging a signal board (SB), signal module (SM) or communication module (CM) so the "
            + "slot / position number comes from TIA instead of a guess — slot numbers differ per CPU family and are "
            + "never hardcoded by this server. A signal board plugs into the CPU device item itself, so pass the CPU "
            + "path (e.g. 'PLC_1'). Get paths from GetDeviceItemTree.")]
        public static ResponseMessage GetDevicePlugLocations(
            [Description("deviceItemPath: path to the host device item, e.g. 'PLC_1' for a CPU")] string deviceItemPath)
        {
            try
            {
                var slots = Portal.GetDevicePlugLocations(deviceItemPath);

                if (slots == null)
                {
                    // 「没连上」和「路径不存在」都会是 null，对调用方是两件事，一次说清楚。
                    throw new McpException(
                        $"读不到 '{deviceItemPath}' 的槽位：要么没有连接项目（先 Connect，再 AttachToOpenProject），"
                        + "要么这个设备项路径不存在（用 GetDeviceItemTree 确认每一段）。",
                        McpErrorCode.InvalidParams);
                }

                var (free, occupied) = slots.Value;

                var freeArr = new JsonArray();
                foreach (var f in free)
                {
                    freeArr.Add(new JsonObject
                    {
                        ["positionNumber"] = f.PositionNumber,
                        ["label"] = f.Label
                    });
                }

                var occArr = new JsonArray();
                foreach (var o in occupied)
                {
                    occArr.Add(new JsonObject
                    {
                        ["positionNumber"] = o.PositionNumber,
                        ["name"] = o.Name,
                        ["isPlugged"] = o.IsPlugged,
                        ["isBuiltIn"] = o.IsBuiltIn,
                        ["typeIdentifier"] = o.TypeIdentifier
                    });
                }

                // 槽位是 TIA 运行时报出来的实际占用情况，读到什么报什么。
                var msg = free.Count == 0
                    ? $"'{deviceItemPath}' 当前没有空闲槽位（已占 {occupied.Count} 个）。"
                    : $"'{deviceItemPath}' 有 {free.Count} 个空闲槽位、{occupied.Count} 个已占槽位。";

                return new ResponseMessage
                {
                    Message = msg,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["deviceItemPath"] = deviceItemPath,
                        ["freeSlotCount"] = free.Count,
                        ["freeSlots"] = freeArr,
                        ["occupiedSlots"] = occArr,
                        ["note"] = "positionNumber 直接喂给 PlugDeviceItem。空闲槽位由 TIA 运行时报告，"
                                 + "本服务不硬编码任何 CPU 的槽位表。"
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error reading plug locations of '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlugDeviceItem"), Description(
            "[L2][Hardware][Destructive] Insert a SUBMODULE into an existing device: signal board (SB, e.g. SB 1221 "
            + "6ES7221-3BD30-0XB0), signal module (SM), or communication module (CM). This is the 'InsertDeviceItem' / "
            + "'AddSignalBoard' operation — AddDevice only creates whole stations and cannot plug boards into a CPU. "
            + "Defaults to dryRun=true, which runs a REAL TIA feasibility check (CanPlugNew) without writing. "
            + "Pass positionNumber=-1 to let the server pick a free slot reported by TIA; slot numbers are never "
            + "hardcoded — use GetDevicePlugLocations to see them. After a successful plug the module is read back and "
            + "verified (IsPlugged / name / slot). This tool does NOT set addresses: to make the inputs start at %I2.0, "
            + "call SetDeviceItemIoAddress afterwards with startAddress=2, then CompileSoftware and SaveProject. "
            + "Failures are reported by category: SlotOccupied, SlotNotAvailable, OrderNumberNotFound, "
            + "NotSupportedByDevice, PlugFailed, VerifyFailed.")]
        public static ResponseMessage PlugDeviceItem(
            [Description("deviceItemPath: host device item. A signal board plugs into the CPU itself, e.g. 'PLC_1'")] string deviceItemPath,
            [Description("orderNumber: MLFB of the module, e.g. '6ES7221-3BD30-0XB0' (with or without the space). A full 'OrderNumber:.../V1.1' type identifier is also accepted")] string orderNumber,
            [Description("version: module/firmware version, e.g. 'V1.1'. Leave empty to let TIA pick the default")] string version = "",
            [Description("positionNumber: target slot. -1 (default) = pick the first free slot TIA accepts")] int positionNumber = -1,
            [Description("name: name for the new module. Empty = auto-generated and de-duplicated against siblings")] string name = "",
            [Description("dryRun: true (default) only runs the CanPlugNew feasibility check; set false to actually plug")] bool dryRun = true)
        {
            try
            {
                var r = Portal.PlugSubmodule(deviceItemPath, orderNumber, version, positionNumber, name, dryRun);

                // Reason=VerifyFailed 是 Portal 明写的"插完之后重新定位失败，无法确认结果"——
                // 插没插上答不上来，既不能报成功也不能报失败。本线没有 Unknown 这一档，
                // 所以它**不抛**，走下面 verified=false 的路径如实说「未验证」。
                var unverified = !r.Ok && string.Equals(r.Reason, "VerifyFailed", StringComparison.Ordinal);

                if (!r.Ok && !unverified)
                {
                    // 其余失败类别都是可判定的。attempts 是排障的全部依据，抛异常时正文里必须带上，
                    // 否则它随 Meta 一起消失，调用方只剩一句"插不上"。
                    var attemptText = r.Attempts.Count == 0
                        ? ""
                        : " | attempts: " + string.Join("; ", r.Attempts.Take(20));
                    throw new McpException(
                        $"PlugDeviceItem failed [{r.Reason}]: {r.Message}{attemptText}",
                        McpErrorCode.InvalidParams);
                }

                var attempts = new JsonArray();
                foreach (var a in r.Attempts)
                {
                    attempts.Add(a);
                }

                var meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["dryRun"] = dryRun,
                    // 失败类别是给调用方判定用的结构化字段，别让它只出现在中文正文里。
                    ["reason"] = r.Reason,
                    ["verified"] = !unverified,
                    ["deviceItemPath"] = deviceItemPath,
                    ["orderNumber"] = orderNumber,
                    ["version"] = version,
                    ["requestedPositionNumber"] = positionNumber,
                    ["resolvedTypeIdentifier"] = r.TypeIdentifier,
                    ["resolvedPositionNumber"] = r.PositionNumber,
                    ["attempts"] = attempts
                };

                if (r.FreeSlots != null)
                {
                    var freeArr = new JsonArray();
                    foreach (var f in r.FreeSlots)
                    {
                        freeArr.Add(new JsonObject
                        {
                            ["positionNumber"] = f.PositionNumber,
                            ["label"] = f.Label
                        });
                    }

                    meta["freeSlots"] = freeArr;
                }

                if (r.Plugged != null)
                {
                    meta["plugged"] = new JsonObject
                    {
                        ["name"] = r.Plugged.Name,
                        ["positionNumber"] = r.Plugged.PositionNumber,
                        ["isPlugged"] = r.Plugged.IsPlugged,
                        ["typeIdentifier"] = r.Plugged.TypeIdentifier
                    };
                }

                if (r.Addresses != null)
                {
                    var addrArr = new JsonArray();
                    foreach (var a in r.Addresses)
                    {
                        addrArr.Add(new JsonObject
                        {
                            ["ioType"] = a.IoType,
                            ["startAddress"] = a.StartAddress,
                            ["length"] = a.Length
                        });
                    }

                    meta["addresses"] = addrArr;
                }

                if (r.Ok && !dryRun)
                {
                    meta["nextActions"] = new JsonArray
                    {
                        "SetDeviceItemIoAddress —— 要让输入从 %I2.0 开始就传 startAddress=2（本工具不改地址）",
                        "CompileSoftware —— 硬件改动必须编译过才算数",
                        "SaveProject —— 编译 0 错之后再存盘"
                    };
                }

                return new ResponseMessage
                {
                    Message = unverified
                        ? $"⚠ 未验证：{r.Message} —— PlugNew 已经调用过，模块**可能已经插进去了**，"
                          + "但读回确认失败，所以无法判定结果。请用 GetDevicePlugLocations 或 TIA 界面核对后再继续，"
                          + "不要直接重试（会重复插入）。"
                        : r.Message,
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error plugging '{orderNumber}' into '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
