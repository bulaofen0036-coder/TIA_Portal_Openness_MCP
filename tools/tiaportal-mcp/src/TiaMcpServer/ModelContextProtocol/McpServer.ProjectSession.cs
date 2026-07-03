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
    // Partial: project/session. Extracted from McpServer.cs (god-file split); behavior unchanged.
    public static partial class McpServer
    {
        #region project/session

        [McpServerTool(Name = "GetProject"), Description("[L1][Project] List all open local projects and multi-user sessions with their attributes. Requires: Connect. Use this to confirm which project is active, or to find the project name for AttachToOpenProject.")]
        public static ResponseGetProjects GetProjects()
        {
            try
            {
                var list = Portal.GetProjects();

                list.AddRange(Portal.GetSessions());

                var responseList = new List<ResponseProjectInfo>();
                foreach (var project in list)
                {
                    var attributes = Helper.GetAttributeList(project);

                    if (project != null)
                    {
                        responseList.Add(new ResponseProjectInfo
                        {
                            Name = project.Name,
                            Attributes = attributes
                        });
                    }
                }

                return new ResponseGetProjects
                {
                    Message = "Open projects and sessions retrieved",
                    Items = responseList,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving open projects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "OpenProject"), Description("[L1][Project] Open a local TIA Portal project (.apXX) or multi-user session (.alsXX) file, where XX is the TIA version number (e.g. .ap21, .als21). Requires: Connect. Closes any currently open project first. After success, call GetProjectTree to explore its structure.")]
        public static ResponseOpenProject OpenProject(
            [Description("path: defines the path where to the project/session")] string path)
        {
            try
            {
                if (Portal.ProjectIsValid)
                {
                    Portal.CloseProject();
                }

                // get project extension
                string extension = Path.GetExtension(path).ToLowerInvariant();

                // use regex to check if extension is .ap\d+ or .als\d+
                if (!Regex.IsMatch(extension, @"^\.ap\d+$") &&
                    !Regex.IsMatch(extension, @"^\.als\d+$"))
                {
                    throw new McpException("Invalid project file extension. Use .apXX for projects or .alsXX for sessions, where XX=18,19,20,....", McpErrorCode.InvalidParams);
                }

                bool success = false;

                if (extension.StartsWith(".ap"))
                {
                    success = Portal.OpenProject(path);
                }
                if (extension.StartsWith(".als"))
                {
                    success = Portal.OpenSession(path);
                }

                if (success)
                {
                    return new ResponseOpenProject
                    {
                        Message = $"Project '{path}' opened",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    var detail = Portal.LastConnectError;
                    throw new McpException(
                        string.IsNullOrWhiteSpace(detail)
                            ? $"Failed to open project '{path}'"
                            : $"Failed to open project '{path}': {detail}",
                        McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error opening project '{path}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AttachToOpenProject"), Description("[L1][Project]Attach MCP to an already-open TIA Portal project by name (avoids disposed project handles).")]
        public static ResponseMessage AttachToOpenProject(
            [Description("projectName: name shown in TIA (e.g. '项目1')")] string projectName)
        {
            try
            {
                var ok = Portal.AttachToOpenProject(projectName);
                if (ok)
                {
                    return new ResponseMessage
                    {
                        Message = $"Attached to open project '{projectName}'",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Failed to attach to open project '{projectName}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching to open project '{projectName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CreateProject"), Description("[L1][Project] Create a new empty TIA Portal project. Requires: Connect. After creation, call AddDevice to add PLCs/HMIs, then GetProjectTree to verify. The project is automatically opened after creation — no separate OpenProject call needed.")]
        public static ResponseMessage CreateProject(
            [Description("directoryPath: folder where project will be created")] string directoryPath,
            [Description("projectName: project name")] string projectName)
        {
            try
            {
                if (Portal.ProjectIsValid)
                {
                    Portal.CloseProject();
                }
                var ok = Portal.CreateProject(directoryPath, projectName);
                if (!ok)
                    throw new McpException($"Failed to create project '{projectName}' in '{directoryPath}'", McpErrorCode.InternalError);

                return new ResponseMessage
                {
                    Message = $"Project '{projectName}' created in '{directoryPath}'",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error creating project: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // NOTE: Deprecated demo tool removed.
        // In V21, prefer importing block XML via ImportBlock/ImportBlocksFromDirectory, then CompileSoftware.

        [McpServerTool(Name = "ScaffoldProject"), Description("[L1][Project] One-shot project generator: from a single JSON spec it creates the project, adds PLC (and optional Unified HMI) hardware, builds UDTs/global DBs/PLC tag tables, imports SCL external sources and LAD S7DCL documents, compiles, sets up the HMI connection/screens/tags, and saves — collapsing the ~20-step runbook into one call. Auto-connects if needed. Critical-step failures (connect/createProject/PLC device) abort; per-element failures are collected and reported. Spec keys: projectName(required); directoryPath?(default %TEMP%); plcName?(PLC_1); plcFamily?(S7-1500); plcMlfb?; hmiName?(omit to skip all HMI); hmiFamily?(WinCCUnifiedPC); hmiSoftwarePath?(HMI_RT_1); connectionName?(HMI_Connection_1); udt?/globalDb?/tagTable? = arrays of the same json objects PlcBuildAndImport accepts; sclSourceFiles? = array of .scl file paths; ladDocs? = array of {importPath,name}; hmiScreens? = array of {screenName,width,height,designJson(object)}; hmiTags? = array of {tagTableName?,tagName,hmiDataType?,plcTag?,address?}; compile?(true); save?(true). Returns a per-step report with compile error/warning counts. dryRun DEFAULTS TO TRUE (safety): the default call only validates the spec offline (PLC block JSON shapes, SCL/LAD file paths, designJson) WITHOUT connecting to TIA or creating anything; after a clean dry run, call again with dryRun=false to actually create the project.")]
        public static ResponseScaffold ScaffoldProject(
            [Description("spec: JSON object describing the project to generate. See tool description for keys.")] string spec,
            [Description("dryRun: DEFAULT true — validates the spec offline (no TIA connection, nothing created). Pass dryRun=false explicitly to actually create the project (after a clean dry run).")] bool dryRun = true)
        {
            var resp = new ResponseScaffold { Ok = true };
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });

            JsonNode root;
            try { root = JsonNode.Parse(spec) ?? throw new Exception("spec parsed to null"); }
            catch (Exception ex) { throw new McpException($"ScaffoldProject: invalid spec JSON: {ex.Message}", McpErrorCode.InvalidParams); }

            string S(string key, string def = "") { try { return root[key]?.GetValue<string>() ?? def; } catch { return def; } }
            bool B(string key, bool def) { try { return root[key] is JsonNode n ? n.GetValue<bool>() : def; } catch { return def; } }
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            string IS(JsonNode? n, string key, string def = "") { try { return n?[key]?.GetValue<string>() ?? def; } catch { return def; } }
            uint IU(JsonNode? n, string key) { try { return (uint)(n?[key]?.GetValue<int>() ?? 0); } catch { return 0; } }

            var projectName = S("projectName");
            if (string.IsNullOrWhiteSpace(projectName))
                throw new McpException("ScaffoldProject: 'projectName' is required", McpErrorCode.InvalidParams);
            var directoryPath = S("directoryPath");
            if (string.IsNullOrWhiteSpace(directoryPath))
                directoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia_mcp_scaffold_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var plcName = S("plcName", "PLC_1");
            var plcFamily = S("plcFamily", "S7-1500");
            var plcMlfb = S("plcMlfb");
            var hmiName = S("hmiName");
            var hmiFamily = S("hmiFamily", "WinCCUnifiedPC");
            var hmiSoftwarePathSpec = S("hmiSoftwarePath"); // empty = auto-probe the real path after device add
            var connectionName = S("connectionName", "HMI_Connection_1");
            resp.ProjectName = projectName;
            resp.DirectoryPath = directoryPath;

            // ---- dryRun: offline spec validation only (no TIA connection, nothing created) ----
            if (dryRun)
            {
                foreach (var pair in new[] { ("udt", "udt"), ("globalDb", "globaldb"), ("tagTable", "tagtable") })
                    foreach (var item in Arr(pair.Item1))
                    {
                        try { PlcBuildAndImport(plcName, pair.Item2, item!.ToJsonString(), "", "", "", false, true); Step(pair.Item2, "ok", "dryRun: XML built"); }
                        catch (Exception ex) { Step(pair.Item2, "failed", ex.Message); resp.Ok = false; }
                    }
                foreach (var item in Arr("sclSourceFiles"))
                {
                    string path; try { path = item?.GetValue<string>() ?? ""; } catch { path = ""; }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    bool exists = System.IO.File.Exists(path);
                    Step("scl", exists ? "ok" : "failed", (exists ? "exists: " : "MISSING: ") + path); if (!exists) resp.Ok = false;
                }
                foreach (var item in Arr("ladDocs"))
                {
                    var importPath = IS(item, "importPath"); var name = IS(item, "name");
                    bool exists = !string.IsNullOrWhiteSpace(importPath) && !string.IsNullOrWhiteSpace(name) && System.IO.File.Exists(System.IO.Path.Combine(importPath, name + ".s7dcl"));
                    Step("lad", exists ? "ok" : "failed", exists ? $"{name} (.s7dcl found)" : $"MISSING .s7dcl under {importPath} for {name}"); if (!exists) resp.Ok = false;
                }
                foreach (var item in Arr("hmiScreens"))
                {
                    var screenName = IS(item, "screenName");
                    bool ok = !string.IsNullOrWhiteSpace(screenName) && item?["designJson"] != null;
                    Step("hmiScreen", ok ? "ok" : "failed", ok ? screenName : "missing screenName/designJson"); if (!ok) resp.Ok = false;
                }
                var okN = resp.Steps.Count(s => s.Status == "ok");
                var failN = resp.Steps.Count(s => s.Status == "failed");
                resp.Message = $"ScaffoldProject dryRun '{projectName}': {okN} ok, {failN} failed (offline validation, nothing created)." +
                    (failN == 0 ? " Spec is valid — call ScaffoldProject again with dryRun=false to actually create the project." : " Fix the failed steps, then re-run.");
                resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok, ["dryRun"] = true };
                return resp;
            }

            // ---- critical: connect + create project + PLC device ----
            try
            {
                if (!Portal.IsConnected()) { Connect(); Step("connect", "ok"); }
                else Step("connect", "skipped", "already connected");
            }
            catch (Exception ex) { Step("connect", "failed", ex.Message); resp.Ok = false; throw new McpException($"ScaffoldProject aborted at connect: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try { CreateProject(directoryPath, projectName); Step("createProject", "ok", directoryPath); }
            catch (Exception ex) { Step("createProject", "failed", ex.Message); resp.Ok = false; throw new McpException($"ScaffoldProject aborted at createProject: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try
            {
                var d = AddDeviceWithFallback(plcMlfb, "", plcName, plcFamily);
                if (d.Ok == true) Step("addDevicePlc", "ok", $"{plcName} {d.MlfbUsed}");
                else throw new McpException($"PLC device add failed: {d.Error}", McpErrorCode.InternalError);
            }
            catch (McpException) { Step("addDevicePlc", "failed", "see error"); resp.Ok = false; throw; }
            catch (Exception ex) { Step("addDevicePlc", "failed", ex.Message); resp.Ok = false; throw new McpException($"ScaffoldProject aborted at addDevicePlc: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            // ---- optional HMI device ----
            bool hmiRequested = !string.IsNullOrWhiteSpace(hmiName);
            bool hmiDeviceOk = false;
            if (hmiRequested)
            {
                try
                {
                    var d = AddDeviceWithFallback("", "", hmiName, hmiFamily);
                    if (d.Ok == true) { hmiDeviceOk = true; Step("addDeviceHmi", "ok", $"{hmiName} {d.MlfbUsed}"); }
                    else { Step("addDeviceHmi", "failed", d.Error); resp.Ok = false; }
                }
                catch (Exception ex) { Step("addDeviceHmi", "failed", ex.Message); resp.Ok = false; }
            }

            // ---- PLC elements (per-item collect) ----
            void BuildList(string key, string kind)
            {
                foreach (var item in Arr(key))
                {
                    try { PlcBuildAndImport(plcName, kind, item!.ToJsonString(), "", "", "", false, false); Step(kind, "ok"); }
                    catch (Exception ex) { Step(kind, "failed", ex.Message); resp.Ok = false; }
                }
            }
            BuildList("udt", "udt");
            BuildList("globalDb", "globaldb");
            BuildList("tagTable", "tagtable");

            // ---- SCL external sources ----
            foreach (var item in Arr("sclSourceFiles"))
            {
                string path; try { path = item?.GetValue<string>() ?? ""; } catch { path = ""; }
                if (string.IsNullOrWhiteSpace(path)) continue;
                var srcName = System.IO.Path.GetFileName(path);
                try { ImportPlcExternalSource(plcName, "", path); GenerateBlocksFromExternalSource(plcName, srcName); Step("scl", "ok", srcName); }
                catch (Exception ex) { Step("scl", "failed", $"{srcName}: {ex.Message}"); resp.Ok = false; }
            }

            // ---- LAD via S7DCL documents ----
            foreach (var item in Arr("ladDocs"))
            {
                var importPath = IS(item, "importPath");
                var name = IS(item, "name");
                if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(name)) { Step("lad", "skipped", "missing importPath/name"); continue; }
                try { ImportFromDocuments(plcName, "", importPath, name); Step("lad", "ok", name); }
                catch (Exception ex) { Step("lad", "failed", $"{name}: {ex.Message}"); resp.Ok = false; }
            }

            // ---- compile ----
            if (B("compile", true))
            {
                try
                {
                    var c = CompileAndDiagnosePlc(plcName);
                    resp.CompileState = c.State; resp.CompileErrorCount = c.ErrorCount; resp.CompileWarningCount = c.WarningCount;
                    bool clean = (c.ErrorCount ?? 0) == 0;
                    Step("compile", clean ? "ok" : "failed", $"state={c.State} errors={c.ErrorCount} warnings={c.WarningCount}");
                    if (!clean) resp.Ok = false;
                }
                catch (Exception ex) { Step("compile", "failed", ex.Message); resp.Ok = false; }
            }

            // ---- HMI connection / screens / tags ----
            if (hmiRequested && hmiDeviceOk)
            {
                // Resolve the real Unified HMI software path instead of assuming HMI_RT_1 — it varies
                // with device naming. Probe candidates with GetHmiProgramInfo (first that succeeds wins).
                string hmiPath = "";
                var candidates = new List<string> { hmiSoftwarePathSpec, "HMI_RT_1", hmiName + ".HMI_RT_1", hmiName + "_RT_1", hmiName };
                foreach (var c in candidates)
                {
                    if (string.IsNullOrWhiteSpace(c)) continue;
                    try { GetHmiProgramInfo(c); hmiPath = c; break; } catch { }
                }

                if (string.IsNullOrWhiteSpace(hmiPath))
                {
                    Step("hmiResolve", "failed", $"could not resolve HMI software path (tried: {string.Join(", ", candidates.Where(x => !string.IsNullOrWhiteSpace(x)))})");
                    resp.Ok = false;
                }
                else
                {
                    Step("hmiResolve", "ok", hmiPath);

                    try { EnsureUnifiedHmiConnection(hmiPath, connectionName, plcName); Step("hmiConnection", "ok"); }
                    catch (Exception ex) { Step("hmiConnection", "failed", ex.Message); resp.Ok = false; }

                    foreach (var item in Arr("hmiScreens"))
                    {
                        var screenName = IS(item, "screenName");
                        if (string.IsNullOrWhiteSpace(screenName)) continue;
                        try
                        {
                            EnsureUnifiedHmiScreen(hmiPath, screenName, IU(item, "width"), IU(item, "height"));
                            var design = item?["designJson"];
                            if (design != null) ApplyUnifiedHmiScreenDesignJson(hmiPath, screenName, design.ToJsonString(), true);
                            Step("hmiScreen", "ok", screenName);
                        }
                        catch (Exception ex) { Step("hmiScreen", "failed", $"{screenName}: {ex.Message}"); resp.Ok = false; }
                    }

                    foreach (var item in Arr("hmiTags"))
                    {
                        var tagName = IS(item, "tagName");
                        if (string.IsNullOrWhiteSpace(tagName)) continue;
                        var tagTable = IS(item, "tagTableName", "Default tag table");
                        var dt = IS(item, "hmiDataType", "Bool");
                        var plcTag = IS(item, "plcTag");
                        var address = IS(item, "address");
                        try { EnsureUnifiedHmiTag(hmiPath, tagTable, tagName, dt, plcName, plcTag, connectionName, address, true); Step("hmiTag", "ok", tagName); }
                        catch (Exception ex) { Step("hmiTag", "failed", $"{tagName}: {ex.Message}"); resp.Ok = false; }
                    }
                }
            }

            // ---- save ----
            if (B("save", true))
            {
                try { SaveProject(); Step("save", "ok"); }
                catch (Exception ex) { Step("save", "failed", ex.Message); resp.Ok = false; }
            }

            var okCount = resp.Steps.Count(s => s.Status == "ok");
            var failCount = resp.Steps.Count(s => s.Status == "failed");
            resp.Message = $"ScaffoldProject '{projectName}': {okCount} ok, {failCount} failed; compile state={resp.CompileState ?? "(skipped)"} errors={resp.CompileErrorCount}.";
            resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok };
            return resp;
        }

        [McpServerTool(Name = "SaveProject"), Description("[L1][Project] Save the currently open project or session to disk. Requires: Connect + OpenProject. Call after any significant change (device add, block import, HMI edit). Compile first if there are pending changes to ensure consistency.")]
        public static ResponseSaveProject SaveProject()
        {
            try
            {
                if (Portal.IsLocalSession)
                {
                    if (Portal.SaveSession())
                    {
                        return new ResponseSaveProject
                        {
                            Message = "Local session saved",
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
                        };
                    }
                    else
                    {
                        throw new McpException("Failed to save local session", McpErrorCode.InternalError);
                    }
                }
                else
                {
                    if (Portal.SaveProject())
                    {
                        return new ResponseSaveProject
                        {
                            Message = "Local project saved",
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
                        };
                    }
                    else
                    {
                        throw new McpException("Failed to save project", McpErrorCode.InternalError);
                    }
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error saving local project/session: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SaveAsProject"), Description("[L2][Project]Save current TIA-Portal project/session with a new name")]
        public static ResponseSaveAsProject SaveAsProject(
            [Description("newProjectPath: defines the new path where to save the project")] string newProjectPath)
        {
            try
            {
                if (Portal.IsLocalSession)
                {
                    throw new McpException($"Cannot save local session as '{newProjectPath}'", McpErrorCode.InvalidParams);
                }
                else
                {
                    if (Portal.SaveAsProject(newProjectPath))
                    {
                        return new ResponseSaveAsProject
                        {
                            Message = $"Local project saved as '{newProjectPath}'",
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
                        };
                    }
                    else
                    {
                        throw new McpException($"Failed saving local project as '{newProjectPath}'", McpErrorCode.InternalError);
                    }
                }

            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error saving local project/session as '{newProjectPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CloseProject"), Description("[L1][Project] Close the currently open project or multi-user session. Requires: Connect + OpenProject. Any unsaved changes are lost — call SaveProject first. After closing, the connection remains active but no project is open.")]
        public static ResponseCloseProject CloseProject()
        {
            try
            {
                bool success;

                if (Portal.IsLocalSession)
                {
                    success = Portal.CloseSession();
                    if (success)
                    {
                        return new ResponseCloseProject
                        {
                            Message = "Local session closed",
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
                        };
                    }
                    else
                    {
                        throw new McpException("Failed closing local session", McpErrorCode.InternalError);
                    }
                }
                else
                {
                    success = Portal.CloseProject();
                    if (success)
                    {
                        return new ResponseCloseProject
                        {
                            Message = "Local project closed",
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = true
                            }
                        };
                    }
                    else
                    {
                        throw new McpException("Failed closing project", McpErrorCode.InternalError);
                    }
                }

            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error closing local project/session: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
