using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial：Unified HMI 连接「运行时连不连得上」（issue #50）。
    /// 真机事实与判据全在 <see cref="HmiConnectionSpec"/> 顶部。
    /// </summary>
    public partial class Portal
    {
        internal sealed class UnifiedConnectionRuntimeReport
        {
            public bool DisabledAtStartupOk;
            public bool PeerAddressOk;
            /// <summary>两端 IP 从硬件组态里答不上来（不是错，是不知道）。</summary>
            public bool PeerAddressUnknown;
            /// <summary>两端网口确实挂在**同一条**子网上。IP 写对了不代表通得了。</summary>
            public bool PeerSubnetOk;
            public bool Integrated;
            public List<string> Notes = new List<string>();
        }

        /// <summary>
        /// 把连接的两件「编译全绿、运行期全废」的事做对并**读回证明**：
        ///  ① DisabledAtStartup → false（Openness 新建默认 True = 运行时根本不连）；
        ///  ② InitialAddress 里的 PlcAddress / HostAddress → 硬件组态里 PLC / HMI 的真实 IP
        ///     （默认是写死的 192.168.0.1 / .2，不跟组态走）。只动这两个键，其它键原样保留。
        ///  ③ 只读核对：两端网口是不是挂在**同一条**子网上（不改组态）。IP 写对了不等于通得了 ——
        ///     没组网时照样能把地址写进去、读回一致，这一步不说，下游才炸。
        /// 写失败不吞：原因进 Notes，对应的 Ok 为 false。
        /// </summary>
        internal UnifiedConnectionRuntimeReport EnsureUnifiedConnectionRuntime(object connection, string hmiSoftwarePath, string plcSoftwarePath)
        {
            var r = new UnifiedConnectionRuntimeReport();

            // ① DisabledAtStartup
            var before = TryGetPropertyValue(connection, "DisabledAtStartup") as bool?;
            if (before != false)
            {
                string? err = null;
                try { connection.GetType().GetProperty("DisabledAtStartup")?.SetValue(connection, false); }
                catch (Exception ex) { err = (ex.InnerException ?? ex).Message; }
                var after = TryGetPropertyValue(connection, "DisabledAtStartup") as bool?;
                r.DisabledAtStartupOk = after == false;
                r.Notes.Add(r.DisabledAtStartupOk
                    ? $"DisabledAtStartup {Show(before)}→False（运行时启动即建立连接）"
                    : $"DisabledAtStartup 没改成 False（读回 {Show(after)}{(err != null ? "；" + err : "")}）—— 运行时这条连接不会建立，画面上所有值都不刷新");
            }
            else
            {
                r.DisabledAtStartupOk = true;
                r.Notes.Add("DisabledAtStartup=False");
            }

            // 集成与否（Partner/Station/Node 只读；软件侧建出来的恒为空，V21 硬件层建出来的有值）
            var partnerBack = TryGetPropertyValue(connection, "Partner")?.ToString();
            r.Integrated = !string.IsNullOrWhiteSpace(partnerBack);
            r.Notes.Add(r.Integrated
                ? $"集成连接：Partner={partnerBack}、Station={TryGetPropertyValue(connection, "Station")}（读回值，请核对是不是目标 PLC）"
                : "非集成连接（Partner/Station/Node 为空）："
                  + "HMI 变量只能用 address 绝对地址绑定（非优化 DB），按 PLC 符号绑定会失败；"
                  + "要符号绑定需要集成连接：TIA V21 上删掉这条连接、两端连到同一子网后重跑本工具即可建出（V20 只能在博途「设备和网络 → 连接」里手动建）");
            if (r.Integrated)
            {
                // 集成连接（V21 硬件层或博途界面建的）的地址跟着伙伴组态走：不去改写，免得把一条好连接改坏或误判成失败。
                r.PeerAddressUnknown = true;
                r.Notes.Add($"集成连接的地址由博途按伙伴组态管理，本工具未改写：InitialAddress={TryGetPropertyValue(connection, "InitialAddress")}");
                return r;
            }

            // ② 两端 IP
            var plcNodes = ReadIeNodesOfSoftware(plcSoftwarePath, out var plcScanError);
            var hmiNodes = ReadIeNodesOfSoftware(hmiSoftwarePath, out var hmiScanError);
            var (plcIp, hmiIp, reason) = HmiConnectionSpec.PickPeerAddresses(plcNodes, hmiNodes);
            var original = TryGetPropertyValue(connection, "InitialAddress")?.ToString() ?? string.Empty;
            var kvs = HmiConnectionSpec.ParseInitialAddress(original);
            var curPlc = HmiConnectionSpec.GetValue(kvs, "PlcAddress");
            var curHost = HmiConnectionSpec.GetValue(kvs, "HostAddress");

            if (plcIp == null)
            {
                r.PeerAddressUnknown = true;
                r.Notes.Add($"没法从硬件组态确定 PLC IP（{reason}{ScanNote(plcScanError, hmiScanError)}），InitialAddress 未动：PlcAddress={curPlc ?? "<无>"}。请人工核对");
                return r;
            }

            var wantHost = hmiIp;   // HMI IP 答不上来时 HostAddress 不动
            bool plcDiff = !string.Equals(curPlc, plcIp, StringComparison.OrdinalIgnoreCase);
            bool hostDiff = wantHost != null && !string.Equals(curHost, wantHost, StringComparison.OrdinalIgnoreCase);
            if (!plcDiff && !hostDiff)
            {
                r.PeerAddressOk = true;
                r.Notes.Add($"PlcAddress={curPlc}、HostAddress={curHost} 与硬件组态一致");
            }
            else
            {
                var composed = HmiConnectionSpec.ComposeInitialAddress(original, plcIp, wantHost);
                string? err = null;
                try { connection.GetType().GetProperty("InitialAddress")?.SetValue(connection, composed); }
                catch (Exception ex) { err = (ex.InnerException ?? ex).Message; }
                var readback = HmiConnectionSpec.ParseInitialAddress(TryGetPropertyValue(connection, "InitialAddress")?.ToString());
                var rbPlc = HmiConnectionSpec.GetValue(readback, "PlcAddress");
                var rbHost = HmiConnectionSpec.GetValue(readback, "HostAddress");
                r.PeerAddressOk = string.Equals(rbPlc, plcIp, StringComparison.OrdinalIgnoreCase)
                                  && (wantHost == null || string.Equals(rbHost, wantHost, StringComparison.OrdinalIgnoreCase));
                r.Notes.Add(r.PeerAddressOk
                    ? $"InitialAddress 按硬件组态改正：PlcAddress {curPlc ?? "<无>"}→{rbPlc}，HostAddress {curHost ?? "<无>"}→{rbHost}（其它键未动）"
                    : $"InitialAddress 没改成功（要 PlcAddress={plcIp} HostAddress={wantHost ?? "<不动>"}，读回 PlcAddress={rbPlc} HostAddress={rbHost}{(err != null ? "；" + err : "")}）—— 运行时会去连错的地址");
            }
            if (wantHost == null)
            {
                // 原来只发一条 note 就完了，Outcome 照样 Success —— 而 HostAddress 这时很可能还是 Openness
                // 写死的 192.168.0.2，面板运行时拿一个不是自己的地址去建连接。答不上来就不能报成功。
                r.PeerAddressUnknown = true;
                r.Notes.Add($"HMI 侧 IP 答不上来（{reason ?? "未知"}{ScanNote(null, hmiScanError)}），HostAddress={curHost ?? "<无>"} 未核对");
            }

            // ③ 两端到底挂没挂在同一条子网上 —— 地址写对了不等于通得了。
            // 原来这一层根本没查：PickPeerAddresses 的后备分支（两边没有共同子网、但各自只有一个地址就用它）
            // 回的 reason 是 null，和「同一条子网上确定的一对」长得一模一样；于是 PeerAddressOk=true，工具报 Success。
            // 后果（2026-09-20 真机）：ConnectDeviceNodesToProfinetSubnet 已经失败、两端一条子网都没挂上，
            // 这一步照样把 IP 写进去报成功，一路到 EnsureUnifiedHmiTag 才炸，炸的地方离根因十万八千里。
            var plcSubnet = SubnetOfAddress(plcNodes, plcIp);
            var hmiSubnet = wantHost == null ? string.Empty : SubnetOfAddress(hmiNodes, wantHost);
            if (plcScanError != null || hmiScanError != null)
            {
                r.Notes.Add($"设备树没走全（{plcScanError ?? hmiScanError}），两端子网核对不了");
            }
            else if (plcSubnet.Length == 0 || (wantHost != null && hmiSubnet.Length == 0))
            {
                r.Notes.Add($"⚠ 两端还没组网：{(plcSubnet.Length == 0 ? "PLC" : "HMI")} 的以太网口没有 ConnectedSubnet。"
                          + "IP 写进去了，运行时照样连不上 —— 先把两台设备连到同一条 PROFINET 子网"
                          + "（ConnectDeviceNodesToProfinetSubnet，它失败了就别往下走），再回来核一次。");
            }
            else if (wantHost != null && !string.Equals(plcSubnet, hmiSubnet, StringComparison.OrdinalIgnoreCase))
            {
                r.Notes.Add($"⚠ 两端不在同一条子网：PLC 在 '{plcSubnet}'、HMI 在 '{hmiSubnet}'。IP 写对了也通不了。");
            }
            else if (wantHost != null)
            {
                r.PeerSubnetOk = true;
                r.Notes.Add($"两端同在子网 '{plcSubnet}' 上");
            }

            // 2026-09-21：这里原来有一句「借 PeerAddressUnknown 表达子网不对」的降级
            //     if (!r.PeerSubnetOk && r.PeerAddressOk) r.PeerAddressUnknown = true;
            // 已经删掉 —— 上游 EnsureUnifiedHmiConnection 现在**直接读 PeerSubnetOk**。
            // 借字段的写法能跑，但每个字段就不再只表示一件事：拿到 Unknown 的人分不清是
            // "地址读不回来"还是"两端不在同一条子网"，而这条耦合的两头在两个文件里，
            // 谁改一下就无声断掉。每个字段只说一件事，判成败的地方自己去组合。
            return r;
        }

        private static string Show(bool? b) => b.HasValue ? b.Value.ToString() : "<读不到>";

        /// <summary>某个 IP 所在节点连着的子网名；没挂子网或读不到 → 空串。</summary>
        private static string SubnetOfAddress(List<HmiConnectionSpec.NodeInfo> nodes, string address)
        {
            foreach (var n in nodes)
            {
                if (n == null) continue;
                if (string.Equals((n.Address ?? string.Empty).Trim(), address, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(n.Subnet))
                    return n.Subnet.Trim();
            }
            return string.Empty;
        }

        private static string ScanNote(string? plcError, string? hmiError)
            => plcError == null && hmiError == null ? string.Empty : $"；设备树没走全：{plcError ?? hmiError}";

        /// <summary>软件所在设备上所有工业以太网节点的 (IP, 子网)。读不到返回空表，不抛。
        /// 走不全时 <paramref name="error"/> 写明原因 —— 原来整块 <c>catch { }</c> 把病因吞了，
        /// 半截名单和完整名单长得一模一样，上游会拿半截名单「确定地」挑出一对 IP。</summary>
        private List<HmiConnectionSpec.NodeInfo> ReadIeNodesOfSoftware(string softwarePath, out string? error)
        {
            error = null;
            var list = new List<HmiConnectionSpec.NodeInfo>();
            try
            {
                object? cur = GetSoftwareContainer(softwarePath)?.Parent;
                while (cur != null && cur is not Device)
                    cur = (cur as DeviceItem)?.Parent;
                if (cur is not Device device)
                {
                    error = $"从 '{softwarePath}' 往上没找到所属设备（Device）";
                    return list;
                }
                foreach (var root in device.DeviceItems)
                {
                    foreach (var n in FindNetworkNodes(root))
                    {
                        if (!IsIndustrialEthernetNode(n.Node)) continue;
                        var subnet = TryGetPropertyValue(n.Node, "ConnectedSubnet");
                        list.Add(new HmiConnectionSpec.NodeInfo
                        {
                            Address = ReadNodeAddress(n.Node) ?? string.Empty,
                            Subnet = TryGetName(subnet) ?? string.Empty,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {(ex.InnerException ?? ex).Message}";
            }
            return list;
        }
    }
}
