using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    // Unified HMI 连接「运行时通不通」的纯逻辑 —— 零 Openness 依赖，离线用例够得着（issue #50）。
    //
    // ── V21 真机事实（2026-09-17）──
    //  1. HmiConnection.Partner / Station / Node 在 Openness 里是 **{ get; }**，SetAttribute 报
    //     "'set_Partner' is not supported"。所以软件侧 Connections.Create 建出来的**永远是非集成连接**。
    //     V20 官方文档同样写的只读（issue #50 引用）。
    //     V21 另有硬件层接口（2026-10-22，issue #13 用户指出、真机复现）：
    //     CommunicationManagement.Connections.Create<HmiConnection>(HMI 节点, PLC 的 CPU 项, PLC 节点)，
    //     owner 是 HMI 的运行系统项。Unified 用它建出来是**集成连接**（Partner/Station 有值，符号绑定可编译），
    //     经典面板也能建。同一对设备已有一条连接时 Create 失败。V20 没有这个接口。
    //  2. 非集成连接上 PLC 符号寻址必死：set_PlcTag("DB_HMI.CmdStop") 抛「未找到控制器变量」。只剩绝对地址一条路。
    //  3. 新建连接 DisabledAtStartup 默认 **True**（运行时不连），InitialAddress 默认
    //     "Version=16.0.0.0;CommunicationInterface=Industrial Ethernet;HostAccessPoint=S7ONLINE;HostAddress=192.168.0.2;PlcAddress=192.168.0.1;"
    //     —— 这两个 IP 是**写死的默认值**，不跟硬件组态走。PLC 改了 IP，连接照样去连 192.168.0.1。
    //  4. InitialAddress 写入格式很挑：读回来的原串（**末尾带分号**）原样写回会被拒
    //     "Unsupported format for Initial Address"；去掉末尾分号就收。裸 IP 也被拒。
    internal static class HmiConnectionSpec
    {
        // ---------------- InitialAddress ----------------

        /// <summary>按分号拆成有序的 key=value；不认识的片段原样保留（key 为片段全文、value 为 null）。</summary>
        public static List<KeyValuePair<string, string?>> ParseInitialAddress(string? initialAddress)
        {
            var list = new List<KeyValuePair<string, string?>>();
            foreach (var raw in (initialAddress ?? string.Empty).Split(';'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) { list.Add(new KeyValuePair<string, string?>(part, null)); continue; }
                list.Add(new KeyValuePair<string, string?>(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim()));
            }
            return list;
        }

        public static string? GetValue(List<KeyValuePair<string, string?>> kvs, string key)
            => kvs.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>
        /// 只替换 PlcAddress / HostAddress 两个键，其余键（Version、访问点、Rack/Slot、循环模式…）原样保留、顺序不动。
        /// 传 null 表示那个键不动。结果**不带末尾分号**（带了 V21 会拒收，见顶部第 4 条）。
        /// 原串里缺这个键时追加到末尾。
        /// </summary>
        public static string ComposeInitialAddress(string? original, string? plcAddress, string? hostAddress)
        {
            var kvs = ParseInitialAddress(original);
            void Put(string key, string? value)
            {
                if (value == null) return;
                int i = kvs.FindIndex(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) kvs[i] = new KeyValuePair<string, string?>(kvs[i].Key, value);
                else kvs.Add(new KeyValuePair<string, string?>(key, value));
            }
            Put("PlcAddress", plcAddress);
            Put("HostAddress", hostAddress);
            return string.Join(";", kvs.Select(k => k.Value == null ? k.Key : k.Key + "=" + k.Value));
        }

        // ---------------- 两端 IP 从硬件组态里挑 ----------------

        public sealed class NodeInfo
        {
            public string Address = "";
            public string Subnet = "";
        }

        /// <summary>
        /// 从 PLC 和 HMI 各自的工业以太网节点里挑出「这条连接该用的」两个 IP。
        /// 一个网口 FindNetworkNodes 会回好几条（端口子项重复），先按地址去重。
        /// 规则：两边有同一子网 → 取该子网上的那一对；否则各自只有一个不同地址 → 用它；
        /// 否则答不上来（返回 null，并在 reason 里说为什么），绝不瞎挑一个。
        /// </summary>
        public static (string? PlcIp, string? HmiIp, string? Reason) PickPeerAddresses(
            IEnumerable<NodeInfo> plcNodes, IEnumerable<NodeInfo> hmiNodes)
        {
            var plc = Distinct(plcNodes);
            var hmi = Distinct(hmiNodes);

            foreach (var p in plc.Where(n => n.Subnet.Length > 0))
            {
                var peers = hmi.Where(h => string.Equals(h.Subnet, p.Subnet, StringComparison.OrdinalIgnoreCase)).ToList();
                if (peers.Count == 1)
                {
                    var plcOnSubnet = plc.Where(x => string.Equals(x.Subnet, p.Subnet, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (plcOnSubnet.Count == 1) return (p.Address, peers[0].Address, null);
                }
            }

            string? plcIp = plc.Select(n => n.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? plc[0].Address : null;
            string? hmiIp = hmi.Select(n => n.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? hmi[0].Address : null;
            string? reason = null;
            if (plcIp == null)
                reason = plc.Count == 0
                    ? "PLC 上一个带 IP 的工业以太网节点都没读到"
                    : $"PLC 有 {plc.Count} 个不同 IP（{string.Join(", ", plc.Select(n => n.Address))}）且和 HMI 没有共同子网，不知道该连哪个";
            else if (hmiIp == null)
                reason = hmi.Count == 0
                    ? "HMI 上一个带 IP 的工业以太网节点都没读到"
                    : $"HMI 有 {hmi.Count} 个不同 IP（{string.Join(", ", hmi.Select(n => n.Address))}）且和 PLC 没有共同子网，不知道该用哪个口";
            return (plcIp, hmiIp, reason);
        }

        // ---------------- V21 硬件层建连接 ----------------

        /// <summary>
        /// 硬件层 Create 要两端各一个以太网节点，且挂在**同一条**子网上（真机：没组网时 Create 直接失败）。
        /// 返回两边下标；挑不出 → (-1, -1, 原因)。不替用户组网 —— 改网络组态不是建连接该顺手做的事。
        /// </summary>
        public static (int Plc, int Hmi, string? Reason) PickSharedSubnetPair(IList<NodeInfo> plcNodes, IList<NodeInfo> hmiNodes)
        {
            if (plcNodes.Count == 0) return (-1, -1, "the PLC has no Industrial Ethernet interface");
            if (hmiNodes.Count == 0) return (-1, -1, "the HMI has no Industrial Ethernet interface");
            for (int i = 0; i < plcNodes.Count; i++)
                for (int j = 0; j < hmiNodes.Count; j++)
                    if (plcNodes[i].Subnet.Length > 0
                        && string.Equals(plcNodes[i].Subnet, hmiNodes[j].Subnet, StringComparison.OrdinalIgnoreCase))
                        return (i, j, null);

            var plcNets = plcNodes.Select(n => n.Subnet).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hmiNets = hmiNodes.Select(n => n.Subnet).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var why = plcNets.Count == 0 ? "the PLC's Ethernet interface is not on any subnet"
                    : hmiNets.Count == 0 ? "the HMI's Ethernet interface is not on any subnet"
                    : $"the PLC is on '{string.Join("', '", plcNets)}' and the HMI on '{string.Join("', '", hmiNets)}', not the same subnet";
            return (-1, -1, why + " — connect both to the same PROFINET subnet first (ConnectDeviceNodesToProfinetSubnet; for the HMI pass its interface module, e.g. secondRootPath='HMI_1/HMI_1.IE_CP_1')");
        }

        /// <summary>硬件层 Create 失败时给人看的话。HMI 上已有连接时点明：同一对设备不能再建第二条。</summary>
        public static string HardwareCreateFailed(string hmiPath, string plcName, string error, IEnumerable<string> existingConnections)
        {
            var names = (existingConnections ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var s = $"Creating the HMI connection {hmiPath}↔{plcName} through the TIA V21 hardware connection API failed: {(error ?? string.Empty).Trim().TrimEnd('.')}.";
            if (names.Count > 0)
                s += $" The HMI already has connection(s) '{string.Join("', '", names)}' — Openness cannot add another connection to the same PLC while one exists: "
                   + "pass that connection's name to reuse it, or delete it in the TIA UI and call again.";
            return s;
        }

        public static string ClassicPanelOnV20(string hmiPath)
            => $"'{hmiPath}' is a classic WinCC panel (Basic/Comfort). TIA V20 Openness cannot create classic HMI connections "
             + "(the hardware connection API exists from TIA V21 on). Create the connection once in the TIA UI (Devices & networks → HMI connection) "
             + "or use TIA V21, where EnsureUnifiedHmiConnection creates it. Nothing was changed.";

        private static List<NodeInfo> Distinct(IEnumerable<NodeInfo> nodes)
            => (nodes ?? Enumerable.Empty<NodeInfo>())
                .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Address))
                .GroupBy(n => n.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new NodeInfo { Address = g.Key, Subnet = g.Select(x => x.Subnet ?? "").FirstOrDefault(s => s.Length > 0) ?? "" })
                .ToList();
    }
}
