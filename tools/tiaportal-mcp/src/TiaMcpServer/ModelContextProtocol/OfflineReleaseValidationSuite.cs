using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// 发布级离线总验收套件。
    /// 用于交付/商用前一条命令验证核心离线能力和在线安全红线，不连接 TIA Portal，不修改工程。
    /// </summary>
    public static class OfflineReleaseValidationSuite
    {
        public static JsonObject Run(string workspaceRoot, string reportDirectory)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
                throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));
            if (string.IsNullOrWhiteSpace(reportDirectory))
                throw new ArgumentException("Report directory is required.", nameof(reportDirectory));

            workspaceRoot = Path.GetFullPath(workspaceRoot);
            Directory.CreateDirectory(reportDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var suiteDir = Path.Combine(reportDirectory, "suite_" + stamp);
            Directory.CreateDirectory(suiteDir);

            var plcFixtureDir = Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify");
            var hmiTemplateDir = Path.Combine(workspaceRoot, "docs", "hmi_templates");
            var hmiTemplateMappingPath = Path.Combine(workspaceRoot, "docs", "hmi_template_mappings", "tia-hmi-template-plc-mapping.release.json");

            var plcSuite = PlcBuilderOfflineValidationSuite.Run(plcFixtureDir, Path.Combine(suiteDir, "plc_builder"));
            var classicSuite = ClassicHmiOfflineValidationSuite.Run(Path.Combine(suiteDir, "classic_hmi"));
            var classicImportPreflight = ClassicHmiTemporaryImportPreflightSuite.Run(workspaceRoot, Path.Combine(suiteDir, "classic_hmi_temporary_import_preflight"));
            var plcSymbolProbe = PlcSymbolManifestBuilder.RunProbe(Path.Combine(suiteDir, "plc_symbol_manifest"));
            // 发布闸必须跑真的执行 JSON 构建检查：以前这里不传委托，等于把「没报错」当成「验过了」，对外发版从没验过这条路径。
            var hmiLayout = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(hmiTemplateDir, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
            WriteJsonAndMarkdown(
                hmiLayout,
                Path.Combine(suiteDir, "hmi_template_layout", "hmi_template_layout_release_" + stamp + ".json"),
                Path.Combine(suiteDir, "hmi_template_layout", "hmi_template_layout_release_" + stamp + ".md"),
                BuildHmiLayoutMarkdown);
            var hmiAction = HmiActionScriptRecipeBuilder.RunProbe(hmiTemplateDir, Path.Combine(suiteDir, "hmi_action_script_recipe"));
            var hmiPlcSync = HmiTemplatePlcSyncPrecheckSuite.Run(hmiTemplateDir, plcFixtureDir, Path.Combine(suiteDir, "hmi_template_plc_sync_precheck"), hmiTemplateMappingPath);
            var safety = BuildSafetyResult();

            var items = new JsonArray(
                SuiteItem("plc-builder-offline-suite", "PLC Builder 离线套件", plcSuite),
                SuiteItem("classic-hmi-offline-suite", "Classic HMI 离线套件", classicSuite),
                SuiteItem("classic-hmi-temporary-import-preflight", "Classic HMI 临时导入预检", classicImportPreflight),
                SuiteItem("plc-symbol-manifest-probe", "PLC 符号清单提取探针", plcSymbolProbe),
                SuiteItem("unified-hmi-template-layout", "Unified HMI 模板布局 QA", hmiLayout),
                SuiteItem("hmi-action-script-recipe", "HMI 动作脚本配方探针", hmiAction),
                SuiteItem("hmi-template-plc-sync-precheck", "Unified HMI 模板 PLC 符号同步预检", hmiPlcSync),
                SuiteItem("online-monitoring-safety", "在线监视安全红线自检", safety));

            var ok = items.OfType<JsonObject>().All(x => x["ok"]?.GetValue<bool>() == true);
            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "tia-mcp-offline-release-validation-suite",
                ["offlineOnly"] = true,
                ["workspaceRoot"] = workspaceRoot,
                ["suiteDirectory"] = suiteDir,
                ["ok"] = ok,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "发布级离线验收：不连接 TIA Portal，不打开工程，不导入 PLC/HMI 对象。",
                    ["write"] = "只写 reports 目录下的套件文件和报告，不修改工程、reference 或交付包。",
                    ["online"] = "在线监视仅执行静态安全自检，确认没有 Force 工具并保留只读红线。"
                },
                ["items"] = items,
                ["plcSuite"] = plcSuite,
                ["classicSuite"] = classicSuite,
                ["classicImportPreflight"] = classicImportPreflight,
                ["plcSymbolProbe"] = plcSymbolProbe,
                ["hmiLayout"] = hmiLayout,
                ["hmiAction"] = hmiAction,
                ["hmiTemplatePlcSyncPrecheck"] = hmiPlcSync,
                ["onlineSafety"] = safety
            };

            var diagnostics = ReleaseDiagnosticReportBuilder.Build(root);
            root["diagnostics"] = diagnostics;

            var jsonPath = Path.Combine(reportDirectory, "offline_release_validation_suite_" + stamp + ".json");
            var mdPath = Path.Combine(reportDirectory, "offline_release_validation_suite_" + stamp + ".md");
            var diagJsonPath = Path.Combine(reportDirectory, "offline_release_diagnostics_" + stamp + ".json");
            var diagMdPath = Path.Combine(reportDirectory, "offline_release_diagnostics_" + stamp + ".md");
            var runbookJsonPath = Path.Combine(reportDirectory, "offline_release_runbook_" + stamp + ".json");
            var runbookMdPath = Path.Combine(reportDirectory, "offline_release_runbook_" + stamp + ".md");
            var manifestJsonPath = Path.Combine(reportDirectory, "offline_release_manifest_" + stamp + ".json");
            var manifestMdPath = Path.Combine(reportDirectory, "offline_release_manifest_" + stamp + ".md");
            var readinessGateJsonPath = Path.Combine(reportDirectory, "offline_release_readiness_gate_" + stamp + ".json");
            var readinessGateMdPath = Path.Combine(reportDirectory, "offline_release_readiness_gate_" + stamp + ".md");
            root["jsonPath"] = jsonPath;
            root["markdownPath"] = mdPath;
            File.WriteAllText(diagJsonPath, diagnostics.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(diagMdPath, ReleaseDiagnosticReportBuilder.BuildMarkdown(diagnostics, diagJsonPath), Encoding.UTF8);
            diagnostics["jsonPath"] = diagJsonPath;
            diagnostics["markdownPath"] = diagMdPath;
            root["diagnosticJsonPath"] = diagJsonPath;
            root["diagnosticMarkdownPath"] = diagMdPath;

            var runbook = ReleaseRunbookBuilder.Build(root, diagnostics);
            File.WriteAllText(runbookJsonPath, runbook.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(runbookMdPath, ReleaseRunbookBuilder.BuildMarkdown(runbook, runbookJsonPath), Encoding.UTF8);
            runbook["jsonPath"] = runbookJsonPath;
            runbook["markdownPath"] = runbookMdPath;
            root["runbook"] = runbook;

            root["runbookJsonPath"] = runbookJsonPath;
            root["runbookMarkdownPath"] = runbookMdPath;
            var manifest = ReleaseManifestBuilder.Build(root, diagnostics, runbook);
            File.WriteAllText(manifestJsonPath, manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(manifestMdPath, ReleaseManifestBuilder.BuildMarkdown(manifest, manifestJsonPath), Encoding.UTF8);
            manifest["jsonPath"] = manifestJsonPath;
            manifest["markdownPath"] = manifestMdPath;
            root["manifest"] = manifest;
            root["manifestJsonPath"] = manifestJsonPath;
            root["manifestMarkdownPath"] = manifestMdPath;

            var readinessGate = manifest["releaseReadinessGate"] as JsonObject
                ?? ReleaseReadinessGateBuilder.Build(root, diagnostics, runbook, manifest);
            File.WriteAllText(readinessGateJsonPath, readinessGate.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(readinessGateMdPath, ReleaseReadinessGateBuilder.BuildMarkdown(readinessGate, readinessGateJsonPath), Encoding.UTF8);
            readinessGate["jsonPath"] = readinessGateJsonPath;
            readinessGate["markdownPath"] = readinessGateMdPath;
            root["releaseReadinessGate"] = readinessGate.DeepClone();
            root["releaseReadinessGateJsonPath"] = readinessGateJsonPath;
            root["releaseReadinessGateMarkdownPath"] = readinessGateMdPath;

            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildMarkdown(root, jsonPath), Encoding.UTF8);
            return root;
        }

        private static JsonObject SuiteItem(string id, string title, JsonObject result)
        {
            return new JsonObject
            {
                ["id"] = id,
                ["title"] = title,
                ["ok"] = result["ok"]?.GetValue<bool>() == true,
                ["summary"] = BuildSummary(result),
                ["markdownPath"] = result["markdownPath"]?.ToString() ?? "",
                ["jsonPath"] = result["jsonPath"]?.ToString() ?? ""
            };
        }

        private static string BuildSummary(JsonObject result)
        {
            if (result["items"] is JsonArray items) return "items=" + items.Count;
            if (result["symbolCount"] != null) return "symbolCount=" + result["symbolCount"];
            if (result["templateCount"] != null) return "templateCount=" + result["templateCount"] + ", failed=" + (result["failed"]?.ToString() ?? "0");
            if (result["generatedActionCount"] != null) return "generatedActionCount=" + result["generatedActionCount"];
            if (result["checkedTools"] != null) return "checkedTools=" + result["checkedTools"];
            return "ok=" + result["ok"];
        }

        private static JsonObject BuildSafetyResult()
        {
            var response = McpServer.RunOnlineMonitoringSafetySelfTest();
            return new JsonObject
            {
                ["format"] = "online-monitoring-safety-self-test-summary-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["ok"] = response.Ok == true,
                ["message"] = response.Message ?? "",
                ["checkedTools"] = response.Meta?["checkedTools"]?.GetValue<int>() ?? 0,
                ["policy"] = new JsonArray((response.Policy ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["items"] = new JsonArray((response.Items ?? Array.Empty<CapabilitySelfTestItem>()).Select(x => new JsonObject
                {
                    ["id"] = x.Id ?? "",
                    ["name"] = x.Name ?? "",
                    ["status"] = x.Status ?? "",
                    ["detail"] = x.Detail ?? ""
                }).ToArray())
            };
        }

        private static void WriteJsonAndMarkdown(JsonObject root, string jsonPath, string mdPath, Func<JsonObject, string, string> markdownBuilder)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, markdownBuilder(root, jsonPath), Encoding.UTF8);
            root["jsonPath"] = jsonPath;
            root["markdownPath"] = mdPath;
        }

        private static string BuildHmiLayoutMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# Unified HMI Template Layout Release QA");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Template count: " + root["templateCount"]);
            md.AppendLine("- Failed: " + root["failed"]);
            md.AppendLine("- Warnings: " + root["warnings"]);
            return md.ToString();
        }

        private static string BuildMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# TIA MCP Offline Release Validation Suite");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- 发布级离线验收，不连接 TIA Portal，不打开工程，不导入 PLC/HMI 对象。");
            md.AppendLine("- 只写 reports 目录下的套件文件和报告，不修改工程、reference 或交付包。");
            md.AppendLine("- 在线监视仅执行静态安全自检，确认无 Force 工具并保留只读红线。");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Suite directory: " + root["suiteDirectory"]);
            md.AppendLine("- Diagnostic report: " + root["diagnosticMarkdownPath"]);
            md.AppendLine("- Runbook: " + root["runbookMarkdownPath"]);
            md.AppendLine("- Manifest: " + root["manifestMarkdownPath"]);
            md.AppendLine();
            md.AppendLine("## Items");
            foreach (var node in root["items"] as JsonArray ?? new JsonArray())
            {
                if (node is not JsonObject item) continue;
                md.AppendLine("- " + item["title"] + ": " + (item["ok"]?.GetValue<bool>() == true ? "PASS" : "FAIL") + " (" + item["summary"] + ")");
                if (!string.IsNullOrWhiteSpace(item["markdownPath"]?.ToString()))
                    md.AppendLine("  - report: " + item["markdownPath"]);
            }
            md.AppendLine();
            md.AppendLine("## Next Validation");
            md.AppendLine("- 该套件通过后，下一步仍需执行临时 TIA 工程导入/读回/编译诊断，补齐真实 Openness 验证链。");
            return md.ToString();
        }
    }
}
