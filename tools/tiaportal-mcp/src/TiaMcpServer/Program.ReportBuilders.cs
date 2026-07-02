using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;


namespace TiaMcpServer
{
    // Partial: report/analysis builders. Extracted from Program.cs (god-file split); behavior unchanged.
    public partial class Program
    {
        private static void TryWriteText(string path, string content)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(content))
                    return;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogDiag("TryWriteText failed for " + path + ": " + ex.Message);
            }
        }

        private static string FindLatestReport(string reportDir, string searchPattern)
        {
            if (!Directory.Exists(reportDir))
                return "";

            return Directory.EnumerateFiles(reportDir, searchPattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault() ?? "";
        }

        private static string GetWorkspaceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "TMP_EXPORT")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "tools")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "TMP_EXPORT")) &&
                    Directory.Exists(Path.Combine(current.FullName, "tools")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return Environment.CurrentDirectory;
        }

        private static void RunAnalyzeReferenceAssets(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var referenceProject = string.IsNullOrWhiteSpace(options.ReferenceProjectPath)
                ? Path.Combine(workspaceRoot, "reference", "XM_Mxxxx_PL007N_MP301_002_V21")
                : options.ReferenceProjectPath!;
            var referenceLibrary = string.IsNullOrWhiteSpace(options.ReferenceGlobalLibraryPath)
                ? Path.Combine(workspaceRoot, "reference", "HMI_Template_Suite_WinCC_Unified_V18", "HMI Template Suite (WinCC Unified)_V18_V21")
                : options.ReferenceGlobalLibraryPath!;
            var reportDir = string.IsNullOrWhiteSpace(options.ReferenceReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "reference_analysis")
                : options.ReferenceReportDirectory!;

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "reference_assets_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "reference_assets_" + stamp + ".md");

            var projectInfo = AnalyzeReferenceProject(referenceProject);
            var libraryInfo = AnalyzeReferenceLibrary(referenceLibrary);
            var recommendations = BuildReferenceRecommendations(projectInfo, libraryInfo);

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["safetyPolicy"] = new JsonObject
                {
                    ["onlineMonitor"] = "Only read current variable status. Do not modify watch-table objects while online.",
                    ["force"] = "Force-table and force-related operations are not exposed.",
                    ["deliveryPackage"] = "This analysis does not write to TIA_MCP_DELIVERY_FOR_OTHER_AI."
                },
                ["referenceProject"] = projectInfo,
                ["referenceGlobalLibrary"] = libraryInfo,
                ["recommendations"] = recommendations
            };

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildReferenceAnalysisMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Reference analysis report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static void RunAnalyzeGlobalLibraryPackage(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var libraryPath = string.IsNullOrWhiteSpace(options.GlobalLibraryPackagePath)
                ? string.IsNullOrWhiteSpace(options.ReferenceGlobalLibraryPath)
                    ? Path.Combine(workspaceRoot, "reference", "HMI_Template_Suite_WinCC_Unified_V18", "HMI Template Suite (WinCC Unified)_V18_V21")
                    : options.ReferenceGlobalLibraryPath!
                : options.GlobalLibraryPackagePath!;
            var reportDir = string.IsNullOrWhiteSpace(options.GlobalLibraryReportDirectory)
                ? string.IsNullOrWhiteSpace(options.ReferenceReportDirectory)
                    ? Path.Combine(workspaceRoot, "reports", "global_library_analysis")
                    : options.ReferenceReportDirectory!
                : options.GlobalLibraryReportDirectory!;

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "global_library_package_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "global_library_package_" + stamp + ".md");

            var root = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
            root["timestamp"] = DateTime.Now.ToString("O");
            root["safetyPolicy"] = new JsonObject
            {
                ["mode"] = "Offline file-system analysis only.",
                ["tia"] = "TIA Portal is not connected or opened by this analysis.",
                ["write"] = "No global library content is imported, modified, or written."
            };

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, GlobalLibraryPackageAnalyzer.BuildMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Global library package analysis report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static void RunAnalyzeHmiTemplateReference(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var referenceProject = string.IsNullOrWhiteSpace(options.ReferenceProjectPath)
                ? Path.Combine(workspaceRoot, "reference", "XM_Mxxxx_PL007N_MP301_002_V21")
                : options.ReferenceProjectPath!;
            var referenceLibrary = string.IsNullOrWhiteSpace(options.ReferenceGlobalLibraryPath)
                ? Path.Combine(workspaceRoot, "reference", "HMI_Template_Suite_WinCC_Unified_V18", "HMI Template Suite (WinCC Unified)_V18_V21")
                : options.ReferenceGlobalLibraryPath!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiTemplateReferenceReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_template_reference")
                : options.HmiTemplateReferenceReportDirectory!;

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "hmi_template_reference_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "hmi_template_reference_" + stamp + ".md");

            var root = HmiTemplateReferenceAnalyzer.Analyze(templateDir, referenceProject, referenceLibrary);
            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, HmiTemplateReferenceAnalyzer.BuildMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("HMI template reference analysis report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static void RunAnalyzeHmiComponentCatalog(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var probeJson = string.IsNullOrWhiteSpace(options.GlobalLibraryProbeJsonPath)
                ? FindLatestReport(Path.Combine(workspaceRoot, "reports", "global_library_probe"), "global_library_probe_*.json")
                : options.GlobalLibraryProbeJsonPath!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiComponentCatalogReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_component_catalog")
                : options.HmiComponentCatalogReportDirectory!;

            var root = HmiComponentCatalogAnalyzer.Analyze(probeJson, templateDir);
            HmiComponentCatalogAnalyzer.WriteReports(root, reportDir);

            LogDiag("HMI component catalog report written to:");
            LogDiag(reportDir);
        }

        private static void RunHmiActionScriptRecipeProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiTemplateReferenceReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_action_script_recipe")
                : options.HmiTemplateReferenceReportDirectory!;

            var root = HmiActionScriptRecipeBuilder.RunProbe(templateDir, reportDir);
            LogDiag("HMI action script recipe probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunHmiActionScriptRecipeSafetySelfTest()
        {
            var root = HmiActionScriptRecipeBuilder.RunSafetySelfTest();
            LogDiag("HMI action script recipe safety self-test:");
            LogDiag("OK: " + root["ok"]);
            LogDiag("Cases: " + root["caseCount"]);
            foreach (var node in root["cases"] as JsonArray ?? new JsonArray())
            {
                if (node is not JsonObject item) continue;
                LogDiag("- " + item["id"] + ": pass=" + item["pass"] +
                        ", kind=" + item["recipeKind"] +
                        ", blocked=" + item["actualApplyBlocked"] +
                        ", safeApply=" + item["actualSafeApplyCandidate"]);
            }
        }

        private static void RunHmiTemplateLayoutProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiTemplateLayoutReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_template_layout")
                : options.HmiTemplateLayoutReportDirectory!;
            Directory.CreateDirectory(reportDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "hmi_template_layout_probe_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "hmi_template_layout_probe_" + stamp + ".md");
            var root = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(templateDir, TemplateExecutionJsonBuilds);
            var results = root["results"] as JsonArray ?? new JsonArray();
            var failed = Convert.ToInt32(root["failed"]?.ToString() ?? "0");
            var warningCount = Convert.ToInt32(root["warnings"]?.ToString() ?? "0");
            var templateCount = Convert.ToInt32(root["templateCount"]?.ToString() ?? "0");

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);

            var md = new StringBuilder();
            md.AppendLine("# HMI Template Layout Probe");
            md.AppendLine();
            md.AppendLine("- TemplateDirectory: `" + templateDir + "`");
            md.AppendLine("- Result: `" + (failed == 0 ? "PASS" : "FAIL") + "`");
            md.AppendLine("- Templates: `" + templateCount + "`");
            md.AppendLine("- Warnings: `" + warningCount + "`");
            md.AppendLine();
            md.AppendLine("| Template | Status | Size | Items | Theme | Palette | Errors | Warnings |");
            md.AppendLine("|---|---|---:|---:|---|---:|---:|---:|");
            foreach (var row in results.OfType<JsonObject>())
            {
                md.AppendLine("| "
                    + Path.GetFileName(row["file"]?.ToString() ?? "") + " | "
                    + row["status"] + " | "
                    + row["width"] + "x" + row["height"] + " | "
                    + row["itemCount"] + " | "
                    + row["designSystemName"] + " | "
                    + row["paletteColorCount"] + " | "
                    + ((row["errors"] as JsonArray)?.Count ?? 0) + " | "
                    + ((row["warnings"] as JsonArray)?.Count ?? 0) + " |");
            }
            md.AppendLine();
            md.AppendLine("## Notes");
            md.AppendLine();
            md.AppendLine("- 该探针只做离线 JSON/布局质量检查，不连接 TIA，不写项目。");
            md.AppendLine("- 重叠、密度和文字溢出属于启发式警告；越界、重复名称、无效尺寸和执行 JSON 生成失败属于阻断错误。");
            File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);

            LogDiag("HMI template layout probe report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
            LogDiag("OK: " + root["ok"]);
            if (failed > 0)
            {
                throw new InvalidOperationException("HMI template layout probe failed. Report: " + jsonPath);
            }

            bool TemplateExecutionJsonBuilds(string templateFile)
            {
                var templateRoot = JsonNode.Parse(File.ReadAllText(templateFile, Encoding.UTF8)) as JsonObject;
                var items = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                var screen = templateRoot?["Screen"] as JsonObject ?? templateRoot?["screen"] as JsonObject ?? new JsonObject();
                var width = int.TryParse((screen["Width"] ?? screen["width"])?.ToString(), out var parsedWidth) ? parsedWidth : 800;
                var height = int.TryParse((screen["Height"] ?? screen["height"])?.ToString(), out var parsedHeight) ? parsedHeight : 480;
                var execution = JsonNode.Parse(HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(templateFile, width, height)) as JsonObject;
                return execution != null
                    && execution["items"] is JsonArray executionItems
                    && executionItems.Count == items;
            }
        }

        private static void RunGeneratePlcBuilderFixtureReadiness(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderFixtureReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_fixture_readiness")
                : options.PlcBuilderFixtureReportDirectory!;

            var root = PlcBuilderFixtureReadinessAnalyzer.Analyze(fixtureDir);
            PlcBuilderFixtureReadinessAnalyzer.WriteReports(root, reportDir);

            LogDiag("PLC builder fixture readiness report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunClassicHmiMinimalPackageProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.ClassicHmiPackageReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "classic_hmi_minimal_package_probe")
                : options.ClassicHmiPackageReportDirectory!;
            Directory.CreateDirectory(reportDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var packageDir = Path.Combine(reportDir, "package_" + stamp);
            Directory.CreateDirectory(packageDir);

            var packageJson = BuildClassicHmiMinimalPackageProbeJson();
            var writeResult = ClassicHmiMinimalPackageBuilder.WriteFiles(packageJson, packageDir);
            var validateGood = ClassicHmiMinimalPackageBuilder.ValidateFiles(packageDir);
            var plcFixtureDir = Path.Combine(packageDir, "plc_symbol_fixture");
            Directory.CreateDirectory(plcFixtureDir);
            File.WriteAllText(Path.Combine(plcFixtureDir, "MotorTags.xml"), BuildClassicHmiMinimalPackageProbePlcTagTableXml(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(plcFixtureDir, "DB1_MotorData.xml"), BuildClassicHmiMinimalPackageProbePlcDbXml(includeSpeedSet: true), Encoding.UTF8);
            var plcManifest = PlcSymbolManifestBuilder.BuildFromXmlPath(plcFixtureDir);
            var validatePlcSyncGood = ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(packageDir, plcManifest["symbolNames"]?.ToJsonString() ?? "[]");
            var validatePlcSyncBad = ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(packageDir, BuildClassicHmiMinimalPackageProbePlcSymbolsJson(includeSpeedSet: false));

            var badDir = Path.Combine(packageDir, "bad_missing_tag");
            Directory.CreateDirectory(badDir);
            var screenPath = writeResult["screenXmlPath"]?.ToString() ?? "";
            var tagPath = writeResult["tagTableXmlPath"]?.ToString() ?? "";
            var badScreenPath = Path.Combine(badDir, "Bad_Screen.xml");
            var badTagPath = Path.Combine(badDir, "Bad_TagTable.xml");
            if (File.Exists(screenPath)) File.Copy(screenPath, badScreenPath, true);
            var tagXml = File.Exists(tagPath) ? File.ReadAllText(tagPath, Encoding.UTF8) : "";
            tagXml = tagXml.Replace("<Name>Speed_Set</Name>", "<Name>Speed_Set_Deleted</Name>");
            File.WriteAllText(badTagPath, tagXml, Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(badDir, "Bad_manifest.json"),
                @"{""format"":""probe-bad-case"",""tagTableXmlPath"":""Bad_TagTable.xml"",""screenXmlPath"":""Bad_Screen.xml""}",
                Encoding.UTF8);
            var validateBad = ClassicHmiMinimalPackageBuilder.ValidateFiles(badDir);

            var ok = writeResult["ok"]?.GetValue<bool>() == true &&
                     validateGood["ok"]?.GetValue<bool>() == true &&
                     validatePlcSyncGood["ok"]?.GetValue<bool>() == true &&
                     validatePlcSyncBad["ok"]?.GetValue<bool>() != true &&
                     Convert.ToInt32(validatePlcSyncBad["missingPlcSymbolCount"]?.ToString() ?? "0") == 1 &&
                     validateBad["ok"]?.GetValue<bool>() != true &&
                     Convert.ToInt32(validateBad["missingTagCount"]?.ToString() ?? "0") == 1;

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "classic-hmi-minimal-package-offline-probe",
                ["offlineOnly"] = true,
                ["ok"] = ok,
                ["packageDirectory"] = packageDir,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线探针：不连接 TIA Portal，不打开工程，不导入 HMI 文件。",
                    ["write"] = "只写 reports 目录下的临时探针文件，不修改工程、reference 或交付包。",
                    ["binding"] = "验证 HMI 控件动态绑定和按钮事件引用的 HMI tag 是否在变量表 XML 中声明。"
                },
                ["checks"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "write-package",
                        ["ok"] = writeResult["ok"]?.GetValue<bool>() == true,
                        ["fileCount"] = writeResult["fileCount"]?.GetValue<int>() ?? 0
                    },
                    new JsonObject
                    {
                        ["id"] = "validate-good-package",
                        ["ok"] = validateGood["ok"]?.GetValue<bool>() == true,
                        ["declaredTagCount"] = validateGood["declaredTagCount"]?.GetValue<int>() ?? 0,
                        ["referencedTagCount"] = validateGood["referencedTagCount"]?.GetValue<int>() ?? 0,
                        ["missingTagCount"] = validateGood["missingTagCount"]?.GetValue<int>() ?? 0,
                        ["dynamicBindingCount"] = validateGood["screenAnalysis"]?["dynamicBindingCount"]?.GetValue<int>() ?? 0,
                        ["eventActionCount"] = validateGood["screenAnalysis"]?["eventActionCount"]?.GetValue<int>() ?? 0
                    },
                    new JsonObject
                    {
                        ["id"] = "validate-good-plc-symbol-sync",
                        ["ok"] = validatePlcSyncGood["ok"]?.GetValue<bool>() == true,
                        ["controllerTagCount"] = validatePlcSyncGood["controllerTagCount"]?.GetValue<int>() ?? 0,
                        ["missingPlcSymbolCount"] = validatePlcSyncGood["missingPlcSymbolCount"]?.GetValue<int>() ?? 0
                    },
                    new JsonObject
                    {
                        ["id"] = "validate-bad-plc-symbol-sync",
                        ["ok"] = validatePlcSyncBad["ok"]?.GetValue<bool>() != true,
                        ["missingPlcSymbolCount"] = validatePlcSyncBad["missingPlcSymbolCount"]?.GetValue<int>() ?? 0,
                        ["missingPlcSymbols"] = validatePlcSyncBad["missingPlcSymbols"]?.DeepClone()
                    },
                    new JsonObject
                    {
                        ["id"] = "validate-bad-package-missing-tag",
                        ["ok"] = validateBad["ok"]?.GetValue<bool>() != true,
                        ["missingTagCount"] = validateBad["missingTagCount"]?.GetValue<int>() ?? 0,
                        ["missingTags"] = validateBad["missingTags"]?.DeepClone()
                    }),
                ["writeResult"] = writeResult,
                ["validateGood"] = validateGood,
                ["plcManifest"] = plcManifest,
                ["validatePlcSyncGood"] = validatePlcSyncGood,
                ["validatePlcSyncBad"] = validatePlcSyncBad,
                ["validateBad"] = validateBad
            };

            var jsonPath = Path.Combine(reportDir, "classic_hmi_minimal_package_probe_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "classic_hmi_minimal_package_probe_" + stamp + ".md");
            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildClassicHmiMinimalPackageProbeMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Classic HMI minimal package probe report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunPlcSymbolManifestProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.PlcSymbolManifestReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_symbol_manifest")
                : options.PlcSymbolManifestReportDirectory!;
            var root = PlcSymbolManifestBuilder.RunProbe(reportDir);
            LogDiag("PLC symbol manifest probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunClassicHmiOfflineSuite(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.ClassicHmiOfflineSuiteReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "classic_hmi_offline_suite")
                : options.ClassicHmiOfflineSuiteReportDirectory!;
            var root = ClassicHmiOfflineValidationSuite.Run(reportDir);
            LogDiag("Classic HMI offline validation suite report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunOfflineReleaseSuite(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.OfflineReleaseSuiteReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "offline_release_suite")
                : options.OfflineReleaseSuiteReportDirectory!;
            var root = OfflineReleaseValidationSuite.Run(workspaceRoot, reportDir);
            LogDiag("Offline release validation suite report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag(root["diagnosticMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["runbookMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["manifestMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["releaseReadinessGateMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunV2PlanCompletionAudit(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.OfflineReleaseSuiteReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "v2_plan_completion_audit")
                : options.OfflineReleaseSuiteReportDirectory!;
            var root = V2PlanCompletionAuditor.Run(workspaceRoot, reportDir);
            LogDiag("V2 plan completion audit written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("StrictCompletionPercent: " + root["strictCompletionPercent"]);
            LogDiag("CanClaimV2Complete: " + root["canClaimV2Complete"]);
        }

        private static void RunRebuildReleaseHandoff(CliOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.OfflineReleaseSuiteJsonPath))
            {
                throw new InvalidOperationException("--offline-release-suite-json-path is required for --rebuild-release-handoff.");
            }

            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.OfflineReleaseSuiteReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "offline_release_handoff_rebuilt")
                : options.OfflineReleaseSuiteReportDirectory!;
            var root = ReleaseHandoffArtifactBuilder.RebuildFromSuiteJson(options.OfflineReleaseSuiteJsonPath!, reportDir);
            LogDiag("Release handoff artifacts rebuilt:");
            LogDiag(root["diagnosticMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["runbookMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["manifestMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["releaseReadinessGateMarkdownPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
            LogDiag("ReleaseReady: " + root["releaseReady"]);
        }

        private static void RunHmiTemplatePlcSyncPrecheckSuite(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var plcXmlPath = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiTemplatePlcSyncPrecheckReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_template_plc_sync_precheck")
                : options.HmiTemplatePlcSyncPrecheckReportDirectory!;
            var root = HmiTemplatePlcSyncPrecheckSuite.Run(templateDir, plcXmlPath, reportDir, options.HmiTemplateMappingPath ?? "");
            LogDiag("HMI template PLC sync precheck suite report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunClassicHmiTemporaryImportPreflight(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.ClassicHmiTemporaryImportPreflightReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "classic_hmi_temporary_import_preflight")
                : options.ClassicHmiTemporaryImportPreflightReportDirectory!;
            var root = ClassicHmiTemporaryImportPreflightSuite.Run(workspaceRoot, reportDir);
            LogDiag("Classic HMI temporary import preflight report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static string BuildClassicHmiMinimalPackageProbeJson()
        {
            return @"{
  ""Name"": ""Classic_Motor_ValidateProbe"",
  ""TagTable"": {
    ""Name"": ""Motor_HMI_Tags"",
    ""Tags"": [
      {""Name"":""Motor_Start"",""DataType"":""Bool"",""Length"":""1"",""Connection"":""HMI_Connection_1"",""PlcTag"":""DB1_MotorData.Motor.Start""},
      {""Name"":""Motor_Run"",""DataType"":""Bool"",""Length"":""1"",""Connection"":""HMI_Connection_1"",""PlcTag"":""DB1_MotorData.Motor.Run""},
      {""Name"":""Speed_Set"",""DataType"":""Int"",""Length"":""2"",""Connection"":""HMI_Connection_1"",""PlcTag"":""DB1_MotorData.SpeedSet""}
    ]
  },
  ""ScreenDesign"": {
    ""Screen"": {""Name"":""Motor_Main"",""Width"":640,""Height"":480},
    ""Items"": [
      {""Type"":""Text"",""Name"":""Title"",""Left"":20,""Top"":20,""Width"":260,""Height"":36,""Text"":{""zh-CN"":""电机控制""}},
      {""Type"":""Button"",""Name"":""Btn_Start"",""Left"":20,""Top"":82,""Width"":130,""Height"":46,""Text"":{""zh-CN"":""启动""},""Actions"":[
        {""Event"":""Press"",""ActionKind"":""SetBit"",""TargetTag"":""Motor_Start""},
        {""Event"":""Release"",""ActionKind"":""ResetBit"",""TargetTag"":""Motor_Start""}
      ]},
      {""Type"":""Lamp"",""Name"":""Lamp_Run"",""Left"":180,""Top"":86,""Width"":42,""Height"":42,""Tag"":""Motor_Run""},
      {""Type"":""IOField"",""Name"":""IO_Speed"",""Left"":20,""Top"":154,""Width"":140,""Height"":38,""ProcessValueTag"":""Speed_Set""}
    ]
  }
}";
        }

        private static string BuildClassicHmiMinimalPackageProbePlcSymbolsJson(bool includeSpeedSet)
        {
            return includeSpeedSet
                ? @"[
  ""DB1_MotorData.Motor.Start"",
  ""DB1_MotorData.Motor.Run"",
  ""DB1_MotorData.SpeedSet""
]"
                : @"[
  ""DB1_MotorData.Motor.Start"",
  ""DB1_MotorData.Motor.Run""
]";
        }

        private static string BuildClassicHmiMinimalPackageProbePlcTagTableXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <SW.Tags.PlcTagTable ID=""0"">
    <AttributeList><Name>MotorTags</Name></AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID=""1"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Motor_Start</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""2"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.2</LogicalAddress><Name>Motor_Run</Name></AttributeList></SW.Tags.PlcTag>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>";
        }

        private static string BuildClassicHmiMinimalPackageProbePlcDbXml(bool includeSpeedSet)
        {
            var speedSet = includeSpeedSet ? @"            <Member Name=""SpeedSet"" Datatype=""Int"" />" : "";
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <SW.Blocks.GlobalDB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Static"">
            <Member Name=""Motor"" Datatype=""&quot;UDT_Motor&quot;"">
              <Member Name=""Start"" Datatype=""Bool"" />
              <Member Name=""Run"" Datatype=""Bool"" />
            </Member>
" + speedSet + @"
          </Section>
        </Sections>
      </Interface>
      <Name>DB1_MotorData</Name>
      <Number>1</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>";
        }

        private static string BuildClassicHmiMinimalPackageProbeMarkdown(JsonObject root, string jsonPath)
        {
            var good = root["validateGood"] as JsonObject ?? new JsonObject();
            var syncGood = root["validatePlcSyncGood"] as JsonObject ?? new JsonObject();
            var syncBad = root["validatePlcSyncBad"] as JsonObject ?? new JsonObject();
            var bad = root["validateBad"] as JsonObject ?? new JsonObject();
            var md = new StringBuilder();
            md.AppendLine("# Classic HMI Minimal Package Probe");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- 离线探针，不连接 TIA Portal，不打开工程，不导入 HMI 文件。");
            md.AppendLine("- 只写 reports 目录下的临时探针文件，不修改工程、reference 或交付包。");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Package directory: " + root["packageDirectory"]);
            md.AppendLine("- Good package missing tags: " + (good["missingTagCount"]?.ToString() ?? "0"));
            md.AppendLine("- Good package dynamic bindings: " + (good["screenAnalysis"]?["dynamicBindingCount"]?.ToString() ?? "0"));
            md.AppendLine("- Good package event actions: " + (good["screenAnalysis"]?["eventActionCount"]?.ToString() ?? "0"));
            md.AppendLine("- Good PLC sync missing symbols: " + (syncGood["missingPlcSymbolCount"]?.ToString() ?? "0"));
            md.AppendLine("- Bad PLC sync missing symbols: " + (syncBad["missingPlcSymbols"]?.ToJsonString() ?? "[]"));
            md.AppendLine("- Bad package missing tags: " + (bad["missingTags"]?.ToJsonString() ?? "[]"));
            md.AppendLine();
            md.AppendLine("## Next Validation");
            md.AppendLine("- 离线自检通过后，仍需导入临时 Classic/Basic HMI 工程，读回 tags/items/bindings/events 并编译诊断。");
            return md.ToString();
        }

        private static void RunPlcBuilderOfflineSuite(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderSuiteReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_suite")
                : options.PlcBuilderSuiteReportDirectory!;

            var root = PlcBuilderOfflineValidationSuite.Run(fixtureDir, reportDir);
            LogDiag("PLC builder offline validation suite report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunPlcTagTableBuilderProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_probes")
                : options.PlcBuilderProbeReportDirectory!;

            var root = PlcTagTableXmlBuilder.RunProbe(fixtureDir, reportDir);
            LogDiag("PLC tag table builder probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunPlcUdtBuilderProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_probes")
                : options.PlcBuilderProbeReportDirectory!;

            var root = PlcUdtXmlBuilder.RunProbe(fixtureDir, reportDir);
            LogDiag("PLC UDT builder probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunStructuredTextBuilderProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_probes")
                : options.PlcBuilderProbeReportDirectory!;

            var root = StructuredTextXmlBuilder.RunProbe(fixtureDir, reportDir);
            LogDiag("StructuredText builder probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunPlcFcBlockComposerProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_probes")
                : options.PlcBuilderProbeReportDirectory!;

            var root = PlcFcBlockXmlComposer.RunProbe(fixtureDir, reportDir);
            LogDiag("PLC FC block composer probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunPlcGlobalDbBuilderProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var fixtureDir = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "_verify")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_probes")
                : options.PlcBuilderProbeReportDirectory!;

            var root = PlcGlobalDbXmlBuilder.RunProbe(fixtureDir, reportDir);
            LogDiag("PLC Global DB builder probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunFlgNetCallBuilderProbe(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var reportDir = string.IsNullOrWhiteSpace(options.PlcBuilderProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "plc_builder_probes")
                : options.PlcBuilderProbeReportDirectory!;

            var root = FlgNetCallXmlBuilder.RunProbe(workspaceRoot, reportDir);
            LogDiag("LAD FlgNet call builder probe report written:");
            LogDiag(root["markdownPath"]?.ToString() ?? reportDir);
            LogDiag(root["jsonPath"]?.ToString() ?? reportDir);
            LogDiag("OK: " + root["ok"]);
        }

        private static void RunGenerateMonitoringReadOnlyReport(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var requestedSoftwarePath = string.IsNullOrWhiteSpace(options.PlcSoftwarePath) ? "PLC_1" : options.PlcSoftwarePath!;
            var softwarePath = requestedSoftwarePath;
            var reportDir = string.IsNullOrWhiteSpace(options.MonitoringReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "monitoring_readonly")
                : options.MonitoringReportDirectory!;
            var regexName = options.WatchTableRegex ?? "";

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var exportDir = Path.Combine(reportDir, "watch_tables_" + stamp);
            var jsonPath = Path.Combine(reportDir, "monitoring_readonly_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "monitoring_readonly_" + stamp + ".md");

            var safety = McpServer.RunOnlineMonitoringSafetySelfTest();
            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "read-only-monitoring-report",
                ["requestedPlcSoftwarePath"] = requestedSoftwarePath,
                ["softwarePath"] = softwarePath,
                ["actualPlcSoftwarePath"] = softwarePath,
                ["plcSoftwarePathResolution"] = "requested",
                ["watchTableRegex"] = regexName,
                ["exportDirectory"] = exportDir,
                ["safetyPolicy"] = new JsonObject
                {
                    ["onlineMonitor"] = "Only current-value read is allowed after separate online proof. This report does not read or write PLC values.",
                    ["watchTables"] = "This report may list/export existing watch tables only; it never creates, imports, deletes, or modifies watch-table objects.",
                    ["force"] = "Force-table and force-related operations remain forbidden.",
                    ["deliveryPackage"] = "This report does not write to TIA_MCP_DELIVERY_FOR_OTHER_AI."
                },
                ["safetySelfTest"] = SafetySelfTestToJson(safety)
            };

            try
            {
                var connect = McpServer.Connect();
                root["connect"] = new JsonObject
                {
                    ["message"] = connect.Message ?? "",
                    ["success"] = connect.Meta?["success"]?.GetValue<bool>() == true
                };
            }
            catch (Exception ex)
            {
                root["connect"] = new JsonObject
                {
                    ["success"] = false,
                    ["error"] = ex.InnerException?.Message ?? ex.Message
                };
            }

            if (!string.IsNullOrWhiteSpace(options.ProjectName))
            {
                try
                {
                    var attach = McpServer.AttachToOpenProject(options.ProjectName!);
                    root["attachToOpenProject"] = new JsonObject
                    {
                        ["projectName"] = options.ProjectName,
                        ["message"] = attach.Message ?? "",
                        ["success"] = attach.Meta?["success"]?.GetValue<bool>() == true
                    };
                }
                catch (Exception ex)
                {
                    root["attachToOpenProject"] = new JsonObject
                    {
                        ["projectName"] = options.ProjectName,
                        ["success"] = false,
                        ["error"] = ex.InnerException?.Message ?? ex.Message
                    };
                }
            }

            try
            {
                var state = McpServer.GetState();
                root["state"] = new JsonObject
                {
                    ["isConnected"] = state.IsConnected == true,
                    ["project"] = state.Project ?? "",
                    ["session"] = state.Session ?? ""
                };
            }
            catch (Exception ex)
            {
                root["state"] = new JsonObject { ["error"] = ex.InnerException?.Message ?? ex.Message };
            }

            try
            {
                var context = McpServer.ValidateAutomationContext("", "");
                root["automationContext"] = new JsonObject
                {
                    ["message"] = context.Message ?? "",
                    ["success"] = context.Meta?["success"]?.GetValue<bool>() == true,
                    ["meta"] = context.Meta?.DeepClone()
                };
                var software = context.Meta?["software"] as JsonArray;
                var firstPlc = software?
                    .OfType<JsonObject>()
                    .FirstOrDefault(x => (x["softwareType"]?.ToString() ?? "").IndexOf("PlcSoftware", StringComparison.OrdinalIgnoreCase) >= 0);
                var detectedPath = firstPlc?["softwareName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(detectedPath))
                {
                    root["detectedPlcSoftwarePath"] = detectedPath;
                    root["detectedPlcSoftwarePathNote"] = "First PlcSoftware discovered by ValidateAutomationContext; used only when requested path is the placeholder PLC_1.";
                    if (string.Equals(softwarePath, "PLC_1", StringComparison.OrdinalIgnoreCase))
                    {
                        softwarePath = detectedPath!;
                        root["softwarePath"] = softwarePath;
                        root["actualPlcSoftwarePath"] = softwarePath;
                        root["plcSoftwarePathResolution"] = "fallback-to-first-detected-plc";
                    }
                }
            }
            catch (Exception ex)
            {
                root["automationContext"] = new JsonObject { ["error"] = ex.InnerException?.Message ?? ex.Message };
            }

            try
            {
                var tables = McpServer.GetPlcWatchTables(softwarePath);
                root["watchTables"] = new JsonArray((tables.Items ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray());
            }
            catch (Exception ex)
            {
                root["watchTablesError"] = ex.InnerException?.Message ?? ex.Message;
            }

            try
            {
                var export = McpServer.ExportPlcWatchTablesToDirectory(softwarePath, exportDir, regexName);
                root["watchTableExport"] = new JsonObject
                {
                    ["message"] = export.Message ?? "",
                    ["exported"] = new JsonArray((export.Imported ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                    ["exportedSummary"] = new JsonArray((export.Imported ?? Array.Empty<string>()).Select(AnalyzeExportedWatchTableXml).ToArray()),
                    ["failed"] = new JsonArray((export.Failed ?? Array.Empty<ImportFailure>()).Select(x => new JsonObject
                    {
                        ["path"] = x.Path ?? "",
                        ["error"] = x.Error ?? ""
                    }).ToArray())
                };
            }
            catch (Exception ex)
            {
                root["watchTableExport"] = new JsonObject
                {
                    ["error"] = ex.InnerException?.Message ?? ex.Message
                };
            }

            try
            {
                var probe = McpServer.ProbePlcMonitorOnlineCapabilities(softwarePath);
                root["onlineCapabilityProbe"] = probe.Data ?? new JsonObject();
                root["onlineCapabilityProbeOk"] = probe.Ok == true;
            }
            catch (Exception ex)
            {
                root["onlineCapabilityProbe"] = new JsonObject { ["error"] = ex.InnerException?.Message ?? ex.Message };
                root["onlineCapabilityProbeOk"] = false;
            }

            try
            {
                var tableNames = (root["watchTables"] as JsonArray ?? new JsonArray())
                    .Select(x => x?.ToString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                var selectedTable = tableNames.FirstOrDefault(x =>
                    string.IsNullOrWhiteSpace(regexName) || Regex.IsMatch(x, regexName, RegexOptions.IgnoreCase))
                    ?? tableNames.FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(selectedTable))
                {
                    var read = McpServer.ReadPlcWatchTableCurrentValuesReadOnly(softwarePath, selectedTable!, 50);
                    root["onlineCurrentValueRead"] = read.Data ?? new JsonObject();
                    root["onlineCurrentValueReadOk"] = read.Ok == true;
                    root["onlineCurrentValueReadMessage"] = read.Message ?? "";
                }
                else
                {
                    root["onlineCurrentValueRead"] = new JsonObject { ["message"] = "No watch table available for current-value read." };
                    root["onlineCurrentValueReadOk"] = false;
                }
            }
            catch (Exception ex)
            {
                root["onlineCurrentValueRead"] = new JsonObject { ["error"] = ex.InnerException?.Message ?? ex.Message };
                root["onlineCurrentValueReadOk"] = false;
            }

            var safetyOk = safety.Ok == true;
            var exportFailures = root["watchTableExport"]?["failed"] as JsonArray;
            root["actualPlcSoftwarePath"] = softwarePath;
            root["ok"] = safetyOk && (exportFailures == null || exportFailures.Count == 0);
            root["liveCurrentValueReadVerified"] = root["onlineCurrentValueReadOk"]?.GetValue<bool>() == true;
            root["liveCurrentValueReadNote"] = root["liveCurrentValueReadVerified"]?.GetValue<bool>() == true
                ? "online-current-value-read: existing watch table current/monitor values were read without writes."
                : "Not verified. Current values require an attached online PLC and a readable existing watch table.";

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildMonitoringReadOnlyMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Monitoring read-only report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static void RunGenerateGlobalLibraryProbeReport(CliOptions options)
        {
            var workspaceRoot = GetWorkspaceRoot();
            var libraryPath = string.IsNullOrWhiteSpace(options.GlobalLibraryPackagePath)
                ? string.IsNullOrWhiteSpace(options.ReferenceGlobalLibraryPath)
                    ? Path.Combine(workspaceRoot, "reference", "HMI_Template_Suite_WinCC_Unified_V18", "HMI Template Suite (WinCC Unified)_V18_V21")
                    : options.ReferenceGlobalLibraryPath!
                : options.GlobalLibraryPackagePath!;
            var reportDir = string.IsNullOrWhiteSpace(options.GlobalLibraryProbeReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "global_library_probe")
                : options.GlobalLibraryProbeReportDirectory!;

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "global_library_probe_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "global_library_probe_" + stamp + ".md");

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["libraryPath"] = libraryPath,
                ["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "TIA-connected read-only global-library probe.",
                    ["import"] = "No master copy, type, or library object is imported into a project.",
                    ["write"] = "No library or project content is modified.",
                    ["deliveryPackage"] = "This report does not write to TIA_MCP_DELIVERY_FOR_OTHER_AI."
                }
            };

            try
            {
                var connect = McpServer.Connect();
                root["connect"] = new JsonObject
                {
                    ["success"] = connect.Meta?["success"]?.GetValue<bool>() == true,
                    ["message"] = connect.Message ?? ""
                };
            }
            catch (Exception ex)
            {
                root["connect"] = new JsonObject
                {
                    ["success"] = false,
                    ["error"] = ex.InnerException?.Message ?? ex.Message
                };
            }

            try
            {
                var probe = McpServer.ProbeGlobalLibrary(libraryPath, 1000);
                root["probe"] = GlobalLibraryProbeToJson(probe);
                root["ok"] = probe.Ok == true;
            }
            catch (Exception ex)
            {
                root["probe"] = new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = ex.InnerException?.Message ?? ex.Message
                };
                root["ok"] = false;
            }

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildGlobalLibraryProbeMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Global library probe report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static void RunValidateGlobalLibraryMasterCopyImport(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var libraryPath = string.IsNullOrWhiteSpace(options.GlobalLibraryPackagePath)
                ? string.IsNullOrWhiteSpace(options.ReferenceGlobalLibraryPath)
                    ? Path.Combine(workspaceRoot, "reference", "HMI_Template_Suite_WinCC_Unified_V18", "HMI Template Suite (WinCC Unified)_V18_V21")
                    : options.ReferenceGlobalLibraryPath!
                : options.GlobalLibraryPackagePath!;
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_GlobalLibrary_Import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var reportDir = string.IsNullOrWhiteSpace(options.GlobalLibraryReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "global_library_mastercopy_import")
                : options.GlobalLibraryReportDirectory!;
            var masterCopyName = string.IsNullOrWhiteSpace(options.HmiTemplateMappingPath)
                ? "HMI Template Suite/WinCC Unified/SIMATIC WinCC Unified Comfort Panel/02 - 800 x 480/04 - Grouped objects/Notifications/Notification_Error"
                : options.HmiTemplateMappingPath!;
            var screenName = "MasterCopy_Import_Test";
            var importedItemName = "MCP_MasterCopy_Imported";

            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "global_library_mastercopy_import_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "global_library_mastercopy_import_" + stamp + ".md");

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["format"] = "tia-mcp-global-library-mastercopy-import-validation-v1",
                ["libraryPath"] = libraryPath,
                ["masterCopyName"] = masterCopyName,
                ["projectDirectory"] = projectDirectory,
                ["projectName"] = projectName,
                ["hmiSoftwarePath"] = "HMI_RT_1",
                ["screenName"] = screenName,
                ["importedItemName"] = importedItemName,
                ["safetyPolicy"] = new JsonObject
                {
                    ["target"] = "New temporary project only.",
                    ["deliveryPackage"] = "This validation does not write to TIA_MCP_DELIVERY_FOR_OTHER_AI.",
                    ["save"] = "Project is saved only to keep evidence after readback.",
                    ["online"] = "No online or force operation is executed."
                }
            };

            try
            {
                LogDiag($"Global library MasterCopy import validation: project={projectName}, library={libraryPath}, masterCopy={masterCopyName}");
                var connect = McpServer.Connect();
                root["connect"] = ResponseMessageToJson(connect);

                var create = McpServer.CreateProject(projectDirectory, projectName);
                root["createProject"] = ResponseMessageToJson(create);

                var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
                root["addHmi"] = new JsonObject
                {
                    ["ok"] = hmi.Ok == true,
                    ["mlfbUsed"] = hmi.MlfbUsed ?? "",
                    ["versionUsed"] = hmi.VersionUsed ?? "",
                    ["error"] = hmi.Error ?? "",
                    ["attempts"] = new JsonArray((hmi.Attempts ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray())
                };
                if (hmi.Ok != true)
                    throw new InvalidOperationException("Failed to add Unified HMI for MasterCopy import validation: " + hmi.Error);

                root["ensureScreen"] = ResponseMessageToJson(McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480));

                var probe = McpServer.ProbeGlobalLibrary(libraryPath, 1000);
                root["probe"] = GlobalLibraryProbeToJson(probe);
                if (probe.Ok != true)
                    throw new InvalidOperationException("ProbeGlobalLibrary failed before import: " + probe.Error);

                var import = McpServer.ImportMasterCopyFromGlobalLibrary(libraryPath, masterCopyName, "HMI_RT_1", screenName, importedItemName, 40, 40);
                root["import"] = GlobalLibraryImportToJson(import);

                var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                root["screenReadback"] = new JsonArray(screens.Select(x => JsonValue.Create(x)).ToArray());
                var screenExists = screens.Any(x => string.Equals(x, screenName, StringComparison.OrdinalIgnoreCase));
                root["masterCopyImportReadbackOk"] = import.Ok == true;
                root["screenExists"] = screenExists;
                root["ok"] = import.Ok == true && screenExists;

                root["save"] = ResponseMessageToJson(McpServer.SaveProject());
            }
            catch (Exception ex)
            {
                root["ok"] = false;
                root["error"] = ex.InnerException?.Message ?? ex.Message;
                root["exception"] = ex.ToString();
            }

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildGlobalLibraryMasterCopyImportMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Global library MasterCopy import validation report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);

            if (root["ok"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("Global library MasterCopy import validation failed. Report: " + mdPath);
            }
        }

        private static void RunValidateUnifiedHmiActionSyntaxCheck(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_Unified_Action_Syntax_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiComponentCatalogReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "unified_hmi_action_syntaxcheck")
                : options.HmiComponentCatalogReportDirectory!;

            const string hmiSoftwarePath = "HMI_RT_1";
            const string screenName = "Action_Syntax_Test";
            const string tagTableName = "ActionTags";
            const string tagName = "Cmd_Start";
            const string buttonName = "Btn_Start";
            const string eventType = "Tapped";
            const string actionKind = "set-bit";

            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "unified_hmi_action_syntaxcheck_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "unified_hmi_action_syntaxcheck_" + stamp + ".md");

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["format"] = "tia-mcp-unified-hmi-action-syntaxcheck-validation-v1",
                ["projectDirectory"] = projectDirectory,
                ["projectName"] = projectName,
                ["hmiSoftwarePath"] = hmiSoftwarePath,
                ["screenName"] = screenName,
                ["tagTableName"] = tagTableName,
                ["tagName"] = tagName,
                ["buttonName"] = buttonName,
                ["eventType"] = eventType,
                ["actionKind"] = actionKind,
                ["safetyPolicy"] = new JsonObject
                {
                    ["target"] = "New temporary project only.",
                    ["script"] = "Only deterministic set-bit recipe is applied.",
                    ["online"] = "No online, watch-table edit, write-current-value, or force operation is executed.",
                    ["deliveryPackage"] = "This validation does not write to TIA_MCP_DELIVERY_FOR_OTHER_AI."
                }
            };

            try
            {
                LogDiag($"Unified HMI action SyntaxCheck validation: project={projectName}");
                root["connect"] = ResponseMessageToJson(McpServer.Connect());
                root["createProject"] = ResponseMessageToJson(McpServer.CreateProject(projectDirectory, projectName));

                var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", hmiSoftwarePath, "WinCCUnifiedPC");
                root["addHmi"] = new JsonObject
                {
                    ["ok"] = hmi.Ok == true,
                    ["mlfbUsed"] = hmi.MlfbUsed ?? "",
                    ["versionUsed"] = hmi.VersionUsed ?? "",
                    ["error"] = hmi.Error ?? "",
                    ["attempts"] = new JsonArray((hmi.Attempts ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray())
                };
                if (hmi.Ok != true)
                    throw new InvalidOperationException("Failed to add Unified HMI for action SyntaxCheck validation: " + hmi.Error);

                root["ensureScreen"] = ResponseMessageToJson(McpServer.EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, 800, 480));
                root["ensureTagTable"] = ResponseMessageToJson(McpServer.EnsureUnifiedHmiTagTable(hmiSoftwarePath, tagTableName));
                root["ensureTag"] = ResponseMessageToJson(McpServer.EnsureUnifiedHmiTag(hmiSoftwarePath, tagTableName, tagName, "Bool", "", tagName, "", "", false));
                root["ensureButton"] = ResponseMessageToJson(McpServer.EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, buttonName, "Button", 40, 40, 160, 56, "Start"));

                var build = McpServer.BuildUnifiedHmiButtonActionScript(actionKind, eventType, tagName);
                root["recipe"] = ResponseMessageToJson(build);
                var script = build.Meta?["script"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(script))
                    throw new InvalidOperationException("Generated action script is empty.");

                root["ensureAction"] = ResponseMessageToJson(McpServer.EnsureUnifiedHmiButtonAction(hmiSoftwarePath, screenName, buttonName, eventType, actionKind, tagName));
                var setMeta = root["ensureAction"]?["meta"]?["setMeta"] as JsonObject;
                var syntaxErrorCount = setMeta?["syntaxErrorCount"]?.GetValue<int>() ?? -1;
                var syntaxWarningCount = setMeta?["syntaxWarningCount"]?.GetValue<int>() ?? -1;
                root["syntaxErrorCount"] = syntaxErrorCount;
                root["syntaxWarningCount"] = syntaxWarningCount;
                root["syntaxCheckZeroError"] = syntaxErrorCount == 0;
                root["syntaxCheckEvidence"] = syntaxErrorCount == 0 ? "SyntaxCheck 0 error" : "SyntaxCheck did not report 0 error.";

                var readback = McpServer.DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, 120);
                root["scriptReadback"] = new JsonObject
                {
                    ["message"] = readback.Message ?? "",
                    ["memberCount"] = readback.Members?.Count() ?? 0,
                    ["typeName"] = readback.TypeName ?? ""
                };

                root["ok"] = syntaxErrorCount == 0;
                root["save"] = ResponseMessageToJson(McpServer.SaveProject());
            }
            catch (Exception ex)
            {
                root["ok"] = false;
                root["error"] = ex.InnerException?.Message ?? ex.Message;
                root["exception"] = ex.ToString();
            }

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildUnifiedHmiActionSyntaxCheckMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("Unified HMI action SyntaxCheck validation report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);

            if (root["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Unified HMI action SyntaxCheck validation failed. Report: " + mdPath);
        }

        private static void RunGenerateHmiTemplateSyncPrecheck(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var softwarePath = string.IsNullOrWhiteSpace(options.PlcSoftwarePath) ? "Zone1_PLC1516TF" : options.PlcSoftwarePath!;
            var tagTableRegex = options.PlcTagTableRegex ?? "";
            var maxTagTablesToExport = Math.Max(0, options.MaxPlcTagTablesToExport ?? 0);
            var plcExportDirectory = options.PlcExportDirectory ?? "";
            var mappingPath = options.HmiTemplateMappingPath ?? "";
            var shouldReadPlc = !string.IsNullOrWhiteSpace(tagTableRegex) && maxTagTablesToExport > 0;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiTemplateSyncPrecheckReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_template_sync_precheck")
                : options.HmiTemplateSyncPrecheckReportDirectory!;

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var tagExportDir = Path.Combine(reportDir, "plc_tags_" + stamp);
            var jsonPath = Path.Combine(reportDir, "hmi_template_sync_precheck_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "hmi_template_sync_precheck_" + stamp + ".md");
            var exported = new List<string>();
            var failures = new List<ImportFailure>();

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["templateDirectory"] = templateDir,
                ["plcSoftwarePath"] = softwarePath,
                ["plcTagTableRegex"] = tagTableRegex,
                ["maxPlcTagTablesToExport"] = maxTagTablesToExport,
                ["plcExportDirectory"] = plcExportDirectory,
                ["hmiTemplateMappingPath"] = mappingPath,
                ["plcReadMode"] = shouldReadPlc
                    ? "bounded TIA read enabled"
                    : "skipped by default; pass --plc-tag-table-regex and --max-plc-tag-tables-to-export to read a bounded subset",
                ["tagExportDirectory"] = tagExportDir,
                ["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Read-only PLC/HMI template synchronization precheck.",
                    ["project"] = shouldReadPlc ? "A bounded PLC tag table subset may be listed/exported read-only." : "TIA project access skipped by default for large-project safety.",
                    ["hmi"] = "No HMI screen, tag, event, or connection is created or modified.",
                    ["deliveryPackage"] = "This report does not write to TIA_MCP_DELIVERY_FOR_OTHER_AI."
                }
            };

            if (shouldReadPlc)
            {
                try
                {
                    var connect = McpServer.Connect();
                    root["connect"] = new JsonObject
                    {
                        ["success"] = connect.Meta?["success"]?.GetValue<bool>() == true,
                        ["message"] = connect.Message ?? ""
                    };
                }
                catch (Exception ex)
                {
                    root["connect"] = new JsonObject { ["success"] = false, ["error"] = ex.InnerException?.Message ?? ex.Message };
                }

                try
                {
                    var tables = McpServer.GetPlcTagTables(softwarePath).Items?.ToArray() ?? Array.Empty<string>();
                    root["plcTagTables"] = new JsonArray(tables.Select(x => JsonValue.Create(x)).ToArray());
                    var selectedTables = SelectPlcTagTablesForPrecheck(tables, tagTableRegex, maxTagTablesToExport);
                    root["selectedPlcTagTablesForExport"] = new JsonArray(selectedTables.Select(x => JsonValue.Create(x)).ToArray());
                    root["tagExportMode"] = selectedTables.Length == 0 ? "no tag table matched regex" : "bounded subset export";

                    if (selectedTables.Length > 0)
                    {
                        Directory.CreateDirectory(tagExportDir);
                        foreach (var table in selectedTables)
                        {
                            var outPath = Path.Combine(tagExportDir, MakeSafeReportFileName(table) + ".xml");
                            try
                            {
                                var export = McpServer.ExportPlcTagTable(softwarePath, table, outPath);
                                if (export.Meta?["success"]?.GetValue<bool>() == true)
                                {
                                    exported.Add(outPath);
                                }
                                else
                                {
                                    failures.Add(new ImportFailure { Path = table, Error = export.Message ?? "Export failed" });
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Add(new ImportFailure { Path = table, Error = ex.InnerException?.Message ?? ex.Message });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    root["plcTagTablesError"] = ex.InnerException?.Message ?? ex.Message;
                }
            }
            else
            {
                root["connect"] = new JsonObject { ["success"] = false, ["skipped"] = true };
                root["plcTagTables"] = new JsonArray();
                root["selectedPlcTagTablesForExport"] = new JsonArray();
                root["tagExportMode"] = "offline template contract check only";
            }

            var plcSymbols = ExtractPlcSymbolsFromTagTableExports(exported);
            var plcExportCatalog = AnalyzePlcExportDirectory(plcExportDirectory);
            var plcSymbolCatalog = BuildPlcSymbolCatalog(plcExportCatalog);
            foreach (var symbol in (plcExportCatalog["symbols"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                plcSymbols.Add(symbol);
            }
            root["plcTagExport"] = new JsonObject
            {
                ["exported"] = new JsonArray(exported.Select(x => JsonValue.Create(x)).ToArray()),
                ["failed"] = new JsonArray(failures.Select(x => new JsonObject { ["path"] = x.Path ?? "", ["error"] = x.Error ?? "" }).ToArray()),
                ["symbolCount"] = plcSymbols.Count,
                ["symbolsSample"] = new JsonArray(plcSymbols.Take(120).Select(x => JsonValue.Create(x)).ToArray())
            };
            root["plcExportCatalog"] = plcExportCatalog;

            var templates = HmiTemplateReferenceAnalyzer.Analyze(templateDir, "", "");
            var templateArray = templates["templates"] as JsonArray ?? new JsonArray();
            var mappingFile = LoadHmiTemplateMappingFile(mappingPath);
            root["templateAnalysis"] = templateArray.DeepClone();
            root["mappingFile"] = mappingFile;
            root["effectiveTemplateAnalysis"] = ApplyHmiTemplateMapping(templateArray, mappingFile);
            root["syncPrecheck"] = BuildHmiTemplateSyncPrecheck(root["effectiveTemplateAnalysis"] as JsonArray ?? new JsonArray(), plcSymbols, plcSymbolCatalog);
            root["ok"] = failures.Count == 0 && root["plcTagTablesError"] == null;

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildHmiTemplateSyncPrecheckMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("HMI template sync precheck report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static void RunGenerateHmiTemplateMappingSkeleton(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var plcExportDirectory = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "Source", "5T车", "Blocks")
                : options.PlcExportDirectory!;
            var outputPath = string.IsNullOrWhiteSpace(options.HmiTemplateMappingPath)
                ? Path.Combine(workspaceRoot, "reports", "hmi_template_plc_mapping", "hmi_template_mapping.skeleton.json")
                : options.HmiTemplateMappingPath!;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            var templates = HmiTemplateReferenceAnalyzer.Analyze(templateDir, "", "");
            var plcCatalog = AnalyzePlcExportDirectory(plcExportDirectory);
            var plcSymbols = BuildPlcSymbolCatalog(plcCatalog);
            var mappingAnalysis = BuildHmiTemplatePlcMapping(templates["templates"] as JsonArray ?? new JsonArray(), plcSymbols);
            var skeleton = BuildHmiTemplateMappingSkeletonJson(templateDir, plcExportDirectory, mappingAnalysis, plcSymbols);

            File.WriteAllText(outputPath, skeleton.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);

            LogDiag("HMI template mapping skeleton written:");
            LogDiag(outputPath);
        }

        private static JsonObject AnalyzeReferenceProject(string projectPath)
        {
            var info = new JsonObject
            {
                ["path"] = projectPath,
                ["exists"] = Directory.Exists(projectPath),
                ["hmiRuntimeDetected"] = false
            };

            if (!Directory.Exists(projectPath))
            {
                info["error"] = "Reference project directory not found.";
                return info;
            }

            var apFiles = Directory.EnumerateFiles(projectPath, "*.ap*", SearchOption.TopDirectoryOnly).ToList();
            info["apFiles"] = new JsonArray(apFiles.Select(x => JsonValue.Create(x)).ToArray());

            var runtimeRoot = Directory.EnumerateDirectories(projectPath, "currentConfiguration", SearchOption.AllDirectories).FirstOrDefault();
            if (runtimeRoot == null)
            {
                info["error"] = "currentConfiguration folder not found.";
                return info;
            }

            info["hmiRuntimeDetected"] = true;
            info["runtimeRoot"] = runtimeRoot;

            var sections = new JsonObject();
            foreach (var section in new[] { "screens", "faceplates", "graphics", "fonts", "general", "screenwindowlayouts", "device", "system" })
            {
                var dir = Path.Combine(runtimeRoot, section);
                sections[section] = AnalyzeDirectorySummary(dir, section == "screens" || section == "faceplates" ? 20 : 12);
            }
            info["sections"] = sections;

            var rdfFiles = Directory.EnumerateFiles(runtimeRoot, "*.rdf", SearchOption.AllDirectories).ToList();
            var allFiles = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories).ToList();
            info["fileCounts"] = new JsonObject
            {
                ["total"] = allFiles.Count,
                ["rdf"] = rdfFiles.Count,
                ["screenRdf"] = Directory.Exists(Path.Combine(runtimeRoot, "screens")) ? Directory.EnumerateFiles(Path.Combine(runtimeRoot, "screens"), "*.rdf").Count() : 0,
                ["faceplateRdf"] = Directory.Exists(Path.Combine(runtimeRoot, "faceplates")) ? Directory.EnumerateFiles(Path.Combine(runtimeRoot, "faceplates"), "*.rdf").Count() : 0
            };

            info["stringHints"] = AnalyzeBinaryStringHints(runtimeRoot, 120);
            return info;
        }

        private static JsonObject AnalyzeReferenceLibrary(string libraryPath)
        {
            var info = new JsonObject
            {
                ["path"] = libraryPath,
                ["exists"] = Directory.Exists(libraryPath) || File.Exists(libraryPath)
            };

            if (!Directory.Exists(libraryPath) && !File.Exists(libraryPath))
            {
                info["error"] = "Reference global library path not found.";
                return info;
            }

            var rootDir = Directory.Exists(libraryPath) ? libraryPath : Path.GetDirectoryName(libraryPath) ?? libraryPath;
            var alFiles = Directory.EnumerateFiles(rootDir, "*.al*", SearchOption.TopDirectoryOnly).ToList();
            info["libraryFiles"] = new JsonArray(alFiles.Select(x => JsonValue.Create(x)).ToArray());
            info["topLevel"] = AnalyzeDirectorySummary(rootDir, 40);

            var dirs = new JsonObject();
            foreach (var section in new[] { "AdditionalFiles", "IM", "Logs", "src", "System", "tmp", "UserFiles", "Vci", "XRef" })
            {
                dirs[section] = AnalyzeDirectorySummary(Path.Combine(rootDir, section), 12);
            }
            info["sections"] = dirs;

            var infoFiles = Directory.EnumerateFiles(rootDir, "*.info", SearchOption.TopDirectoryOnly).ToList();
            var infoSnippets = new JsonArray();
            foreach (var file in infoFiles.Take(5))
            {
                try
                {
                    infoSnippets.Add(new JsonObject
                    {
                        ["path"] = file,
                        ["text"] = File.ReadAllText(file, Encoding.UTF8)
                    });
                }
                catch (Exception ex)
                {
                    infoSnippets.Add(new JsonObject { ["path"] = file, ["error"] = ex.Message });
                }
            }
            info["infoFiles"] = infoSnippets;
            return info;
        }

        private static JsonObject AnalyzeDirectorySummary(string dir, int sampleLimit)
        {
            var obj = new JsonObject
            {
                ["path"] = dir,
                ["exists"] = Directory.Exists(dir)
            };

            if (!Directory.Exists(dir))
            {
                obj["fileCount"] = 0;
                obj["totalBytes"] = 0;
                obj["samples"] = new JsonArray();
                return obj;
            }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .ToList();

            var byExt = files
                .GroupBy(f => string.IsNullOrWhiteSpace(f.Extension) ? "<none>" : f.Extension.ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => new JsonObject
                {
                    ["extension"] = g.Key,
                    ["count"] = g.Count(),
                    ["bytes"] = g.Sum(f => f.Length)
                })
                .ToArray();

            obj["fileCount"] = files.Count;
            obj["totalBytes"] = files.Sum(f => f.Length);
            obj["extensions"] = new JsonArray(byExt);
            obj["samples"] = new JsonArray(files.Take(sampleLimit).Select(f => new JsonObject
            {
                ["name"] = f.Name,
                ["relativePath"] = MakeRelativePath(dir, f.FullName),
                ["bytes"] = f.Length
            }).ToArray());
            return obj;
        }

        private static string MakeRelativePath(string baseDir, string fullPath)
        {
            try
            {
                var baseUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(baseDir)));
                var fileUri = new Uri(Path.GetFullPath(fullPath));
                var relative = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString());
                return relative.Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return fullPath;
            }
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString())
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static JsonObject AnalyzeBinaryStringHints(string runtimeRoot, int limit)
        {
            var patterns = new[]
            {
                "Tag",
                "Tags",
                "PLC",
                "HMI",
                "Faceplate",
                "Screen",
                "ProdDataInterface",
                "DataInterface",
                "Cycle",
                "Alarm",
                "Trend",
                "Recipe",
                "Button",
                "IO"
            };

            var hits = new JsonObject();
            foreach (var pattern in patterns)
            {
                hits[pattern] = 0;
            }

            var examples = new JsonArray();
            foreach (var file in Directory.EnumerateFiles(runtimeRoot, "*.rdf", SearchOption.AllDirectories).Take(500))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var ascii = Encoding.UTF8.GetString(bytes);
                    foreach (var pattern in patterns)
                    {
                        var count = CountOccurrences(ascii, pattern);
                        if (count > 0)
                        {
                            hits[pattern] = (int)(hits[pattern]?.GetValue<int>() ?? 0) + count;
                            if (examples.Count < limit)
                            {
                                examples.Add(new JsonObject
                                {
                                    ["file"] = file,
                                    ["pattern"] = pattern,
                                    ["count"] = count
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // Binary runtime files are best-effort only.
                }
            }

            return new JsonObject
            {
                ["patternCounts"] = hits,
                ["examples"] = examples
            };
        }

        private static int CountOccurrences(string text, string pattern)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }

        private static JsonArray BuildReferenceRecommendations(JsonObject projectInfo, JsonObject libraryInfo)
        {
            var list = new JsonArray
            {
                "HMI画面优化应优先抽象为JSON设计模板，不直接复制runtime RDF二进制文件；RDF只作为命名、布局复杂度和控件类型参考。",
                "全局库应通过ProbeGlobalLibrary只读打开并列出MasterCopies/Types后，再决定是否增加导入工具；未完成Openness读回前不要承诺批量导入。",
                "在线监控只允许读取当前变量状态；监控表对象不得在线新增、删除或修改。",
                "强制表和强制相关服务保持不可见、不可调用、不可通过反射绕过。",
                "HMI控件绑定必须同时检查HMI变量和PLC变量/DB成员存在，避免出现只绑定HMI侧、PLC侧无变量的假同步。"
            };

            var hmiRuntimeDetected = projectInfo["hmiRuntimeDetected"]?.GetValue<bool>() == true;
            if (hmiRuntimeDetected)
            {
                list.Add("参考项目包含完整Unified runtime结构，可用于提取画面分类、faceplate数量、图形资源和字体/样式基线。");
            }

            var libraryExists = libraryInfo["exists"]?.GetValue<bool>() == true;
            if (libraryExists)
            {
                list.Add("HMI Template Suite全局库目录存在，下一步应使用TIA Openness只读ProbeGlobalLibrary验证可见的库对象层级。");
            }

            return list;
        }

        private static string BuildReferenceAnalysisMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# Reference Asset Analysis");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]?.ToString());
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety Policy");
            md.AppendLine("- Online monitor: read current variable status only; do not modify watch-table objects while online.");
            md.AppendLine("- Force-table and force-related operations: not exposed.");
            md.AppendLine("- Delivery package: not modified by this analysis.");
            md.AppendLine();

            var project = root["referenceProject"] as JsonObject;
            md.AppendLine("## Reference HMI Runtime");
            md.AppendLine("- Path: " + project?["path"]);
            md.AppendLine("- Exists: " + project?["exists"]);
            md.AppendLine("- HMI runtime detected: " + project?["hmiRuntimeDetected"]);
            var counts = project?["fileCounts"] as JsonObject;
            if (counts != null)
            {
                md.AppendLine("- Files: total=" + counts["total"] + ", rdf=" + counts["rdf"] + ", screens=" + counts["screenRdf"] + ", faceplates=" + counts["faceplateRdf"]);
            }
            AppendSectionSummary(md, project, "screens", "Screens");
            AppendSectionSummary(md, project, "faceplates", "Faceplates");
            AppendSectionSummary(md, project, "graphics", "Graphics");
            md.AppendLine();

            var library = root["referenceGlobalLibrary"] as JsonObject;
            md.AppendLine("## Reference Global Library");
            md.AppendLine("- Path: " + library?["path"]);
            md.AppendLine("- Exists: " + library?["exists"]);
            AppendSectionSummary(md, library, "System", "System");
            AppendSectionSummary(md, library, "XRef", "XRef");
            AppendSectionSummary(md, library, "src", "src");
            md.AppendLine();

            md.AppendLine("## Recommendations");
            if (root["recommendations"] is JsonArray recs)
            {
                foreach (var rec in recs)
                {
                    md.AppendLine("- " + rec);
                }
            }

            return md.ToString();
        }

        private static JsonObject SafetySelfTestToJson(ResponseSafetySelfTest safety)
        {
            return new JsonObject
            {
                ["ok"] = safety.Ok == true,
                ["message"] = safety.Message ?? "",
                ["policy"] = new JsonArray((safety.Policy ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["items"] = new JsonArray((safety.Items ?? Array.Empty<CapabilitySelfTestItem>()).Select(x => new JsonObject
                {
                    ["id"] = x.Id ?? "",
                    ["name"] = x.Name ?? "",
                    ["status"] = x.Status ?? "",
                    ["detail"] = x.Detail ?? ""
                }).ToArray())
            };
        }

        private static string BuildMonitoringReadOnlyMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# Monitoring Read-Only Report");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- Online mode may only read current variable status/value after separate sacrificial proof.");
            md.AppendLine("- This report only lists and exports existing watch tables; it does not create, import, delete, modify, write values, or force values.");
            md.AppendLine("- Force-table and force-related operations remain forbidden.");
            md.AppendLine();

            md.AppendLine("## Summary");
            md.AppendLine("- Requested PLC software path: " + root["requestedPlcSoftwarePath"]);
            md.AppendLine("- Actual PLC software path: " + root["actualPlcSoftwarePath"]);
            md.AppendLine("- PLC software path resolution: " + root["plcSoftwarePathResolution"]);
            if (root["detectedPlcSoftwarePath"] != null)
            {
                md.AppendLine("- First detected PLC software path: " + root["detectedPlcSoftwarePath"]);
            }
            md.AppendLine("- Watch table regex: " + (string.IsNullOrWhiteSpace(root["watchTableRegex"]?.ToString()) ? "<none>" : root["watchTableRegex"]));
            md.AppendLine("- Export directory: " + root["exportDirectory"]);
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Live current-value read verified: " + root["liveCurrentValueReadVerified"]);
            md.AppendLine();

            var safety = root["safetySelfTest"] as JsonObject;
            md.AppendLine("## Safety Self-Test");
            md.AppendLine("- OK: " + safety?["ok"]);
            if (safety?["items"] is JsonArray safetyItems)
            {
                foreach (var node in safetyItems)
                {
                    var item = node as JsonObject;
                    md.AppendLine("- " + item?["status"] + " " + item?["id"] + ": " + item?["detail"]);
                }
            }
            md.AppendLine();

            md.AppendLine("## Watch Tables");
            if (root["watchTables"] is JsonArray watchTables && watchTables.Count > 0)
            {
                foreach (var table in watchTables)
                {
                    md.AppendLine("- " + table);
                }
            }
            else if (root["watchTablesError"] != null)
            {
                md.AppendLine("- Error: " + root["watchTablesError"]);
            }
            else
            {
                md.AppendLine("- No watch tables were listed.");
            }
            md.AppendLine();

            md.AppendLine("## Export");
            var export = root["watchTableExport"] as JsonObject;
            if (export?["exported"] is JsonArray exported && exported.Count > 0)
            {
                md.AppendLine("- Exported files:");
                foreach (var path in exported)
                {
                    md.AppendLine("  - " + path);
                }
            }
            if (export?["exportedSummary"] is JsonArray summaries && summaries.Count > 0)
            {
                md.AppendLine("- Table summaries:");
                foreach (var summaryNode in summaries)
                {
                    var summary = summaryNode as JsonObject;
                    md.AppendLine("  - " + summary?["tableName"] + ": entries=" + summary?["entryCount"] + ", symbolic=" + summary?["symbolicEntryCount"] + ", absolute=" + summary?["absoluteAddressEntryCount"]);
                }
            }
            if (export?["failed"] is JsonArray failed && failed.Count > 0)
            {
                md.AppendLine("- Failures:");
                foreach (var failure in failed)
                {
                    var obj = failure as JsonObject;
                    md.AppendLine("  - " + obj?["path"] + ": " + obj?["error"]);
                }
            }
            if (export?["error"] != null)
            {
                md.AppendLine("- Error: " + export["error"]);
            }
            md.AppendLine();

            md.AppendLine("## Online Capability Probe");
            md.AppendLine("- Probe OK: " + root["onlineCapabilityProbeOk"]);
            md.AppendLine("- Note: " + root["liveCurrentValueReadNote"]);
            md.AppendLine();
            md.AppendLine("## Online Current Value Read");
            md.AppendLine("- OK: " + root["onlineCurrentValueReadOk"]);
            md.AppendLine("- Message: " + root["onlineCurrentValueReadMessage"]);
            var currentRead = root["onlineCurrentValueRead"] as JsonObject;
            if (currentRead?["entries"] is JsonArray currentEntries && currentEntries.Count > 0)
            {
                foreach (var entryNode in currentEntries.Take(20))
                {
                    var entry = entryNode as JsonObject;
                    md.AppendLine("- " + (entry?["name"] ?? entry?["address"] ?? entry?["type"]) + ": current=" + (entry?["currentValue"] ?? entry?["monitorValue"] ?? entry?["value"] ?? "<not exposed>"));
                }
            }
            if (currentRead?["error"] != null)
            {
                md.AppendLine("- Error: " + currentRead["error"]);
            }
            return md.ToString();
        }

        private static JsonObject AnalyzeExportedWatchTableXml(string path)
        {
            var info = new JsonObject
            {
                ["path"] = path,
                ["exists"] = File.Exists(path),
                ["tableName"] = Path.GetFileNameWithoutExtension(path),
                ["entryCount"] = 0,
                ["symbolicEntryCount"] = 0,
                ["absoluteAddressEntryCount"] = 0,
                ["entries"] = new JsonArray()
            };

            if (!File.Exists(path)) return info;

            try
            {
                var doc = new XmlDocument();
                doc.XmlResolver = null; // 安全：禁用外部实体/DTD 解析，防 XXE
                doc.Load(path);
                var tableNameNode = doc.SelectSingleNode("//*[local-name()='SW.WatchAndForceTables.PlcWatchTable']/*[local-name()='AttributeList']/*[local-name()='Name']");
                if (tableNameNode != null)
                {
                    info["tableName"] = tableNameNode.InnerText;
                }

                var entries = new JsonArray();
                foreach (XmlElement entry in doc.GetElementsByTagName("SW.WatchAndForceTables.PlcWatchTableEntry").OfType<XmlElement>())
                {
                    var attrs = entry["AttributeList"];
                    var name = attrs?["Name"]?.InnerText ?? "";
                    var address = attrs?["Address"]?.InnerText ?? "";
                    var displayFormat = attrs?["DisplayFormat"]?.InnerText ?? "";
                    var modifyValue = attrs?["ModifyValue"]?.InnerText ?? "";
                    var comment = entry.GetElementsByTagName("Text")
                        .OfType<XmlElement>()
                        .Select(x => x.InnerText)
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                    entries.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["address"] = address,
                        ["displayFormat"] = displayFormat,
                        ["modifyValuePresent"] = !string.IsNullOrWhiteSpace(modifyValue),
                        ["comment"] = comment
                    });
                }

                info["entries"] = new JsonArray(entries.Take(50).Select(x => x?.DeepClone()).ToArray());
                info["entryCount"] = entries.Count;
                info["symbolicEntryCount"] = entries.OfType<JsonObject>().Count(x => !string.IsNullOrWhiteSpace(x["name"]?.ToString()));
                info["absoluteAddressEntryCount"] = entries.OfType<JsonObject>().Count(x => !string.IsNullOrWhiteSpace(x["address"]?.ToString()));
            }
            catch (Exception ex)
            {
                info["error"] = ex.Message;
            }

            return info;
        }

        private static JsonObject GlobalLibraryProbeToJson(ResponseGlobalLibraryProbe probe)
        {
            return new JsonObject
            {
                ["ok"] = probe.Ok == true,
                ["message"] = probe.Message ?? "",
                ["libraryPath"] = probe.LibraryPath ?? "",
                ["resolvedLibraryFile"] = probe.ResolvedLibraryFile ?? "",
                ["libraryType"] = probe.LibraryType ?? "",
                ["error"] = probe.Error ?? "",
                ["members"] = new JsonArray((probe.Members ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["masterCopies"] = new JsonArray((probe.MasterCopies ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["types"] = new JsonArray((probe.Types ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["folders"] = new JsonArray((probe.Folders ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["warnings"] = new JsonArray((probe.Warnings ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["raw"] = probe.Raw?.DeepClone()
            };
        }

        private static JsonObject GlobalLibraryImportToJson(ResponseGlobalLibraryImport import)
        {
            return new JsonObject
            {
                ["ok"] = import.Ok == true,
                ["message"] = import.Message ?? "",
                ["libraryPath"] = import.LibraryPath ?? "",
                ["resolvedLibraryFile"] = import.ResolvedLibraryFile ?? "",
                ["masterCopyName"] = import.MasterCopyName ?? "",
                ["hmiSoftwarePath"] = import.HmiSoftwarePath ?? "",
                ["screenName"] = import.ScreenName ?? "",
                ["importedItemName"] = import.ImportedItemName ?? "",
                ["error"] = import.Error ?? "",
                ["attempts"] = new JsonArray((import.Attempts ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["readbackItems"] = new JsonArray((import.ReadbackItems ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["warnings"] = new JsonArray((import.Warnings ?? Array.Empty<string>()).Select(x => JsonValue.Create(x)).ToArray()),
                ["raw"] = import.Raw?.DeepClone()
            };
        }

        private static JsonObject ResponseMessageToJson(ResponseMessage response)
        {
            return new JsonObject
            {
                ["message"] = response.Message ?? "",
                ["success"] = response.Meta?["success"]?.GetValue<bool>() == true,
                ["meta"] = response.Meta?.DeepClone()
            };
        }

        private static string BuildGlobalLibraryMasterCopyImportMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# Global Library MasterCopy Import Validation");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- New temporary project only.");
            md.AppendLine("- No online or force operation.");
            md.AppendLine("- Delivery package is not modified.");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Project: " + root["projectName"]);
            md.AppendLine("- Project directory: " + root["projectDirectory"]);
            md.AppendLine("- Library: " + root["libraryPath"]);
            md.AppendLine("- MasterCopy: " + root["masterCopyName"]);
            md.AppendLine("- Screen: " + root["screenName"]);
            md.AppendLine("- masterCopyImportReadbackOk: " + root["masterCopyImportReadbackOk"]);
            md.AppendLine("- screenExists: " + root["screenExists"]);
            if (!string.IsNullOrWhiteSpace(root["error"]?.ToString()))
                md.AppendLine("- Error: " + root["error"]);
            md.AppendLine();

            var import = root["import"] as JsonObject;
            if (import != null)
            {
                md.AppendLine("## Import Result");
                md.AppendLine("- OK: " + import["ok"]);
                md.AppendLine("- Imported item: " + import["importedItemName"]);
                md.AppendLine("- Error: " + import["error"]);
                md.AppendLine();
                AppendJsonArrayPreview(md, import["attempts"] as JsonArray, "Import Attempts", 80);
                AppendJsonArrayPreview(md, import["readbackItems"] as JsonArray, "ScreenItems Readback", 80);
                AppendJsonArrayPreview(md, import["warnings"] as JsonArray, "Warnings", 20);
            }

            return md.ToString();
        }

        private static string BuildUnifiedHmiActionSyntaxCheckMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# Unified HMI Action SyntaxCheck Validation");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- New temporary project only.");
            md.AppendLine("- Deterministic set-bit recipe only.");
            md.AppendLine("- No online, watch-table edit, current-value write, or force operation.");
            md.AppendLine("- Delivery package is not modified.");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Project: " + root["projectName"]);
            md.AppendLine("- Project directory: " + root["projectDirectory"]);
            md.AppendLine("- Screen: " + root["screenName"]);
            md.AppendLine("- Button: " + root["buttonName"]);
            md.AppendLine("- Event: " + root["eventType"]);
            md.AppendLine("- syntaxErrorCount: " + root["syntaxErrorCount"]);
            md.AppendLine("- syntaxWarningCount: " + root["syntaxWarningCount"]);
            md.AppendLine("- Evidence: " + root["syntaxCheckEvidence"]);
            if (!string.IsNullOrWhiteSpace(root["error"]?.ToString()))
                md.AppendLine("- Error: " + root["error"]);
            md.AppendLine();
            md.AppendLine("## Apply Meta");
            md.AppendLine("```json");
            md.AppendLine((root["ensureAction"] as JsonObject)?.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) ?? "{}");
            md.AppendLine("```");
            return md.ToString();
        }

        private static string BuildGlobalLibraryProbeMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# Global Library Probe Report");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- TIA-connected read-only probe.");
            md.AppendLine("- No master copy/type import and no project/library write.");
            md.AppendLine("- Delivery package is not modified.");
            md.AppendLine();

            var probe = root["probe"] as JsonObject;
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Library path: " + root["libraryPath"]);
            md.AppendLine("- Resolved file: " + probe?["resolvedLibraryFile"]);
            md.AppendLine("- Library type: " + probe?["libraryType"]);
            md.AppendLine("- Master copies: " + ((probe?["masterCopies"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- Types: " + ((probe?["types"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- Folders: " + ((probe?["folders"] as JsonArray)?.Count ?? 0));
            if (!string.IsNullOrWhiteSpace(probe?["error"]?.ToString()))
            {
                md.AppendLine("- Error: " + probe?["error"]);
            }
            md.AppendLine();

            AppendJsonArrayPreview(md, probe?["masterCopies"] as JsonArray, "Master Copies", 40);
            AppendJsonArrayPreview(md, probe?["types"] as JsonArray, "Types", 40);
            AppendJsonArrayPreview(md, probe?["folders"] as JsonArray, "Folders", 40);
            AppendJsonArrayPreview(md, probe?["warnings"] as JsonArray, "Warnings", 20);
            return md.ToString();
        }

        private static SortedSet<string> ExtractPlcSymbolsFromTagTableExports(IEnumerable<string> exportedFiles)
        {
            var symbols = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in exportedFiles)
            {
                try
                {
                    var doc = new XmlDocument();
                    doc.XmlResolver = null; // 安全：禁用外部实体/DTD 解析，防 XXE
                    doc.Load(file);
                    foreach (XmlElement name in doc.GetElementsByTagName("Name").OfType<XmlElement>())
                    {
                        var value = name.InnerText?.Trim();
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        if (value!.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal) && value.Length >= 2)
                        {
                            value = value.Substring(1, value.Length - 2);
                        }
                        if (value.IndexOf(' ') >= 0) continue;
                        symbols.Add(value);
                    }
                }
                catch
                {
                    // Exported XML analysis is best-effort; export failures are reported separately.
                }
            }
            return symbols;
        }

        private static string[] SelectPlcTagTablesForPrecheck(string[] tables, string regexText, int maxCount)
        {
            if (tables.Length == 0 || maxCount <= 0 || string.IsNullOrWhiteSpace(regexText))
            {
                return Array.Empty<string>();
            }

            try
            {
                var regex = new Regex(regexText, RegexOptions.IgnoreCase);
                return tables
                    .Where(x => regex.IsMatch(x))
                    .Take(maxCount)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static void RunAnalyzeHmiTemplatePlcMapping(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var templateDir = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var plcExportDirectory = string.IsNullOrWhiteSpace(options.PlcExportDirectory)
                ? Path.Combine(workspaceRoot, "TMP_EXPORT", "Source", "5T车", "Blocks")
                : options.PlcExportDirectory!;
            var reportDir = string.IsNullOrWhiteSpace(options.HmiTemplatePlcMappingReportDirectory)
                ? Path.Combine(workspaceRoot, "reports", "hmi_template_plc_mapping")
                : options.HmiTemplatePlcMappingReportDirectory!;

            Directory.CreateDirectory(reportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var jsonPath = Path.Combine(reportDir, "hmi_template_plc_mapping_" + stamp + ".json");
            var mdPath = Path.Combine(reportDir, "hmi_template_plc_mapping_" + stamp + ".md");

            var templates = HmiTemplateReferenceAnalyzer.Analyze(templateDir, "", "");
            var plcCatalog = AnalyzePlcExportDirectory(plcExportDirectory);
            var plcSymbols = BuildPlcSymbolCatalog(plcCatalog);
            var mappings = BuildHmiTemplatePlcMapping(templates["templates"] as JsonArray ?? new JsonArray(), plcSymbols);

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["templateDirectory"] = templateDir,
                ["plcExportDirectory"] = plcExportDirectory,
                ["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Offline PLC symbol mapping suggestion only.",
                    ["tia"] = "TIA Portal is not connected or opened.",
                    ["write"] = "No PLC block, HMI tag, HMI screen, event, template, or delivery package is modified.",
                    ["binding"] = "Candidates are suggestions. Only exact full-symbol matches are treated as verified mappings."
                },
                ["plcExportCatalog"] = plcCatalog,
                ["plcSymbolCount"] = plcSymbols.Count,
                ["templates"] = mappings,
                ["ok"] = Directory.Exists(templateDir) && Directory.Exists(plcExportDirectory)
            };

            File.WriteAllText(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildHmiTemplatePlcMappingMarkdown(root, jsonPath), Encoding.UTF8);

            LogDiag("HMI template PLC mapping report written:");
            LogDiag(mdPath);
            LogDiag(jsonPath);
        }

        private static JsonObject AnalyzePlcExportDirectory(string directory)
        {
            var root = new JsonObject
            {
                ["directory"] = directory,
                ["exists"] = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
                ["mode"] = "Read-only offline PLC export XML/SCL symbol scan.",
                ["filesScanned"] = 0,
                ["blocks"] = new JsonArray(),
                ["symbols"] = new JsonArray(),
                ["warnings"] = new JsonArray()
            };

            if (string.IsNullOrWhiteSpace(directory))
            {
                root["mode"] = "skipped; pass --plc-export-directory to include offline DB/block member symbols";
                return root;
            }

            if (!Directory.Exists(directory))
            {
                (root["warnings"] as JsonArray)?.Add("PLC export directory does not exist.");
                return root;
            }

            var symbols = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var blocks = root["blocks"] as JsonArray ?? new JsonArray();
            var warnings = root["warnings"] as JsonArray ?? new JsonArray();
            var files = Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                .Where(x => x.IndexOf("\\ForceTables\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Take(2000)
                .ToList();
            root["filesScanned"] = files.Count;
            var udtCatalog = BuildUdtMemberCatalog(files, warnings);
            root["udtTypes"] = new JsonArray(udtCatalog
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JsonObject
                {
                    ["name"] = x.Key,
                    ["memberCount"] = x.Value.Count,
                    ["membersSample"] = new JsonArray(x.Value.Take(80).Select(m => PlcMemberSymbolToJson(m)).ToArray())
                })
                .ToArray());
            root["udtTypeCount"] = udtCatalog.Count;

            foreach (var file in files)
            {
                try
                {
                    var doc = new XmlDocument();
                    doc.XmlResolver = null; // 安全：禁用外部实体/DTD 解析，防 XXE
                    doc.Load(file);
                    var blockElement = doc.GetElementsByTagName("SW.Blocks.GlobalDB").OfType<XmlElement>().FirstOrDefault()
                        ?? doc.GetElementsByTagName("SW.Blocks.FB").OfType<XmlElement>().FirstOrDefault()
                        ?? doc.GetElementsByTagName("SW.Blocks.FC").OfType<XmlElement>().FirstOrDefault()
                        ?? doc.GetElementsByTagName("SW.Blocks.OB").OfType<XmlElement>().FirstOrDefault();
                    if (blockElement == null) continue;

                    var blockName = GetFirstDescendantText(blockElement, "Name");
                    if (string.IsNullOrWhiteSpace(blockName))
                    {
                        blockName = Path.GetFileNameWithoutExtension(file);
                    }

                    var kind = blockElement.Name.Replace("SW.Blocks.", "");
                    symbols.Add(blockName);
                    var members = new JsonArray();
                    foreach (XmlElement section in blockElement.GetElementsByTagName("Section"))
                    {
                        var sectionName = section.GetAttribute("Name");
                        foreach (var member in ExtractPlcMembers(blockName, sectionName, section, 0))
                        {
                            symbols.Add(member.Symbol);
                            members.Add(PlcMemberSymbolToJson(member));
                            foreach (var expanded in ExpandUdtMembers(member, udtCatalog, 0))
                            {
                                symbols.Add(expanded.Symbol);
                                members.Add(PlcMemberSymbolToJson(expanded));
                            }
                        }
                    }

                    blocks.Add(new JsonObject
                    {
                        ["name"] = blockName,
                        ["kind"] = kind,
                        ["file"] = file,
                        ["memberCount"] = members.Count,
                        ["members"] = new JsonArray(members.Select(x => x?.DeepClone()).ToArray()),
                        ["membersSample"] = new JsonArray(members.Take(80).Select(x => x?.DeepClone()).ToArray())
                    });
                }
                catch (Exception ex)
                {
                    if (warnings.Count < 100)
                    {
                        warnings.Add(Path.GetFileName(file) + ": " + ex.Message);
                    }
                }
            }

            root["symbols"] = new JsonArray(symbols.Select(x => JsonValue.Create(x)).ToArray());
            root["symbolCount"] = symbols.Count;
            return root;
        }

        private static Dictionary<string, List<PlcMemberSymbol>> BuildUdtMemberCatalog(List<string> files, JsonArray warnings)
        {
            var catalog = new Dictionary<string, List<PlcMemberSymbol>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try
                {
                    var doc = new XmlDocument();
                    doc.XmlResolver = null; // 安全：禁用外部实体/DTD 解析，防 XXE
                    doc.Load(file);
                    var udtElement = doc.GetElementsByTagName("SW.Types.PlcStruct").OfType<XmlElement>().FirstOrDefault();
                    if (udtElement == null) continue;

                    var typeName = GetFirstDescendantText(udtElement, "Name");
                    if (string.IsNullOrWhiteSpace(typeName))
                    {
                        typeName = Path.GetFileNameWithoutExtension(file);
                    }

                    var members = new List<PlcMemberSymbol>();
                    foreach (XmlElement section in udtElement.GetElementsByTagName("Section"))
                    {
                        var sectionName = section.GetAttribute("Name");
                        foreach (var member in ExtractPlcMembers("", sectionName, section, 0))
                        {
                            member.Source = "udt-definition";
                            member.OwnerType = typeName;
                            members.Add(member);
                        }
                    }

                    if (members.Count > 0)
                    {
                        catalog[NormalizePlcDataType(typeName)] = members;
                    }
                }
                catch (Exception ex)
                {
                    if (warnings.Count < 100)
                    {
                        warnings.Add(Path.GetFileName(file) + " UDT scan: " + ex.Message);
                    }
                }
            }

            return catalog;
        }

        private static IEnumerable<PlcMemberSymbol> ExpandUdtMembers(PlcMemberSymbol parent, Dictionary<string, List<PlcMemberSymbol>> udtCatalog, int depth)
        {
            if (depth > 8) yield break;
            var typeName = NormalizePlcDataType(parent.DataType);
            if (string.IsNullOrWhiteSpace(typeName) || !udtCatalog.TryGetValue(typeName, out var members))
                yield break;

            foreach (var udtMember in members)
            {
                var expanded = new PlcMemberSymbol
                {
                    Symbol = parent.Symbol + "." + udtMember.Symbol,
                    Name = udtMember.Name,
                    Section = parent.Section,
                    DataType = udtMember.DataType,
                    Source = "udt-expanded",
                    OwnerType = typeName
                };
                yield return expanded;

                foreach (var child in ExpandUdtMembers(expanded, udtCatalog, depth + 1))
                {
                    yield return child;
                }
            }
        }

        private static JsonObject PlcMemberSymbolToJson(PlcMemberSymbol member)
        {
            return new JsonObject
            {
                ["symbol"] = member.Symbol,
                ["name"] = member.Name,
                ["section"] = member.Section,
                ["dataType"] = member.DataType,
                ["source"] = member.Source,
                ["ownerType"] = member.OwnerType
            };
        }

        private static List<PlcSymbolCandidate> BuildPlcSymbolCatalog(JsonObject plcExportCatalog)
        {
            var list = new List<PlcSymbolCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var blockNode in plcExportCatalog["blocks"] as JsonArray ?? new JsonArray())
            {
                if (blockNode is not JsonObject block) continue;
                var blockName = block["name"]?.ToString() ?? "";
                var kind = block["kind"]?.ToString() ?? "";
                var file = block["file"]?.ToString() ?? "";
                foreach (var memberNode in block["members"] as JsonArray ?? new JsonArray())
                {
                    if (memberNode is not JsonObject member) continue;
                    var symbol = NormalizePlcSymbol(member["symbol"]?.ToString() ?? "");
                    if (string.IsNullOrWhiteSpace(symbol) || !seen.Add(symbol)) continue;
                    list.Add(new PlcSymbolCandidate
                    {
                        Symbol = symbol,
                        LeafName = member["name"]?.ToString() ?? "",
                        DataType = NormalizePlcDataType(member["dataType"]?.ToString() ?? ""),
                        Section = member["section"]?.ToString() ?? "",
                        BlockName = blockName,
                        BlockKind = kind,
                        File = file
                    });
                }
            }

            return list
                .OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static JsonArray BuildHmiTemplatePlcMapping(JsonArray templates, List<PlcSymbolCandidate> plcSymbols)
        {
            var result = new JsonArray();
            foreach (var templateNode in templates)
            {
                if (templateNode is not JsonObject template) continue;
                var templateName = template["templateName"]?.ToString() ?? "";
                var rows = new JsonArray();
                var verified = 0;
                var highConfidence = 0;
                var review = 0;
                var missing = 0;

                foreach (var tagNode in template["requiredTags"] as JsonArray ?? new JsonArray())
                {
                    if (tagNode is not JsonObject tag) continue;
                    var hmiTag = tag["Name"]?.ToString() ?? "";
                    var dataType = NormalizePlcDataType(tag["DataType"]?.ToString() ?? "");
                    var desiredPlcTag = NormalizePlcSymbol(tag["PlcTag"]?.ToString() ?? "");
                    var candidates = RankPlcSymbolCandidates(hmiTag, desiredPlcTag, dataType, plcSymbols)
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToArray();
                    var best = candidates.FirstOrDefault();
                    var status = best == null
                        ? "no-candidate"
                        : best.VerifiedExact
                            ? "verified-exact"
                            : best.Score >= 92 && best.DataTypeMatch
                                ? "high-confidence-review"
                                : "review-required";
                    var gateStatus = status == "verified-exact"
                        ? "ready-for-sync-precheck"
                        : status == "high-confidence-review"
                            ? "blocked-needs-explicit-mapping"
                            : "blocked-needs-manual-review";
                    var gateReason = status == "verified-exact"
                        ? "Full PLC symbol already exists exactly as requested."
                        : status == "high-confidence-review"
                            ? "A strong candidate exists, but candidate scoring is not allowed to auto-bind."
                            : best == null
                                ? "No usable PLC symbol candidate was found in the export catalog."
                                : "Candidate is weak, ambiguous, or requires human/project-rule confirmation.";
                    var recommendedNextAction = status == "verified-exact"
                        ? "Keep this exact mapping and run HMI template sync precheck."
                        : status == "high-confidence-review"
                            ? "Review the best candidate, then write it into an explicit mapping file if correct."
                            : best == null
                                ? "Create/export the matching PLC tag or DB member, or add a project-specific mapping rule."
                                : "Inspect candidates and fill MappedPlcTag only after verification.";

                    if (status == "verified-exact") verified++;
                    else if (status == "high-confidence-review") highConfidence++;
                    else if (status == "review-required") review++;
                    else missing++;

                    rows.Add(new JsonObject
                    {
                        ["hmiTag"] = hmiTag,
                        ["dataType"] = dataType,
                        ["desiredPlcTag"] = desiredPlcTag,
                        ["status"] = status,
                        ["gateStatus"] = gateStatus,
                        ["gateReason"] = gateReason,
                        ["recommendedNextAction"] = recommendedNextAction,
                        ["bestCandidate"] = best == null ? new JsonObject() : PlcMappingCandidateToJson(best),
                        ["candidates"] = new JsonArray(candidates.Select(PlcMappingCandidateToJson).ToArray())
                    });
                }

                var ready = verified == rows.Count && rows.Count > 0;
                result.Add(new JsonObject
                {
                    ["templateName"] = templateName,
                    ["requiredTagCount"] = rows.Count,
                    ["verifiedExact"] = verified,
                    ["highConfidenceCandidates"] = highConfidence,
                    ["reviewRequired"] = review,
                    ["noCandidate"] = missing,
                    ["status"] = ready ? "mapping-ready" : "mapping-needs-review",
                    ["gateStatus"] = ready ? "ready-for-sync-precheck" : "blocked-needs-explicit-mapping",
                    ["gateReason"] = ready
                        ? "Every RequiredTag maps to an exact PLC export symbol."
                        : "One or more RequiredTags are missing exact PLC symbols; candidates cannot be applied automatically.",
                    ["mappings"] = rows
                });
            }

            return result;
        }

        private static IEnumerable<PlcMappingCandidate> RankPlcSymbolCandidates(string hmiTag, string desiredPlcTag, string dataType, List<PlcSymbolCandidate> plcSymbols)
        {
            var desired = NormalizePlcSymbol(desiredPlcTag);
            var desiredTokens = TokenizeSymbol(desired);
            var hmiTokens = TokenizeSymbol(hmiTag);
            var expectedTokens = desiredTokens.Count > 0 ? desiredTokens : hmiTokens;
            var desiredLeaf = ExtractLeafName(desired);
            var hmiLeaf = ExtractLeafName(hmiTag);

            foreach (var symbol in plcSymbols)
            {
                var normalizedSymbol = NormalizePlcSymbol(symbol.Symbol);
                var symbolTokens = TokenizeSymbol(normalizedSymbol);
                var leaf = ExtractLeafName(normalizedSymbol);
                var dataTypeMatch = string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(symbol.DataType) || ArePlcDataTypesCompatible(dataType, symbol.DataType);
                var score = 0;
                var reasons = new List<string>();
                var verifiedExact = !string.IsNullOrWhiteSpace(desired) && string.Equals(normalizedSymbol, desired, StringComparison.OrdinalIgnoreCase);

                if (verifiedExact)
                {
                    score += 120;
                    reasons.Add("完整PLC符号精确匹配");
                }

                if (!string.IsNullOrWhiteSpace(desiredLeaf) && string.Equals(leaf, desiredLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    score += 45;
                    reasons.Add("叶子名称匹配");
                }
                else if (!string.IsNullOrWhiteSpace(hmiLeaf) && string.Equals(leaf, hmiLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    score += 35;
                    reasons.Add("HMI名称叶子匹配");
                }

                var overlap = expectedTokens.Intersect(symbolTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (overlap > 0)
                {
                    score += Math.Min(35, overlap * 9);
                    reasons.Add("名称关键词重合:" + overlap);
                }

                if (dataTypeMatch)
                {
                    score += 20;
                    reasons.Add("数据类型兼容");
                }
                else
                {
                    score -= 25;
                    reasons.Add("数据类型不匹配");
                }

                if (normalizedSymbol.IndexOf("HMI", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 10;
                    reasons.Add("HMI相关DB/变量");
                }

                if (desired.IndexOf("Axis", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    Regex.IsMatch(normalizedSymbol, "Axis|Gantry|Crane|大车|小车|行走|速度|位置", RegexOptions.IgnoreCase))
                {
                    score += 8;
                    reasons.Add("轴/运动语义相关");
                }

                if (desired.IndexOf("PID", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    Regex.IsMatch(normalizedSymbol, "PID|PV|SP|Kp|Ti|OUT|输出|给定|反馈", RegexOptions.IgnoreCase))
                {
                    score += 8;
                    reasons.Add("PID语义相关");
                }

                if (score <= 0) continue;

                yield return new PlcMappingCandidate
                {
                    Symbol = normalizedSymbol,
                    DataType = symbol.DataType,
                    Section = symbol.Section,
                    BlockName = symbol.BlockName,
                    BlockKind = symbol.BlockKind,
                    File = symbol.File,
                    Score = score,
                    DataTypeMatch = dataTypeMatch,
                    VerifiedExact = verifiedExact,
                    Reasons = reasons.ToArray()
                };
            }
        }

        private static JsonObject PlcMappingCandidateToJson(PlcMappingCandidate candidate)
        {
            return new JsonObject
            {
                ["symbol"] = candidate.Symbol,
                ["dataType"] = candidate.DataType,
                ["score"] = candidate.Score,
                ["dataTypeMatch"] = candidate.DataTypeMatch,
                ["verifiedExact"] = candidate.VerifiedExact,
                ["section"] = candidate.Section,
                ["blockName"] = candidate.BlockName,
                ["blockKind"] = candidate.BlockKind,
                ["file"] = candidate.File,
                ["reasons"] = new JsonArray(candidate.Reasons.Select(x => JsonValue.Create(x)).ToArray())
            };
        }

        private static string BuildHmiTemplatePlcMappingMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# HMI Template PLC Mapping Analysis");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- Offline mapping suggestion only; no TIA connection and no project write.");
            md.AppendLine("- No HMI tag, screen, event, PLC block, template, or delivery package is modified.");
            md.AppendLine("- Only `verified-exact` means the requested PLC symbol already exists. Candidates require review before binding.");
            md.AppendLine();
            md.AppendLine("## Inputs");
            md.AppendLine("- Template directory: " + root["templateDirectory"]);
            md.AppendLine("- PLC export directory: " + root["plcExportDirectory"]);
            md.AppendLine("- PLC symbols analyzed: " + root["plcSymbolCount"]);
            md.AppendLine();

            md.AppendLine("## Template Summary");
            foreach (var templateNode in root["templates"] as JsonArray ?? new JsonArray())
            {
                if (templateNode is not JsonObject template) continue;
                md.AppendLine("- " + template["templateName"] + ": " + template["status"] +
                              ", gate=" + template["gateStatus"] +
                              ", verified=" + template["verifiedExact"] +
                              ", highConfidence=" + template["highConfidenceCandidates"] +
                              ", review=" + template["reviewRequired"] +
                              ", none=" + template["noCandidate"] +
                              ", reason=" + template["gateReason"]);
            }
            md.AppendLine();

            foreach (var templateNode in root["templates"] as JsonArray ?? new JsonArray())
            {
                if (templateNode is not JsonObject template) continue;
                md.AppendLine("## " + template["templateName"]);
                md.AppendLine("| HMI tag | Desired PLC tag | Type | Status | Gate | Best candidate | Score | Next action |");
                md.AppendLine("|---|---|---|---|---|---|---:|---|");
                foreach (var mappingNode in template["mappings"] as JsonArray ?? new JsonArray())
                {
                    if (mappingNode is not JsonObject mapping) continue;
                    var best = mapping["bestCandidate"] as JsonObject;
                    md.AppendLine("| " + EscapeMarkdownCell(mapping["hmiTag"]?.ToString() ?? "") +
                                  " | " + EscapeMarkdownCell(mapping["desiredPlcTag"]?.ToString() ?? "") +
                                  " | " + EscapeMarkdownCell(mapping["dataType"]?.ToString() ?? "") +
                                  " | " + EscapeMarkdownCell(mapping["status"]?.ToString() ?? "") +
                                  " | " + EscapeMarkdownCell(mapping["gateStatus"]?.ToString() ?? "") +
                                  " | " + EscapeMarkdownCell(best?["symbol"]?.ToString() ?? "") +
                                  " | " + (best?["score"]?.ToString() ?? "") +
                                  " | " + EscapeMarkdownCell(mapping["recommendedNextAction"]?.ToString() ?? "") + " |");
                }
                md.AppendLine();
            }

            md.AppendLine("## Release Readiness Gate");
            md.AppendLine("- `mapping-ready` only means every required tag has an exact or high-confidence candidate; it is still not applied automatically.");
            md.AppendLine("- Before real application, generate an explicit mapping file, run sync precheck with full-symbol matches, then run temporary-project binding validation.");
            md.AppendLine("- Low-confidence or type-mismatch candidates must be corrected by a human or deterministic project rule.");
            return md.ToString();
        }

        private static JsonObject BuildHmiTemplateMappingSkeletonJson(string templateDir, string plcExportDirectory, JsonArray mappingAnalysis, List<PlcSymbolCandidate>? plcSymbols = null)
        {
            var deterministicRuleCount = 0;
            var root = new JsonObject
            {
                ["Format"] = "tia-hmi-template-plc-mapping-v1",
                ["GeneratedAt"] = DateTime.Now.ToString("O"),
                ["TemplateDirectory"] = templateDir,
                ["PlcExportDirectory"] = plcExportDirectory,
                ["Safety"] = new JsonObject
                {
                    ["Comment"] = "本文件是显式映射文件骨架。只有 MappedPlcTag 非空的项才允许进入后续同步预检和绑定验证。",
                    ["Rule"] = "候选 Candidates 不能自动绑定，必须人工确认或由确定性规则写入 MappedPlcTag。",
                    ["DeterministicRuleGate"] = "确定性规则必须命中完整 PLC 符号、数据类型兼容，并记录 MappingSource/DeterministicRule。"
                },
                ["Templates"] = new JsonArray()
            };

            var templatesOut = root["Templates"] as JsonArray ?? new JsonArray();
            foreach (var templateNode in mappingAnalysis)
            {
                if (templateNode is not JsonObject template) continue;
                var mappingsOut = new JsonArray();
                foreach (var mappingNode in template["mappings"] as JsonArray ?? new JsonArray())
                {
                    if (mappingNode is not JsonObject mapping) continue;
                    var best = mapping["bestCandidate"] as JsonObject ?? new JsonObject();
                    var status = mapping["status"]?.ToString() ?? "";
                    var mapped = status == "verified-exact" ? best["symbol"]?.ToString() ?? "" : "";
                    var mappingSource = !string.IsNullOrWhiteSpace(mapped) ? "verified-exact" : "";
                    var deterministicRule = "";
                    var deterministicReason = "";
                    if (string.IsNullOrWhiteSpace(mapped) &&
                        TryResolveDeterministicHmiTemplateMapping(
                            template["templateName"]?.ToString() ?? "",
                            mapping["hmiTag"]?.ToString() ?? "",
                            mapping["desiredPlcTag"]?.ToString() ?? "",
                            mapping["dataType"]?.ToString() ?? "",
                            plcSymbols ?? new List<PlcSymbolCandidate>(),
                            out var ruleMapped,
                            out deterministicRule,
                            out deterministicReason))
                    {
                        mapped = ruleMapped;
                        mappingSource = "deterministic-project-rule";
                        status = "deterministic-rule";
                        deterministicRuleCount++;
                    }
                    var autoBindAllowed = !string.IsNullOrWhiteSpace(mapped);
                    mappingsOut.Add(new JsonObject
                    {
                        ["HmiTag"] = mapping["hmiTag"]?.ToString() ?? "",
                        ["OriginalPlcTag"] = mapping["desiredPlcTag"]?.ToString() ?? "",
                        ["MappedPlcTag"] = mapped,
                        ["DataType"] = mapping["dataType"]?.ToString() ?? "",
                        ["Status"] = autoBindAllowed ? status : "needs-review",
                        ["AutoBindAllowed"] = autoBindAllowed,
                        ["MappingSource"] = mappingSource,
                        ["DeterministicRule"] = deterministicRule,
                        ["RejectReason"] = autoBindAllowed ? "" : "没有完整PLC符号精确匹配；候选项只能用于人工/确定性规则确认，不能自动绑定。",
                        ["Comment"] = autoBindAllowed
                            ? (mappingSource == "deterministic-project-rule"
                                ? "中文说明：该映射由确定性项目规则写入，已确认完整 PLC 符号存在且数据类型兼容。"
                                : "完整 PLC 符号已精确存在，可进入同步预检和临时项目绑定验证。")
                            : "请确认候选后填写 MappedPlcTag；未填写时不会用于绑定。",
                        ["RuleEvidence"] = deterministicReason,
                        ["ReviewChecklist"] = new JsonArray
                        {
                            "确认 PLC 符号完整路径存在，不能只确认根 DB 或块名。",
                            "确认数据类型与 HMI 控件用途兼容。",
                            "确认该变量允许 HMI 读写；命令类变量还需要事件安全策略。",
                            "确认映射来源是人工确认或确定性项目规则，不是候选分数自动选择。"
                        },
                        ["Candidates"] = (mapping["candidates"] as JsonArray)?.DeepClone() ?? new JsonArray()
                    });
                }

                templatesOut.Add(new JsonObject
                {
                    ["TemplateName"] = template["templateName"]?.ToString() ?? "",
                    ["Status"] = template["status"]?.ToString() ?? "",
                    ["Mappings"] = mappingsOut
                });
            }

            root["DeterministicRuleMappingCount"] = deterministicRuleCount;
            return root;
        }

        private static bool TryResolveDeterministicHmiTemplateMapping(
            string templateName,
            string hmiTag,
            string originalPlcTag,
            string hmiDataType,
            List<PlcSymbolCandidate> plcSymbols,
            out string mappedPlcTag,
            out string ruleName,
            out string evidence)
        {
            mappedPlcTag = "";
            ruleName = "";
            evidence = "";

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["drive-axis-control|Cmd_Axis_Reset"] = "HMI_Data.大车复位",
                ["drive-axis-control|Cmd_Axis_JogFwd"] = "HMI_Data.大车向前",
                ["drive-axis-control|Cmd_Axis_JogRev"] = "HMI_Data.大车向后",
                ["equipment-overview|Sys_Auto"] = "Global_Data.CMS.Auto",
                ["equipment-overview|Sys_Fault"] = "A5_DB2_Faults_DB.Sys_Fault_Flag",
                ["equipment-overview|Cmd_Stop"] = "21_DB_interface.Auto.Stop",
                ["equipment-overview|Cmd_Reset"] = "21_DB_interface.Auto.Reset",
                ["hmi-data-export-probe|HMI_RunEnable"] = "HMI_Data.总运行使能",
                ["hmi-data-export-probe|HMI_GantryForward"] = "HMI_Data.大车向前",
                ["hmi-data-export-probe|HMI_GantrySpeedSet"] = "HMI_Data.大车频率给定"
            };

            var key = (templateName ?? "") + "|" + (hmiTag ?? "");
            if (!aliases.TryGetValue(key, out var targetSymbol))
                return false;

            var candidate = plcSymbols.FirstOrDefault(x =>
                string.Equals(NormalizePlcSymbol(x.Symbol), NormalizePlcSymbol(targetSymbol), StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                evidence = "Deterministic alias target not found in PLC export catalog: " + targetSymbol;
                return false;
            }

            if (!string.Equals(candidate.BlockKind, "GlobalDB", StringComparison.OrdinalIgnoreCase))
            {
                evidence = "Deterministic alias target is not a GlobalDB member: " + targetSymbol;
                return false;
            }

            if (!ArePlcDataTypesCompatible(hmiDataType, candidate.DataType))
            {
                evidence = "Deterministic alias target data type is incompatible. HMI=" + hmiDataType + ", PLC=" + candidate.DataType;
                return false;
            }

            mappedPlcTag = NormalizePlcSymbol(candidate.Symbol);
            ruleName = "builtin-5t-hmi-template-alias-v1";
            evidence = "Template=" + templateName + ", HmiTag=" + hmiTag + ", OriginalPlcTag=" + originalPlcTag +
                       ", MappedPlcTag=" + mappedPlcTag + ", DataType=" + candidate.DataType +
                       ", Source=offline PLC export catalog.";
            return true;
        }

        private static JsonObject LoadHmiTemplateMappingFile(string mappingPath)
        {
            var root = new JsonObject
            {
                ["path"] = mappingPath,
                ["exists"] = !string.IsNullOrWhiteSpace(mappingPath) && File.Exists(mappingPath),
                ["loaded"] = false,
                ["entries"] = new JsonArray(),
                ["warnings"] = new JsonArray()
            };

            if (string.IsNullOrWhiteSpace(mappingPath)) return root;
            if (!File.Exists(mappingPath))
            {
                (root["warnings"] as JsonArray)?.Add("Mapping file not found.");
                return root;
            }

            try
            {
                var json = JsonNode.Parse(File.ReadAllText(mappingPath, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidOperationException("Mapping root must be a JSON object.");
                root["format"] = json["Format"]?.ToString() ?? "";
                var entries = root["entries"] as JsonArray ?? new JsonArray();
                foreach (var templateNode in json["Templates"] as JsonArray ?? new JsonArray())
                {
                    if (templateNode is not JsonObject template) continue;
                    var templateName = template["TemplateName"]?.ToString() ?? "";
                    foreach (var mappingNode in template["Mappings"] as JsonArray ?? new JsonArray())
                    {
                        if (mappingNode is not JsonObject mapping) continue;
                        var hmiTag = mapping["HmiTag"]?.ToString() ?? "";
                        var mapped = NormalizePlcSymbol(mapping["MappedPlcTag"]?.ToString() ?? "");
                        if (string.IsNullOrWhiteSpace(templateName) || string.IsNullOrWhiteSpace(hmiTag) || string.IsNullOrWhiteSpace(mapped)) continue;
                        entries.Add(new JsonObject
                        {
                            ["templateName"] = templateName,
                            ["hmiTag"] = hmiTag,
                            ["mappedPlcTag"] = mapped,
                            ["dataType"] = mapping["DataType"]?.ToString() ?? "",
                            ["status"] = mapping["Status"]?.ToString() ?? "",
                            ["mappingSource"] = mapping["MappingSource"]?.ToString() ?? "",
                            ["deterministicRule"] = mapping["DeterministicRule"]?.ToString() ?? "",
                            ["ruleEvidence"] = mapping["RuleEvidence"]?.ToString() ?? ""
                        });
                    }
                }
                root["loaded"] = true;
            }
            catch (Exception ex)
            {
                root["error"] = ex.Message;
                (root["warnings"] as JsonArray)?.Add("Mapping file parse failed: " + ex.Message);
            }

            return root;
        }

        private static JsonArray ApplyHmiTemplateMapping(JsonArray templates, JsonObject mappingFile)
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var entryNode in mappingFile["entries"] as JsonArray ?? new JsonArray())
            {
                if (entryNode is not JsonObject entry) continue;
                var key = (entry["templateName"]?.ToString() ?? "") + "\u001f" + (entry["hmiTag"]?.ToString() ?? "");
                var mapped = NormalizePlcSymbol(entry["mappedPlcTag"]?.ToString() ?? "");
                if (!string.IsNullOrWhiteSpace(mapped)) map[key] = entry;
            }

            var result = new JsonArray();
            foreach (var templateNode in templates)
            {
                if (templateNode is not JsonObject template) continue;
                var clone = template.DeepClone().AsObject();
                var templateName = clone["templateName"]?.ToString() ?? "";
                foreach (var tagNode in clone["requiredTags"] as JsonArray ?? new JsonArray())
                {
                    if (tagNode is not JsonObject tag) continue;
                    var hmiTag = tag["Name"]?.ToString() ?? "";
                    var key = templateName + "\u001f" + hmiTag;
                    if (!map.TryGetValue(key, out var entry)) continue;
                    var mapped = NormalizePlcSymbol(entry["mappedPlcTag"]?.ToString() ?? "");
                    if (string.IsNullOrWhiteSpace(mapped)) continue;
                    tag["OriginalPlcTag"] = tag["PlcTag"]?.ToString() ?? "";
                    tag["PlcTag"] = mapped;
                    tag["MappedBy"] = "hmi-template-mapping-file";
                    tag["MappingStatus"] = entry["status"]?.ToString() ?? "";
                    tag["MappingSource"] = entry["mappingSource"]?.ToString() ?? "";
                    tag["DeterministicRule"] = entry["deterministicRule"]?.ToString() ?? "";
                    tag["RuleEvidence"] = entry["ruleEvidence"]?.ToString() ?? "";
                }
                result.Add(clone);
            }

            return result;
        }

        private static string EscapeMarkdownCell(string value)
        {
            return (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static string ExtractLeafName(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return "";
            var normalized = NormalizePlcSymbol(symbol);
            var dot = normalized.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < normalized.Length) return normalized.Substring(dot + 1);
            var underscore = normalized.LastIndexOf('_');
            if (underscore >= 0 && underscore + 1 < normalized.Length) return normalized.Substring(underscore + 1);
            return normalized;
        }

        private static HashSet<string> TokenizeSymbol(string symbol)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(symbol)) return result;
            var normalized = NormalizePlcSymbol(symbol);
            foreach (Match match in Regex.Matches(normalized, @"[\p{L}\p{Nd}]+"))
            {
                var token = match.Value.Trim();
                if (token.Length >= 2) result.Add(token);
            }

            foreach (Match match in Regex.Matches(normalized, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[\p{IsCJKUnifiedIdeographs}]+"))
            {
                var token = match.Value.Trim();
                if (token.Length >= 2) result.Add(token);
            }

            return result;
        }

        private static string NormalizePlcDataType(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return "";
            return dataType.Trim().Trim('"').Replace("&quot;", "").Replace("&QUOT;", "");
        }

        private static bool ArePlcDataTypesCompatible(string expected, string actual)
        {
            expected = NormalizePlcDataType(expected);
            actual = NormalizePlcDataType(actual);
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return true;
            if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return true;

            var realTypes = new HashSet<string>(new[] { "Real", "LReal" }, StringComparer.OrdinalIgnoreCase);
            var intTypes = new HashSet<string>(new[] { "SInt", "USInt", "Byte", "Int", "UInt", "Word", "DInt", "UDInt", "DWord", "LInt", "ULInt", "LWord" }, StringComparer.OrdinalIgnoreCase);
            if (realTypes.Contains(expected) && realTypes.Contains(actual)) return true;
            if (intTypes.Contains(expected) && intTypes.Contains(actual)) return true;
            return false;
        }

        private static IEnumerable<PlcMemberSymbol> ExtractPlcMembers(string rootName, string sectionName, XmlElement parent, int depth)
        {
            if (depth > 12) yield break;

            foreach (XmlElement member in parent.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "Member"))
            {
                var name = member.GetAttribute("Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var dataType = member.GetAttribute("Datatype");
                var current = string.IsNullOrWhiteSpace(rootName) ? name : rootName + "." + name;
                yield return new PlcMemberSymbol
                {
                    Symbol = current,
                    Name = name,
                    Section = sectionName,
                    DataType = dataType
                };

                foreach (var child in ExtractPlcMembers(current, sectionName, member, depth + 1))
                {
                    yield return child;
                }
            }
        }

        private static string GetFirstDescendantText(XmlElement root, string tagName)
        {
            foreach (XmlElement element in root.GetElementsByTagName(tagName).OfType<XmlElement>())
            {
                var value = element.InnerText?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim('"');
                }
            }

            return "";
        }

        private static JsonArray BuildHmiTemplateSyncPrecheck(JsonArray templates, SortedSet<string> plcSymbols, List<PlcSymbolCandidate>? plcSymbolCatalog = null)
        {
            var result = new JsonArray();
            var symbolByName = (plcSymbolCatalog ?? new List<PlcSymbolCandidate>())
                .GroupBy(x => NormalizePlcSymbol(x.Symbol), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var node in templates)
            {
                if (node is not JsonObject template) continue;
                var templateName = template["templateName"]?.ToString() ?? "";
                var requiredTags = template["requiredTags"] as JsonArray ?? new JsonArray();
                var requiredTagNames = requiredTags
                    .OfType<JsonObject>()
                    .Select(x => x["Name"]?.ToString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var bindings = new JsonArray();
                var missing = new JsonArray();
                var dbMemberRefs = new JsonArray();
                var dataTypeMismatches = new JsonArray();
                foreach (var tagNode in requiredTags)
                {
                    if (tagNode is not JsonObject tag) continue;
                    var hmiName = tag["Name"]?.ToString() ?? "";
                    var hmiDataType = NormalizePlcDataType(tag["DataType"]?.ToString() ?? "");
                    var plcTag = tag["PlcTag"]?.ToString() ?? "";
                    var normalizedPlcTag = NormalizePlcSymbol(plcTag);
                    var rootSymbol = ExtractPlcRootSymbol(plcTag);
                    var fullSymbolExists = !string.IsNullOrWhiteSpace(normalizedPlcTag) && plcSymbols.Contains(normalizedPlcTag);
                    var rootExists = !string.IsNullOrWhiteSpace(rootSymbol) && plcSymbols.Contains(rootSymbol);
                    var exists = fullSymbolExists || (!plcTag.Contains(".") && rootExists);
                    var isDbMember = plcTag.Contains(".");
                    symbolByName.TryGetValue(normalizedPlcTag, out var fullCandidate);
                    if (fullCandidate == null && !plcTag.Contains("."))
                    {
                        symbolByName.TryGetValue(rootSymbol, out fullCandidate);
                    }
                    var plcDataType = fullCandidate?.DataType ?? "";
                    var dataTypeVerified = fullCandidate != null && !string.IsNullOrWhiteSpace(plcDataType);
                    var dataTypeCompatible = !dataTypeVerified || ArePlcDataTypesCompatible(hmiDataType, plcDataType);
                    var item = new JsonObject
                    {
                        ["hmiTag"] = hmiName,
                        ["hmiDataType"] = hmiDataType,
                        ["plcTag"] = plcTag,
                        ["plcDataType"] = plcDataType,
                        ["normalizedPlcTag"] = normalizedPlcTag,
                        ["rootSymbol"] = rootSymbol,
                        ["fullSymbolFound"] = fullSymbolExists,
                        ["rootSymbolFound"] = rootExists,
                        ["verifiedByExportCatalog"] = fullSymbolExists,
                        ["dataTypeVerified"] = dataTypeVerified,
                        ["dataTypeCompatible"] = dataTypeCompatible,
                        ["requiresDbMemberReadback"] = isDbMember && !fullSymbolExists
                    };
                    bindings.Add(item);
                    if (!exists) missing.Add(item.DeepClone());
                    if (isDbMember && !fullSymbolExists) dbMemberRefs.Add(item.DeepClone());
                    if (exists && !dataTypeCompatible) dataTypeMismatches.Add(item.DeepClone());
                }

                var hmiUsageChecks = BuildHmiTemplateTagUsageChecks(template, requiredTagNames);
                var missingHmiTagDefinitions = new JsonArray(
                    hmiUsageChecks
                        .OfType<JsonObject>()
                        .Where(x => x["definedInRequiredTags"]?.GetValue<bool>() != true)
                        .Select(x => x.DeepClone())
                        .ToArray());
                var commandTagChecks = new JsonArray(
                    hmiUsageChecks
                        .OfType<JsonObject>()
                        .Where(x => string.Equals(x["usageKind"]?.ToString(), "action", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.DeepClone())
                        .ToArray());

                result.Add(new JsonObject
                {
                    ["templateName"] = templateName,
                    ["requiredTagCount"] = requiredTags.Count,
                    ["bindingChecks"] = bindings,
                    ["hmiUsageChecks"] = hmiUsageChecks,
                    ["commandTagChecks"] = commandTagChecks,
                    ["missingHmiTagDefinitions"] = missingHmiTagDefinitions,
                    ["missingRootSymbols"] = missing,
                    ["dataTypeMismatches"] = dataTypeMismatches,
                    ["dbMemberReferences"] = dbMemberRefs,
                    ["status"] = missing.Count == 0 && missingHmiTagDefinitions.Count == 0 && dataTypeMismatches.Count == 0 ? "plc-symbols-present" : "missing-plc-or-hmi-symbols",
                    ["note"] = dbMemberRefs.Count == 0
                        ? "PLC symbol precheck completed against tag-table exports and/or offline PLC export catalog. Final application still requires TIA readback."
                        : "DB/member references require additional block/DB member export readback before treating the binding as verified."
                });
            }
            return result;
        }

        private static JsonArray BuildHmiTemplateTagUsageChecks(JsonObject template, HashSet<string> requiredTagNames)
        {
            var result = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddUsage(string tagName, string usageKind, string itemName, string eventName, string propertyName)
            {
                if (string.IsNullOrWhiteSpace(tagName)) return;
                var key = string.Join("\u001f", tagName, usageKind, itemName, eventName, propertyName);
                if (!seen.Add(key)) return;
                result.Add(new JsonObject
                {
                    ["hmiTag"] = tagName,
                    ["usageKind"] = usageKind,
                    ["item"] = itemName,
                    ["event"] = eventName,
                    ["property"] = propertyName,
                    ["definedInRequiredTags"] = requiredTagNames.Contains(tagName)
                });
            }

            foreach (var dyn in template["dynamizations"] as JsonArray ?? new JsonArray())
            {
                if (dyn is not JsonObject dynObj) continue;
                AddUsage(
                    dynObj["tag"]?.ToString() ?? "",
                    "dynamization",
                    dynObj["item"]?.ToString() ?? "",
                    "",
                    dynObj["property"]?.ToString() ?? "");
            }

            foreach (var recipe in template["actionRecipeSummary"]?["effectiveRecipes"] as JsonArray ?? new JsonArray())
            {
                if (recipe is not JsonObject recipeObj) continue;
                foreach (var tagNode in recipeObj["targetTags"] as JsonArray ?? new JsonArray())
                {
                    AddUsage(
                        tagNode?.ToString() ?? "",
                        "action",
                        recipeObj["item"]?.ToString() ?? "",
                        recipeObj["event"]?.ToString() ?? "",
                        "");
                }
            }

            return result;
        }

        private static string ExtractPlcRootSymbol(string plcTag)
        {
            if (string.IsNullOrWhiteSpace(plcTag)) return "";
            var value = NormalizePlcSymbol(plcTag);
            var dot = value.IndexOf('.');
            if (dot > 0) value = value.Substring(0, dot);
            return value.Trim('"');
        }

        private static string NormalizePlcSymbol(string plcTag)
        {
            if (string.IsNullOrWhiteSpace(plcTag)) return "";
            return plcTag.Trim().Trim('"');
        }

        private static string BuildHmiTemplateSyncPrecheckMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# HMI Template Sync Precheck");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- Read-only PLC tag table export and offline template analysis.");
            md.AppendLine("- No HMI screen, tag, event, or connection is created or modified.");
            md.AppendLine("- Delivery package is not modified.");
            md.AppendLine();

            var tagExport = root["plcTagExport"] as JsonObject;
            md.AppendLine("## PLC Tags");
            md.AppendLine("- PLC software path: " + root["plcSoftwarePath"]);
            md.AppendLine("- PLC tag table regex: " + (string.IsNullOrWhiteSpace(root["plcTagTableRegex"]?.ToString()) ? "<none>" : root["plcTagTableRegex"]));
            md.AppendLine("- Max tag tables to export: " + root["maxPlcTagTablesToExport"]);
            md.AppendLine("- Export mode: " + root["tagExportMode"]);
            md.AppendLine("- Exported tag tables: " + ((tagExport?["exported"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- Export failures: " + ((tagExport?["failed"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- Symbol count: " + tagExport?["symbolCount"]);
            md.AppendLine();

            var mappingFile = root["mappingFile"] as JsonObject;
            md.AppendLine("## Mapping File");
            md.AppendLine("- Path: " + (string.IsNullOrWhiteSpace(root["hmiTemplateMappingPath"]?.ToString()) ? "<none>" : root["hmiTemplateMappingPath"]));
            md.AppendLine("- Exists: " + mappingFile?["exists"]);
            md.AppendLine("- Loaded: " + mappingFile?["loaded"]);
            md.AppendLine("- Effective mappings: " + ((mappingFile?["entries"] as JsonArray)?.Count ?? 0));
            if (!string.IsNullOrWhiteSpace(mappingFile?["error"]?.ToString()))
            {
                md.AppendLine("- Error: " + mappingFile?["error"]);
            }
            md.AppendLine();

            var exportCatalog = root["plcExportCatalog"] as JsonObject;
            md.AppendLine("## PLC Export Catalog");
            md.AppendLine("- Directory: " + (string.IsNullOrWhiteSpace(root["plcExportDirectory"]?.ToString()) ? "<none>" : root["plcExportDirectory"]));
            md.AppendLine("- Mode: " + exportCatalog?["mode"]);
            md.AppendLine("- Exists: " + exportCatalog?["exists"]);
            md.AppendLine("- Files scanned: " + exportCatalog?["filesScanned"]);
            md.AppendLine("- Blocks found: " + ((exportCatalog?["blocks"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- Export symbols: " + exportCatalog?["symbolCount"]);
            md.AppendLine();

            md.AppendLine("## Template Results");
            if (root["syncPrecheck"] is JsonArray prechecks)
            {
                foreach (var node in prechecks)
                {
                    var obj = node as JsonObject;
                    md.AppendLine("- " + obj?["templateName"] + ": " + obj?["status"] + ", requiredTags=" + obj?["requiredTagCount"] + ", usages=" + ((obj?["hmiUsageChecks"] as JsonArray)?.Count ?? 0) + ", commandTags=" + ((obj?["commandTagChecks"] as JsonArray)?.Count ?? 0) + ", missingHmiTags=" + ((obj?["missingHmiTagDefinitions"] as JsonArray)?.Count ?? 0) + ", missingPlc=" + ((obj?["missingRootSymbols"] as JsonArray)?.Count ?? 0) + ", typeMismatch=" + ((obj?["dataTypeMismatches"] as JsonArray)?.Count ?? 0) + ", dbRefs=" + ((obj?["dbMemberReferences"] as JsonArray)?.Count ?? 0));
                }
            }
            md.AppendLine();

            md.AppendLine("## Next Verification");
            md.AppendLine("- Missing PLC symbols must be created or mapped before applying templates.");
            md.AppendLine("- Every dynamization/action HMI tag must be declared in RequiredTags and mapped to a verified PLC tag or DB member.");
            md.AppendLine("- HMI tag data types must be compatible with the verified PLC symbol data types.");
            md.AppendLine("- DB/member references must be verified by exporting/readback of the related DB or block interface.");
            md.AppendLine("- Event scripts must be read back after actual HMI application in a temporary project.");
            return md.ToString();
        }

        private sealed class PlcMemberSymbol
        {
            public string Symbol { get; set; } = "";
            public string Name { get; set; } = "";
            public string Section { get; set; } = "";
            public string DataType { get; set; } = "";
            public string Source { get; set; } = "direct";
            public string OwnerType { get; set; } = "";
        }

        private sealed class PlcSymbolCandidate
        {
            public string Symbol { get; set; } = "";
            public string LeafName { get; set; } = "";
            public string DataType { get; set; } = "";
            public string Section { get; set; } = "";
            public string BlockName { get; set; } = "";
            public string BlockKind { get; set; } = "";
            public string File { get; set; } = "";
        }

        private sealed class PlcMappingCandidate
        {
            public string Symbol { get; set; } = "";
            public string DataType { get; set; } = "";
            public string Section { get; set; } = "";
            public string BlockName { get; set; } = "";
            public string BlockKind { get; set; } = "";
            public string File { get; set; } = "";
            public int Score { get; set; }
            public bool DataTypeMatch { get; set; }
            public bool VerifiedExact { get; set; }
            public string[] Reasons { get; set; } = Array.Empty<string>();
        }

    }
}
