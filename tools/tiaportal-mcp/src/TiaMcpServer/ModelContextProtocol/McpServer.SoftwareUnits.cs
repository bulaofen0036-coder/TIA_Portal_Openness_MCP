using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetSoftwareUnits"), Description("[L1][PLC-Software] Read-only. List the PLC software units (Software units, S7-1500) of a PLC software and the blocks inside each unit. Openness reaches units only through PlcUnitProvider, so they are not children of the PLC software and GetSoftwareTree / InvokeService alone cannot enumerate them. Every block comes with a blockPath of the form 'Software units/<unit>/<group>/<block>' that ExportBlock, GetBlockInfo, DescribeBlockLogic and the other block tools accept as-is. Requires: Connect + OpenProject. softwarePath from GetProjectTree.")]
        public static ResponseMessage GetSoftwareUnits(
            [Description("softwarePath: PLC software name from GetProjectTree, e.g. 'PLC_1'")] string softwarePath,
            [Description("includeBlocks: true lists every block in every unit (name, type, number, language, blockPath); false returns unit names and block counts only")] bool includeBlocks = true)
        {
            try
            {
                return Portal.GetSoftwareUnits(softwarePath, includeBlocks);
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound || pex.Code == PortalErrorCode.InvalidState ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing software units of '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
