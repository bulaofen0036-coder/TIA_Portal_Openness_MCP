using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Read a block's or PLC data type's declaration section (Input/Output/InOut/Static/Temp/Constant)
    /// without making the caller parse SimaticML.
    ///
    /// V19 note: the typed API exposes <c>DataBlock.Interface</c> only. FB/FC/OB and PlcType have no
    /// CLR property for their declarations, so this tool uses the XML export that every object kind
    /// supports. The export happens on the engine machine and is deleted afterwards; the caller gets
    /// JSON, which also avoids the VM/host path boundary.
    /// </summary>
    public class ResponseInterfaceStructure : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectType { get; set; }
        public string? ObjectName { get; set; }
        public string? ObjectPath { get; set; }
        public string? ObjectNamespace { get; set; }
        public int? Number { get; set; }
        public string? ProgrammingLanguage { get; set; }
        public string? MemoryLayout { get; set; }
        public bool? IsConsistent { get; set; }
        public DateTime? InterfaceModifiedDate { get; set; }
        public int? MemberCount { get; set; }
        public bool? Truncated { get; set; }
        public IEnumerable<InterfaceSection>? Sections { get; set; }
        public string? Source { get; set; }
    }

    public static partial class McpServer
    {
        [McpServerTool(Name = "GetBlockInterface"), Description(
            "[L2][PLC-Software] Read a block's INTERFACE (declarations) — FB/FC/OB/DB. " +
            "Returns Input/Output/InOut/Static/Temp/Constant sections; each member has name, datatype, " +
            "start value, comment, array bounds and nested struct members. Use this instead of ExportBlock " +
            "plus hand-parsing XML when you need an FB's parameters, a DB's layout, or any block's variable " +
            "declarations. Works for DBs too: the DB structure is its Static section. " +
            "Requires: Connect + OpenProject + block consistent (compile first if IsConsistent=false) and offline " +
            "(GoOffline first — the XML export does not work in online mode). " +
            "For a UDT / PLC data type use GetTypeStructure.")]
        public static ResponseInterfaceStructure GetBlockInterface(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1' or 'T6_PLC_1' (from GetProjectTree)")] string softwarePath,
            [Description("blockPath: fully qualified 'Group/Subgroup/Name' from GetSoftwareTree")] string blockPath,
            [Description("includeComments: include member comments (default true)")] bool includeComments = true,
            [Description("maxMembers: maximum members to return, 1-5000 (default 1000); Truncated=true when hit")] int maxMembers = 1000)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpInterface_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);

                var block = Portal.ExportBlock(softwarePath, blockPath, tempDir);
                if (block == null)
                {
                    throw new McpException(
                        $"Could not export '{blockPath}' from '{softwarePath}' to read its interface. " +
                        "If IsConsistent=false, compile first; if online, GoOffline first; verify the path with GetSoftwareTree.",
                        McpErrorCode.InternalError);
                }

                var xmlFile = new DirectoryInfo(tempDir).GetFiles("*.xml")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (xmlFile == null)
                {
                    throw new McpException(
                        $"Export of '{blockPath}' produced no XML; cannot read its interface.",
                        McpErrorCode.InternalError);
                }

                var info = InterfaceXmlReader.ReadFile(xmlFile.FullName, "Block", maxMembers, includeComments);
                var truncatedNote = info.Truncated
                    ? $" Output truncated at maxMembers={Math.Max(1, Math.Min(5000, maxMembers))}; raise it to see the rest."
                    : "";

                return new ResponseInterfaceStructure
                {
                    ObjectKind = "Block",
                    ObjectType = info.ObjectType,
                    ObjectName = block.Name,
                    ObjectPath = blockPath,
                    ObjectNamespace = info.ObjectNamespace,
                    Number = block.Number,
                    ProgrammingLanguage = block.ProgrammingLanguage.ToString(),
                    MemoryLayout = info.MemoryLayout ?? block.MemoryLayout.ToString(),
                    IsConsistent = block.IsConsistent,
                    InterfaceModifiedDate = block.InterfaceModifiedDate,
                    MemberCount = info.MemberCount,
                    Truncated = info.Truncated,
                    Sections = info.Sections,
                    Source = "block-xml-export",
                    Message = $"Interface of block '{block.Name}': {info.MemberCount} member(s) in {info.Sections.Count} section(s).{truncatedNote}",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["memberCount"] = info.MemberCount,
                        ["sectionCount"] = info.Sections.Count,
                        ["xmlBytes"] = xmlFile.Length
                    }
                };
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw MapInterfaceFailure("GetBlockInterface", blockPath, ex);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [McpServerTool(Name = "GetTypeStructure"), Description(
            "[L2][PLC-Software] Read a PLC data type (UDT / PlcType) structure — members, datatypes, " +
            "start values, comments, array bounds and nested struct members. Use this for UDTs; for " +
            "FB/FC/OB/DB use GetBlockInterface. Requires: Connect + OpenProject + offline (GoOffline first — " +
            "the XML export does not work in online mode); type must be consistent (compile first if IsConsistent=false).")]
        public static ResponseInterfaceStructure GetTypeStructure(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1' or 'T6_PLC_1' (from GetProjectTree)")] string softwarePath,
            [Description("typePath: fully qualified 'Group/Subgroup/TypeName' from GetSoftwareTree or GetTypes")] string typePath,
            [Description("includeComments: include member comments (default true)")] bool includeComments = true,
            [Description("maxMembers: maximum members to return, 1-5000 (default 1000); Truncated=true when hit")] int maxMembers = 1000)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpInterface_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);

                var type = Portal.ExportType(softwarePath, typePath, tempDir);
                if (type == null)
                {
                    throw new McpException(
                        $"Could not export type '{typePath}' from '{softwarePath}' to read its structure. " +
                        "If IsConsistent=false, compile first; if online, GoOffline first; verify the path with GetTypes.",
                        McpErrorCode.InternalError);
                }

                var xmlFile = new DirectoryInfo(tempDir).GetFiles("*.xml")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (xmlFile == null)
                {
                    throw new McpException(
                        $"Export of type '{typePath}' produced no XML; cannot read its structure.",
                        McpErrorCode.InternalError);
                }

                var info = InterfaceXmlReader.ReadFile(xmlFile.FullName, "Type", maxMembers, includeComments);
                var truncatedNote = info.Truncated
                    ? $" Output truncated at maxMembers={Math.Max(1, Math.Min(5000, maxMembers))}; raise it to see the rest."
                    : "";

                return new ResponseInterfaceStructure
                {
                    ObjectKind = "Type",
                    ObjectType = info.ObjectType,
                    ObjectName = type.Name,
                    ObjectPath = typePath,
                    ObjectNamespace = type.Namespace,
                    Number = null,
                    ProgrammingLanguage = null,
                    MemoryLayout = info.MemoryLayout,
                    IsConsistent = type.IsConsistent,
                    InterfaceModifiedDate = type.InterfaceModifiedDate,
                    MemberCount = info.MemberCount,
                    Truncated = info.Truncated,
                    Sections = info.Sections,
                    Source = "type-xml-export",
                    Message = $"Structure of type '{type.Name}': {info.MemberCount} member(s) in {info.Sections.Count} section(s).{truncatedNote}",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["memberCount"] = info.MemberCount,
                        ["sectionCount"] = info.Sections.Count,
                        ["xmlBytes"] = xmlFile.Length
                    }
                };
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw MapInterfaceFailure("GetTypeStructure", typePath, ex);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>True when ListObjectChildren is being asked for the block/type interface.</summary>
        internal static bool IsInterfaceCollection(string objectKind, string collectionProperty)
        {
            if (!string.Equals(objectKind, "Block", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(objectKind, "Type", StringComparison.OrdinalIgnoreCase))
                return false;

            var p = (collectionProperty ?? string.Empty).Trim();
            return p.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Interface.Sections", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Interface.Members", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compatibility route for the old reflection tool: it used to return an empty list for
        /// Interface.Members because FB/FC in V19 have no Interface CLR property. Serve the same
        /// request from the interface reader instead of lying.
        /// </summary>
        internal static ResponseObjectChildren GetInterfaceChildren(
            string objectKind,
            string objectPath,
            string collectionProperty,
            string softwarePath,
            int limit)
        {
            var isType = string.Equals(objectKind, "Type", StringComparison.OrdinalIgnoreCase);
            // Parse the whole interface (up to the tool's hard cap), then apply the LISTING limit to the
            // children this route actually emits. Passing `limit` to the parser lets nested struct members
            // consume the budget, so a short but apparently complete list comes back.
            const int parseCap = 5000;
            var iface = isType
                ? GetTypeStructure(softwarePath, objectPath, includeComments: false, maxMembers: parseCap)
                : GetBlockInterface(softwarePath, objectPath, includeComments: false, maxMembers: parseCap);

            var sections = (iface.Sections ?? Enumerable.Empty<InterfaceSection>()).ToList();
            var items = new List<string>();
            var take = Math.Max(1, Math.Min(2000, limit));
            var isSections = collectionProperty.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                || collectionProperty.Equals("Interface.Sections", StringComparison.OrdinalIgnoreCase);
            var total = isSections ? sections.Count : sections.Sum(s => s.Members.Count());

            if (isSections)
            {
                foreach (var section in sections)
                {
                    items.Add(section.Name);
                    if (items.Count >= take) break;
                }
            }
            else
            {
                foreach (var section in sections)
                {
                    foreach (var member in section.Members)
                    {
                        items.Add(section.Name + "/" + member.Name);
                        if (items.Count >= take) break;
                    }
                    if (items.Count >= take) break;
                }
            }

            var truncated = iface.Truncated == true || total > items.Count;

            return new ResponseObjectChildren
            {
                Message = truncated
                    ? $"OK (truncated at limit={take}; {total} top-level item(s) available)."
                    : "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                Collection = collectionProperty,
                Items = items,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["count"] = items.Count,
                    ["total"] = total,
                    ["truncated"] = truncated,
                    ["sourceTruncated"] = iface.Truncated == true,
                    ["empty"] = items.Count == 0,
                    ["source"] = isType ? "type-xml-export" : "block-xml-export"
                }
            };
        }

        private static McpException MapInterfaceFailure(string tool, string objectPath, Exception ex)
        {
            if (ex is PortalException pex)
            {
                switch (pex.Code)
                {
                    case PortalErrorCode.NotFound:
                        return new McpException(
                            $"{tool}: '{objectPath}' not found. Resolve the exact path first " +
                            "(GetSoftwareTree / GetBlocksWithHierarchy for blocks; GetTypes for PLC data types).",
                            ex,
                            McpErrorCode.InvalidParams);

                    case PortalErrorCode.InvalidParams:
                    case PortalErrorCode.InvalidState:
                        return new McpException(pex.Message, ex, McpErrorCode.InvalidParams);

                    case PortalErrorCode.ExportFailed:
                    {
                        var reason = pex.InnerException?.Message?.Trim();
                        var text = $"{tool}: export failed for '{objectPath}'"
                                   + (string.IsNullOrEmpty(reason) ? "." : ": " + reason);
                        text += " Reading the structure uses the XML export: the object must be consistent " +
                                "(compile first) and the PLC offline (GoOffline first).";
                        return new McpException(text, ex, McpErrorCode.InternalError);
                    }
                }

                return new McpException($"{tool} failed for '{objectPath}': {pex.Message}", ex, McpErrorCode.InternalError);
            }

            return new McpException(
                $"{tool} failed for '{objectPath}': {ex.Message}{McpHints.Recovery(ex)}",
                ex,
                McpErrorCode.InternalError);
        }
    }
}
