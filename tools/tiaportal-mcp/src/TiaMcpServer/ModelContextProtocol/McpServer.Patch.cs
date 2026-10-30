using ModelContextProtocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // PatchProject — apply a spec to an EXISTING project (upsert), used by `tia patch`.
    // Mirrors ScaffoldProject's element handling but OPENS an existing project instead of
    // creating one and does not add hardware. Reuses the same engine statics, so behaviour
    // stays consistent with `tia gen`. Kept in a partial file so the McpServer god-file is
    // not touched.
    public static partial class McpServer
    {
        public static ResponseScaffold PatchProject(string spec, bool dryRun = false, bool noOverwrite = false)
        {
            var resp = new ResponseScaffold { Ok = true };
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });

            JsonNode root;
            try { root = JsonNode.Parse(spec) ?? throw new Exception("spec parsed to null"); }
            catch (Exception ex) { throw new McpException($"PatchProject: invalid spec JSON: {ex.Message}", McpErrorCode.InvalidParams); }

            string S(string key, string def = "") { try { return root[key]?.GetValue<string>() ?? def; } catch { return def; } }
            bool B(string key, bool def) { try { return root[key] is JsonNode n ? n.GetValue<bool>() : def; } catch { return def; } }
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            string IS(JsonNode? n, string key, string def = "") { try { return n?[key]?.GetValue<string>() ?? def; } catch { return def; } }
            uint IU(JsonNode? n, string key) { try { return (uint)(n?[key]?.GetValue<int>() ?? 0); } catch { return 0; } }

            var projectPath = S("projectPath");
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new McpException("PatchProject: 'projectPath' is required (the .apXX to open)", McpErrorCode.InvalidParams);
            // Openness resolves relative paths against the exe dir; resolve against CWD so a
            // relative projectPath in the spec opens the project the user actually means.
            projectPath = Path.GetFullPath(projectPath);
            var plcName = S("plcName", "PLC_1");
            var ladOption = noOverwrite ? "None" : "Override";
            resp.ProjectName = Path.GetFileNameWithoutExtension(projectPath);
            resp.DirectoryPath = projectPath;

            // ---- dryRun: offline validation only ----
            if (dryRun)
            {
                bool projExists = File.Exists(projectPath);
                Step("projectPath", projExists ? "ok" : "failed", (projExists ? "exists: " : "MISSING: ") + projectPath);
                if (!projExists) resp.Ok = false;
                foreach (var pair in new[] { ("udt", "udt"), ("globalDb", "globaldb"), ("tagTable", "tagtable") })
                    foreach (var item in Arr(pair.Item1))
                    {
                        try { PlcBuildAndImport(plcName, pair.Item2, item!.ToJsonString(), "", "", "", false, true); Step(pair.Item2, "ok", "dryRun: XML built"); }
                        catch (Exception ex) { Step(pair.Item2, "failed", ex.Message); resp.Ok = false; }
                    }
                foreach (var item in Arr("sclSourceFiles"))
                {
                    string p; try { p = item?.GetValue<string>() ?? ""; } catch { p = ""; }
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    bool ex = File.Exists(p);
                    Step("scl", ex ? "ok" : "failed", (ex ? "exists: " : "MISSING: ") + p); if (!ex) resp.Ok = false;
                }
                foreach (var item in Arr("ladDocs"))
                {
                    var importPath = IS(item, "importPath"); var name = IS(item, "name");
                    bool ex = !string.IsNullOrWhiteSpace(importPath) && !string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(importPath, name + ".s7dcl"));
                    Step("lad", ex ? "ok" : "failed", ex ? $"{name} (.s7dcl found)" : $"MISSING .s7dcl under {importPath} for {name}"); if (!ex) resp.Ok = false;
                }
                foreach (var item in Arr("hmiScreens"))
                {
                    var screenName = IS(item, "screenName");
                    bool ok = !string.IsNullOrWhiteSpace(screenName) && item?["designJson"] != null;
                    Step("hmiScreen", ok ? "ok" : "failed", ok ? screenName : "missing screenName/designJson"); if (!ok) resp.Ok = false;
                }
                var okN = resp.Steps.Count(s => s.Status == "ok");
                var failN = resp.Steps.Count(s => s.Status == "failed");
                resp.Message = $"PatchProject dryRun '{resp.ProjectName}': {okN} ok, {failN} failed (offline validation, nothing changed).";
                resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok, ["dryRun"] = true };
                return resp;
            }

            // ---- critical: connect + open existing project ----
            try
            {
                if (!Portal.IsConnected()) { Connect(); Step("connect", "ok"); }
                else Step("connect", "skipped", "already connected");
            }
            catch (Exception ex) { Step("connect", "failed", ex.Message); resp.Ok = false; throw new McpException($"PatchProject aborted at connect: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try { OpenProject(projectPath); Step("openProject", "ok", projectPath); }
            catch (Exception ex) { Step("openProject", "failed", ex.Message); resp.Ok = false; throw new McpException($"PatchProject aborted at openProject: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            // ---- PLC elements (per-item collect; re-import = upsert) ----
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

            foreach (var item in Arr("sclSourceFiles"))
            {
                string p; try { p = item?.GetValue<string>() ?? ""; } catch { p = ""; }
                if (string.IsNullOrWhiteSpace(p)) continue;
                var srcName = Path.GetFileName(p);
                try { ImportPlcExternalSource(plcName, "", p); GenerateBlocksFromExternalSource(plcName, srcName); Step("scl", "ok", srcName); }
                catch (Exception ex) { Step("scl", "failed", $"{srcName}: {ex.Message}"); resp.Ok = false; }
            }

            foreach (var item in Arr("ladDocs"))
            {
                var importPath = IS(item, "importPath");
                var name = IS(item, "name");
                if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(name)) { Step("lad", "skipped", "missing importPath/name"); continue; }
                try { ImportFromDocuments(plcName, "", importPath, name, ladOption); Step("lad", "ok", name + (noOverwrite ? " (no-overwrite)" : "")); }
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

            // ---- HMI connection / screens / tags (Ensure* are idempotent upserts) ----
            var hmiName = S("hmiName");
            if (!string.IsNullOrWhiteSpace(hmiName))
            {
                var connectionName = S("connectionName", "HMI_Connection_1");
                var hmiSoftwarePathSpec = S("hmiSoftwarePath");
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
            resp.Message = $"PatchProject '{resp.ProjectName}': {okCount} ok, {failCount} failed; compile state={resp.CompileState ?? "(skipped)"} errors={resp.CompileErrorCount}.";
            resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok };
            return resp;
        }
    }
}
