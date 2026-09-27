using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
#if !TIA_V20
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW.CommunicationConnections;
#endif

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial: PLC 连接表（issue #29）。V21 起 Openness 暴露 Siemens.Engineering.HW.CommunicationConnections：
    /// CPU 设备项上 GetService&lt;CommunicationManagement&gt;() → Connections.Create&lt;S7Connection&gt;(本地接口节点, 伙伴 CPU, 伙伴接口节点)。
    /// V21 Upd2 两台 S7-1200 实测：同一工程的伙伴会自动生成对端连接；伙伴在别的工程时传 null 伙伴，
    /// 再设 PartnerAddress / PartnerRack / PartnerSlot，硬件编译通过。V20 没有这套 API。
    /// </summary>
    public partial class Portal
    {
        /// <summary>从 PLC 软件名解析出 CPU 设备项，以及它的第一个以太网节点（优先已连到子网的）。</summary>
        private (DeviceItem cpu, Node? node) ResolvePlcCpuAndNode(string plcName)
        {
            var sw = GetPlcSoftware(plcName)
                ?? throw new PortalException(PortalErrorCode.NotFound, $"PLC '{plcName}' not found. Use GetProjectTree for the PLC software name (e.g. 'PLC_1').");
            var cpu = (sw.Parent as SoftwareContainer)?.Parent as DeviceItem
                ?? throw new PortalException(PortalErrorCode.NotFound, $"Could not resolve the CPU device item of PLC '{plcName}'.");

            var nodes = new List<Node>();
            void Walk(DeviceItemComposition items)
            {
                foreach (var item in items)
                {
                    try
                    {
                        var ni = item.GetService<NetworkInterface>();
                        if (ni != null) nodes.AddRange(ni.Nodes);
                    }
                    catch { /* 不是网络接口的设备项 */ }
                    Walk(item.DeviceItems);
                }
            }
            Walk(cpu.DeviceItems);

            Node? node = nodes.FirstOrDefault(n => SafeSubnetOf(n) != null) ?? nodes.FirstOrDefault();
            return (cpu, node);
        }

        private static Subnet? SafeSubnetOf(Node? node)
        {
            try { return node?.ConnectedSubnet; } catch { return null; }
        }

        public JsonObject GetPlcConnections(string plcName)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open.");
            Capability.RequireSupported(TiaFeature.PlcConnections);
#if TIA_V20
            throw new PortalException(PortalErrorCode.NotSupportedOnVersion, Capability.Describe(TiaFeature.PlcConnections));
#else
            var (cpu, _) = ResolvePlcCpuAndNode(plcName);
            var cm = cpu.GetService<CommunicationManagement>()
                ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, $"The CPU of '{plcName}' offers no CommunicationManagement service.");
            var list = new JsonArray();
            foreach (var c in cm.Connections) list.Add(DescribeConnection(c));
            return new JsonObject
            {
                ["plc"] = plcName,
                ["cpu"] = cpu.Name,
                ["count"] = list.Count,
                ["connections"] = list
            };
#endif
        }

        public JsonObject CreateS7Connection(
            string plcName, string partnerPlc, string partnerIp, string connectionName, string localConnectionId,
            string partnerConnectionId, bool localActive, int partnerRack, int partnerSlot, bool verifyWithCompile)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open.");
            Capability.RequireSupported(TiaFeature.PlcConnections);

            // 先把参数全部校验完，再碰工程。
            var ip = S7ConnectionArgs.ValidatePartner(partnerPlc, partnerIp);
            var localId = S7ConnectionArgs.NormalizeConnectionId(localConnectionId, "localConnectionId");
            var partnerId = S7ConnectionArgs.NormalizeConnectionId(partnerConnectionId, "partnerConnectionId");
            if (partnerRack < 0 || partnerSlot < 0)
                throw new PortalException(PortalErrorCode.InvalidParams, "partnerRack / partnerSlot must be >= 0.");
#if TIA_V20
            throw new PortalException(PortalErrorCode.NotSupportedOnVersion, Capability.Describe(TiaFeature.PlcConnections));
