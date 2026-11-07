using Microsoft.Extensions.Logging;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial: 往已存在的设备（CPU / 机架）上**插入子模块** —— 信号板 SB、信号模块 SM、通信模块 CM。
    ///
    /// 为什么单独一份：整机添加走 <c>Devices.CreateWithItem</c>，插子模块走的是完全不同的一套
    /// （<c>HardwareObject.PlugNew</c>），失败模式也不一样 —— 整机失败是「订货号不存在」，
    /// 插子模块失败还多出「槽位被占」和「这台 CPU 不接受这块板」两种，必须分开报。
    ///
    /// API 形态是**反射实测**出来的，不是推理的（2026-09-03，V21，Siemens.Engineering.Base.dll）：
    ///   DeviceItem : HardwareObject                      → 子模块直接插在 DeviceItem（CPU）上
    ///   HardwareObject.PlugNew(string typeIdentifier, string name, int positionNumber) → DeviceItem
    ///   HardwareObject.CanPlugNew(string, string, int)   → bool，**插之前可以预检**（dryRun 靠它）
    ///   HardwareObject.GetPlugLocations()                → IList&lt;PlugLocation&gt;，元素只有
    ///                                                      Label(string) + PositionNumber(int)，都只读
    ///   DeviceItem.PositionNumber / IsPlugged / IsBuiltIn → 只读，用来读回验证
    /// 注意 <c>DeviceItemComposition</c> 上**没有**任何 Create/PlugNew 方法（只有 CreateFrom(MasterCopy)），
    /// 所以插模块只能走宿主 HardwareObject，不能往集合里塞。
    ///
    /// **槽位号不写死**：S7-1200 信号板的槽位号由 <c>GetPlugLocations()</c> 在运行时报出来，
    /// 引擎不猜也不硬编码任何 CPU 的槽位表。
    /// </summary>
    public partial class Portal
    {
        #region plug submodule

        /// <summary>一个可插槽位（空位）。Label 是 TIA 给的槽位描述，PositionNumber 是 PlugNew 要的那个数。</summary>
        public sealed class PlugLocationInfo
        {
            public int PositionNumber { get; set; }
            public string Label { get; set; } = "";
        }

        /// <summary>一个已经插着东西的槽位。用来把「槽位被占」和「槽位不存在」分开。</summary>
        public sealed class PluggedItemInfo
        {
            public string Name { get; set; } = "";
            public int PositionNumber { get; set; }
            public bool IsPlugged { get; set; }
            public bool IsBuiltIn { get; set; }
            public string TypeIdentifier { get; set; } = "";
        }

        /// <summary>插入子模块的结果。失败时 <see cref="Reason"/> 给出**可判定的失败类别**，不是一句「插入失败」。</summary>
        public sealed class PlugResult
        {
            /// <summary>整体成功（dryRun 时表示「预检通过、可以插」）。</summary>
            public bool Ok { get; set; }

            /// <summary>
            /// 失败类别，取值：NotConnected / DeviceItemNotFound / InvalidParams /
            /// SlotOccupied / SlotNotAvailable / OrderNumberNotFound / NotSupportedByDevice /
            /// PlugFailed / VerifyFailed。成功时为 null。
            /// </summary>
            public string? Reason { get; set; }

            public string Message { get; set; } = "";

            /// <summary>最终被接受的 TypeIdentifier（试出来的那个变体），失败时可能为 null。</summary>
            public string? TypeIdentifier { get; set; }

            /// <summary>最终落位的槽位号。positionNumber 传 -1 时这里是自动选中的那个。</summary>
            public int? PositionNumber { get; set; }

            /// <summary>插入后**读回**的模块信息。dryRun 或失败时为 null。</summary>
            public PluggedItemInfo? Plugged { get; set; }

            /// <summary>插入后读回的 I/O 地址（读到什么就报什么，不换算）。</summary>
            public IReadOnlyList<IoAddressInfo>? Addresses { get; set; }

            /// <summary>预检时该宿主上的空闲槽位，帮调用方直接改参数重试。</summary>
            public IReadOnlyList<PlugLocationInfo>? FreeSlots { get; set; }

            /// <summary>已被占用的槽位。</summary>
            public IReadOnlyList<PluggedItemInfo>? OccupiedSlots { get; set; }

            /// <summary>实际试过的 TypeIdentifier 变体和结论，失败时排障全靠它。</summary>
            public List<string> Attempts { get; } = new List<string>();
        }

        /// <summary>
        /// 读一个宿主（CPU / 机架）上的槽位情况：哪些空着、哪些被占。
        /// 返回 null 表示「没连项目 / 设备项没找到」——和「这台设备一个空槽都没有」（空列表）是两回事。
        /// </summary>
        public (IReadOnlyList<PlugLocationInfo> free, IReadOnlyList<PluggedItemInfo> occupied)? GetDevicePlugLocations(
            string deviceItemPath)
        {
            _logger?.LogInformation($"Getting plug locations of: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var item = GetDeviceItemByPath(deviceItemPath);
            if (item == null)
            {
                return null;
            }

            return (ReadFreeSlots(item), ReadOccupiedSlots(item));
        }

        private List<PlugLocationInfo> ReadFreeSlots(DeviceItem host)
        {
            var list = new List<PlugLocationInfo>();
            try
            {
                var locations = host.GetPlugLocations();
                if (locations == null)
                {
                    return list;
                }

                foreach (var loc in locations)
                {
                    if (loc == null)
                    {
                        continue;
                    }

                    list.Add(new PlugLocationInfo
                    {
                        PositionNumber = loc.PositionNumber,
                        Label = loc.Label ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                // 有的宿主对象（接口、通道之类）根本不支持插拔，这里会抛。空列表就是答案，不该整体失败。
                _logger?.LogWarning(ex, "GetPlugLocations failed; treating as no free slots");
            }

            return list.OrderBy(x => x.PositionNumber).ToList();
        }

        private static List<PluggedItemInfo> ReadOccupiedSlots(DeviceItem host)
        {
            var list = new List<PluggedItemInfo>();
            var children = host.DeviceItems;
            if (children == null)
            {
                return list;
            }

            foreach (DeviceItem child in children)
            {
                if (child == null)
                {
                    continue;
                }

                list.Add(DescribeItem(child));
            }

            return list.OrderBy(x => x.PositionNumber).ToList();
        }

        private static PluggedItemInfo DescribeItem(DeviceItem item)
        {
            var info = new PluggedItemInfo { Name = item.Name ?? "" };

            // 这些属性个别对象会抛（未插的代理/特殊类型），逐个兜底，别让一个属性毁掉整条描述。
            try { info.PositionNumber = item.PositionNumber; } catch { info.PositionNumber = -1; }
            try { info.IsPlugged = item.IsPlugged; } catch { info.IsPlugged = false; }
            try { info.IsBuiltIn = item.IsBuiltIn; } catch { info.IsBuiltIn = false; }
            try { info.TypeIdentifier = item.TypeIdentifier ?? ""; } catch { info.TypeIdentifier = ""; }

            return info;
        }

        /// <summary>
        /// 往一个宿主设备项上插子模块（信号板 / 信号模块 / 通信模块）。
        /// </summary>
        /// <param name="deviceItemPath">宿主设备项路径。信号板插在 **CPU 本体**上，所以传 CPU 的路径。</param>
        /// <param name="orderNumber">订货号，例如 6ES7221-3BD30-0XB0，带不带空格都行。也可直接传完整 TypeIdentifier（OrderNumber:.../V1.1）。</param>
        /// <param name="version">固件/模块版本，例如 V1.1。留空则让 TIA 自己挑。</param>
        /// <param name="positionNumber">槽位号。传 -1 表示由引擎从空闲槽位里自动挑一个能插的。</param>
        /// <param name="name">新模块名。留空则自动生成且避开同名兄弟。</param>
        /// <param name="dryRun">true 时只用 CanPlugNew 预检，绝不写工程。</param>
        public PlugResult PlugSubmodule(
            string deviceItemPath, string orderNumber, string version, int positionNumber, string? name, bool dryRun)
        {
            _logger?.LogInformation(
                $"Plug submodule: host={deviceItemPath}, order={orderNumber}, version={version}, "
                + $"pos={positionNumber}, dryRun={dryRun}");

            var result = new PlugResult();

            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                result.Reason = "InvalidParams";
                result.Message = "orderNumber 是空的。传信号板/模块的订货号，例如 6ES7221-3BD30-0XB0。";
                return result;
            }

            if (IsProjectNull())
            {
                result.Reason = "NotConnected";
                result.Message = "没有连接到 TIA Portal 项目。先调用 Connect，"
                               + "再用 AttachToOpenProject 接管已经打开的工程（或 OpenProject 打开本地工程）。";
                return result;
            }

            var host = GetDeviceItemByPath(deviceItemPath);
            if (host == null)
            {
                result.Reason = "DeviceItemNotFound";
                result.Message = $"设备项 '{deviceItemPath}' 没找到。信号板要插在 **CPU 本体** 上，"
                               + "路径形如 'PLC_1' 或 'PLC_1/PLC_1'；用 GetDeviceItemTree 确认每一段。";
                return result;
            }

            var free = ReadFreeSlots(host);
            var occupied = ReadOccupiedSlots(host);
            result.FreeSlots = free;
            result.OccupiedSlots = occupied;

            // ---- 槽位先判定，这样「槽位被占」不会被后面的 CanPlugNew 混成「不支持这块板」 ----
            List<int> slotCandidates;
            if (positionNumber >= 0)
            {
                var taken = occupied.FirstOrDefault(x => x.PositionNumber == positionNumber);
                if (taken != null)
                {
                    result.Reason = "SlotOccupied";
                    result.Message = $"槽位 {positionNumber} 已经被 '{taken.Name}' 占用"
                                   + (taken.IsBuiltIn ? "（该模块是 CPU 集成的，拔不掉）" : "")
                                   + $"。空闲槽位：{FormatSlots(free)}。";
                    return result;
                }

                if (free.Count > 0 && free.All(x => x.PositionNumber != positionNumber))
                {
                    result.Reason = "SlotNotAvailable";
                    result.Message = $"槽位 {positionNumber} 不是 '{deviceItemPath}' 的可插槽位。"
                                   + $"这台设备当前报告的空闲槽位是：{FormatSlots(free)}。"
                                   + "槽位号由 TIA 运行时报出，引擎不做任何硬编码 —— "
                                   + "先用 GetDevicePlugLocations 看清楚再插。";
                    return result;
                }

                slotCandidates = new List<int> { positionNumber };
            }
            else
            {
                if (free.Count == 0)
                {
                    result.Reason = "SlotNotAvailable";
                    result.Message = $"'{deviceItemPath}' 上没有任何空闲槽位可用（TIA 报告的空位为 0）。"
                                   + "确认路径指向的是 CPU 本体而不是机架/接口，或者先腾出一个槽位。";
                    return result;
                }

                slotCandidates = free.Select(x => x.PositionNumber).ToList();
            }

            var typeIdentifiers = BuildPlugTypeIdentifiers(orderNumber, version);
            if (typeIdentifiers.Count == 0)
            {
                result.Reason = "InvalidParams";
                result.Message = $"从 orderNumber='{orderNumber}' version='{version}' 拼不出可用的 TypeIdentifier。";
                return result;
            }

            var itemName = ResolveNewItemName(host, name, slotCandidates[0]);

            // ---- CanPlugNew 预检：这是 dryRun 的依据，也是「不支持」判定的依据 ----
            string? acceptedType = null;
            int acceptedSlot = -1;

            foreach (var slot in slotCandidates)
            {
                foreach (var typeId in typeIdentifiers)
                {
                    bool can;
                    try
                    {
                        can = host.CanPlugNew(typeId, itemName, slot);
                    }
                    catch (Exception ex)
                    {
                        // 一抛异常代理就可能死掉，必须重新取宿主句柄再继续试，否则后面全是 disposed 假象。
                        result.Attempts.Add($"slot={slot} {typeId} -> 预检异常: {ex.Message}");
                        var again = GetDeviceItemByPath(deviceItemPath);
                        if (again == null)
                        {
                            result.Reason = "PlugFailed";
                            result.Message = $"预检时代理对象失效且无法重新定位 '{deviceItemPath}'：{ex.Message}";
                            return result;
                        }

                        host = again;
                        continue;
                    }

                    result.Attempts.Add($"slot={slot} {typeId} -> CanPlugNew={can}");
                    if (can)
                    {
                        acceptedType = typeId;
                        acceptedSlot = slot;
                        break;
                    }
                }

                if (acceptedType != null)
                {
                    break;
                }
            }

            if (acceptedType == null)
            {
                // 「订货号根本不存在」和「这台 CPU 不接受这块板」是两种病，靠硬件目录分开。
                var (known, catalogNote) = ProbeCatalogForOrderNumber(orderNumber);
                if (known == false)
                {
                    result.Reason = "OrderNumberNotFound";
                    result.Message = $"订货号 '{orderNumber}' 在 TIA 硬件目录里查不到{catalogNote}。"
                                   + "先用 SearchHardwareCatalog 搜一下确认订货号和版本，"
                                   + "GSD/HSP 没装的模块也会是这个结果。";
                }
                else if (known == null)
                {
                    // 目录查不了就别冒充结论：只能说「这台设备不接受」，不能说「订货号不存在」。
                    result.Reason = "NotSupportedByDevice";
                    result.Message = $"'{deviceItemPath}' 的槽位 {FormatSlots(free)} 都不接受 '{orderNumber}'"
                                   + $"{catalogNote}，所以无法进一步区分是订货号写错还是该 CPU 不支持这块板。"
                                   + "试过的变体见 attempts。";
                }
                else
                {
                    result.Reason = "NotSupportedByDevice";
                    result.Message = $"硬件目录里能查到 '{orderNumber}'{catalogNote}，"
                                   + $"但 '{deviceItemPath}' 的槽位 {FormatSlots(free)} 都不接受它 —— "
                                   + "常见原因：这块板不适配该 CPU 型号/固件版本，或 version 传错了"
                                   + $"（本次试过的版本变体见 attempts）。";
                }

                result.TypeIdentifier = null;
                return result;
            }

            result.TypeIdentifier = acceptedType;
            result.PositionNumber = acceptedSlot;

            if (dryRun)
            {
                result.Ok = true;
                result.Message = $"[dryRun] 预检通过：可以把 '{acceptedType}' 以名字 '{itemName}' 插到 "
                               + $"'{deviceItemPath}' 的槽位 {acceptedSlot}。用 dryRun=false 实际写入。"
                               + "插完如果要改起始地址（例如让输入从 %I2.0 开始），"
                               + "用已有的 SetDeviceItemIoAddress，不要在这里传地址。";
                return result;
            }

            DeviceItem? created;
            try
            {
                created = host.PlugNew(acceptedType, itemName, acceptedSlot);
            }
            catch (Exception ex)
            {
                result.Reason = "PlugFailed";
                result.Message = $"CanPlugNew 说可以，但 PlugNew 实际执行失败：{ex.Message}"
                               + "（工程可能被占用/只读，或该槽位在写入瞬间被别的操作占了）。";
                return result;
            }

            if (created == null)
            {
                result.Reason = "PlugFailed";
                result.Message = "PlugNew 没有抛异常但返回了 null，模块没有被创建。";
                return result;
            }

            // ---- 读回验证：调用没报错 ≠ 模块真的在那个槽位上 ----
            var verifyHost = GetDeviceItemByPath(deviceItemPath);
            if (verifyHost == null)
            {
                result.Reason = "VerifyFailed";
                result.Message = $"插入后重新定位 '{deviceItemPath}' 失败，无法确认结果，请在 TIA 界面里核对。";
                return result;
            }

            var after = ReadOccupiedSlots(verifyHost).FirstOrDefault(x => x.PositionNumber == acceptedSlot);
            if (after == null)
            {
                result.Reason = "VerifyFailed";
                result.Message = $"读回验证失败：槽位 {acceptedSlot} 上没有读到任何模块。"
                               + "PlugNew 已返回对象但组态里没落位，请在 TIA 界面里核对。";
                return result;
            }

            if (!after.IsPlugged)
            {
                result.Plugged = after;
                result.Reason = "VerifyFailed";
                result.Message = $"读回验证失败：槽位 {acceptedSlot} 上的 '{after.Name}' 的 IsPlugged=false。";
                return result;
            }

            result.Plugged = after;

            // 地址一并读回：issue #26 的另一半就是要改这个，直接把现值摆出来，省一次往返。
            var verifyItem = verifyHost.DeviceItems?.FirstOrDefault(d =>
            {
                try { return d.PositionNumber == acceptedSlot; } catch { return false; }
            });
            if (verifyItem != null)
            {
                result.Addresses = ReadAddresses(verifyItem);
            }

            result.Ok = true;
            var addrText = result.Addresses == null || result.Addresses.Count == 0
                ? "该模块当前没有 I/O 地址"
                : "当前地址 " + string.Join(" / ", result.Addresses.Select(a => a.ToString()));

            result.Message = $"已把 '{acceptedType}' 插到 '{deviceItemPath}' 的槽位 {acceptedSlot}，"
                           + $"模块名 '{after.Name}'，读回确认 IsPlugged=true；{addrText}。"
                           + "要把起始地址改成别的值（例如输入从 %I2.0 开始 → startAddress=2），"
                           + "用 SetDeviceItemIoAddress，本工具不负责地址。改完 CompileSoftware + SaveProject。";
            return result;
        }

        private static string FormatSlots(IReadOnlyList<PlugLocationInfo> free)
        {
            return free.Count == 0
                ? "（一个都没有）"
                : string.Join(", ", free.Select(x => string.IsNullOrWhiteSpace(x.Label)
                    ? x.PositionNumber.ToString()
                    : $"{x.PositionNumber}({x.Label})"));
        }

        /// <summary>
        /// 拼 TypeIdentifier 变体。订货号空格写法（6ES7221... / 6ES7 221...）TIA 只认其中一种，
        /// 而用户两种都会写，所以复用整机添加那套归一化逻辑挨个试。
        /// </summary>
        private static List<string> BuildPlugTypeIdentifiers(string orderNumber, string version)
        {
            var raw = (orderNumber ?? "").Trim();

            // 用户直接给了完整 TypeIdentifier 就别再拼了，原样用。
            if (raw.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { raw };
            }

            var orders = new List<string>
            {
                raw,
                NormalizeOrderNumber(raw),
                TryFormatMlfbWithSpaces(raw),
                TryFormatMlfbWithSpaces(NormalizeOrderNumber(raw))
            }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var v = (version ?? "").Trim();
            var vNoV = v.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? v.Substring(1) : v;

            var versions = new List<string>();
            if (!string.IsNullOrWhiteSpace(v))
            {
                versions.Add(v);
                versions.Add("V" + vNoV);
                versions.Add(vNoV);
                if (v.Contains('.'))
                {
                    versions.Add("V" + vNoV + ".0");
                }
            }

            versions = versions.Where(x => !string.IsNullOrWhiteSpace(x))
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var result = new List<string>();
            foreach (var o in orders)
            {
                foreach (var ver in versions)
                {
                    result.Add($"OrderNumber:{o}/{ver}");
                }

                // 不带版本的形态放最后：让 TIA 自己挑默认版本，是版本写错时的最后一根救命稻草。
                result.Add($"OrderNumber:{o}");
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>新模块名：用户没给就自动生成，并且避开同名兄弟（重名 PlugNew 会直接失败）。</summary>
        private static string ResolveNewItemName(DeviceItem host, string? requested, int slot)
        {
            var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DeviceItem child in host.DeviceItems)
                {
                    if (child?.Name != null)
                    {
                        siblings.Add(child.Name);
                    }
                }
            }
            catch
            {
                // 读不到兄弟就不做去重，交给 TIA 自己报重名。
            }

            var baseName = string.IsNullOrWhiteSpace(requested) ? $"Module_{slot}" : requested!.Trim();
            if (!siblings.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 100; i++)
            {
                var candidate = $"{baseName}_{i}";
                if (!siblings.Contains(candidate))
                {
                    return candidate;
                }
            }

            return baseName;
        }

        /// <summary>
        /// 问硬件目录认不认这个订货号。
        /// 返回 (null, 说明) 表示**查不了**（没连 Portal / 目录不可用）—— 这和「查了但没有」必须分开，
        /// 否则会把「目录用不了」误报成「订货号不存在」。
        /// </summary>
        private (bool? known, string note) ProbeCatalogForOrderNumber(string orderNumber)
        {
            try
            {
                var hits = SearchHardwareCatalog(NormalizeOrderNumber(orderNumber), 5);
                if (hits == null || hits.Count == 0)
                {
                    return (false, "");
                }

                var first = hits.FirstOrDefault();
                var desc = first?.Description ?? first?.TypeName ?? "";
                var ver = string.IsNullOrWhiteSpace(first?.Version) ? "" : $"，目录版本 {first!.Version}";
                return (true, string.IsNullOrWhiteSpace(desc) ? ver : $"（{desc}{ver}）");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Hardware catalog probe failed while classifying plug failure");
                return (null, "（硬件目录当前查不了，无法确认订货号是否存在）");
            }
        }

        #endregion
    }
}
