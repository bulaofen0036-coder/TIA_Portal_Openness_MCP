using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: blocks/types. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region blocks/types

        /// <summary>
        /// 按名字在一个组里定位**唯一一个**对象。三级次序，专治「返回错块还冠上你请求的名字」：
        /// 1) 先按字面精确同名（OrdinalIgnoreCase）—— 西门子块名里常带 '.'，而 '.' 在 _regexChars 里，
        ///    老代码见到 '.' 就直接当正则走，于是请求 "FB_Motor.V2" 会被没锚定的 IsMatch 匹配上
        ///    "X_FB_MotorAV2_Old"，FirstOrDefault 把排在前面的那个错块返回、调用方还以为拿到的是自己要的；
        /// 2) 没有同名、且名字确实含元字符时才当模式用，并且**锚定** ^(?:...)$ ——
        ///    保留模式能力，但杜绝部分命中；正则非法则返回 null（等同找不到，与老行为一致）；
        /// 3) 锚定后仍命中多个 → 宁可报歧义也不猜（团队在删除口早就拒绝含元字符的路径，
        ///    读/导出口一直没堵，这里补上）。
        /// </summary>
        private T? ResolveSingleByName<T>(IEnumerable<T> items, string name, Func<T, string> nameOf, string kind)
            where T : class
        {
            var exact = items.FirstOrDefault(i => nameOf(i).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            if (name.IndexOfAny(_regexChars) < 0)
            {
                return null;
            }

            Regex regex;
            try
            {
                regex = new Regex($"^(?:{name})$", RegexOptions.IgnoreCase);
            }
            catch (Exception)
            {
                // Invalid regex, return null
                return null;
            }

            // 匹配与抛歧义都放在 try 之外：否则「歧义」这个异常会被上面捕获非法正则的 catch 吞成 null。
            var matches = items.Where(i => regex.IsMatch(nameOf(i))).Take(11).ToList();
            if (matches.Count == 0)
            {
                return null;
            }
            if (matches.Count > 1)
            {
                var candidates = matches.Take(10).Select(nameOf).ToList();
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"Ambiguous {kind} name '{name}': it matched {(matches.Count > 10 ? "more than 10" : matches.Count.ToString())} {kind}s. Use the exact path instead.",
                    candidates);
            }

            return matches[0];
        }

        public PlcBlock? GetBlock(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block by path: {blockPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var blockGroup = plcSoftware?.BlockGroup;

                if (blockGroup != null)
                {
                    var path = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                    var regexName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                    var group = GetPlcBlockGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        return ResolveSingleByName(group.Blocks.Cast<PlcBlock>(), regexName, b => b.Name, "block");
                    }
                }
            }

            return null;
        }

        public PlcType? GetType(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type by path: {typePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var typeGroup = plcSoftware?.TypeGroup;

                if (typeGroup != null)
                {
                    var path = typePath.Contains("/") ? typePath.Substring(0, typePath.LastIndexOf("/")) : string.Empty;
                    var regexName = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;

                    var group = GetPlcTypeGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        return ResolveSingleByName(group.Types.Cast<PlcType>(), regexName, t => t.Name, "type");
                    }
                }
            }

            return null;
        }

        public string GetBlockPath(PlcBlock block)
        {
            if (block == null)
            {
                return string.Empty;
            }

            if (block.Parent is PlcBlockGroup parentGroup)
            {
                var groupPath = GetPlcBlockGroupPath(parentGroup);
                return string.IsNullOrEmpty(groupPath) ? block.Name : $"{groupPath}/{block.Name}";
            }

            return block.Name;
        }

        /// <summary>
        /// 取块清单。**没打开项目时返回 null，不是空列表** —— 这两件事对调用方完全不同：
        /// 空列表意味着「这个 PLC 里确实没有块」，null 意味着「根本没查成」。
        /// 以前返回 `[]`，于是工具层那句 `if (list != null)` 恒为真、`else throw` 永不执行，
        /// 离线调用得到「成功，0 个块」—— 调用方据此认定这个 PLC 是空的，继续往下走。
        /// </summary>
        public List<PlcBlock>? GetBlocks(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting blocks...");

            if (IsProjectNull())
            {
                return null;
            }

            var list = new List<PlcBlock>();

            // 路径解析不到 ≠ 这个 PLC 里没有块。原来两件事都返回空列表，于是把 softwarePath
            // 写错也得到「成功，0 个块」—— 调用方（尤其是模型）会据此认为 PLC 是空的，
            // 转头去建一堆已经存在的块。真机实测过这条：传 'PLC_NOT_EXIST_XYZ' 得到 success=true。
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetBlocks: PLC software not found at '{softwarePath}'." + AvailablePlcPathsSuffix());
            }

            var group = plcSoftware.BlockGroup;
            if (group == null)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetBlocks: PLC '{softwarePath}' resolved, but its BlockGroup is not available — "
                    + "the block list could NOT be read. This is not the same as 'the PLC has no blocks'.");
            }

            try
            {
                GetBlocksRecursive(group, list, regexName);
            }
            catch (PortalException)
            {
                // 参数类错误（比如 regexName 不是合法正则）要原样上抛：包成「遍历失败」
                // 会给出一个**不准确**的原因，调用方照着去查 Openness 就跑偏了。
                throw;
            }
            catch (Exception ex)
            {
                // 遍历炸在半路时原来只记日志、把**残缺的**列表当完整结果返回。
                // 「少了几个块」比「一个都没有」更难发现，因为它看起来完全正常。
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetBlocks: block enumeration failed after {list.Count} block(s) in '{softwarePath}'; "
                    + "the returned list would have been INCOMPLETE, so it is not returned at all. "
                    + $"Root cause: {ex.Message}", null, ex);
            }

            return list;
        }

        public PlcBlockGroup? GetBlockRootGroup(string softwarePath)
        {
            _logger?.LogInformation("Getting block root group...");

            if (IsProjectNull())
            {
                return null;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    return plcSoftware.BlockGroup;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting block root group");
            }

            return null;
        }

        /// <summary>同 GetBlocks：没打开项目时返回 null，别把「没查成」伪装成「确实没有」。</summary>
        public List<PlcType>? GetTypes(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting types...");

            if (IsProjectNull())
            {
                return null;
            }

            var list = new List<PlcType>();

            // 与 GetBlocks 同因：路径解析不到 ≠ 这个 PLC 没有 UDT。
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetTypes: PLC software not found at '{softwarePath}'." + AvailablePlcPathsSuffix());
            }

            var group = plcSoftware.TypeGroup;
            if (group == null)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetTypes: PLC '{softwarePath}' resolved, but its TypeGroup is not available — "
                    + "the type list could NOT be read. This is not the same as 'the PLC has no types'.");
            }

            try
            {
                GetTypesRecursive(group, list, regexName);
            }
            catch (PortalException)
            {
                throw;   // 同上：参数类错误不许被包成「遍历失败」
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetTypes: type enumeration failed after {list.Count} type(s) in '{softwarePath}'; "
                    + "the returned list would have been INCOMPLETE, so it is not returned at all. "
                    + $"Root cause: {ex.Message}", null, ex);
            }

            return list;
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block by path: {blockPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                }

                var block = Guard.RequireNotNull(GetBlock(softwarePath, blockPath), "Block", blockPath);

                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                // TIA Portal never exports inconsistent blocks
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent; TIA Portal does not export inconsistent blocks.");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                block.Export(new FileInfo(exportPath), ExportOptions.None);

                return block;
            }
            catch (Exception ex)
            {
                //If the exception is already a PortalException, use it; otherwise, wrap it in a new PortalException
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
        }

        public PlcType? ExportType(string softwarePath, string typePath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting type by path: {typePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                }

                var type = Guard.RequireNotNull(GetType(softwarePath, typePath), "Type", typePath);

                // TIA Portal never exports inconsistent types
                if (!type.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Type is inconsistent; TIA Portal does not export inconsistent types.");
                }

                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                type.Export(new FileInfo(exportPath), ExportOptions.None);

                return type;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                if (!pex.Data.Contains("softwarePath")) pex.Data["softwarePath"] = softwarePath;
                if (!pex.Data.Contains("typePath")) pex.Data["typePath"] = typePath;
                if (!pex.Data.Contains("exportPath")) pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportType failed for {SoftwarePath} {TypePath} -> {ExportPath}", softwarePath, typePath, exportPath);
                throw pex;
            }
        }

        // Prepare a block/type XML file for Openness import. Two things are fixed on a temp
        // copy (the user's original file is never touched):
        //   1) Engineering version: Openness rejects an XML whose <Engineering version="Vxx"/>
        //      is newer than the connected portal ("The engineering version 'V21' ... is not
        //      supported."). The XML builders historically hardcode V21, so on a V20 portal
        //      every import fails. The header is rewritten to the detected major version.
        //   2) Encoding/BOM: block/type XML carrying Chinese comments must be UTF-8 *with BOM*
        //      or TIA imports the text as mojibake (中文乱码). Callers (and the model that wrote
        //      the file) frequently emit BOM-less UTF-8, so we always re-emit with a BOM here.
        private const string ImportCopyPrefix = "tia_mcp_import_";

        /// <summary>Every rewritten import used to leave its temp copy behind for good. The import reads
        /// the file synchronously, so a copy older than an hour is certainly finished with.</summary>
        private static void PruneOldImportCopies()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), ImportCopyPrefix + "*.xml"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { /* in use or gone */ }
                }
            }
            catch { /* best effort */ }
        }

        private static string PrepareXmlForImport(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = File.ReadAllText(path, Encoding.UTF8);

                var fixedText = text;
                int major = Engineering.TiaMajorVersion;
                if (major > 0)
                {
                    fixedText = Regex.Replace(text,
                        "<Engineering\\s+version=\"V\\d+\"\\s*/>",
                        $"<Engineering version=\"V{major}\" />");
                }

                // Already correct: version matches (or unknown) AND a BOM is present -> import as-is.
                if (fixedText == text && hasBom) return path;

                PruneOldImportCopies();
                var tmp = Path.Combine(Path.GetTempPath(), ImportCopyPrefix + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, fixedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return tmp;
            }
            catch
            {
                return path; // best effort; on any failure import the original file
            }
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing block from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC block group not found for groupPath='{groupPath}'; use empty string for root program blocks");

                if (!new FileInfo(importPath).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(PrepareXmlForImport(importPath));

                var imported = group.Blocks.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Blocks.Import returned an empty collection");

                return true;
            }
            catch (Exception ex)
            {
                // Surface the real Openness error to callers — without this the message
                // is just "Import failed" which is useless for diagnosing bad LAD/SCL XML.
                var inner = UnwrapImportError(ex);
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, $"Import failed: {inner}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportBlock failed for {SoftwarePath} group={GroupPath} file={ImportPath}: {Inner}", softwarePath, groupPath, importPath, inner);
                throw pex;
            }
        }

        // Walk InnerException chain and concatenate type+message — Openness wraps the
        // useful XML-validation error several layers deep.
        private static string UnwrapImportError(Exception ex)
        {
            var parts = new List<string>();
            var cur = ex;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            return string.Join(" | ", parts);
        }

        public ResponseImportBatch ImportBlocksFromDirectory(string softwarePath, string groupPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"PlcSoftware not found at '{softwarePath}'" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"Block group not found (groupPath='{groupPath}')" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name))
                    {
                        continue;
                    }

                    try
                    {
                        if (!new FileInfo(file).Exists)
                        {
                            failed.Add(new ImportFailure { Path = file, Error = "File not found" });
                            continue;
                        }
                        var fi = new FileInfo(PrepareXmlForImport(file));

                        if (!overwrite)
                        {
                            // overwrite=false is a promise not to replace anything. If we cannot check
                            // whether the block exists, we cannot keep it, so this file is skipped -
                            // it used to fall through and import with Override anyway.
                            PlcBlock? exists;
                            try
                            {
                                exists = group.Blocks.Find(name);
                            }
                            catch (Exception fx)
                            {
                                failed.Add(new ImportFailure { Path = file, Error = $"Could not check whether block '{name}' already exists ({fx.Message}); not imported because overwrite=false" });
                                continue;
                            }
                            if (exists != null)
                            {
                                failed.Add(new ImportFailure { Path = file, Error = $"Block '{name}' already exists (overwrite=false)" });
                                continue;
                            }
                        }

                        // The Find above only sees this group and the file name; the block name inside the
                        // XML can differ and the block can live in another group. ImportOptions.None makes
                        // TIA itself refuse to replace anything.
                        var list = group.Blocks.Import(fi, overwrite ? ImportOptions.Override : ImportOptions.None);
                        var names = list?.Select(b => b?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList()
                                    ?? new List<string>();
                        if (names.Count > 0)
                        {
                            imported.AddRange(names);
                        }
                        else
                        {
                            // An import that yields no block did not import anything; it used to be
                            // counted as imported under the file name.
                            failed.Add(new ImportFailure { Path = file, Error = "Import returned no block (nothing was imported)" });
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public bool ImportType(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing type from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcTypeGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC type group not found for groupPath='{groupPath}'; use empty string for root PLC data types");

                if (!new FileInfo(importPath).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(PrepareXmlForImport(importPath));

                var imported = group.Types.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Types.Import returned an empty collection");

                return true;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, "Import failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportType failed for {SoftwarePath} group={GroupPath} file={ImportPath}", softwarePath, groupPath, importPath);
                throw pex;
            }
        }

        public IEnumerable<PlcBlock>? ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();
            
            PlcBlock[] list;

            try
            {
                list = (GetBlocks(softwarePath, regexName) is { } got ? got.ToArray() : []);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve block list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int k = 0; k < list.Count(); k++)
            {
                var block = list[k];

                _logger?.LogDebug($"- Exporting block {k}/{list.Count()} : {block.Name}");

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                try
                {
                    if (!block.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent block {Name}", block.Name);

                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{block.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);

                            continue;
                        }
                    }

                    try
                    {
                        block.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (LicenseNotFoundException licEx)
                    {
                        failures.Add($"{block.Name}: license not found ({licEx.Message})");
                        _logger?.LogError(licEx, "License issue exporting {Block}", block.Name);

                        continue;
                    }
                    catch (EngineeringTargetInvocationException engEx)
                    {
                        failures.Add($"{block.Name}: target invocation failed ({engEx.Message})");
                        _logger?.LogError(engEx, "TargetInvocationException exporting {Block}", block.Name);

                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for {Block}", block.Name);

                        continue;
                    }

                    exportList.Add(block);
                }
                catch (Exception ex)
                {
                    // Catch only truly unexpected wrapper-level errors
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at block {Block}", block.Name);
                    // continue with next block
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocks completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optionally: _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocks completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
        }

        public IEnumerable<PlcType>? ExportTypes(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting types...");

            if (IsProjectNull())
            {
                return null;
            }

            var exportList = new List<PlcType>();
            var failures = new List<string>();

            PlcType[] list;

            try
            {
                list = (GetTypes(softwarePath, regexName) is { } got ? got.ToArray() : []);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve type list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var type = list[i];

                _logger?.LogDebug("- Exporting type {Index}/{Total} : {Name}", i, list.Count(), type.Name);

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                try
                {
                    if (!type.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent type {Name}", type.Name);
                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{type.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);
                            continue;
                        }
                    }

                    try
                    {
                        type.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{type.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for type {Type}", type.Name);
                        continue;
                    }

                    exportList.Add(type);
                }
                catch (Exception ex)
                {
                    failures.Add($"{type.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at type {Type}", type.Name);
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportTypes completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _logger?.LogInformation($"ExportTypes completed successfully. Exported {exportList.Count} types.");
            }

            return exportList;
        }

        public (string TempDir, List<string> Paths)? ExportBlockToTemp(string softwarePath, string blockPath, bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var blk = ExportBlock(softwarePath, blockPath, tempDir, preservePath);
            if (blk == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportTypeToTemp(string softwarePath, string typePath, bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var t = ExportType(softwarePath, typePath, tempDir, preservePath);
            if (t == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportBlocksToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportBlocks(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportTypesToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportTypes(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }
        

        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block as documents by path: {blockPath}");
            var success = false;
            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");
                }

                Capability.RequireSupported(TiaFeature.DocumentExport);

                
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    if (plcSoftware != null)
                    {
                        // Export code blocks as documents
                        // https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500

                        var groupPath = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                        var blockName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                        var group = GetPlcBlockGroupByPath(softwarePath, groupPath);

                        //group?.Blocks.ForEach(b => Console.WriteLine($"Block: {b.Name}, Type: {b.GetType().Name}"));

                        // join exportPath and groupPath
                        if (!Directory.Exists(exportPath))
                        {
                            Directory.CreateDirectory(exportPath);
                        }

                        if (preservePath && !string.IsNullOrEmpty(groupPath))
                        {
                            exportPath = Path.Combine(exportPath, groupPath);

                            if (!Directory.Exists(exportPath))
                            {
                                Directory.CreateDirectory(exportPath);
                            }
                        }

                        try
                        {
                            // delete files s7dcl/s7res if already exists
                            var blockFiles7dclPath = Path.Combine(exportPath, $"{blockName}.s7dcl");
                            if (File.Exists(blockFiles7dclPath))
                            {
                                File.Delete(blockFiles7dclPath);
                            }
                            var blockFiles7resPath = Path.Combine(exportPath, $"{blockName}.s7res");
                            if (File.Exists(blockFiles7resPath))
                            {
                                File.Delete(blockFiles7resPath);
                            }

                            var result = group?.Blocks.Find(blockName)?.ExportAsDocuments(new DirectoryInfo(exportPath), blockName);

                            if (result != null && result.State == DocumentResultState.Success)
                            {
                                success = true;
                            }
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            // The export or import of blocks with mixed programming languages is not possible
                            throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at block '{blockName}'. {ex.Message}", null, ex);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.ExportFailed, $"Exception at block '{blockName}'. {ex.Message}", null, ex);
                        }

                    }

                }


            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportAsDocuments failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
            return success;
        }

        // TIA portal crashes when exporting blocks as documents, :-(
        /// <summary>
        /// Per-block failure reasons from the most recent ExportBlocksAsDocuments call (empty on full success).
        /// The tool layer surfaces this so a "totalBlocks &gt; 0 but exportedBlocks == 0" no longer looks silent.
        /// </summary>
        public IReadOnlyList<string> LastExportAsDocumentsFailures { get; private set; } = new List<string>();

        public IEnumerable<PlcBlock>? ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks as documents...");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ExportBlocksAsDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();

            PlcBlock[] list;
            try
            {
                list = (GetBlocks(softwarePath, regexName) is { } got ? got.ToArray() : []);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to retrieve block list for {softwarePath}");
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var block = list[i];

                _logger?.LogDebug($"- Exporting block as document {i}/{list.Count()} : {block.Name}");

                // Skip inconsistent blocks (TIA generally won’t export them)
                if (!block.IsConsistent)
                {
                    _logger?.LogWarning($"Skipping inconsistent block {block.Name}");
                    continue;
                }

                // Determine base directory (preserve group path if requested)
                string targetDir = exportPath;
                if (preservePath && block.Parent is PlcBlockGroup parentGroup)
                {
                    var groupPath = GetPlcBlockGroupPath(parentGroup);
                    if (!string.IsNullOrWhiteSpace(groupPath))
                    {
                        targetDir = Path.Combine(exportPath, groupPath.Replace('/', '\\'));
                    }
                }

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: cannot create directory '{targetDir}' ({ex.Message})");
                    _logger?.LogError(ex, $"Directory creation failed for {targetDir}");
                    continue;
                }

                var fileDcl = Path.Combine(targetDir, $"{block.Name}.s7dcl");
                var fileRes = Path.Combine(targetDir, $"{block.Name}.s7res");

                // Clean previous artifacts
                foreach (var f in new[] { fileDcl, fileRes })
                {
                    try
                    {
                        if (File.Exists(f))
                        {
                            File.Delete(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: cannot delete existing '{Path.GetFileName(f)}' ({ex.Message})");
                        _logger?.LogError(ex, $"Failed deleting existing file {f}");
                        // Continue anyway; export might overwrite.
                    }
                }

                try
                {
                    DocumentExportResult? result = null;
                    try
                    {
                        result = block.ExportAsDocuments(new DirectoryInfo(targetDir), block.Name);
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        failures.Add($"{block.Name}: not supported ({ex.Message})");
                        _logger?.LogWarning(ex, $"EngineeringNotSupported exporting {block.Name}");
                        continue;
                    }
                    catch (LicenseNotFoundException ex)
                    {
                        failures.Add($"{block.Name}: license not found ({ex.Message})");
                        _logger?.LogError(ex, $"License issue exporting {block.Name}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export threw ({ex.Message})");
                        _logger?.LogError(ex, $"ExportAsDocuments failed for {block.Name}");
                        continue;
                    }

                    if (result == null)
                    {
                        failures.Add($"{block.Name}: no result returned");
                        continue;
                    }

                    if (result.State == DocumentResultState.Success)
                    {
                        exportList.Add(block);
                    }
                    else
                    {
                        failures.Add($"{block.Name}: result state {result.State}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, $"Unexpected wrapper error for {block.Name}");
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocksAsDocuments completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optional verbose list:
                // _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocksAsDocuments completed successfully. Exported {exportList.Count} blocks.");
            }

            LastExportAsDocumentsFailures = failures;
            return exportList;
        }

        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, ImportDocumentOptions option)
        {
            _logger?.LogInformation($"Importing block from documents: {fileNameWithoutExtension} in {importPath}");

            if (IsProjectNull())
            {
                return false;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportFromDocuments is only supported on TIA Portal V20 or newer");
                return false;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (!(softwareContainer?.Software is PlcSoftware plcSoftware))
            {
                throw new PortalException(PortalErrorCode.NotFound, $"PLC software '{softwarePath}' not found. Use GetProjectTree for the exact PLC name.");
            }

            var dir = new DirectoryInfo(importPath);
            if (!dir.Exists)
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"Import directory does not exist: {importPath}");
            }
            if (!File.Exists(Path.Combine(importPath, fileNameWithoutExtension + ".s7dcl")))
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"No '{fileNameWithoutExtension}.s7dcl' found under {importPath}. importPath is the DIRECTORY holding the .s7dcl/.s7res, and fileNameWithoutExtension omits the extension.");
            }

            // Resolve the target group. Empty path = root. A non-empty path that does NOT resolve is a
            // caller error — DO NOT silently retarget root (that is how a nested-group import used to
            // land the block at root and get AutoNumber-renumbered).
            PlcBlockGroup targetGroup;
            if (string.IsNullOrWhiteSpace(groupPath))
            {
                targetGroup = plcSoftware.BlockGroup;
            }
            else
            {
                targetGroup = GetPlcBlockGroupByPath(softwarePath, groupPath)
                    ?? throw new PortalException(PortalErrorCode.NotFound,
                        $"Group path '{groupPath}' not found under PLC '{softwarePath}'. Use GetSoftwareTree for exact group names, or pass an empty groupPath to import at the root.");
            }

            // Capture the existing block's identity BEFORE import. Openness Override, when the block
            // lives in a different group than the import target, deletes+recreates it (losing its
            // number and original group). We restore the number afterwards so callers/instance DBs
            // and the project tree stay stable.
            var existing = FindBlockRecursive(plcSoftware.BlockGroup, fileNameWithoutExtension);
            int? prevNumber = null;
            bool prevAutoNumber = false;
            try { if (existing != null) { prevNumber = existing.Number; prevAutoNumber = existing.AutoNumber; } } catch { }

            DocumentImportResult? result;
            try
            {
                result = targetGroup.Blocks.ImportFromDocuments(dir, fileNameWithoutExtension, option);
            }
            catch (EngineeringNotSupportedException ex)
            {
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, $"ImportFromDocuments not supported for '{fileNameWithoutExtension}': {ex.Message}", null, ex);
            }
            catch (EngineeringTargetInvocationException ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, $"ImportFromDocuments failed for '{fileNameWithoutExtension}' into group '{(string.IsNullOrWhiteSpace(groupPath) ? "<root>" : groupPath)}': {ex.Message}. Check the .s7dcl syntax (types/attributes) and that .s7res matches the S7_MLC ids.", null, ex);
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, $"ImportFromDocuments failed for '{fileNameWithoutExtension}' into group '{(string.IsNullOrWhiteSpace(groupPath) ? "<root>" : groupPath)}': {ex.Message}", null, ex);
            }

            if (result == null || result.State != DocumentResultState.Success)
            {
                throw new PortalException(PortalErrorCode.ImportFailed,
                    $"ImportFromDocuments returned state '{result?.State.ToString() ?? "null"}' for '{fileNameWithoutExtension}'. The document set was not imported.");
            }

            // Restore the original block number if Override renumbered it (symbolic/optimized blocks
            // are addressed by name, so this is cosmetic-but-important for a stable, diffable project).
            if (prevNumber.HasValue)
            {
                var imported = FindBlockRecursive(plcSoftware.BlockGroup, fileNameWithoutExtension);
                if (imported != null)
                {
                    try
                    {
                        if (imported.Number != prevNumber.Value)
                        {
                            imported.AutoNumber = false;
                            imported.Number = prevNumber.Value;
                        }
                        else
                        {
                            imported.AutoNumber = prevAutoNumber;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, $"Could not restore block number {prevNumber} for {fileNameWithoutExtension}");
                    }
                }
            }
            return true;
        }

        /// <summary>Depth-first search for a block by exact name across all nested block groups.</summary>
        private PlcBlock? FindBlockRecursive(PlcBlockGroup group, string blockName)
        {
            if (group == null)
            {
                return null;
            }
            var here = group.Blocks.Find(blockName);
            if (here != null)
            {
                return here;
            }
            foreach (var sub in group.Groups)
            {
                var found = FindBlockRecursive(sub, blockName);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 上一次 <see cref="ImportBlocksFromDocuments"/> 里**逐文件失败的原因**。
        ///
        /// 这个属性存在的理由：原来每个文件的导入异常只 `LogWarning` 就吞掉，函数返回空列表，
        /// 工具层于是报「0 blocks imported，无警告」—— 调用方（尤其是弱模型）只能得出
        /// 「这个服务器坏了」的结论，而真相往往只是文档里某一个元素不合法。
        /// Openness 对 .s7dcl 是**原子失败且不给行号**的，把仅有的这点异常文本也扔掉，
        /// 等于把唯一的诊断线索销毁。
        /// </summary>
        public IReadOnlyList<string> LastImportFromDocumentsFailures { get; private set; } = new List<string>();

        /// <summary>上一次扫描到的 .s7dcl 文件数（未经 regex 过滤）。0 和「都失败了」是两回事。</summary>
        public int LastImportFromDocumentsScanned { get; private set; }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, ImportDocumentOptions option, bool preservePath = false)
        {
            _logger?.LogInformation($"Importing blocks from documents in {importPath} with regex '{regexName}'");
            var failures = new List<string>();
            LastImportFromDocumentsFailures = failures;
            LastImportFromDocumentsScanned = 0;

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportBlocksFromDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var imported = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    var dir = new DirectoryInfo(importPath);
                    if (!dir.Exists)
                    {
                        _logger?.LogWarning($"Import directory does not exist: {importPath}");
                        return imported;
                    }

                    var rx = string.IsNullOrWhiteSpace(regexName)
                        ? null
                        : new Regex(regexName, RegexOptions.Compiled);

                    // Consider .s7dcl as the primary index; .s7res is optional supplemental
                    var files = dir.GetFiles("*.s7dcl", SearchOption.TopDirectoryOnly);
                    LastImportFromDocumentsScanned = files.Length;
                    foreach (var file in files)
                    {
                        var name = Path.GetFileNameWithoutExtension(file.Name);
                        if (rx != null && !rx.IsMatch(name))
                        {
                            // 被 regex 滤掉也要留痕：「文件在、但被你自己的 regexName 挡了」
                            // 和「文件不合法」是完全不同的两件事，报同一个 0 会把人带偏。
                            failures.Add($"{name}: 被 regexName '{regexName}' 过滤，未尝试导入");
                            continue;
                        }

                        try
                        {
                            var result = (group != null)
                                ? group.Blocks.ImportFromDocuments(dir, name, option)
                                : plcSoftware.BlockGroup.Blocks.ImportFromDocuments(dir, name, option);

                            if (result != null && result.State == DocumentResultState.Success && result.ImportedPlcBlocks != null)
                            {
                                foreach (var blk in result.ImportedPlcBlocks)
                                {
                                    if (blk != null)
                                    {
                                        imported.Add(blk);
                                    }
                                }
                            }
                            else
                            {
                                // State 不是 Success 也是失败，只是不抛异常 —— 以前这条路径连日志都没有。
                                failures.Add($"{name}: ImportFromDocuments 返回 state="
                                             + (result?.State.ToString() ?? "null") + "，整份文档未导入");
                            }
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            _logger?.LogWarning(ex, "Skipping '{Name}': not supported (likely mixed languages)", name);
                            failures.Add($"{name}: 本版本不支持（常见于混编语言）—— {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Skipping '{Name}' due to import error", name);
                            failures.Add($"{name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error importing blocks from documents");
            }

            return imported;
        }

        #endregion
    }
}