#else
            var (cpu, localNode) = ResolvePlcCpuAndNode(plcName);
            if (localNode == null)
                throw new PortalException(PortalErrorCode.InvalidParams, $"PLC '{plcName}' has no Ethernet/PROFINET interface node.");
            var cm = cpu.GetService<CommunicationManagement>()
                ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, $"The CPU of '{plcName}' offers no CommunicationManagement service.");
            var name = S7ConnectionArgs.ValidateConnectionName(connectionName, cm.Connections.Select(ConnectionName));

            DeviceItem? partnerCpu = null;
            Node? partnerNode = null;
            if (ip.Length == 0)
            {
                (partnerCpu, partnerNode) = ResolvePlcCpuAndNode(partnerPlc);
                if (partnerCpu.Equals(cpu))
                    throw new PortalException(PortalErrorCode.InvalidParams, "partnerPlc is the same PLC as plc.");
                var localSubnet = SafeSubnetOf(localNode);
                var partnerSubnet = SafeSubnetOf(partnerNode);
                if (partnerNode == null || localSubnet == null || partnerSubnet == null || !string.Equals(localSubnet.Name, partnerSubnet.Name, StringComparison.Ordinal))
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"'{plcName}' and '{partnerPlc}' are not on the same subnet (local: {localSubnet?.Name ?? "none"}, partner: {partnerSubnet?.Name ?? "none"}). "
                        + "Connect both with ConnectDeviceNodesToProfinetSubnet first.");
            }

            var warnings = new JsonArray();
            var idWarning = S7ConnectionArgs.IdWarning(localId);
            if (idWarning != null) warnings.Add(idWarning);

            S7Connection? s7 = null;
            try
            {
                s7 = cm.Connections.Create<S7Connection>(localNode, partnerCpu!, partnerNode!) as S7Connection
                     ?? throw new PortalException(PortalErrorCode.OpennessError, "Connections.Create<S7Connection> returned no S7 connection.");

                if (name.Length > 0) s7.LocalConnectionName = name;
                if (localId.Length > 0) s7.LocalConnectionId = localId;
                s7.LocalActiveEstablishment = localActive;
                if (ip.Length > 0)
                {
                    s7.PartnerAddress = ip;
                    s7.PartnerRack = partnerRack;
                    s7.PartnerSlot = partnerSlot;
                }
                if (partnerId.Length > 0) s7.PartnerConnectionId = partnerId;

                // 读回：要求的值必须真的落下，不拿「setter 没抛」当证据。
                var mismatches = new List<string>();
                if (name.Length > 0 && !string.Equals(s7.LocalConnectionName, name, StringComparison.Ordinal)) mismatches.Add($"LocalConnectionName={s7.LocalConnectionName}");
                if (localId.Length > 0 && !SameHexId(s7.LocalConnectionId, localId)) mismatches.Add($"LocalConnectionId={s7.LocalConnectionId}");
                if (s7.LocalActiveEstablishment != localActive) mismatches.Add($"LocalActiveEstablishment={s7.LocalActiveEstablishment}");
                if (ip.Length > 0 && !string.Equals(s7.PartnerAddress, ip, StringComparison.Ordinal)) mismatches.Add($"PartnerAddress={s7.PartnerAddress}");
                if (partnerId.Length > 0 && !SameHexId(s7.PartnerConnectionId, partnerId)) mismatches.Add($"PartnerConnectionId={s7.PartnerConnectionId}");
                if (mismatches.Count > 0)
                    throw new PortalException(PortalErrorCode.OpennessError, "Read-back does not match the requested values: " + string.Join(", ", mismatches));

                var finalName = s7.LocalConnectionName;
                var result = new JsonObject
                {
                    ["plc"] = plcName,
                    ["partner"] = ip.Length > 0 ? $"unspecified (address {ip})" : partnerPlc,
                    ["connection"] = DescribeConnection(s7),
                    ["warnings"] = warnings
                };

                if (partnerCpu != null)
                {
                    // 同一工程的伙伴：TIA 自动在伙伴 CPU 上生成对端连接，读出来给调用方核对。
                    var pcm = partnerCpu.GetService<CommunicationManagement>();
                    var mirror = pcm?.Connections.FirstOrDefault(c => c is S7Connection p && SameHexId(p.PartnerConnectionId, s7.LocalConnectionId)
                                                                     && string.Equals(p.PartnerConnectionName, finalName, StringComparison.Ordinal));
                    result["partnerSide"] = mirror == null ? null : DescribeConnection(mirror);
                }

                if (verifyWithCompile)
                {
                    var compile = cpu.GetService<ICompilable>()?.Compile();
                    if (compile == null)
                    {
                        warnings.Add("Hardware compile not available on this CPU; the connection was not compile-checked.");
                    }
                    else
                    {
                        var messages = S7ConnectionArgs.MessagesForConnection(compile.Messages.Select(ToCompileNode), finalName, out var otherErrors);
                        result["compile"] = new JsonObject
                        {
                            ["state"] = compile.State.ToString(),
                            ["connectionMessages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray()),
                            ["otherErrorCount"] = otherErrors
                        };
                        if (S7ConnectionArgs.HasError(messages))
                            throw new PortalException(PortalErrorCode.OpennessError,
                                $"Hardware compile rejected connection '{finalName}': {string.Join(" | ", messages)}");
                        if (otherErrors > 0)
                            warnings.Add($"The hardware compile of '{plcName}' reports {otherErrors} error(s) unrelated to this connection (e.g. device security settings); the connection itself compiled clean.");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                // 失败就回滚：别把一条半配置的连接留在用户工程里。
                string rollback = "nothing to roll back";
                if (s7 != null)
                {
                    try { s7.Delete(); rollback = "the connection was deleted again"; }
                    catch (Exception dex) { rollback = $"⚠ rollback failed ({dex.Message}); delete the connection in TIA"; }
                }
                if (ex is PortalException pex)
                    throw new PortalException(pex.Code, $"{pex.Message} ({rollback}).", null, ex);
                throw new PortalException(PortalErrorCode.OpennessError, $"CreateS7Connection failed: {ex.Message} ({rollback}).", null, ex);
            }
#endif
        }

#if !TIA_V20
        private static string ConnectionName(Connection c)
        {
            try { return (c as S7Connection)?.LocalConnectionName ?? c.GetType().GetProperty("LocalConnectionName")?.GetValue(c)?.ToString() ?? ""; }
            catch { return ""; }
        }

        private static JsonObject DescribeConnection(Connection c)
        {
            var o = new JsonObject { ["type"] = c.GetType().Name };
            foreach (var p in c.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name == "Parent") continue;
                object? v;
                try { v = p.GetValue(c); } catch { continue; }
                o[p.Name] = v switch
                {
                    null => null,
                    bool b => b,
                    int i => i,
                    string s => s,
                    IEngineeringObject eo => eo.GetType().GetProperty("Name")?.GetValue(eo)?.ToString(),
                    _ => v.ToString()
                };
            }
            return o;
        }

        private static CompileNode ToCompileNode(CompilerResultMessage m) =>
            new CompileNode(m.State.ToString(), m.Path ?? "", m.Description ?? "", m.Messages.Select(ToCompileNode).ToList());
#endif

        private static bool SameHexId(string? actual, string expected)
        {
            try { return actual != null && S7ConnectionArgs.NormalizeConnectionId(actual) == expected; }
            catch { return false; }
        }
    }
}
