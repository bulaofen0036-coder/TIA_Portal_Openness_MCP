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
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: documents. Extracted from McpServer.cs (god-file split); behavior unchanged.
    public static partial class McpServer
    {
        #region documents

        /// <summary>
        /// 扫描到了 .s7dcl 但一份都没导进去时附给调用方的自救步骤。
        /// 只引用本版本真实存在的工具，别指向不存在的东西再把人带偏一次。
        /// </summary>
        internal const string DocumentImportHelp =
            "\r\n按这个顺序处理，别改语法瞎猜：\r\n"
            + "1) GetAuthoringGuide(topic:'lad')（SCL 用 'scl'）—— 拿到本引擎验证过的语法与编码规则，"
            + "把你的文件逐条对齐；只改名字和操作数，别改结构。\r\n"
            + "2) .s7dcl 是**原子失败**：整份文档任何一处不合法都整份不导入，Openness 不给行号。"
            + "所以一次只放一个 NETWORK，导入→编译通过→再加下一段。整份写完再导，出错时没有任何定位信息。\r\n"
            + "3) 一个 NETWORK = 一个程序段；同一个 NETWORK 里的多条 RUNG 是同一段的并联分支。"
            + "要 12 段就写 12 个 NETWORK。\r\n"
            + "4) 编码必须 UTF-8 **带 BOM**。别把 BOM 的转义序列当成六个字符写进文件正文 —— "
            + "那是转义没生效，不是 BOM。\r\n"
            + "5) 只需要 DB / UDT / 变量表的话，改走 PlcBuildAndImport 的 JSON 路，完全绕开 .s7dcl。";

        [McpServerTool(Name = "ExportAsDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for exporting one block. Exports a single program block to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML (ExportBlock). Requires TIA Portal V20 or newer.")]
        public static ResponseExportAsDocuments ExportAsDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ExportAsDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                if (WithAutoOffline(() => Portal.ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath)))
                {
                    return new ResponseExportAsDocuments
                    {
                        Message = $"Documents exported from '{blockPath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting documents from '{blockPath}' to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting documents from '{blockPath}' to '{exportPath}': {ex}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportBlocksAsDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for batch export. Exports multiple program blocks to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML. Requires TIA Portal V20 or newer.")]
        public static async Task<ResponseExportBlocksAsDocuments> ExportBlocksAsDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;
            
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ExportBlocksAsDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                // First, get the list of blocks to determine total count
                Logger?.LogInformation($"Starting export of blocks as documents from '{softwarePath}' to '{exportPath}'");
                
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
                            Message = "No blocks found to export as documents",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportBlocksAsDocuments
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
                        Message = $"Starting export of {totalBlocks} blocks as documents...",
                        progressToken
                    });
                }

                // Export blocks as documents asynchronously
                var exportedBlocks = await Task.Run(() => Portal.ExportBlocksAsDocuments(softwarePath, exportPath, regexName, preservePath));
                
                // Send progress update after export completion
                if (exportedBlocks != null && progressToken != null)
                {
                    var exportedCount = exportedBlocks.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalBlocks,
                        Message = $"Exported {exportedCount} of {totalBlocks} blocks as documents",
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
                            Message = $"Document export completed: {processedCount} blocks exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Document export completed: {processedCount} blocks exported in {duration:F2} seconds");

                    // Surface per-block skip/failure reasons so a "matched N but exported 0" is never silent.
                    var failures = Portal.LastExportAsDocumentsFailures;
                    var skipped = totalBlocks - processedCount;
                    var msg = $"Document export completed: {processedCount}/{totalBlocks} blocks (regex '{regexName}') from '{softwarePath}' to '{exportPath}'";
                    if (skipped > 0)
                    {
                        var reason = (failures != null && failures.Count > 0)
                            ? string.Join("; ", failures)
                            : "no reason captured (inconsistent block? compile first, or the block type/language may not support SIMATIC SD export — try ExportBlock for XML)";
                        msg += $". {skipped} not exported: {reason}";
                    }

                    var meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = processedCount > 0 || totalBlocks == 0,
                        ["totalBlocks"] = totalBlocks,
                        ["exportedBlocks"] = processedCount,
                        ["skippedBlocks"] = skipped,
                        ["duration"] = duration
                    };
                    if (failures != null && failures.Count > 0)
                    {
                        var arr = new JsonArray();
                        foreach (var f in failures) arr.Add(f);
                        meta["failures"] = arr;
                    }

                    return new ResponseExportBlocksAsDocuments
                    {
                        Message = msg,
                        Items = responseList,
                        Meta = meta
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting documents to '{exportPath}'", McpErrorCode.InternalError);
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
                            Message = $"Document export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
                        // Ignore notification errors during error handling
                    }
                }
                
                Logger?.LogError(ex, $"Failed exporting documents to '{exportPath}'");
                throw new McpException($"Unexpected error exporting documents to '{exportPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportFromDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for importing one block. Imports a single program block from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer. After import it reads back to confirm the block is present (Meta.verified).")]
        public static ResponseImportFromDocuments ImportFromDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the block should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("fileNameWithoutExtension: name of the block file without extension") ] string fileNameWithoutExtension,
            [Description("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ImportFromDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                var option = ParseImportDocumentOption(importOption);

                // Pre-check .s7res for missing en-US tags
                var warnings = new JsonArray();
                try
                {
                    var missingIds = GetResMissingEnUsIds(importPath, fileNameWithoutExtension);
                    if (missingIds != null && missingIds.Count > 0)
                    {
                        Logger?.LogWarning($".s7res for '{fileNameWithoutExtension}' missing en-US tags for {missingIds.Count} items: {string.Join(", ", missingIds)}");
                        warnings.Add(new JsonObject
                        {
                            ["name"] = fileNameWithoutExtension,
                            ["missingEnUsIds"] = new JsonArray(missingIds.Select(id => (JsonNode)id).ToArray())
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Never silently swallow: a pre-check that cannot run must say so,
                    // otherwise "no warnings" is indistinguishable from "check crashed".
                    Logger?.LogWarning(ex, "Failed to evaluate .s7res warnings");
                    warnings.Add(new JsonObject
                    {
                        ["name"] = fileNameWithoutExtension,
                        ["precheckError"] = ex.Message
                    });
                }

                var ok = WithAutoOffline(() => Portal.ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, option));
                if (ok)
                {
                    // Read-back verification: confirm the block is actually present after import.
                    // Wrapped so a verification hiccup never masks a successful import.
                    bool verified = false;
                    string verifyDetail;
                    try
                    {
                        var escaped = Regex.Escape(fileNameWithoutExtension);
                        var found = Portal.GetBlocks(softwarePath, $"^{escaped}$");
                        if (found == null || found.Count == 0) found = Portal.GetBlocks(softwarePath, escaped);
                        verified = found != null && found.Count > 0;
                        verifyDetail = verified
                            ? $"block '{fileNameWithoutExtension}' present after import"
                            : $"block '{fileNameWithoutExtension}' NOT found after import — check name/group";
                    }
                    catch (Exception vex) { verifyDetail = "readback skipped: " + vex.Message; }

                    return new ResponseImportFromDocuments
                    {
                        Message = $"Imported '{fileNameWithoutExtension}' from '{importPath}'" + (verified ? " (verified)" : ""),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["verified"] = verified,
                            ["verifyDetail"] = verifyDetail,
                            ["warnings"] = warnings
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed importing '{fileNameWithoutExtension}' from '{importPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing from documents: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportBlocksFromDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for batch import. Imports multiple program blocks from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer.")]
        public static async Task<ResponseImportBlocksFromDocuments> ImportBlocksFromDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the blocks should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("regexName: name or regular expression to select block files (empty for all)")] string regexName = "",
            [Description("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;

            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ImportBlocksFromDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                // Determine total by scanning .s7dcl files matching regex
                int total = 0;
                var scanWarnings = new JsonArray();
                try
                {
                    if (Directory.Exists(importPath))
                    {
                        var rx = string.IsNullOrWhiteSpace(regexName) ? null : new Regex(regexName, RegexOptions.Compiled);
                        var files = Directory.GetFiles(importPath, "*.s7dcl", SearchOption.TopDirectoryOnly);
                        foreach (var f in files)
                        {
                            var name = Path.GetFileNameWithoutExtension(f);
                            if (rx != null && !rx.IsMatch(name))
                                continue;
                            total++;

                            try
                            {
                                var missingIds = GetResMissingEnUsIds(importPath, name);
                                if (missingIds != null && missingIds.Count > 0)
                                {
                                    scanWarnings.Add(new JsonObject
                                    {
                                        ["name"] = name,
                                        ["missingEnUsIds"] = new JsonArray(missingIds.Select(id => (JsonNode)id).ToArray())
                                    });
                                }
                            }
                            catch (Exception rex)
                            {
                                scanWarnings.Add(new JsonObject
                                {
                                    ["name"] = name,
                                    ["precheckError"] = rex.Message
                                });
                            }
                        }
                    }
                }
                catch (Exception sex)
                {
                    scanWarnings.Add(new JsonObject { ["scanError"] = sex.Message });
                }

                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = total,
                        Message = total > 0 ? $"Starting import of {total} blocks from documents..." : "Scanning import directory...",
                        progressToken
                    });
                }

                var option = ParseImportDocumentOption(importOption);
                var imported = await Task.Run(() => Portal.ImportBlocksFromDocuments(softwarePath, groupPath, importPath, regexName, option));

                var responseList = new List<ResponseBlockInfo>();
                int processed = 0;
                if (imported != null)
                {
                    foreach (var block in imported)
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
                        processed++;
                    }
                }

                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = processed,
                        Total = total,
                        Message = $"Document import completed: {processed} blocks imported successfully",
                        progressToken
                    });
                }

                var duration = (DateTime.Now - startTime).TotalSeconds;
                Logger?.LogInformation($"Document import completed: {processed} blocks imported in {duration:F2} seconds");

                // 逐文件失败原因必须回给调用方。以前这些异常只进日志，响应是
                // 「0 blocks imported，无警告」—— 调用方据此得出的结论是「服务器坏了」，
                // 而真相通常只是文档里一个元素不合法。Openness 对 .s7dcl 原子失败且不给行号，
                // 这点异常文本是**唯一**的线索，不能吞。
                var failures = Portal.LastImportFromDocumentsFailures;
                int scanned = Portal.LastImportFromDocumentsScanned;
                var failArr = new JsonArray();
                foreach (var f in failures.Take(50)) failArr.Add(f);

                // 四种「0 块」的病因完全不同，必须分开说 —— 报同一句话就是把人往错方向带。
                string msg;
                bool ok;
                if (processed > 0)
                {
                    msg = $"Document import completed: {processed} blocks imported from '{importPath}'"
                        + (failures.Count > 0 ? $"；另有 {failures.Count} 个文件失败（见 meta.failures）" : "");
                    ok = true;
                }
                else if (imported == null)
                {
                    // Portal 层只有「没连上/没打开项目」和「版本不够」两条路返回 null。
                    // 以前这会被误报成「目录里没文件」，让人去查一个根本没问题的路径。
                    msg = "没有连接到 TIA Portal 项目，导入根本没开始。先调用 Connect / OpenProject "
                        + "（或 AttachToOpenProject 接管已打开的工程），再重试。";
                    ok = false;
                }
                else if (!Directory.Exists(importPath))
                {
                    msg = $"导入目录 '{importPath}' 不存在。检查路径拼写，以及它是否指向**目录**而不是单个文件。";
                    ok = false;
                }
                else if (scanned == 0)
                {
                    msg = $"目录 '{importPath}' 里一个 .s7dcl 文件都没有，所以没有东西可导。"
                        + "检查文件扩展名是否为 .s7dcl（.scl 走 GenerateBlocksFromExternalSource）。";
                    ok = false;
                }
                else
                {
                    msg = $"扫描到 {scanned} 个 .s7dcl，**一个都没导进去**。逐份原因见 meta.failures。"
                        + DocumentImportHelp;
                    ok = false;
                }

                return new ResponseImportBlocksFromDocuments
                {
                    Message = msg,
                    Items = responseList,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        // 一份都没导进去时不能报 success=true —— 那正是「看着绿、其实什么也没发生」。
                        ["success"] = ok,
                        ["totalBlocks"] = total,
                        ["scannedFiles"] = scanned,
                        ["importedBlocks"] = processed,
                        ["duration"] = duration,
                        ["failures"] = failArr,
                        ["warnings"] = scanWarnings
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Document import failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch { }
                }

                Logger?.LogError(ex, $"Failed importing documents from '{importPath}'");
                throw new McpException($"Unexpected error importing documents from '{importPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeObject"), Description("[L2][Reflection]Describe an Openness object via reflection. Use this first when a natural-language TIA operation has no direct MCP tool. objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")]
        public static ResponseObjectDescribe DescribeObject(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
        {
            try
            {
                return Portal.DescribeObject(objectKind, objectPath, softwarePath, maxMembers);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetObjectProperty"), Description("[L2][Reflection]Get an Openness object property by dotted path. Use after DescribeObject/DescribeObjectProperty to safely inspect current state before writing.")]
        public static ResponseObjectValue GetObjectProperty(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("Property path, e.g. Name or BlockGroup.Groups")] string propertyPath,
            [Description("softwarePath required for Block/Type")] string softwarePath = "")
        {
            try
            {
                return Portal.GetObjectProperty(objectKind, objectPath, propertyPath, softwarePath);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading property: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListObjectChildren"), Description("[L2][Reflection]List child items from an enumerable Openness property, e.g. Devices, DeviceItems, Connections, Screens, Blocks. Use to discover paths instead of guessing.")]
        public static ResponseObjectChildren ListObjectChildren(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("Enumerable property name/path, e.g. Devices, DeviceItems, BlockGroup.Blocks")] string collectionProperty,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Max child items to return")] int limit = 200)
        {
            try
            {
                return Portal.ListObjectChildren(objectKind, objectPath, collectionProperty, softwarePath, limit);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing children: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeObject"), Description("[L2][Reflection]Invoke an Openness method via reflection. Default is read-oriented; set allowWrite=true only after DescribeObject confirms the target method/signature. This is the generic bridge for public API operations not yet wrapped by MCP.")]
        public static ResponseObjectValue InvokeObject(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, e.g. [\"AttrName\"]. Empty for no args.")] System.Text.Json.JsonElement[]? args = null,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
            => InvokeObject(objectKind, objectPath, methodName, ToArgsArray(args), softwarePath, allowWrite);

        // Internal overload (no tool attribute): existing in-process callers pass a JsonArray directly.
        public static ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args, string softwarePath = "", bool allowWrite = false)
        {
            try
            {
                return Portal.InvokeObject(objectKind, objectPath, methodName, args, softwarePath, allowWrite);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error invoking method: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeService"), Description("[L2][Reflection]GetService bridge: describe a service object (by type name suffix) from a target object.")]
        public static ResponseObjectDescribe DescribeService(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("Service type suffix, e.g. CrossReferenceService or ICompilable")] string serviceTypeSuffix,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
        {
            try
            {
                return Portal.DescribeService(objectKind, objectPath, serviceTypeSuffix, softwarePath, maxMembers);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing service: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeService"), Description("[L2][Reflection]GetService bridge: invoke a method on a service object (by type name suffix) from a target object.")]
        public static ResponseObjectValue InvokeService(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("Target object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("Service type suffix, e.g. CrossReferenceService or ICompilable")] string serviceTypeSuffix,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, empty for no args")] System.Text.Json.JsonElement[]? args = null,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
        {
            try
            {
                return Portal.InvokeService(objectKind, objectPath, serviceTypeSuffix, methodName, ToArgsArray(args), softwarePath, allowWrite);
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error invoking service method: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Reflection-bridge args arrive as a native JSON array. We accept JsonElement[] (not JsonArray) so the
        // generated tool schema is a well-formed `type:array` WITH `items`, which strict clients (VS Code) require;
        // a bare JsonArray param emits `type:array` without items and gets rejected. Rebuild a JsonArray for Portal.
        private static JsonArray? ToArgsArray(System.Text.Json.JsonElement[]? args)
            => args == null ? null : new JsonArray(args.Select(e => JsonNode.Parse(e.GetRawText())).ToArray());

        private static ImportDocumentOptions ParseImportDocumentOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option)) return ImportDocumentOptions.Override;

            var normalized = option.Trim();

            // Primary: accept exact enum names (case-insensitive)
            if (Enum.TryParse<ImportDocumentOptions>(normalized, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            // Aliases and common misspellings
            switch (normalized.ToLowerInvariant())
            {
                case "override": return ImportDocumentOptions.Override;
                case "none": return ImportDocumentOptions.None;
                case "skipinactiveculture":
                case "skipinactivecultures":
                case "skipinactive":
                case "skipinactivecult":
                    return ImportDocumentOptions.SkipInactiveCultures;
                case "activeinactiveculture":
                case "activateinactivecultures":
                case "activeinactivecultures":
                case "activateinactive":
                    return ImportDocumentOptions.ActivateInactiveCultures;
                default:
                    throw new McpException($"Invalid importOption '{option}'. Allowed: None, Override, SkipInactiveCultures, ActivateInactiveCultures", McpErrorCode.InvalidParams);
            }
        }

        // .s7res is YAML, not XML — see S7ResScanner for why the old XDocument-based
        // implementation threw on every real file and never produced a single warning.
        private static List<string> GetResMissingEnUsIds(string directory, string baseName)
            => S7ResScanner.GetMissingEnUsIds(directory, baseName);

        #endregion
    }
}
