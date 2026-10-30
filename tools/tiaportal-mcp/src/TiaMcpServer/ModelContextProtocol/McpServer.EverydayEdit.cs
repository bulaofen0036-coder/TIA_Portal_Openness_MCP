using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Partial: 日常对象编辑 —— 单个 PLC 变量的读/增/改/删、建变量表 / 监控表、删监控表、给 FB 建背景 DB、
    /// 删设备、改名改号、编译硬件组态、归档工程。实现见 Portal.EverydayEdit.cs。
    /// 删除类与 DeletePlcBlock 一个规格：dryRun 默认 true，预览把影响摆出来，实做后读回确认。
    /// </summary>
    public static partial class McpServer
    {
        private static McpException EditError(string tool, string what, Exception ex)
        {
            if (ex is PortalException pex)
                return new McpException($"{tool} failed for '{what}' [{pex.Code}]: {pex.Message}", pex,
                    pex.Code == PortalErrorCode.InvalidParams || pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams
                    : pex.Code == PortalErrorCode.InvalidState ? McpErrorCode.InvalidRequest : McpErrorCode.InternalError);
            return new McpException($"Unexpected error in {tool} for '{what}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
        }

        private static string[]? WarningsOf(JsonObject data) =>
            data["warnings"] is JsonArray w && w.Count > 0 ? w.Select(x => x?.ToString() ?? "").ToArray() : null;

        [McpServerTool(Name = "GetPlcTags"), Description("[L1][PLC-Software][READ-ONLY] List PLC tags (name, type, address, comment, HMI flags) without an XML export. Filter by table, name substring and/or address prefix ('%Q' = every output). includeConstants adds user constants. Use to find what sits on an address or which addresses are free. Capped by maxTags (default 300, Meta.truncated). Requires: Connect + OpenProject (or AttachToOpenProject).")]
        public static ResponseJsonReport GetPlcTags(
            [Description("PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("one table (bare or group-qualified); empty = all")] string tagTableName = "",
            [Description("case-insensitive name substring; empty = all")] string nameFilter = "",
            [Description("address prefix, e.g. '%Q', '%I0.', '%MW'; empty = all")] string addressFilter = "",
            [Description("also list user constants")] bool includeConstants = false,
            [Description("cap (default 300, max 5000)")] int maxTags = 300)
        {
            try
            {
                var data = Portal.GetPlcTags(softwarePath, tagTableName, nameFilter, addressFilter, includeConstants, maxTags);
                int matched = data["matchedTags"]!.GetValue<int>();
                int returned = data["returnedTags"]!.GetValue<int>();
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = $"{matched} 个变量符合条件（共 {data["totalTags"]} 个），返回 {returned} 个"
                              + (matched > returned ? "；已截断，加筛选或调大 maxTags。" : "。"),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["readOnly"] = true, ["truncated"] = matched > returned },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("GetPlcTags", softwarePath, ex); }
        }

        [McpServerTool(Name = "SetPlcTag"), Description("[L2][PLC-Software] Create or modify ONE PLC tag in place. Existing tag (any table): change newName / dataType / address / comment; omitted fields stay. Missing tag: created in tagTableName with dataType + address. Tag names are unique per PLC; a clash is refused. Read back (Data.verified); warns when the address is shared. Requires: Connect + OpenProject (or AttachToOpenProject). Then CompileAndDiagnosePlc + SaveProject.")]
        public static ResponseJsonReport SetPlcTag(
            [Description("PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("tag to modify, or name of the tag to create")] string tagName,
            [Description("table for a NEW tag (bare or group-qualified); optional for an existing tag")] string tagTableName = "",
            [Description("rename an existing tag (empty = keep)")] string newName = "",
            [Description("e.g. 'Bool', 'Int', 'Real' (empty = keep; required to create)")] string dataType = "",
            [Description("e.g. '%I0.0', '%Q4.2', '%MW100' (empty = keep; required to create)")] string address = "",
            [Description("comment text (empty = keep)")] string comment = "",
            [Description("culture, e.g. 'zh-CN' (empty = zh-CN, else first project language)")] string commentLanguage = "")
        {
            try
            {
                var data = Portal.SetPlcTag(softwarePath, tagName, tagTableName, newName, dataType, address, comment, commentLanguage);
                bool created = data["created"]!.GetValue<bool>();
                bool changed = data["changed"]!.GetValue<bool>();
                bool verified = data["verified"]!.GetValue<bool>();
                var name = data["after"]?["name"]?.ToString() ?? tagName;
                var msg = created ? $"已新建变量 '{name}'（{data["after"]?["dataType"]} {data["after"]?["address"]}，表 '{data["after"]?["table"]}'）。"
                    : changed ? $"已修改变量 '{tagName}'：{string.Join("、", data["changes"]!.AsArray())}。"
                    : $"变量 '{tagName}' 与要求一致，没有改动。";
                msg += verified ? "已读回确认。" : "⚠️ 读回对不上要求的值，见 Data.after。";
                if (changed) msg += "记得 CompileAndDiagnosePlc、SaveProject。";
                return new ResponseJsonReport
                {
                    Ok = verified,
                    Message = msg,
                    Data = data,
                    Warnings = WarningsOf(data),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["created"] = created, ["changed"] = changed, ["verified"] = verified },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("SetPlcTag", tagName, ex); }
        }

        [McpServerTool(Name = "DeletePlcTag"), Description("[L2][PLC-Software][Destructive] Preview or delete ONE PLC tag. Defaults to dryRun=true (shows name/type/address/comment). DANGER: code using the symbol falls back to the absolute address or stops compiling, HMI bindings by name break silently, the comment is lost. Verified after delete; then CompileAndDiagnosePlc + SaveProject.")]
        public static ResponseJsonReport DeletePlcTag(
            [Description("PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("exact tag name")] string tagName,
            [Description("optional table to disambiguate")] string tagTableName = "",
            [Description("true (default) previews; false deletes and verifies")] bool dryRun = true)
        {
            try
            {
                var data = Portal.DeletePlcTag(softwarePath, tagName, tagTableName, dryRun);
                var t = data["tag"]!;
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = dryRun
                        ? $"[dryRun] 未做任何改动。变量 '{t["name"]}'（{t["dataType"]} {t["address"]}，表 '{t["table"]}'）可以删除；确认后 dryRun=false。"
                        : $"变量 '{t["name"]}' 已删除并读回确认不存在。请 CompileAndDiagnosePlc 看有没有引用断掉，再 SaveProject。",
                    Data = data,
                    Warnings = WarningsOf(data),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["dryRun"] = dryRun },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("DeletePlcTag", tagName, ex); }
        }

        [McpServerTool(Name = "CreatePlcTagTable"), Description(
            "[L2][PLC-Software] Create one EMPTY PLC tag table (变量表), optionally inside an existing tag-table group. Names are unique per PLC; a clash is refused. "
            + "Then fill it tag by tag with SetPlcTag (tagTableName = this table), or import a whole table with ImportPlcTagTable. "
            + "Requires: Connect + OpenProject (or AttachToOpenProject). Afterwards SaveProject.")]
        public static ResponseJsonReport CreatePlcTagTable(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("tagTableName: name of the new tag table")] string tagTableName,
            [Description("groupPath: existing tag-table group, e.g. 'IO' (empty = root)")] string groupPath = "")
        {
            try
            {
                var data = Portal.CreatePlcTagTable(softwarePath, tagTableName, groupPath);
                bool verified = data["verified"]!.GetValue<bool>();
                return new ResponseJsonReport
                {
                    Ok = verified,
                    Message = verified ? $"已新建变量表 '{data["tablePath"]}' 并读回确认。用 SetPlcTag 往里加变量，记得 SaveProject。" : "⚠️ 建表调用返回了，但读回没找到这张表。",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["verified"] = verified },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("CreatePlcTagTable", tagTableName, ex); }
        }

        [McpServerTool(Name = "CreatePlcWatchTable"), Description(
            "[L2][PLC-Software] Create one EMPTY watch table (监控表), optionally inside an existing watch-table group. A name clash is refused. "
            + "Entries with a modify value go in with SetWatchTableModifyValue; plain monitor rows are added in the TIA UI. "
            + "Requires: Connect + OpenProject (or AttachToOpenProject). Afterwards SaveProject.")]
        public static ResponseJsonReport CreatePlcWatchTable(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("watchTableName: name of the new watch table")] string watchTableName,
            [Description("groupPath: existing watch-table group (empty = root)")] string groupPath = "")
        {
            try
            {
                var data = Portal.CreatePlcWatchTable(softwarePath, watchTableName, groupPath);
                bool verified = data["verified"]!.GetValue<bool>();
                return new ResponseJsonReport
                {
                    Ok = verified,
                    Message = verified ? $"已新建监控表 '{data["tablePath"]}' 并读回确认。记得 SaveProject。" : "⚠️ 建表调用返回了，但读回没找到这张表。",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["verified"] = verified },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("CreatePlcWatchTable", watchTableName, ex); }
        }

        [McpServerTool(Name = "DeletePlcWatchTable"), Description("[L2][PLC-Software][Destructive] Preview or delete ONE watch table. Defaults to dryRun=true (path and entry count). Affects commissioning only, not the program - but entries, modify values and comments are lost (ExportPlcWatchTable first). Verified after delete; then SaveProject.")]
        public static ResponseJsonReport DeletePlcWatchTable(
            [Description("PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("bare name or group-qualified path")] string watchTableName,
            [Description("true (default) only previews; false deletes and verifies")] bool dryRun = true)
        {
            try
            {
                var data = Portal.DeletePlcWatchTable(softwarePath, watchTableName, dryRun);
                int entries = data["entryCount"]!.GetValue<int>();
                var count = entries >= 0 ? $"{entries} 条条目" : "条目数读不出";
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = dryRun
                        ? $"[dryRun] 未做任何改动。监控表 '{data["watchTable"]}'（{count}）可以删除；确认后 dryRun=false。"
                        : $"监控表 '{data["watchTable"]}' 已删除并读回确认不存在。记得 SaveProject。",
                    Data = data,
                    Warnings = WarningsOf(data),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["dryRun"] = dryRun },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("DeletePlcWatchTable", watchTableName, ex); }
        }

        [McpServerTool(Name = "CreateInstanceDB"), Description("[L2][PLC-Software] Create a single-instance DB (背景 DB) for an existing FB directly - no XML, no dummy call. dbName defaults to '<FB>_DB'; dbNumber 0 = auto; groupPath = existing block group. Read back incl. InstanceOfName (Data.verified). Then call the FB with it, CompileAndDiagnosePlc and SaveProject.")]
        public static ResponseJsonReport CreateInstanceDB(
            [Description("PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("the FB (name or group path), e.g. 'FB_Motor'")] string fbName,
            [Description("new instance DB name (empty = '<FB>_DB')")] string dbName = "",
            [Description("DB number, 0 = automatic")] int dbNumber = 0,
            [Description("existing block group (empty = root)")] string groupPath = "")
        {
            try
            {
                var data = Portal.CreateInstanceDB(softwarePath, fbName, dbName, dbNumber, groupPath);
                bool verified = data["verified"]!.GetValue<bool>();
                return new ResponseJsonReport
                {
                    Ok = verified,
                    Message = $"已为 FB '{data["fbName"]}' 建背景 DB '{data["dbName"]}'（DB{data["dbNumber"]}）。"
                              + (verified ? "已读回确认它是这个 FB 的背景 DB。" : "⚠️ 读回没对上 InstanceOfName，见 Data。") + "记得 SaveProject。",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["verified"] = verified },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("CreateInstanceDB", fbName, ex); }
        }

        [McpServerTool(Name = "DeleteDevice"), Description("[L2][Hardware][Destructive] Preview or delete ONE device / station (PLC, HMI, IO device, drive). Defaults to dryRun=true = lists the device, its modules and software (block count), deletes nothing. Give the device name or its CPU/head-module name; a module name is refused. DANGER: deleting a PLC deletes its whole program; network, IO-system, S7 and HMI connections to it are cut. Verified after delete; then SaveProject.")]
        public static ResponseJsonReport DeleteDevice(
            [Description("device (station) name as listed by GetDevices, or its CPU name, e.g. 'PLC_1'")] string devicePath,
            [Description("true (default) only previews; false deletes and verifies")] bool dryRun = true)
        {
            try
            {
                var data = Portal.DeleteDevice(devicePath, dryRun);
                int sw = data["software"]!.AsArray().Count;
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = dryRun
                        ? $"[dryRun] 未做任何改动。设备 '{data["deviceName"]}'" + (sw > 0 ? $" 里有 {sw} 套软件（程序会一起删掉，见 Data.software）" : "") + "；确认后 dryRun=false。"
                        : $"设备 '{data["deviceName"]}' 已删除并读回确认不存在。记得 SaveProject。",
                    Data = data,
                    Warnings = WarningsOf(data),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["dryRun"] = dryRun, ["softwareCount"] = sw },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("DeleteDevice", devicePath, ex); }
        }

        [McpServerTool(Name = "RenamePlcObject"), Description("[L2][PLC-Software] Rename a PLC block (and/or renumber), a UDT, a tag table, a watch table or a device; kind = block | type | tagtable | watchtable | device. Clashes refused first; read back (Data.verified). TIA updates calls in the project; exported files, HMI scripts and hard-coded DB numbers do not follow. V20 cannot rename watch tables. Then CompileAndDiagnosePlc + SaveProject.")]
        public static ResponseJsonReport RenamePlcObject(
            [Description("block | type | tagtable | watchtable | device")] string kind,
            [Description("current name or path (tables: group-qualified allowed; device: name)")] string objectPath,
            [Description("new name (empty = keep, e.g. when only renumbering)")] string newName = "",
            [Description("new block number (blocks only, 0 = keep)")] int newNumber = 0,
            [Description("PLC software path, e.g. 'PLC_1' (not for kind=device)")] string softwarePath = "")
        {
            try
            {
                var data = Portal.RenamePlcObject(kind, softwarePath, objectPath, newName, newNumber);
                bool changed = data["changed"]!.GetValue<bool>();
                bool verified = data["verified"]!.GetValue<bool>();
                var msg = !changed ? $"'{data["oldName"]}' 已经是要求的名字/编号，没有改动。"
                    : "已改：" + string.Join("，", new[]
                        {
                            data["oldName"]?.ToString() != data["newName"]?.ToString() ? $"'{data["oldName"]}' → '{data["newName"]}'" : null,
                            data["newNumber"] != null && data["oldNumber"]?.ToString() != data["newNumber"]?.ToString() ? $"'{data["newName"]}' 编号 {data["oldNumber"]} → {data["newNumber"]}" : null,
                        }.Where(x => x != null)) + "。"
                      + (verified ? "已读回确认。" : "⚠️ 读回没对上，见 Data。") + "记得 CompileAndDiagnosePlc、SaveProject。";
                return new ResponseJsonReport
                {
                    Ok = verified,
                    Message = msg,
                    Data = data,
                    Warnings = WarningsOf(data),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["changed"] = changed, ["verified"] = verified },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("RenamePlcObject", objectPath, ex); }
        }

        [McpServerTool(Name = "CompileHardware"), Description("[L2][Hardware] Compile the HARDWARE configuration of one device (modules, addresses, network, firmware) and return TIA's errors/warnings - CompileSoftware / CompileAndDiagnosePlc do not cover this; use after PlugDeviceItem, IO-address or network changes. Requires: Connect + OpenProject, and the PLC offline (GoOffline first).")]
        public static ResponseJsonReport CompileHardware(
            [Description("device (station) name as listed by GetDevices, e.g. 'PLC_1'")] string devicePath)
        {
            try
            {
                var (result, target) = Portal.CompileHardware(devicePath);
                var collected = CollectCompilerMessages(result.Messages);
                JsonArray Arr(System.Collections.Generic.IEnumerable<string> xs) => new JsonArray(xs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                bool hasErrors = result.ErrorCount > 0 || result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase);
                bool collectedAll = collected.CollectFailures.Count == 0;
                return new ResponseJsonReport
                {
                    // 编译跑完了，结果本身就是答案；明细没收全时 Ok=false —— 「0 条」可能是没收到。
                    Ok = collectedAll,
                    Message = $"硬件编译完成（{target}）：State={result.State}，错误 {result.ErrorCount}、警告 {result.WarningCount}。"
                              + (hasErrors ? "见 Errors。" : "")
                              + (collectedAll ? "" : "⚠️ 部分明细没收全，见 Data.collectFailures。"),
                    Errors = collected.Errors.ToArray(),
                    Warnings = collected.Warnings.ToArray(),
                    Data = new JsonObject
                    {
                        ["devicePath"] = devicePath,
                        ["compiledVia"] = target,
                        ["state"] = result.State.ToString(),
                        ["errorCount"] = result.ErrorCount,
                        ["warningCount"] = result.WarningCount,
                        ["collectFailures"] = Arr(collected.CollectFailures),
                    },
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["errorCount"] = result.ErrorCount },
                };
            }
            catch (Exception ex) when (ex is not McpException && IsOnlineModeError(ex))
            {
                throw new McpException($"CompileHardware: '{devicePath}' is online, and TIA does not compile while online. Call GoOffline first, then retry. ({ex.Message})", ex, McpErrorCode.InvalidRequest);
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("CompileHardware", devicePath, ex); }
        }

        [McpServerTool(Name = "ArchiveProject"), Description("[L1][Project] Archive the open project into one file (.zap20/.zap21, TIA 'Archive...') for backup or hand-over; the project itself is unchanged. Must be saved first (unsaved changes are refused - SaveProject; the engine never saves on its own). mode: compressed (default) | none | discard | discardcompressed. Existing archives are never overwritten.")]
        public static ResponseJsonReport ArchiveProject(
            [Description("folder for the archive (created if missing)")] string targetDirectory,
            [Description("file name without extension (empty = '<project>_yyyyMMdd_HHmm')")] string archiveName = "",
            [Description("compressed (default) | none | discard | discardcompressed")] string mode = "compressed")
        {
            try
            {
                var data = Portal.ArchiveProject(targetDirectory, archiveName, mode);
                bool verified = data["verified"]!.GetValue<bool>();
                return new ResponseJsonReport
                {
                    Ok = verified,
                    Message = verified
                        ? $"工程已归档：{data["archivePath"]}（{Math.Round((data["sizeBytes"]?.GetValue<long>() ?? 0) / 1048576.0, 1)} MB，{data["seconds"]} 秒）。"
                        : "Archive 已执行，但目标目录里没找到生成的归档文件 —— 请到目录里看一眼。",
                    Data = data,
                    OutputPath = data["archivePath"]?.ToString(),
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["verified"] = verified },
                };
            }
            catch (Exception ex) when (ex is not McpException) { throw EditError("ArchiveProject", targetDirectory, ex); }
        }
    }
}
