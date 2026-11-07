using Microsoft.Extensions.Logging;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial: 硬件 I/O 起始地址的读写。
    ///
    /// 为什么单独一份：这是**硬件组态**的写操作，和块级操作的风险模型完全不同 ——
    /// 改错地址不会编译报错，只会让程序读到别的模块的数据。
    ///
    /// API 形态是**反射实测**出来的，不是推理的（2026-09-03，V21）：
    ///   DeviceItem.Addresses     → AddressComposition，集合本身只读
    ///   Address.StartAddress     → Int32，**可写**
    ///   Address.Length           → Int32，**可写**
    ///   Address.IoType           → AddressIoType，**只读**（None/Input/Output/Substitute/Diagnosis）
    /// 集合只读但元素可写，所以改地址是改 Address 对象，不是往集合里塞新的。
    /// </summary>
    public partial class Portal
    {
        #region io addresses

        /// <summary>一条 I/O 地址的快照。StartAddress / Length 一律是**引擎原值**，不做任何换算。</summary>
        public sealed class IoAddressInfo
        {
            public string IoType { get; set; } = "";
            public int StartAddress { get; set; }
            public int Length { get; set; }

            public override string ToString() => $"{IoType} start={StartAddress} length={Length}";
        }

        /// <summary>
        /// 读一个设备项上的全部 I/O 地址。
        /// 返回 null 表示「没连项目 / 设备项没找到」——和「这个设备项就是没有地址」（空列表）是两回事。
        /// </summary>
        public IReadOnlyList<IoAddressInfo>? GetDeviceItemAddresses(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting IO addresses of device item: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var item = GetDeviceItemByPath(deviceItemPath);
            if (item == null)
            {
                return null;
            }

            return ReadAddresses(item);
        }

        /// <summary>
        /// 一个设备项本身没有 I/O 地址时，看看它的**子项**有没有。
        ///
        /// 为什么需要：分布式 IO（ET200 系列）和不少机架式站，地址挂在子项而不是
        /// 模块对象本身。只回一句「本设备项没有任何 I/O 地址」会让人以为工具读不到，
        /// 于是去查一个没有问题的组态 —— 用户在 issue #33 里就是这么被卡住的。
        /// 返回 "子项名 → 地址摘要" 的清单，只下钻一层（再深就变成整棵树了，噪音大于信息）。
        /// </summary>
        public List<string> DescribeChildItemsWithAddresses(string deviceItemPath)
        {
            var hints = new List<string>();
            if (IsProjectNull()) return hints;
            var item = GetDeviceItemByPath(deviceItemPath);
            if (item == null) return hints;

            foreach (DeviceItem child in item.DeviceItems)
            {
                if (child == null) continue;
                List<IoAddressInfo> childAddresses;
                try { childAddresses = ReadAddresses(child); }
                catch { continue; }
                if (childAddresses.Count == 0) continue;
                hints.Add(child.Name + " → " + string.Join(", ",
                    childAddresses.Select(a => a.IoType + " " + a.StartAddress + "(len " + a.Length + ")")));
            }
            return hints;
        }

        private static List<IoAddressInfo> ReadAddresses(DeviceItem item)
        {
            var list = new List<IoAddressInfo>();
            var addresses = item.Addresses;
            if (addresses == null)
            {
                return list;
            }

            foreach (Address a in addresses)
            {
                if (a == null)
                {
                    continue;
                }

                list.Add(new IoAddressInfo
                {
                    IoType = a.IoType.ToString(),
                    StartAddress = a.StartAddress,
                    Length = a.Length
                });
            }

            return list;
        }

        /// <summary>
        /// 改一个设备项的 I/O 起始地址，并**读回验证**。
        /// </summary>
        /// <param name="deviceItemPath">设备项路径，例如 "PLC_1/DI 8x24VDC_1"</param>
        /// <param name="ioType">Input / Output / Diagnosis / Substitute，大小写不敏感</param>
        /// <param name="startAddress">新的起始地址（引擎原值，字节偏移；%I2.0 对应 2）</param>
        /// <returns>
        /// (ok, message, before, after)。ok=false 时 message 说明**具体**是哪一步不成立，
        /// 绝不返回「成功了但其实没改」——写完一定重新读一次再比对。
        /// </returns>
        public (bool ok, string message, IoAddressInfo? before, IoAddressInfo? after) SetDeviceItemStartAddress(
            string deviceItemPath, string ioType, int startAddress)
        {
            _logger?.LogInformation(
                $"Setting IO start address: item={deviceItemPath}, ioType={ioType}, start={startAddress}");

            if (IsProjectNull())
            {
                return (false, "没有连接到 TIA Portal 项目。先调用 Connect / OpenProject "
                             + "（或 AttachToOpenProject 接管已打开的工程）。", null, null);
            }

            if (startAddress < 0)
            {
                return (false, $"起始地址不能为负数（收到 {startAddress}）。", null, null);
            }

            if (!TryParseIoType(ioType, out var wanted))
            {
                return (false, $"无法识别的 ioType '{ioType}'。可用值：Input、Output、Diagnosis、Substitute。",
                        null, null);
            }

            var item = GetDeviceItemByPath(deviceItemPath);
            if (item == null)
            {
                return (false, $"设备项 '{deviceItemPath}' 没找到。用 GetDeviceItemTree 确认路径的每一段。",
                        null, null);
            }

            var addresses = item.Addresses;
            if (addresses == null || !addresses.Any())
            {
                // 「没有地址」和「地址改不了」是两种病，分开说。
                return (false, $"设备项 '{deviceItemPath}' 上没有任何 I/O 地址。"
                             + "常见于它是机架/电源/接口这类本来就不占 I/O 的对象 —— "
                             + "确认路径指向的是**信号模块本身**。", null, null);
            }

            Address? target = null;
            foreach (Address a in addresses)
            {
                if (a != null && a.IoType == wanted)
                {
                    target = a;
                    break;
                }
            }

            if (target == null)
            {
                var have = string.Join(" / ", ReadAddresses(item).Select(x => x.IoType).Distinct());
                return (false, $"设备项 '{deviceItemPath}' 上没有 {wanted} 类型的地址；它实际有的是：{have}。",
                        null, null);
            }

            var before = new IoAddressInfo
            {
                IoType = target.IoType.ToString(),
                StartAddress = target.StartAddress,
                Length = target.Length
            };

            if (before.StartAddress == startAddress)
            {
                // 幂等：本来就是这个值，如实说没改，别报一次假的「已修改」。
                return (true, $"起始地址本来就是 {startAddress}，未做修改。", before, before);
            }

            try
            {
                target.StartAddress = startAddress;
            }
            catch (Exception ex)
            {
                // 地址重叠、模块不允许改地址等都会走到这里。原因必须回给调用方。
                return (false, $"写入起始地址失败：{ex.Message}"
                             + "（常见原因：与其它模块的地址区重叠，或该模块的地址不允许修改）。",
                        before, null);
            }

            // 写完必须重新取一次再比对。设了不报错 ≠ 真的生效。
            var afterList = ReadAddresses(item);
            var after = afterList.FirstOrDefault(x => x.IoType == before.IoType);

            if (after == null)
            {
                return (false, "写入后读回时找不到同类型地址了，状态异常，请在 TIA 界面里确认。", before, null);
            }

            if (after.StartAddress != startAddress)
            {
                return (false, $"写入没有生效：期望 {startAddress}，读回仍是 {after.StartAddress}。"
                             + "该模块的起始地址可能被组态锁定。", before, after);
            }

            return (true, $"起始地址已从 {before.StartAddress} 改为 {after.StartAddress}"
                        + "（引擎原值，字节偏移）。改完记得 CompileSoftware 并 SaveProject。",
                    before, after);
        }

        private static bool TryParseIoType(string text, out AddressIoType value)
        {
            value = AddressIoType.None;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (AddressIoType candidate in Enum.GetValues(typeof(AddressIoType)))
            {
                if (candidate == AddressIoType.None)
                {
                    continue;
                }

                if (string.Equals(candidate.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }

            return false;
        }

        #endregion
    }
}
