using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseBlockLogic : ResponseMessage
    {
        public string? BlockPath { get; set; }
        public string? Language { get; set; }
        public string? Readable { get; set; }
    }

    public static partial class McpServer
    {
        [McpServerTool(Name = "DescribeBlockLogic"), Description("[L1][PLC-Software] Read a block's LOGIC as READABLE TEXT — the fast, accurate way to analyze LADDER (LAD) without hand-parsing FlgNet XML. For each LAD network it reconstructs the power flow as a boolean-ish expression (series = ' · ', parallel = ' + '), shows coils ( )/(S)/(R), MOVE/compare/timer boxes with their operands, and — critically — FLAGS any contact whose operand is a LITERAL CONSTANT with '⟨常量⟩' (a normally-open contact wired to FALSE silently disables its whole rung; this is nearly impossible to spot by eye). SCL/STL networks are rendered inline as code. Use this to understand or review LAD logic before editing. Requires: Connect + OpenProject + the block consistent (compile first if IsConsistent=false; export does not work in online mode — GoOffline first). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree.")]
        public static ResponseBlockLogic DescribeBlockLogic(
            [Description("softwarePath: PLC software path, e.g. '5T车' or 'PLC_1' (from GetProjectTree)")] string softwarePath,
            [Description("blockPath: fully qualified 'Group/Subgroup/Name' from GetSoftwareTree")] string blockPath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpLogic_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);

                var block = Portal.ExportBlock(softwarePath, blockPath, tempDir);
                if (block == null)
                {
                    throw new McpException($"Could not export '{blockPath}' from '{softwarePath}' for analysis. If IsConsistent=false, compile first; if online, GoOffline first; verify the path with GetSoftwareTree.", McpErrorCode.InternalError);
                }

                var xmlFile = new DirectoryInfo(tempDir).GetFiles("*.xml")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (xmlFile == null)
                {
                    throw new McpException($"Export of '{blockPath}' produced no XML to analyze.", McpErrorCode.InternalError);
                }

                var xml = File.ReadAllText(xmlFile.FullName);
                var readable = LadTextRenderer.Render(xml);
                var lang = block.ProgrammingLanguage.ToString();

                return new ResponseBlockLogic
                {
                    BlockPath = blockPath,
                    Language = lang,
                    Readable = readable,
                    Message = $"Logic of '{block.Name}' [{lang}] decoded. Series contacts joined with ' · ', parallel branches with ' + '; '⟨常量⟩' marks a contact wired to a literal constant (disabled/forced rung).",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["language"] = lang }
                };
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpException($"DescribeBlockLogic failed for '{blockPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
