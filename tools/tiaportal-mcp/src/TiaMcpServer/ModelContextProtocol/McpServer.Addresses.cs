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
    /// Partial: 硬件 I/O 起始地址的读写工具。
    ///
    /// 这是**硬件组态**的写操作。改错地址不会编译报错，只会让程序读到别的模块的数据，
    /// 所以写工具默认 dryRun=true。
    ///
    /// 本线的 ResponseMessage 只有 Message + Meta，**没有三态 Outcome 契约**：
    /// 失败一律抛 McpException（与 McpServer.Blocks.cs 同形），成功才正常返回 ResponseMessage。
    /// 唯一的例外是「写了但读不回来」——那既不是成功也不是失败，见下面 verified=false 的注释。
    /// </summary>
    public static partial class McpServer
    {
        #region io addresses

        [McpServerTool(Name = "GetDeviceItemIoAddresses"), Description(
            "[L2][Hardware] READ-ONLY. List the I/O addresses of one device item (signal module, signal board, "
            + "built-in CPU I/O). Returns each address as ioType + startAddress + length in ENGINE RAW VALUES "
            + "(startAddress is the byte offset: %I2.0 is startAddress 2). Use this to confirm an address before "
            + "and after changing it. Device item path looks like 'PLC_1/DI 8x24VDC_1' — get it from GetDeviceItemTree.")]
        public static ResponseMessage GetDeviceItemIoAddresses(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var addresses = Portal.GetDeviceItemAddresses(deviceItemPath);

                if (addresses == null)
                {
                    // 「没连上」和「设备项不存在」都会返回 null，但对调用方是两件事，分开问一句更有用。
                    throw new McpException(
                        $"读不到 '{deviceItemPath}' 的地址：要么没有连接项目（先 Connect / AttachToOpenProject），"
                        + "要么这个设备项路径不存在（用 GetDeviceItemTree 确认每一段）。",
                        McpErrorCode.InvalidParams);
                }

                var arr = new JsonArray();
                foreach (var a in addresses)
                {
                    arr.Add(new JsonObject
                    {
                        ["ioType"] = a.IoType,
                        ["startAddress"] = a.StartAddress,
                        ["length"] = a.Length
                    });
                }

                // addresses==null（没连上/路径不存在）上面已经抛掉了；走到这里拿到的是真清单，
                // 空清单就是"这个设备项确实不占 I/O"。
                // 空清单时别只说「没有」。地址在分布式 IO（ET200 系列）和机架式站上
                // 常常挂在**子项**而不是模块对象本身，只回一句「没有任何 I/O 地址」
                // 会让人以为是工具读不到，转头去查一个没问题的组态。
                var childHints = addresses.Count == 0
                    ? Portal.DescribeChildItemsWithAddresses(deviceItemPath)
                    : new System.Collections.Generic.List<string>();

                var msg = addresses.Count > 0
                    ? $"设备项 '{deviceItemPath}' 上有 {addresses.Count} 条 I/O 地址。"
                    : childHints.Count > 0
                        ? $"设备项 '{deviceItemPath}' **本级**没有 I/O 地址，但它的子项有 —— "
                          + $"地址挂在子项上（分布式 IO 常见）。改用这些路径：{string.Join("；", childHints)}"
                        : $"设备项 '{deviceItemPath}' 上没有任何 I/O 地址，它的直接子项也没有。"
                          + "常见于它是机架/电源/接口这类本来就不占 I/O 的对象；"
                          + "若你确信它应该有（例如分布式 IO 模块），用 GetDeviceItemTree 看一眼层级，"
                          + "地址可能挂在更深的一层。";

                return new ResponseMessage
                {
                    Message = msg,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["deviceItemPath"] = deviceItemPath,
                        ["addressCount"] = addresses.Count,
                        ["addresses"] = arr,
                        ["childItemsWithAddresses"] = new JsonArray(
                            childHints.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
                        ["note"] = "startAddress/length 是引擎原值，未做任何换算。startAddress 为字节偏移。"
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error reading IO addresses of '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetDeviceItemIoAddress"), Description(
            "[L2][Hardware][Destructive] Preview or change the I/O START ADDRESS of one device item "
            + "(e.g. move a DI module to start at %I2.0 by passing startAddress=2). Defaults to dryRun=true. "
            + "startAddress is the ENGINE RAW byte offset, not '2.0'. It writes hardware configuration, so a wrong "
            + "value does NOT fail compilation — the program silently reads a different module. Read back with "
            + "GetDeviceItemIoAddresses, then CompileSoftware and SaveProject. Overlapping address ranges are "
            + "rejected by TIA and reported back with the reason.")]
        public static ResponseMessage SetDeviceItemIoAddress(
            [Description("deviceItemPath: path in the project structure to the device item, e.g. 'PLC_1/DI 8x24VDC_1'")] string deviceItemPath,
            [Description("ioType: Input, Output, Diagnosis or Substitute")] string ioType,
            [Description("startAddress: new start address as engine raw byte offset (%I2.0 -> 2)")] int startAddress,
            [Description("dryRun: true (default) only previews the change; set false to actually write it")] bool dryRun = true)
        {
            try
            {
                // 参数自身的合法性在分支之前判。放进 dryRun=false 那一路的后果是：
                // 预演对负数一律报「可以改」，等到真写时才失败 —— 预演就成了误导。
                if (startAddress < 0)
                {
                    throw new McpException(
                        $"startAddress 不能为负数（收到 {startAddress}）。它是引擎原值字节偏移，"
                        + "%I2.0 对应 startAddress=2，不要写成 \"2.0\"。",
                        McpErrorCode.InvalidParams);
                }

                if (dryRun)
                {
                    // 预览也必须走真实定位，否则「预览通过、实写失败」毫无意义。
                    var current = Portal.GetDeviceItemAddresses(deviceItemPath);
                    if (current == null)
                    {
                        throw new McpException(
                            $"读不到 '{deviceItemPath}' 的地址：要么没有连接项目（先 Connect / AttachToOpenProject），"
                            + "要么这个设备项路径不存在（用 GetDeviceItemTree 确认每一段）。",
                            McpErrorCode.InvalidParams);
                    }

                    var match = current.FirstOrDefault(
                        x => string.Equals(x.IoType, ioType, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        // 本线没有 Outcome=Failure 这一档，「预演结论是改不了」只能抛 ——
                        // 返回一条正常的 ResponseMessage 会被调用方读成"预检通过"。
                        var have = current.Count == 0
                            ? "（一条都没有）"
                            : string.Join(" / ", current.Select(x => x.IoType).Distinct());
                        throw new McpException(
                            $"[dryRun] 改不了：'{deviceItemPath}' 上没有 {ioType} 类型的地址；实际有的是 {have}。",
                            McpErrorCode.InvalidParams);
                    }

                    var preview = match.StartAddress == startAddress
                        ? $"[dryRun] 无需修改：{ioType} 起始地址本来就是 {startAddress}。"
                        : $"[dryRun] 将把 '{deviceItemPath}' 的 {ioType} 起始地址从 "
                          + $"{match.StartAddress} 改为 {startAddress}（length={match.Length} 不变）。"
                          + "确认无误后用 dryRun=false 实际写入。"
                          + "注意：地址是否与其它模块重叠，只有真正写入时 TIA 才会判定。";

                    return new ResponseMessage
                    {
                        Message = preview,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            // dryRun 走到这里说明本次预演该验的都验到了（地址重不重叠只有真写时
                            // TIA 才判，这一点 Message 里已写明）。改不了的情形上面已经抛掉。
                            ["dryRun"] = true,
                            ["feasible"] = true,
                            ["deviceItemPath"] = deviceItemPath,
                            ["ioType"] = ioType,
                            ["requestedStartAddress"] = startAddress,
                            ["currentStartAddress"] = match.StartAddress,
                            ["currentLength"] = match.Length
                        }
                    };
                }

                var (ok, message, before, after) =
                    Portal.SetDeviceItemStartAddress(deviceItemPath, ioType, startAddress);

                if (!ok)
                {
                    // 地址越界/重叠/模块锁定/路径不存在，Portal 已经把**具体哪一步不成立**写进 message，
                    // 原样抛出去；绝不返回一条看起来像成功的 ResponseMessage。
                    throw new McpException(
                        $"SetDeviceItemIoAddress failed for '{deviceItemPath}' ({ioType} -> {startAddress}): {message}",
                        McpErrorCode.InvalidParams);
                }

                var meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["dryRun"] = false,
                    ["deviceItemPath"] = deviceItemPath,
                    ["ioType"] = ioType,
                    ["requestedStartAddress"] = startAddress,
                    ["beforeStartAddress"] = before?.StartAddress,
                    ["afterStartAddress"] = after?.StartAddress,
                    ["length"] = after?.Length ?? before?.Length,
                    // after 是写后读回的那一份，它才是"地址真的改了"的证据。
                    // ok=true 却读不回 after 时，改没改成答不上来 —— 这既不是成功也不是失败，
                    // 本线没有 Unknown 这一档，所以用 verified=false + Message 里的「未验证」如实说。
                    ["verified"] = after != null
                };

                if (after != null)
                {
                    meta["nextActions"] = new JsonArray
                    {
                        "CompileSoftware —— 硬件改动必须编译过才算数",
                        "SaveProject —— 编译 0 错之后再存盘",
                        "GetDeviceItemIoAddresses —— 独立读回一次做最终确认"
                    };
                }

                return new ResponseMessage
                {
                    Message = after != null
                        ? message
                        : $"⚠ 未验证：{message} —— 写入调用没有报错，但读不回修改后的地址，"
                          + "所以**无法确认**地址是否真的变了。请用 GetDeviceItemIoAddresses 或 TIA 界面自行核对，"
                          + "在核对之前不要把它当成已完成。",
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error setting IO address of '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
