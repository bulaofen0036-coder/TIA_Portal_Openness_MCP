using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: blocks. Extracted from McpServer.cs (god-file split); behavior unchanged.
    public static partial class McpServer
    {
        #region blocks

        [McpServerTool(Name = "GetBlockInfo"), Description("[L2][PLC-Software] Get detailed info for one block (attributes, language, number, modification time). Requires: Connect + OpenProject. blockPath must be fully qualified: 'Group/Subgroup/BlockName' — get it from GetSoftwareTree or GetBlocksWithHierarchy. Returns: IsConsistent (false = must compile before export).")]
        public static ResponseBlockInfo GetBlockInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath)
        {
            try
            {
                var block = Portal.GetBlock(softwarePath, blockPath);
                if (block != null)
                {
                    var attributes = Helper.GetAttributeList(block);

                    return new ResponseBlockInfo
                    {
                        Message = $"Block info retrieved from '{blockPath}' in '{softwarePath}'",
                        Name = block.Name,
                        TypeName = block.GetType().Name,
                        Namespace = block.Namespace,
                        ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage),block.ProgrammingLanguage),
                        MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                        IsConsistent = block.IsConsistent,
                        HeaderName = block.HeaderName,
                        ModifiedDate = block.ModifiedDate,
                        IsKnowHowProtected = block.IsKnowHowProtected,
                        Attributes = attributes,
                        Description = block.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Block not found at '{blockPath}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving block info from '{blockPath}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetBlocks"), Description("[L2][PLC-Software] Get a flat list of all blocks in PLC software. Requires: Connect + OpenProject. Use GetBlocksWithHierarchy instead when you need group/folder paths for ExportBlock. Returns: block name, number, type (OB/FC/FB/GlobalDB/InstanceDB), programming language.")]
        public static ResponseBlocks GetBlocks(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "")
        {
            try
            {
                var list = Portal.GetBlocks(softwarePath, regexName);

                var responseList = new List<ResponseBlockInfo>();
                foreach (var block in list)
                {
                    if (block != null)
                    {
                        var attributes = Helper.GetAttributeList(block);

                        responseList.Add(new ResponseBlockInfo
                        {
                            Name = block.Name,
                            TypeName = block.GetType().Name,
                            Namespace = block.Namespace,
                            ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                            MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                            IsConsistent = block.IsConsistent,
                            HeaderName = block.HeaderName,
                            ModifiedDate = block.ModifiedDate,
                            IsKnowHowProtected = block.IsKnowHowProtected,
                            Attributes = attributes,
                            Description = block.ToString()
                        });
                    }
                }

                if (list != null)
                {
                    return new ResponseBlocks
                    {
                        Message = $"Blocks with regex '{regexName}' retrieved from '{softwarePath}'",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving blocks with regex '{regexName}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving blocks with regex '{regexName}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetBlocksWithHierarchy"), Description("[L2][PLC-Software]Get a list of all blocks with their group hierarchy from the plc software.")]
        public static ResponseBlocksWithHierarchy GetBlocksWithHierarchy(
        [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var rootGroup = Portal.GetBlockRootGroup(softwarePath);
                if (rootGroup != null)
                {
                    var hierarchy = Helper.BuildBlockHierarchy(rootGroup);
                    return new ResponseBlocksWithHierarchy
                    {
                        Message = $"Block hierarchy retrieved from '{softwarePath}'",
                        Root = hierarchy,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    // Specific failure: root group could not be resolved
                    throw new McpException($"Block root group not found for '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Generic unexpected failure wrapper
                throw new McpException($"Unexpected error retrieving block hierarchy for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }



        [McpServerTool(Name = "ExportBlock"), Description("[L2][PLC-Software] Export one block to an XML file. Requires: Connect + OpenProject + block must be consistent (compile first if IsConsistent=false). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree — bare names return InvalidParams with suggestions. Pick the right tool: batch → ExportBlocks; readable SCL/.s7dcl text → ExportAsDocuments.")]
        public static ResponseExportBlock ExportBlock(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: full path to the block in the project structure, e.g. 'Group/Subgroup/Name' (single names are ambiguous)")] string blockPath,
            [Description("exportPath: defines the path where to export the block")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                var block = Portal.ExportBlock(softwarePath, blockPath, exportPath, preservePath);
                if (block != null)
                {
                    return new ResponseExportBlock
                    {
                        Message = $"Block exported from '{blockPath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                // Should not be reachable because Portal.ExportBlock throws on failure
                throw new McpException($"Failed exporting block from '{blockPath}' to '{exportPath}'", McpErrorCode.InternalError);
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                // Map known portal errors to sharper MCP errors and messages.
                switch (pex.Code)
                {
                    case TiaMcpServer.Siemens.PortalErrorCode.NotFound:
                        {
                            var msg = ("Block not found." + BuildBlockDidYouMean(softwarePath, blockPath)).Trim();
                            throw new McpException(msg, McpErrorCode.InvalidParams);
                        }

                    case TiaMcpServer.Siemens.PortalErrorCode.ExportFailed:
                        {
                            // Relay underlying portal error with concise reason; log full details
                            var reason = pex.InnerException?.Message?.Trim();
                            var msg = "Failed to export block.";
                            if (!string.IsNullOrEmpty(reason)) msg += $" Reason: {reason}";

                            Logger?.LogError(pex, "MCP ExportBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}",
                                pex.Data?["softwarePath"], pex.Data?["blockPath"], pex.Data?["exportPath"]);

                            throw new McpException(msg, McpErrorCode.InternalError);
                        }

                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidParams:
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidState:
                        {
                            throw new McpException(pex.Message, McpErrorCode.InvalidParams);
                        }
                }

                // Fallback
                throw new McpException(pex.Message, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting block from '{blockPath}' to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Un-exposed from MCP (tool consolidation): use ExportBlock with a caller-chosen directory. Method kept for internal use.
        public static ResponseTempExport ExportBlockToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("blockPath: full block path inside PLC software")] string blockPath,
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportBlockToTemp(softwarePath, blockPath, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Block exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException("Failed exporting block to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting block to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static string BuildBlockPathSuggestion(string softwarePath, string blockPath)
        {
            if (string.IsNullOrEmpty(blockPath) || blockPath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(blockPath);
                var blocks = Portal.GetBlocks(softwarePath, $"^{escaped}$");
                if (blocks == null || blocks.Count == 0)
                {
                    blocks = Portal.GetBlocks(softwarePath, escaped);
                }

                var candidates = blocks
                    .Take(10)
                    .Select(b =>
                    {
                        var name = b.Name;
                        var parts = new List<string> { name };
                        var parent = b.Parent;
                        while (parent != null)
                        {
                            if (parent is PlcBlockSystemGroup) break;
                            if (parent is PlcBlockGroup grp)
                            {
                                parts.Insert(0, grp.Name);
                                parent = grp.Parent;
                            }
                            else break;
                        }
                        if (parts.Count > 1) parts.RemoveAt(0);
                        return string.Join("/", parts);
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return candidates.Count > 0 ? $" Did you mean: {string.Join(", ", candidates)}?" : string.Empty;
            }
            catch
            {
                return string.Empty; // best effort only
            }
        }

        private static string BestEffortSuggestGroupPath(string softwarePath, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return string.Empty;

            try
            {
                // Suggest existing group paths based on blocks' parent groups (best effort).
                var blocks = Portal.GetBlocks(softwarePath, "");
                if (blocks == null) return string.Empty;

                var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in blocks.Take(300))
                {
                    var parent = b?.Parent;
                    var parts = new List<string>();
                    while (parent != null)
                    {
                        if (parent is PlcBlockSystemGroup) break;
                        if (parent is PlcBlockGroup grp)
                        {
                            parts.Insert(0, grp.Name);
                            parent = grp.Parent;
                        }
                        else break;
                    }
                    if (parts.Count > 0)
                        groups.Add(string.Join("/", parts));
                }

                var key = groupPath.Trim().Trim('/').ToLowerInvariant();
                var candidates = groups
                    .Where(g => g.ToLowerInvariant().Contains(key) || key.Contains(g.ToLowerInvariant()))
                    .Take(10)
                    .ToList();

                return candidates.Count > 0 ? $" Did you mean groupPath: {string.Join(", ", candidates)}?" : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        [McpServerTool(Name = "ImportBlock"), Description("[L1][PLC-Software] Import a single SimaticML XML block file into PLC software. Requires: Connect + OpenProject. importPath must be an absolute path to a .xml file. After import it reads back to confirm the block is present (Meta.verified); call CompileAndDiagnosePlc for full consistency. Pick the right tool: SCL/.s7dcl text → ImportFromDocuments; multiple XML files → ImportBlocksFromDirectory; a full exported program (UDTs+tags+blocks) → ImportPlcProgramFromDirectory; JSON-built blocks → PlcBuildAndImport.")]
        public static ResponseImportBlock ImportBlock(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the block")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the block")] string importPath)
        {
            try
            {
                Portal.ImportBlock(softwarePath, groupPath, importPath);

                // Read-back verification: confirm the block landed (name inferred from the XML file name).
                bool verified = false;
                string verifyDetail;
                try
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(importPath);
                    var escaped = Regex.Escape(name);
                    var found = Portal.GetBlocks(softwarePath, $"^{escaped}$");
                    if (found == null || found.Count == 0) found = Portal.GetBlocks(softwarePath, escaped);
                    verified = found != null && found.Count > 0;
                    verifyDetail = verified
                        ? $"block '{name}' present after import"
                        : $"block '{name}' NOT found after import (the XML's block name may differ from the file name)";
                }
                catch (Exception vex) { verifyDetail = "readback skipped: " + vex.Message; }

                return new ResponseImportBlock
                {
                    Message = $"Block imported from '{importPath}' to '{groupPath}'" + (verified ? " (verified)" : ""),
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["verified"] = verified,
                        ["verifyDetail"] = verifyDetail
                    }
                };
            }
            catch (PortalException pex) when (pex.Code == PortalErrorCode.NotFound)
            {
                var hint = BestEffortSuggestGroupPath(softwarePath, groupPath);
                throw new McpException($"{pex.Message}{hint}", McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Failed importing block from '{importPath}' to '{groupPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportBlocksFromDirectory"), Description("[L2][PLC-Software] Batch import PLC block .xml (SimaticML) files from a directory into a block group. Pick the right tool: SCL/.s7dcl text → ImportBlocksFromDocuments; a full mixed program with UDTs+tag tables+blocks auto-ordered → ImportPlcProgramFromDirectory; a single XML file → ImportBlock.")]
        public static ResponseImportBatch ImportBlocksFromDirectory(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import blocks")] string groupPath,
            [Description("dir: directory that contains block .xml files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override, false=Rename")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} blocks from '{dir}' into '{groupPath}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing blocks from '{dir}' to '{groupPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcProgramFromDirectory"), Description("[L2][PLC-Software] HIGH-LEVEL batch import tool. Recursively scans a directory for PLC XML files, auto-classifies them as UDT/TagTable/Block, imports in correct dependency order (UDTs first, then tag tables, then blocks), and optionally compiles. Requires: Connect + OpenProject. Best for importing a full exported PLC program or a set of generated XML blocks.")]
        public static ResponsePlcProgramImport ImportPlcProgramFromDirectory(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("sourceDir: root directory containing exported PLC XML files")] string sourceDir,
            [Description("typeGroupPath: PLC data type group path; use empty for root")] string typeGroupPath = "",
            [Description("tagFolderPath: PLC tag table group path; use empty for root")] string tagFolderPath = "",
            [Description("technologyFolderPath: PLC technology object group path; use empty for root")] string technologyFolderPath = "",
            [Description("blockGroupPath: PLC block group path; use empty for root Program blocks")] string blockGroupPath = "",
            [Description("regexName: optional regex filter applied to file name without extension")] string regexName = "",
            [Description("compileAfter: compile PLC software after imports")] bool compileAfter = true,
            [Description("stopOnImportFailure: skip remaining imports after first import failure")] bool stopOnImportFailure = false,
            [Description("dryRun: only classify and return discovered objects; do not import or compile")] bool dryRun = false)
        {
            var importedTypes = new List<string>();
            var importedTagTables = new List<string>();
            var importedTechnologyObjects = new List<string>();
            var importedBlocks = new List<string>();
            var failed = new List<ImportFailure>();
            ResponseCompile? compile = null;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    failed.Add(new ImportFailure { Path = sourceDir, Error = "Directory not found" });
                    return BuildPlcProgramImportResponse(sourceDir, dryRun, new List<string>(), new List<string>(), new List<string>(), new List<string>(), importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }

                var files = Directory.GetFiles(sourceDir, "*.xml", SearchOption.AllDirectories)
                    .Where(f => regex == null || regex.IsMatch(Path.GetFileNameWithoutExtension(f)))
                    .Select(f =>
                    {
                        var kind = ClassifyPlcXml(f, out var subKind, out var objectName);
                        return new { File = f, Kind = kind, SubKind = subKind, ObjectName = objectName };
                    })
                    .Where(x => x.Kind != "unknown")
                    .GroupBy(x => $"{x.Kind}:{x.ObjectName}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(x => x.File.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                                  .ThenBy(x => x.File.Length)
                                  .First())
                    .ToList();

                var discoveredTypes = files.Where(x => x.Kind == "type").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredTagTables = files.Where(x => x.Kind == "tagtable").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredTechnologyObjects = files.Where(x => x.Kind == "technology").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredBlocks = files.Where(x => x.Kind == "block").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                if (dryRun)
                {
                    return BuildPlcProgramImportResponse(sourceDir, true, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                foreach (var item in files.Where(x => x.Kind == "type").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportType(softwarePath, typeGroupPath, item.File);
                        importedTypes.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "tagtable").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportPlcTagTable(softwarePath, tagFolderPath, item.File);
                        importedTagTables.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "technology").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportTechnologyObject(softwarePath, technologyFolderPath, item.File);
                        importedTechnologyObjects.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "block")
                                          .OrderBy(x => GetBlockImportOrder(x.SubKind))
                                          .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportBlock(softwarePath, blockGroupPath, item.File);
                        importedBlocks.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                if (compileAfter && !(stopOnImportFailure && failed.Any()))
                {
                    try
                    {
                        var result = Portal.CompileSoftware(softwarePath);
                        var collected = CollectCompilerMessages(result.Messages);
                        compile = new ResponseCompile
                        {
                            Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                            State = result.State.ToString(),
                            ErrorCount = result.ErrorCount,
                            WarningCount = result.WarningCount,
                            Messages = collected.Raw,
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase),
                                ["errorDetailCount"] = collected.Errors.Count,
                                ["warningDetailCount"] = collected.Warnings.Count
                            }
                        };
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = softwarePath, Error = $"[{pex.Code}] {pex.Message}" });
                    }
                }

                return BuildPlcProgramImportResponse(sourceDir, false, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                failed.Add(new ImportFailure { Path = sourceDir, Error = ex.ToString() });
                return BuildPlcProgramImportResponse(sourceDir, dryRun, new List<string>(), new List<string>(), new List<string>(), new List<string>(), importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            }
        }

        [McpServerTool(Name = "CompileAndDiagnosePlc"), Description("[L1][PLC-Software] PREFERRED compile tool. Compiles PLC and returns structured errors/warnings by recursively walking CompilerResult.Messages (V20/V21 PublicAPI). Leaf diagnostics include Path + Description; optional Line/Column via GetAttribute when exposed. Requires: Connect + OpenProject.")]
        public static ResponseCompileDiagnose CompileAndDiagnosePlc(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("password: optional safety password")] string password = "")
        {
            try
            {
                var result = Portal.CompileSoftware(softwarePath, password);

                var raw = new List<string>();
                var errs = new List<string>();
                var warns = new List<string>();
                var info = new List<string>();

                try
                {
                    var collected = CollectCompilerMessages(result.Messages);
                    raw = collected.Raw;
                    errs = collected.Errors;
                    warns = collected.Warnings;
                    info = collected.Info;
                }
                catch { }

                return new ResponseCompileDiagnose
                {
                    Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                    State = result.State.ToString(),
                    ErrorCount = result.ErrorCount,
                    WarningCount = result.WarningCount,
                    Errors = errs,
                    Warnings = warns,
                    Info = info,
                    RawMessages = raw,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase),
                        ["errorDetailCount"] = errs.Count,
                        ["warningDetailCount"] = warns.Count
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RepairAndReimportBlock"), Description("[L2][PLC-Software]Try import a block XML; if compile fails, return diagnostics and best-effort suggestions (no destructive actions).")]
        public static ResponseRepairAndCompile RepairAndReimportBlock(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: block XML path")] string importPath,
            [Description("groupPath: block group path; use empty for root Program blocks")] string groupPath = "",
            [Description("compileAfter: compile PLC after import")] bool compileAfter = true)
        {
            var suggestions = new List<string>();
            try
            {
                // ImportBlock 现以 PortalException 报失败；成功即已导入
                Portal.ImportBlock(softwarePath, groupPath, importPath);

                ResponseCompileDiagnose? compile = null;
                if (compileAfter)
                {
                    compile = CompileAndDiagnosePlc(softwarePath);
                    if (compile.Meta?["success"]?.GetValue<bool>() == false)
                    {
                        suggestions.Add("If errors mention missing symbols, ensure PLC tag table/UDTs are imported before blocks.");
                        suggestions.Add("If block/type is inconsistent, compile PLC software once to update consistency before exporting.");
                    }
                }

                return new ResponseRepairAndCompile
                {
                    Message = "Imported (best-effort) and compiled.",
                    Imported = true,
                    ImportError = null,
                    Compile = compile,
                    Suggestions = suggestions,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false) }
                };
            }
            catch (PortalException pex)
            {
                // 导入失败：返回诊断而非抛出（本工具契约是给出修复建议）
                suggestions.Add("If groupPath is wrong, retry with empty groupPath for root Program blocks.");
                return new ResponseRepairAndCompile
                {
                    Message = "Import failed.",
                    Imported = false,
                    ImportError = $"[{pex.Code}] {pex.Message}",
                    Compile = null,
                    Suggestions = suggestions,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error repairing/reimporting block '{importPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponsePlcProgramImport BuildPlcProgramImportResponse(
            string sourceDir,
            bool dryRun,
            List<string> discoveredTypes,
            List<string> discoveredTagTables,
            List<string> discoveredTechnologyObjects,
            List<string> discoveredBlocks,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedTechnologyObjects,
            List<string> importedBlocks,
            List<ImportFailure> failed,
            ResponseCompile? compile)
        {
            var compileOk = compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false);
            var success = failed.Count == 0 && compileOk;
            return new ResponsePlcProgramImport
            {
                Message = dryRun
                    ? $"PLC program dry-run from '{sourceDir}': types={discoveredTypes.Count}, tagTables={discoveredTagTables.Count}, technologyObjects={discoveredTechnologyObjects.Count}, blocks={discoveredBlocks.Count}, failed={failed.Count}"
                    : $"PLC program import from '{sourceDir}': types={importedTypes.Count}, tagTables={importedTagTables.Count}, technologyObjects={importedTechnologyObjects.Count}, blocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}",
                DryRun = dryRun,
                DiscoveredTypes = discoveredTypes,
                DiscoveredTagTables = discoveredTagTables,
                DiscoveredTechnologyObjects = discoveredTechnologyObjects,
                DiscoveredBlocks = discoveredBlocks,
                ImportedTypes = importedTypes,
                ImportedTagTables = importedTagTables,
                ImportedTechnologyObjects = importedTechnologyObjects,
                ImportedBlocks = importedBlocks,
                Failed = failed,
                Compile = compile,
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = success }
            };
        }

        private static string ClassifyPlcXml(string file, out string subKind, out string objectName)
        {
            subKind = "";
            objectName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = XDocument.Load(file);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase));

                var local = obj?.Name.LocalName ?? "";
                subKind = local;
                var name = obj?.Element("AttributeList")?.Element("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    objectName = name!.Trim();
                }
                if (local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase)) return "type";
                if (string.Equals(local, "SW.Tags.PlcTagTable", StringComparison.OrdinalIgnoreCase)) return "tagtable";
                if (local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)) return "block";
                if (local.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase)) return "technology";
            }
            catch
            {
                subKind = "";
            }

            return "unknown";
        }

        private static int GetBlockImportOrder(string subKind)
        {
            return subKind switch
            {
                "SW.Blocks.GlobalDB" => 10,
                "SW.Blocks.FC" => 20,
                "SW.Blocks.FB" => 20,
                "SW.Blocks.InstanceDB" => 30,
                "SW.Blocks.OB" => 40,
                _ => 50
            };
        }

        [McpServerTool(Name = "ExportBlocks"), Description("[L2][PLC-Software] Export all (or regexName-filtered) blocks to a directory as SimaticML XML. Pick the right tool: readable SCL/.s7dcl text → ExportBlocksAsDocuments; a single block → ExportBlock.")]
        public static async Task<ResponseExportBlocks> ExportBlocks(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the blocks")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;
            
            try
            {
                // First, get the list of blocks to determine total count
                Logger?.LogInformation($"Starting export of blocks from '{softwarePath}' to '{exportPath}'");
                
                var allBlocks = await Task.Run(() => Portal.GetBlocks(softwarePath, regexName));
                var totalBlocks = allBlocks?.Count ?? 0;

                if (totalBlocks == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No blocks found to export",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportBlocks
                    {
                        Message = $"No blocks found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseBlockInfo>(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = 0,
                            ["exportedBlocks"] = 0,
                            ["duration"] = (DateTime.Now - startTime).TotalSeconds
                        }
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalBlocks,
                        Message = $"Starting export of {totalBlocks} blocks...",
                        progressToken
                    });
                }

                // Export blocks asynchronously
                var exportedBlocks = await Task.Run(() => Portal.ExportBlocks(softwarePath, exportPath, regexName, preservePath));

                // Build list of inconsistent (skipped) blocks for reporting
                var inconsistentInfos = new List<ResponseBlockInfo>();
                if (allBlocks != null)
                {
                    foreach (var b in allBlocks)
                    {
                        if (b != null && b.IsConsistent == false)
                        {
                            var attrs = Helper.GetAttributeList(b);
                            inconsistentInfos.Add(new ResponseBlockInfo
                            {
                                Name = b.Name,
                                TypeName = b.GetType().Name,
                                Namespace = b.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), b.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), b.MemoryLayout),
                                IsConsistent = b.IsConsistent,
                                HeaderName = b.HeaderName,
                                ModifiedDate = b.ModifiedDate,
                                IsKnowHowProtected = b.IsKnowHowProtected,
                                Attributes = attrs,
                                Description = b.ToString()
                            });
                        }
                    }
                }
                
                // Send progress update after export completion
                if (exportedBlocks != null && progressToken != null)
                {
                    var exportedCount = exportedBlocks.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalBlocks,
                        Message = $"Exported {exportedCount} of {totalBlocks} blocks",
                        progressToken
                    });
                }

                if (exportedBlocks != null)
                {
                    var responseList = new List<ResponseBlockInfo>();
                    var processedCount = 0;
                    
                    foreach (var block in exportedBlocks)
                    {
                        if (block != null)
                        {
                            var attributes = Helper.GetAttributeList(block);

                            responseList.Add(new ResponseBlockInfo
                            {
                                Name = block.Name,
                                TypeName = block.GetType().Name,
                                Namespace = block.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                                IsConsistent = block.IsConsistent,
                                HeaderName = block.HeaderName,
                                ModifiedDate = block.ModifiedDate,
                                IsKnowHowProtected = block.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = block.ToString()
                            });
                        }
                        processedCount++;
                    }

                    // Send final progress notification
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processedCount,
                            Total = totalBlocks,
                            Message = $"Export completed: {processedCount} blocks exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Export completed: {processedCount} blocks exported in {duration:F2} seconds");

                    return new ResponseExportBlocks
                    {
                        Message = $"Export completed: {processedCount} blocks with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Inconsistent = inconsistentInfos,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = totalBlocks,
                            ["exportedBlocks"] = processedCount,
                            ["inconsistentBlocks"] = inconsistentInfos.Count,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Send error progress notification if we have a progress token
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
                        // Ignore notification errors during error handling
                    }
                }
                
                Logger?.LogError(ex, $"Failed exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}");
                throw new McpException($"Unexpected error exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Un-exposed from MCP (tool consolidation): use ExportBlocks with a caller-chosen directory. Method kept (used internally by CLI self-test).
        public static ResponseTempExport ExportBlocksToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("regexName: optional regex filter")] string regexName = "",
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportBlocksToTemp(softwarePath, regexName, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Blocks exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }
                throw new McpException("Failed exporting blocks to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting blocks to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
