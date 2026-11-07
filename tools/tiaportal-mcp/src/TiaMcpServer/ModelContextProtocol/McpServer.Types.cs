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
    // Partial: types. Extracted from McpServer.cs (god-file split); behavior unchanged.
    public static partial class McpServer
    {
        #region types

        [McpServerTool(Name = "GetTypeInfo"), Description("[L2][PLC-Software]Get a type info from the plc software. typePath is a path from GetTypes (TypePath), including types inside software units ('Software units/<unit>/<group>/<type>'). includeMembers=true also returns the declared fields (hierarchical path, data type, explicit start values, comment) read from an export of the type, with complete=false when part of the structure could not be projected - the raw XML (ExportType) stays the source of truth.")]
        public static ResponseTypeInfo GetTypeInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("typePath: defines the path in the project structure to the type")] string typePath,
            [Description("includeMembers: true = also export the type and return its declared fields (default false)")] bool includeMembers = false)
        {
            try
            {
                var type = Portal.GetType(softwarePath, typePath);
                if (type != null)
                {
                    var attributes = Helper.GetAttributeList(type);
                    JsonObject? members = null;
                    if (includeMembers)
                    {
                        members = ReadTypeMembers(softwarePath, typePath);
                    }

                    return new ResponseTypeInfo
                    {
                        Message = members != null && members["complete"]?.GetValue<bool>() == false
                            ? $"Type info retrieved from '{typePath}' in '{softwarePath}'; the field list is INCOMPLETE (see Members.issues)"
                            : $"Type info retrieved from '{typePath}' in '{softwarePath}'",
                        TypePath = typePath,
                        Members = members,
                        Name = type.Name,
                        TypeName = type.GetType().Name,
                        Namespace = type.Namespace,
                        IsConsistent = type.IsConsistent,
                        ModifiedDate = type.ModifiedDate,
                        IsKnowHowProtected = type.IsKnowHowProtected,
                        Attributes = attributes,
                        Description = type.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Type not found at '{typePath}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving type info from '{typePath}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        /// <summary>Export the type to a temp folder and project its fields (#62). Export failures surface as-is.</summary>
        private static JsonObject ReadTypeMembers(string softwarePath, string typePath)
        {
            (string TempDir, List<string> Paths)? exported;
            try
            {
                exported = Portal.ExportTypeToTemp(softwarePath, typePath);
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                // The type info itself is fine; only the field projection is unavailable (e.g. inconsistent type: compile first).
                var reason = (ex.InnerException?.Message ?? ex.Message).Trim();
                return new JsonObject { ["members"] = new JsonArray(), ["memberCount"] = 0, ["complete"] = false, ["issues"] = new JsonArray("export-failed: " + reason + McpHints.Recovery(ex)) };
            }
            if (exported == null || exported.Value.Paths.Count == 0)
            {
                return new JsonObject { ["members"] = new JsonArray(), ["memberCount"] = 0, ["complete"] = false, ["issues"] = new JsonArray("export-produced-no-file: the type export returned no XML, no fields could be read.") };
            }
            try
            {
                return TypeMemberReader.Read(File.ReadAllText(exported.Value.Paths[0]));
            }
            catch (IOException ex)
            {
                return new JsonObject { ["members"] = new JsonArray(), ["memberCount"] = 0, ["complete"] = false, ["issues"] = new JsonArray("export-read-failed: " + ex.Message) };
            }
            finally
            {
                try { Directory.Delete(exported.Value.TempDir, true); } catch { /* temp cleanup only */ }
            }
        }

        [McpServerTool(Name = "GetTypes"), Description("[L2][PLC-Software]Get a list of the user types of the plc software, including the types inside software units. Each item carries TypePath (pass it to GetTypeInfo / ExportType) and Owner ('PLC_1' or 'Software units/<unit>'). meta.complete=false when the software units could not be read (the list then covers the PLC root only).")]
        public static ResponseTypes GetTypes(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "")
        {
            try
            {
                var list = Portal.GetTypesWithPaths(softwarePath, regexName, out var unitFailure);

                // null = 根本没查成（没连接/没打开项目）；空列表 = 这个 PLC 里确实没有。
                // 以前 Portal 层无项目时返回空列表，两件事被同一个值表示，于是下面那句
                // `if (list != null)` 恒为真、`else throw` 永不执行，离线调用得到「成功，0 个」。
                if (list == null)
                {
                    throw new McpException(
                        $"No TIA project is open, cannot list types of '{softwarePath}'. "
                        + "Call Connect / OpenProject (or AttachToOpenProject) first. "
                        + "This does NOT mean the PLC has no types.",
                        McpErrorCode.InvalidParams);
                }

                var responseList = new List<ResponseTypeInfo>();
                foreach (var (typePath, owner, type) in list)
                {
                    if (type != null)
                    {
                        var attributes = Helper.GetAttributeList(type);

                        responseList.Add(new ResponseTypeInfo
                        {
                            TypePath = typePath,
                            Owner = owner,
                            Name = type.Name,
                            TypeName = type.GetType().Name,
                            Namespace = type.Namespace,
                            IsConsistent = type.IsConsistent,
                            ModifiedDate = type.ModifiedDate,
                            IsKnowHowProtected = type.IsKnowHowProtected,
                            Attributes = attributes,
                            Description = type.ToString()
                        });
                    }
                }

                if (list != null)
                {
                    return new ResponseTypes
                    {
                        Message = unitFailure == null
                            ? $"Types with regex '{regexName}' retrieved from '{softwarePath}' (PLC root and software units)"
                            : $"Types with regex '{regexName}' retrieved from the PLC root of '{softwarePath}' only - the software units could not be read ({unitFailure}); the list is INCOMPLETE",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = unitFailure == null,
                            ["complete"] = unitFailure == null,
                            ["scope"] = unitFailure == null ? "plcRootAndSoftwareUnits" : "plcRootOnly",
                            ["softwareUnitsFailure"] = unitFailure
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving user defined types with regex '{regexName}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving user defined types with regex '{regexName}' in '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportType"), Description("[L2][PLC-Software]Export a type from the plc software")]
        public static ResponseExportType ExportType(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the directory where to export the type; output file will be '<type name>.xml'")] string exportPath,
            [Description("typePath: defines the path in the project structure to the type")] string typePath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                var type = Portal.ExportType(softwarePath, typePath, exportPath, preservePath);
                if (type != null)
                {
                    return new ResponseExportType
                    {
                        Message = $"Type exported from '{typePath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting type from '{typePath}' to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                switch (pex.Code)
                {
                    case TiaMcpServer.Siemens.PortalErrorCode.NotFound:
                        throw new McpException(("Type not found." + BuildTypeDidYouMean(softwarePath, typePath)).Trim(), McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidState:
                        throw new McpException(pex.Message + "\n" + ExportErrorContext.Line("precheck", "Type", softwarePath, typePath, pex), McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidParams:
                        throw new McpException(pex.Message, McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.ExportFailed:
                        {
                            var reason = pex.InnerException?.Message?.Trim();
                            var msg = "Failed to export type.";
                            if (!string.IsNullOrEmpty(reason)) msg += $" Reason: {reason}";
                            Logger?.LogError(pex, "MCP ExportType failed for {SoftwarePath} {TypePath} -> {ExportPath}",
                                pex.Data?["softwarePath"], pex.Data?["typePath"], pex.Data?["exportPath"]);
                            msg += "\n" + ExportErrorContext.Line("export", "Type", softwarePath, typePath, pex);
                            throw new McpException(msg, McpErrorCode.InternalError);
                        }
                }
                throw new McpException(pex.Message, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting type from '{typePath}' to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Un-exposed from MCP (tool consolidation): use ExportType with a caller-chosen directory. Method kept for internal use.
        public static ResponseTempExport ExportTypeToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("typePath: full type path inside PLC software")] string typePath,
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportTypeToTemp(softwarePath, typePath, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Type exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException("Failed exporting type to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting type to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportType"), Description("[L1][PLC-Software]Import a type from file into the plc software")]
        public static ResponseImportType ImportType(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the type")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the type")] string importPath)
        {
            try
            {
                Portal.ImportType(softwarePath, groupPath, importPath);
                return new ResponseImportType
                {
                    Message = $"Type imported from '{importPath}' to '{groupPath}'",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing type from '{importPath}' to '{groupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing type from '{importPath}' to '{groupPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SeedProjectFromReference"), Description("[L2][PLC-Software]Seed PLC blocks/types and HMI screens/tagtables from a reference directory (manifest.json + {{PLACEHOLDER}} replace)")]
        public static ResponseSeed SeedProjectFromReference(
            [Description("plcSoftwarePath: path in the project structure to the PLC software")] string plcSoftwarePath,
            [Description("hmiSoftwarePath: path in the project structure to the HMI software")] string hmiSoftwarePath,
            [Description("referenceDir: directory containing manifest.json and subfolders (plc/blocks, plc/types, hmi/screens, hmi/tags)")] string referenceDir,
            [Description("placeholders: JsonObject key-values for replacement in XML, e.g. {\"PLC_NAME\":\"PLC_1\"}")] JsonObject? placeholders = null)
        {
            try
            {
                var res = Portal.SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders);
                res.Meta ??= new JsonObject();
                res.Meta["timestamp"] = DateTime.Now;
                res.Meta["success"] = (res.Failed == null || !res.Failed.Any());
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error seeding project from reference: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTypes"), Description("[L2][PLC-Software]Export types from the plc software to path")]
        public static async Task<ResponseExportTypes> ExportTypes(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the types")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;
            
            try
            {
                // First, get the list of types to determine total count
                Logger?.LogInformation($"Starting export of types from '{softwarePath}' to '{exportPath}'");
                
                var allTypes = await Task.Run(() => Portal.GetTypes(softwarePath, regexName));
                var totalTypes = allTypes?.Count ?? 0;

                if (totalTypes == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No types found to export",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportTypes
                    {
                        Message = $"No types found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseTypeInfo>(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalTypes"] = 0,
                            ["exportedTypes"] = 0,
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
                        Total = totalTypes,
                        Message = $"Starting export of {totalTypes} types...",
                        progressToken
                    });
                }

                // Export types asynchronously
                var exportedTypes = await Task.Run(() => Portal.ExportTypes(softwarePath, exportPath, regexName, preservePath));

                // Build list of inconsistent (skipped) types for reporting
                var inconsistentTypeInfos = new List<ResponseTypeInfo>();
                if (allTypes != null)
                {
                    foreach (var t in allTypes)
                    {
                        if (t != null && t.IsConsistent == false)
                        {
                            var attrs = Helper.GetAttributeList(t);
                            inconsistentTypeInfos.Add(new ResponseTypeInfo
                            {
                                Name = t.Name,
                                TypeName = t.GetType().Name,
                                Namespace = t.Namespace,
                                IsConsistent = t.IsConsistent,
                                ModifiedDate = t.ModifiedDate,
                                IsKnowHowProtected = t.IsKnowHowProtected,
                                Attributes = attrs,
                                Description = t.ToString()
                            });
                        }
                    }
                }
                
                // Send progress update after export completion
                if (exportedTypes != null && progressToken != null)
                {
                    var exportedCount = exportedTypes.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalTypes,
                        Message = $"Exported {exportedCount} of {totalTypes} types",
                        progressToken
                    });
                }

                if (exportedTypes != null)
                {
                    var responseList = new List<ResponseTypeInfo>();
                    var processedCount = 0;
                    
                    foreach (var type in exportedTypes)
                    {
                        if (type != null)
                        {
                            var attributes = Helper.GetAttributeList(type);

                            responseList.Add(new ResponseTypeInfo
                            {
                                Name = type.Name,
                                TypeName = type.GetType().Name,
                                Namespace = type.Namespace,
                                IsConsistent = type.IsConsistent,
                                ModifiedDate = type.ModifiedDate,
                                IsKnowHowProtected = type.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = type.ToString()
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
                            Total = totalTypes,
                            Message = $"Export completed: {processedCount} types exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Type export completed: {processedCount} types exported in {duration:F2} seconds");

                    return new ResponseExportTypes
                    {
                        Message = $"Export completed: {processedCount} types with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Inconsistent = inconsistentTypeInfos,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalTypes"] = totalTypes,
                            ["exportedTypes"] = processedCount,
                            ["inconsistentTypes"] = inconsistentTypeInfos.Count,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting types '{regexName}' from '{softwarePath}' to {exportPath}", McpErrorCode.InternalError);
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
                            Message = $"Type export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
                        // Ignore notification errors during error handling
                    }
                }
                
                Logger?.LogError(ex, $"Failed exporting types '{regexName}' from '{softwarePath}' to {exportPath}");
                throw new McpException($"Unexpected error exporting types '{regexName}' from '{softwarePath}' to {exportPath}: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Un-exposed from MCP (tool consolidation): use ExportTypes with a caller-chosen directory. Method kept for internal use.
        public static ResponseTempExport ExportTypesToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("regexName: optional regex filter")] string regexName = "",
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportTypesToTemp(softwarePath, regexName, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Types exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }
                throw new McpException("Failed exporting types to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting types to temp: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
