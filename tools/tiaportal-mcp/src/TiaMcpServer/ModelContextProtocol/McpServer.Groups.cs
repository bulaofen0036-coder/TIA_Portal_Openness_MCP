using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Block-group operations. Kept in a partial file so the McpServer god-file is not
    // touched. Fills the long-standing gap "MCP cannot operate on block groups":
    //   - CreatePlcBlockGroup: native create of (nested) program-block user groups.
    //   - MoveBlockToGroup: organize an existing block into a group (export/delete/
    //     import round-trip, because Openness has no block-reparent API).
    public static partial class McpServer
    {
        [McpServerTool(Name = "CreatePlcBlockGroup"), Description("[L2][PLC-Software] Create a (nested) program-block group/folder, creating any missing parent groups along the path. Idempotent — returns ok if the group already exists. groupPath uses '/' separators relative to 'Program blocks' root, e.g. '01_手动控制/手动意图'. Requires: Connect + OpenProject. Use this to set up the 手动/手自动接口/自动 layer folders, then MoveBlockToGroup to organize blocks into them.")]
        public static ResponseMessage CreatePlcBlockGroup(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("groupPath: '/'-separated group path under Program blocks, e.g. '01_手动控制/手动意图'")] string groupPath)
        {
            try
            {
                var group = Portal.EnsurePlcBlockGroup(softwarePath, groupPath, out var created);
                if (group == null)
                {
                    throw new McpException($"Could not create block group '{groupPath}': PlcSoftware not found at '{softwarePath}'", McpErrorCode.InvalidParams);
                }
                return new ResponseMessage
                {
                    Message = created.Count > 0
                        ? $"PLC block group '{groupPath}' ready (created: {string.Join(", ", created)})"
                        : $"PLC block group '{groupPath}' already existed",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["createdCount"] = created.Count
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed creating PLC block group '{groupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error creating PLC block group '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "MoveBlockToGroup"), Description("[L2][PLC-Software] Move/organize an existing block into a program-block group (found anywhere by exact name). Openness cannot reparent a block, so this exports the block, deletes it, and re-imports it into the target group (SIMATIC SD .s7dcl preferred, SimaticML XML fallback for STL/mixed-language). The block number and references are preserved. autoCreateGroup creates the target group path if missing. Requires: Connect + OpenProject + block consistent (compile first). After moving, call CompileAndDiagnosePlc to confirm 0 errors. Note: avoid moving OBs with event bindings via this round-trip.")]
        public static ResponseMessage MoveBlockToGroup(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("blockName: exact block name to move (searched across all groups)")] string blockName,
            [Description("targetGroupPath: '/'-separated destination group under Program blocks, e.g. '02_手自动接口'")] string targetGroupPath,
            [Description("autoCreateGroup: create the target group path if it does not exist (default true)")] bool autoCreateGroup = true)
        {
            try
            {
                var summary = Portal.MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup);
                return new ResponseMessage
                {
                    Message = summary,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed moving block '{blockName}' to '{targetGroupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error moving block '{blockName}' to '{targetGroupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
