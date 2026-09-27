using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Partial: PLC 连接表工具（issue #29）。只在 TIA V21+ 可用（HW.CommunicationConnections 在 V20 上不存在）。
    /// </summary>
    public static partial class McpServer
    {
        [McpServerTool(Name = "CreateS7Connection"), Description(
            "[L2][Hardware] Create an S7 connection in a PLC's connection table - the connection that PUT/GET instructions reference by its ID. "
            + "Requires TIA V21+ (not available on V20), Connect + OpenProject. Two cases: "
            + "(1) partnerPlc = another PLC in this project: both must already be on the same subnet (ConnectDeviceNodesToProfinetSubnet); TIA creates the partner side automatically. "
            + "(2) partnerIp = partner PLC in another project: an unspecified-partner connection with that address (partnerRack/partnerSlot default 0/1 = S7-1200/1500 CPU). "
            + "Connection IDs are HEXADECIMAL as in TIA's connection table ('101' = 16#101); leave localConnectionId empty to let TIA assign one (TIA's default is 16#100; IDs below 16#100 were rejected by the hardware compile on S7-1200). "
            + "The values are read back, and by default the CPU hardware is compiled and any error reported under this connection deletes it again (rollback) and returns the TIA message; "
            + "compile errors unrelated to the connection (e.g. device security settings) are only counted. PUT/GET additionally needs 'Permit PUT/GET access' on the partner (SetPutGetAccess) and non-optimized DBs (PlcBuildAndImport kind=globaldb, optimized=false).")]
        public static ResponseJsonReport CreateS7Connection(
            [Description("plc: PLC software name of the local (calling) PLC, e.g. 'PLC_1'")] string plc,
            [Description("partnerPlc: partner PLC in the SAME project, e.g. 'PLC_2'. Give this OR partnerIp.")] string partnerPlc = "",
            [Description("partnerIp: IPv4 address of a partner PLC in ANOTHER project, e.g. '192.168.0.2'. Give this OR partnerPlc.")] string partnerIp = "",
            [Description("connectionName: optional unique connection name; empty lets TIA name it (S7_Connection_N)")] string connectionName = "",
            [Description("localConnectionId: optional local ID in hex ('101', '16#101', 'W#16#101'); this is the ID the PUT/GET instructions use. Empty lets TIA assign it.")] string localConnectionId = "",
            [Description("partnerConnectionId: optional ID of the connection end point on the partner, in hex")] string partnerConnectionId = "",
            [Description("localActive: true (default) = this PLC establishes the connection actively")] bool localActive = true,
            [Description("partnerRack: rack of the partner CPU for partnerIp connections (default 0)")] int partnerRack = 0,
            [Description("partnerSlot: slot of the partner CPU for partnerIp connections (default 1, S7-1200/1500)")] int partnerSlot = 1,
            [Description("verifyWithCompile: compile the CPU hardware afterwards and roll back on errors under this connection (default true)")] bool verifyWithCompile = true)
        {
            try
            {
                var data = Portal.CreateS7Connection(plc, partnerPlc, partnerIp, connectionName, localConnectionId,
                    partnerConnectionId, localActive, partnerRack, partnerSlot, verifyWithCompile);
                var conn = data["connection"];
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = $"S7 connection '{conn?["LocalConnectionName"]}' (local ID {conn?["LocalConnectionId"]}) created on '{plc}' to {data["partner"]}"
                        + (verifyWithCompile ? "; hardware compile shows no error for it." : "; not compile-checked (verifyWithCompile=false)."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex) when (pex.Code == PortalErrorCode.InvalidParams || pex.Code == PortalErrorCode.NotFound || pex.Code == PortalErrorCode.NotSupportedOnVersion)
            {
                throw new McpException(pex.Message, pex, McpErrorCode.InvalidParams);
            }
            catch (ArgumentException aex)
            {
                throw new McpException(aex.Message, aex, McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"CreateS7Connection failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcConnections"), Description(
            "[L2][Hardware] List a PLC's connection table (S7, TCP, ISO-on-TCP, HMI ... connections) with local/partner IDs, names, addresses, TSAPs and whether each is fully specified. "
            + "Read-only. Requires TIA V21+ (not available on V20), Connect + OpenProject. Use it to find the connection ID a PUT/GET instruction must use, or to check a connection created by CreateS7Connection.")]
        public static ResponseJsonReport GetPlcConnections(
            [Description("plc: PLC software name, e.g. 'PLC_1'")] string plc)
        {
            try
            {
                var data = Portal.GetPlcConnections(plc);
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = $"{data["count"]} connection(s) on '{plc}'",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex) when (pex.Code == PortalErrorCode.InvalidParams || pex.Code == PortalErrorCode.NotFound || pex.Code == PortalErrorCode.NotSupportedOnVersion)
            {
                throw new McpException(pex.Message, pex, McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPlcConnections failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
