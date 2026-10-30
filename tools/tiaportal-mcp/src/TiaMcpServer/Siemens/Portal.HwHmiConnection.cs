using Siemens.Engineering;
using Siemens.Engineering.HW;
#if !TIA_V20 && !TIA_V19
using Siemens.Engineering.HW.CommunicationConnections;
using Siemens.Engineering.HW.Features;
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial：V21 硬件层建 PLC↔HMI 连接（经典面板也能建，Unified 建出集成连接）。
    /// 真机事实在 <see cref="HmiConnectionSpec"/> 顶部第 1 条。
    /// </summary>
    public partial class Portal
    {
#if !TIA_V20 && !TIA_V19
        internal sealed class HwConnectionAttempt
        {
            public bool Created;
            public object? Connection;
            /// <summary>没建成的原因；建成了但改名失败也写这里。</summary>
            public string? Error;
            public string Readback = string.Empty;
        }
#endif

#if !TIA_V20 && !TIA_V19
        /// <summary>
        /// owner = HMI 软件所在的运行系统项（真机：设备根、IE_CP、接口、端口都给得出 CommunicationManagement，
        /// 但只有运行系统项 Create 得成）；partnerTarget = PLC 软件所在的 CPU 项；两个 Node = 同一子网上的以太网节点。
        /// 不抛：失败原因进 Error。
        /// </summary>
        internal HwConnectionAttempt TryCreateHardwareHmiConnection(string hmiSoftwarePath, string plcName, string connectionName)
        {
            var a = new HwConnectionAttempt();
            try
            {
                var hmiItem = GetSoftwareContainer(hmiSoftwarePath)?.Parent as DeviceItem;
                var plcItem = GetSoftwareContainer(plcName)?.Parent as DeviceItem;
                if (hmiItem == null) { a.Error = $"no device item owns HMI software '{hmiSoftwarePath}'"; return a; }
                if (plcItem == null) { a.Error = $"PLC software '{plcName}' not found"; return a; }
                var cm = hmiItem.GetService<CommunicationManagement>();
                if (cm == null) { a.Error = $"'{hmiItem.Name}' exposes no CommunicationManagement service"; return a; }

                var plcNodes = IeNodesOfDevice(plcItem);
                var hmiNodes = IeNodesOfDevice(hmiItem);
                var (pi, hi, reason) = HmiConnectionSpec.PickSharedSubnetPair(
                    plcNodes.Select(n => n.Info).ToList(), hmiNodes.Select(n => n.Info).ToList());
                if (reason != null) { a.Error = reason; return a; }

                var c = cm.Connections.Create<HmiConnection>(hmiNodes[hi].Node, plcItem, plcNodes[pi].Node);
                a.Created = true;
                a.Connection = c;
                try { c.LocalConnectionName = connectionName; }
                catch (Exception ex) { a.Error = $"created, but renaming it to '{connectionName}' failed: {(ex.InnerException ?? ex).Message}"; }
                a.Readback = DescribeHardwareHmiConnection(c);
            }
            catch (Exception ex)
            {
                a.Error = (ex.InnerException ?? ex).Message;
            }
            return a;
        }

        /// <summary>HMI 运行系统项上按 LocalConnectionName 找硬件层连接（经典面板走这条，也认博途界面里建的）。</summary>
        internal HmiConnection? FindHardwareHmiConnection(string hmiSoftwarePath, string connectionName, out List<string> allNames)
        {
            allNames = new List<string>();
            var cm = (GetSoftwareContainer(hmiSoftwarePath)?.Parent as DeviceItem)?.GetService<CommunicationManagement>();
            if (cm == null) return null;
            HmiConnection? hit = null;
            foreach (Connection c in cm.Connections)
            {
                if (c is not HmiConnection h) continue;
                allNames.Add(h.LocalConnectionName);
                if (hit == null && string.Equals(h.LocalConnectionName, connectionName, StringComparison.Ordinal)) hit = h;
            }
            return hit;
        }

        private static string DescribeHardwareHmiConnection(HmiConnection c)
        {
            object? valid = null;
            try { valid = c.GetAttribute("IsValid"); } catch { }
            return $"LocalConnectionName={c.LocalConnectionName}; IsValid={valid ?? "<unreadable>"}; LocalAddress={c.LocalAddress}; PartnerAddress={c.PartnerAddress}";
        }

        /// <summary>设备上所有工业以太网节点（按 IP 去重：一个网口 FindNetworkNodes 会因端口子项回好几条）。</summary>
        private static List<(Node Node, HmiConnectionSpec.NodeInfo Info)> IeNodesOfDevice(DeviceItem anyItem)
        {
            object? cur = anyItem;
            while (cur != null && cur is not Device) cur = (cur as DeviceItem)?.Parent;
            var list = new List<(Node, HmiConnectionSpec.NodeInfo)>();
            if (cur is not Device device) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in device.DeviceItems)
                foreach (var n in FindNetworkNodes(root))
                {
                    if (!IsIndustrialEthernetNode(n.Node) || n.Node is not Node node) continue;
                    var address = ReadNodeAddress(node) ?? string.Empty;
                    if (!seen.Add(address)) continue;
                    list.Add((node, new HmiConnectionSpec.NodeInfo
                    {
                        Address = address,
                        Subnet = TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? string.Empty,
                    }));
                }
            return list;
        }

        /// <summary>经典面板（HmiTarget）：只走硬件层；建不成如实报因，不退回（经典没有软件层 Create）。</summary>
        private ModelContextProtocol.ResponseObjectDescribe EnsureClassicHmiConnection(string hmiSoftwarePath, string connectionName, string plcName)
        {
            var conn = FindHardwareHmiConnection(hmiSoftwarePath, connectionName, out var names);
            var state = "exists";
            if (conn == null)
            {
                var hw = TryCreateHardwareHmiConnection(hmiSoftwarePath, plcName, connectionName);
                if (!hw.Created)
                    throw new InvalidOperationException(HmiConnectionSpec.HardwareCreateFailed(hmiSoftwarePath, plcName, hw.Error ?? "unknown", names) + " Nothing was changed.");
                if (hw.Error != null)
                    throw new InvalidOperationException($"Classic HMI connection to '{plcName}' {hw.Error}. Readback: {hw.Readback}");
                conn = (HmiConnection)hw.Connection!;
                state = "created";
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                ObjectKind = "HmiConnection",
                ObjectPath = $"{hmiSoftwarePath}:{connectionName}",
                TypeName = conn.GetType().FullName,
                Members = DescribeMembers(conn, 220),
                Message = $"Classic HMI connection '{connectionName}' {state} (TIA V21 hardware connection API). Readback: {DescribeHardwareHmiConnection(conn)}. "
                    + "On classic panels import HMI tags with absolute addressing on this connection (AddressAccessMode=Absolute + LogicalAddress, "
                    + "non-optimized DB); Openness refuses symbolic ControllerTag links in classic tag imports. The start screen still has to be set once in the TIA UI."
            };
        }
#endif
    }
}
