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
    // Partial: cli probe/test commands. Extracted from Program.cs (god-file split); behavior unchanged.
    public partial class Program
    {
        private static void RunOnlineMonitoringSafetySelfTest()
        {
            var result = McpServer.RunOnlineMonitoringSafetySelfTest();
            var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            Console.WriteLine(json);
            LogDiag("Online monitoring safety self-test: " + (result.Ok == true ? "PASS" : "FAIL"));

            if (result.Ok != true)
            {
                Environment.ExitCode = 2;
            }
        }

        private static void RunFlowLightTest(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_FlowLight_Test_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;

            LogDiag($"FlowLight test: directory={projectDirectory}, project={projectName}");

            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_FlowLight_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDir);
            WriteFlowLightPlcXml(importDir);

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "CreateProject completed");

            var plc = McpServer.AddDeviceWithFallback("OrderNumber:6ES7 513-1AM03-0AB0/V3.0", "", "PLC_1", "S7-1500");
            LogDiag($"PLC add: ok={plc.Ok}, used={plc.MlfbUsed} {plc.VersionUsed}, error={plc.Error}");
            if (plc.Ok != true)
                throw new InvalidOperationException("PLC device add failed: " + plc.Error);

            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/20.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            LogDiag($"HMI add: ok={hmi.Ok}, used={hmi.MlfbUsed} {hmi.VersionUsed}, error={hmi.Error}");

            var import = McpServer.ImportPlcProgramFromDirectory("PLC_1", importDir, compileAfter: true, stopOnImportFailure: false);
            var failures = import.Failed == null ? Array.Empty<ImportFailure>() : new System.Collections.Generic.List<ImportFailure>(import.Failed).ToArray();
            LogDiag($"PLC import: importedTags={string.Join(",", import.ImportedTagTables ?? Array.Empty<string>())}, importedBlocks={string.Join(",", import.ImportedBlocks ?? Array.Empty<string>())}, failed={failures.Length}");
            if (import.Failed != null)
            {
                foreach (var failure in import.Failed)
                    LogDiag($"PLC import failure: {failure.Path} :: {failure.Error}");
            }
            if (import.Compile != null)
                LogDiag($"PLC compile: {import.Compile.State}, errors={import.Compile.ErrorCount}, warnings={import.Compile.WarningCount}");

            if (hmi.Ok == true)
            {
                try
                {
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", "Main", 1280, 720);
                    McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "FlowLightTags");
                    foreach (var tag in new[] { "Flow_Enable", "Light_1", "Light_2", "Light_3", "Light_4" })
                        McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "FlowLightTags", tag, "Bool", "PLC_1", tag, "");

                    McpServer.EnsureUnifiedHmiScreenItem("HMI_RT_1", "Main", "Title", "Text", 40, 30, 420, 50, "MCP 流水灯验证");
                    McpServer.EnsureUnifiedHmiScreenItem("HMI_RT_1", "Main", "Btn_Enable", "Button", 40, 110, 160, 60, "启动流水灯");
                    for (var i = 1; i <= 4; i++)
                        McpServer.EnsureUnifiedHmiScreenItem("HMI_RT_1", "Main", $"Lamp_{i}", "Rectangle", 240 + (i - 1) * 140, 110, 100, 100, $"灯{i}");

                    LogDiag("HMI screen/tag/item best-effort creation completed.");
                }
                catch (Exception ex)
                {
                    LogDiag("HMI best-effort failed: " + ex.Message);
                }
            }

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "SaveProject completed");
        }

        private static void RunFixCurrentFlowBinding(CliOptions options)
        {
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_FlowLight_Test_20260427_1605"
                : options.ProjectName!;

            LogDiag($"Fix current flow binding: project={projectName}");

            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_FixFlowBinding_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDir);
            WriteFlowLightPlcXml(importDir);

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var attach = McpServer.AttachToOpenProject(projectName);
            LogDiag(attach.Message ?? "Attach completed");

            var import = McpServer.ImportPlcProgramFromDirectory("PLC_1", importDir, compileAfter: true, stopOnImportFailure: false);
            LogDiag($"PLC import: dryRun={import.DryRun}, importedTags={string.Join(",", import.ImportedTagTables ?? Array.Empty<string>())}, importedBlocks={string.Join(",", import.ImportedBlocks ?? Array.Empty<string>())}, failed={import.Failed?.Count() ?? 0}");
            if (import.Failed != null)
            {
                foreach (var f in import.Failed)
                {
                    LogDiag($"PLC import failure: {f.Path} :: {f.Error}");
                }
            }
            if (import.Compile != null)
            {
                LogDiag($"PLC compile: state={import.Compile.State}, errors={import.Compile.ErrorCount}, warnings={import.Compile.WarningCount}");
            }

            McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "FlowLightTags");
            var hmiConnections = McpServer.ListObjectChildren("Software", "HMI_RT_1", "Connections", "", 20);
            var connectionName = (hmiConnections.Items ?? Array.Empty<string>()).FirstOrDefault() ?? "";
            LogDiag("HMI connections: " + string.Join(",", hmiConnections.Items ?? Array.Empty<string>()));
            var connDescription = McpServer.DescribeObjectProperty("Software", "HMI_RT_1", "Connections", "", 160);
            LogDiag("HMI Connections members: " + string.Join(" | ", (connDescription.Members ?? Array.Empty<ObjectMember>()).Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")));
            if (string.IsNullOrWhiteSpace(connectionName))
            {
                connectionName = "HMI_Connection_1";
                var createdConnection = McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1");
                LogDiag(createdConnection.Message ?? $"Ensured HMI connection {connectionName}");
                LogDiag("HMI connection members: " + string.Join(" | ", (createdConnection.Members ?? Array.Empty<ObjectMember>()).Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")));
            }

            EnsurePlcBackedHmiTag("Flow_Enable", "Bool", "%M0.0");
            EnsurePlcBackedHmiTag("Light_1", "Bool", "%Q0.0");
            EnsurePlcBackedHmiTag("Light_2", "Bool", "%Q0.1");
            EnsurePlcBackedHmiTag("Light_3", "Bool", "%Q0.2");
            EnsurePlcBackedHmiTag("Light_4", "Bool", "%Q0.3");
            EnsurePlcBackedHmiTag("Flow_Step", "Int", "%MW2");

            McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", "Main", "IO_Step", "ProcessValue", "Flow_Step", "Int", "Flow_Step", "");

            var hmiStep = McpServer.DescribeHmiTag("HMI_RT_1", "FlowLightTags", "Flow_Step", 120);
            LogDiag("HMI Flow_Step members: " + string.Join(" | ", (hmiStep.Members ?? Array.Empty<ObjectMember>()).Select(m => $"{m.Kind}:{m.Name}:{m.Type}")));
            LogHmiTagAttributes("Flow_Step");
            LogHmiTagAttributes("Flow_Enable");

            var plcCompile = McpServer.CompileAndDiagnosePlc("PLC_1");
            LogDiag($"Final PLC compile: state={plcCompile.State}, errors={plcCompile.ErrorCount}, warnings={plcCompile.WarningCount}");
            foreach (var e in plcCompile.Errors ?? Array.Empty<string>()) LogDiag("Final PLC compile error: " + e);
            foreach (var w in plcCompile.Warnings ?? Array.Empty<string>()) LogDiag("Final PLC compile warning: " + w);

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "SaveProject completed");

            void EnsurePlcBackedHmiTag(string tagName, string hmiDataType, string address)
            {
                var res = McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "FlowLightTags", tagName, hmiDataType, "PLC_1", tagName, connectionName, address);
                LogDiag(res.Message ?? $"Ensured HMI tag {tagName}");
                SetHmiTagAttribute(tagName, "Connection", connectionName);
                SetHmiTagAttribute(tagName, "DataType", hmiDataType);
                SetHmiTagAttribute(tagName, "AccessMode", "AbsoluteAccess");
                SetHmiTagAttribute(tagName, "Address", address);
            }

            void SetHmiTagAttribute(string tagName, string attr, string value)
            {
                try
                {
                    McpServer.InvokeObject("HmiTag", $"HMI_RT_1:FlowLightTags:{tagName}", "SetAttribute", new JsonArray(attr, value), "", true);
                }
                catch (Exception ex)
                {
                    LogDiag($"Set HMI tag attribute failed: {tagName}.{attr}={value}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            void LogHmiTagAttributes(string tagName)
            {
                string Attr(string attr)
                {
                    try
                    {
                        var value = McpServer.InvokeObject("HmiTag", $"HMI_RT_1:FlowLightTags:{tagName}", "GetAttribute", new JsonArray(attr));
                        return value.Value?.ToString() ?? "";
                    }
                    catch (Exception ex)
                    {
                        return "ERR:" + (ex.InnerException?.Message ?? ex.Message);
                    }
                }

                LogDiag($"HMI tag {tagName}: Connection={Attr("Connection")}, PlcName={Attr("PlcName")}, PlcTag={Attr("PlcTag")}, Address={Attr("Address")}, AccessMode={Attr("AccessMode")}, DataType={Attr("DataType")}");
            }
        }

        private static void RunProbeS71200Device(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Path.GetTempPath(), "TiaMcpServer_DeviceProbe")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "Probe_1211C_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;

            Directory.CreateDirectory(projectDirectory);
            LogDiag($"S7-1200 device probe: directory={projectDirectory}, project={projectName}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "CreateProject completed");

            var mlfb = "6ES7211-1BE40-0XB0";
            var versions = new[] { "V4.7", "4.7", "V4.6", "4.6", "V4.5", "4.5", "V4.4", "4.4", "" };
            foreach (var version in versions)
            {
                var res = McpServer.AddDeviceWithFallback(mlfb, version, "PLC_1211C_AC_DC_RLY", "S7-1200");
                LogDiag($"Probe {mlfb} {version}: ok={res.Ok}, used={res.MlfbUsed}, version={res.VersionUsed}, error={res.Error}");
                foreach (var attempt in res.Attempts ?? Array.Empty<string>())
                {
                    LogDiag("  attempt: " + attempt);
                }

                if (res.Ok == true) break;
            }

            var close = McpServer.CloseProject();
            LogDiag(close.Message ?? "Closed probe project");
        }

        private static void RunAdd1511CToCurrentProject(CliOptions options)
        {
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_FlowLight_Test_20260427_1605"
                : options.ProjectName!;

            LogDiag($"Add 1511C to current project: project={projectName}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var attach = McpServer.AttachToOpenProject(projectName);
            LogDiag(attach.Message ?? "Attach completed");

            var candidates = new[]
            {
                ("6ES7511-1CK01-0AB0", "V2.9"),
                ("6ES7511-1CK01-0AB0", "V2.8"),
                ("6ES7511-1CK01-0AB0", "V2.6"),
                ("6ES7511-1CK01-0AB0", ""),
                ("6ES7511-1CK00-0AB0", "V2.1"),
                ("6ES7511-1CK00-0AB0", "V2.0"),
                ("6ES7511-1CK00-0AB0", ""),
            };

            try
            {
                var existing = McpServer.GetDeviceInfo("PLC_1511C_1");
                if (existing != null && !string.IsNullOrWhiteSpace(existing.Name))
                {
                    LogDiag("Deleting existing PLC_1511C_1 before exact 1511C insertion.");
                    McpServer.InvokeObject("Device", "PLC_1511C_1", "Delete", new System.Text.Json.Nodes.JsonArray(), "", true);
                }
            }
            catch (Exception ex)
            {
                LogDiag("Existing PLC_1511C_1 delete skipped: " + (ex.InnerException?.Message ?? ex.Message));
            }

            ResponseMessage? success = null;
            string? successMlfb = null;
            string? successVersion = null;
            foreach (var (mlfb, version) in candidates)
            {
                try
                {
                    var res = McpServer.AddDevice(mlfb, version, "PLC_1511C_1");
                    LogDiag($"Add exact 1511C attempt {mlfb} {version}: {res.Message}");
                    success = res;
                    successMlfb = mlfb;
                    successVersion = version;
                    break;
                }
                catch (Exception ex)
                {
                    LogDiag($"Add exact 1511C attempt {mlfb} {version} failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (success == null)
            {
                throw new InvalidOperationException("Failed to add a 1511C device. See TiaMcpServer.log for attempted MLFB/version combinations.");
            }

            LogDiag($"Added exact 1511C: {successMlfb}/{successVersion}");

            var tree = McpServer.GetProjectTree();
            LogDiag("Project tree after adding 1511C:");
            LogDiag(tree.Tree ?? tree.Message ?? "");

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");
        }

        private static void RunSearchGsd(CliOptions options)
        {
            var keyword = options.SearchGsdKeyword ?? "";
            LogDiag($"Search installed GSD/catalog devices: keyword={keyword}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var res = McpServer.SearchInstalledGsdDevices(keyword, 20);
            LogDiag(res.Message ?? "");
            foreach (var c in res.Items ?? Array.Empty<GsdDeviceCandidate>())
            {
                LogDiag($"[{c.Source}] score={c.Score} typeId={c.TypeIdentifier} article={c.ArticleNumber} dap={c.DapId}/{c.DapName} desc={c.Description} path={c.GsdmlPath ?? c.CatalogPath}");
            }
        }

        private static void RunSearchHardwareCatalog(CliOptions options)
        {
            var keyword = options.SearchHardwareCatalogKeyword ?? "";
            LogDiag($"Search hardware catalog: keyword={keyword}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var res = McpServer.SearchHardwareCatalog(keyword, 50);
            LogDiag(res.Message ?? "");
            if (!string.IsNullOrWhiteSpace(res.Error))
                LogDiag("Search error: " + res.Error);

            foreach (var c in res.Items ?? Array.Empty<HardwareCatalogCandidate>())
            {
                LogDiag($"[{c.Source}] score={c.Score} insertable={c.Insertable} typeId={c.TypeIdentifier} normalized={c.TypeIdentifierNormalized} article={c.ArticleNumber} version={c.Version} type={c.TypeName} desc={c.Description} path={c.CatalogPath}");
            }
        }

        private static void RunProbeKtp700Basic(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_Probe_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;

            Directory.CreateDirectory(projectDirectory);
            LogDiag($"KTP700 Basic probe: directory={projectDirectory}, project={projectName}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var search = McpServer.SearchHardwareCatalog("KTP700 Basic PN", 20);
            LogDiag(search.Message ?? "");
            foreach (var c in search.Items ?? Array.Empty<HardwareCatalogCandidate>())
                LogDiag($"candidate score={c.Score} typeId={c.TypeIdentifier} article={c.ArticleNumber} version={c.Version} type={c.TypeName} path={c.CatalogPath}");

            var add = McpServer.AddHardwareCatalogDeviceWithProbe("KTP700 Basic PN", "HMI_KTP700_1", "6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            LogDiag($"KTP700 add: ok={add.Ok}, used={add.CandidateUsed?.TypeIdentifier}, error={add.Error}");
            foreach (var attempt in add.Attempts ?? Array.Empty<string>())
                LogDiag("  KTP700 attempt: " + attempt);

            var tree = McpServer.GetProjectTree();
            LogDiag("Project tree after KTP700 probe:");
            LogDiag(tree.Tree ?? tree.Message ?? "");

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");

            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: KTP700 Basic PN hardware catalog insertion");
            sb.AppendLine("SearchKeyword: KTP700 Basic PN");
            sb.AppendLine("PreferredText: 6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            sb.AppendLine("Ok: " + add.Ok);
            sb.AppendLine("CandidateUsed: " + add.CandidateUsed?.TypeIdentifier);
            sb.AppendLine("Error: " + add.Error);
            sb.AppendLine();
            sb.AppendLine("Attempts:");
            foreach (var attempt in add.Attempts ?? Array.Empty<string>())
                sb.AppendLine(attempt);
            sb.AppendLine();
            sb.AppendLine("Candidates:");
            foreach (var c in search.Items ?? Array.Empty<HardwareCatalogCandidate>())
                sb.AppendLine($"{c.TypeIdentifier} | {c.ArticleNumber} | {c.Version} | {c.TypeName} | {c.CatalogPath}");
            sb.AppendLine();
            sb.AppendLine("ProjectTree:");
            sb.AppendLine(tree.Tree ?? tree.Message ?? "");
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("KTP700 Basic probe report written: " + reportPath);
        }

        private static void RunProbeKtp700BasicHmiImport(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_HmiImport_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;
            var workspace = Directory.GetCurrentDirectory();
            var screenImportPath = Path.Combine(workspace, "TMP_EXPORT", "optimized_hmi", "Screens", "主画面_优化.xml");
            var probeDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Ktp700HmiImport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probeDir);
            var preparedScreenImportPath = PrepareClassicHmiScreenForKtp700(screenImportPath, probeDir);

            Directory.CreateDirectory(projectDirectory);
            LogDiag($"KTP700 Basic HMI import probe: directory={projectDirectory}, project={projectName}, screen={preparedScreenImportPath}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var add = McpServer.AddHardwareCatalogDeviceWithProbe("KTP700 Basic PN", "HMI_KTP700_1", "6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            LogDiag($"KTP700 add: ok={add.Ok}, used={add.CandidateUsed?.TypeIdentifier}, error={add.Error}");
            foreach (var attempt in add.Attempts ?? Array.Empty<string>())
                LogDiag("  KTP700 attempt: " + attempt);

            var infoBefore = McpServer.GetHmiProgramInfo("HMI_RT_1");
            LogDiag($"HMI info before import: name={infoBefore.Name}, type={infoBefore.ProgramType}, screens={string.Join(",", infoBefore.Screens ?? Array.Empty<string>())}");

            string importMessage;
            bool screenImportOk;
            bool projectUsable = true;
            try
            {
                var import = McpServer.ImportHmiScreen("HMI_RT_1", "", preparedScreenImportPath);
                importMessage = import.Message ?? "";
                screenImportOk = import.Meta?["success"]?.GetValue<bool>() == true;
                LogDiag("Screen import response: " + importMessage);
            }
            catch (Exception ex)
            {
                screenImportOk = false;
                importMessage = ex.InnerException?.Message ?? ex.Message;
                projectUsable = !importMessage.Contains("NonRecoverableException", StringComparison.OrdinalIgnoreCase)
                    && !importMessage.Contains("disposed", StringComparison.OrdinalIgnoreCase);
                LogDiag("Screen import exception: " + ex);
            }

            var infoAfter = projectUsable ? SafeHmiInfo("HMI_RT_1") : "Skipped because TIA project may be disposed after import failure.";
            var screens = projectUsable ? SafeStringList(() => McpServer.GetHmiScreens("HMI_RT_1")) : new[] { "Skipped because TIA project may be disposed after import failure." };
            var tagTables = projectUsable ? SafeStringList(() => McpServer.GetHmiTagTables("HMI_RT_1")) : new[] { "Skipped because TIA project may be disposed after import failure." };
            string treeText;
            if (projectUsable)
            {
                try
                {
                    var tree = McpServer.GetProjectTree();
                    treeText = tree.Tree ?? tree.Message ?? "";
                }
                catch (Exception ex)
                {
                    treeText = "ERR: " + (ex.InnerException?.Message ?? ex.Message);
                    projectUsable = false;
                }
            }
            else
            {
                treeText = "Skipped because TIA project may be disposed after import failure.";
            }

            if (projectUsable)
            {
                var save = McpServer.SaveProject();
                LogDiag(save.Message ?? "Project saved");
            }

            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: KTP700 Basic Classic/Basic HMI screen import");
            sb.AppendLine("HMI Device: OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0");
            sb.AppendLine("OriginalScreenImportPath: " + screenImportPath);
            sb.AppendLine("PreparedScreenImportPath: " + preparedScreenImportPath);
            sb.AppendLine("ScreenImportOk: " + screenImportOk);
            sb.AppendLine("ScreenImportMessage: " + importMessage);
            sb.AppendLine("ProjectUsableAfterImport: " + projectUsable);
            sb.AppendLine();
            sb.AppendLine("HMI Info Before:");
            sb.AppendLine($"Name={infoBefore.Name}; Type={infoBefore.ProgramType}; Screens={string.Join(",", infoBefore.Screens ?? Array.Empty<string>())}");
            sb.AppendLine();
            sb.AppendLine("HMI Info After:");
            sb.AppendLine(infoAfter);
            sb.AppendLine();
            sb.AppendLine("Screens:");
            foreach (var s in screens) sb.AppendLine(s);
            sb.AppendLine();
            sb.AppendLine("TagTables:");
            foreach (var t in tagTables) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("ProjectTree:");
            sb.AppendLine(treeText);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("KTP700 Basic HMI import probe report written: " + reportPath);

            static string PrepareClassicHmiScreenForKtp700(string sourcePath, string outputDir)
            {
                var dest = Path.Combine(outputDir, Path.GetFileName(sourcePath));
                var doc = new XmlDocument();
                doc.XmlResolver = null; // 安全：禁用外部实体/DTD 解析，防 XXE
                doc.PreserveWhitespace = true;
                doc.Load(sourcePath);

                var screen = doc.GetElementsByTagName("Hmi.Screen.Screen").OfType<XmlElement>().FirstOrDefault();
                var attrs = screen?["AttributeList"];
                SetChildText(attrs, "Width", "800");
                SetChildText(attrs, "Height", "480");
                doc.Save(dest);
                return dest;

                static void SetChildText(XmlElement? parent, string name, string value)
                {
                    if (parent == null) return;
                    var node = parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == name);
                    if (node != null) node.InnerText = value;
                }
            }

            static string SafeHmiInfo(string softwarePath)
            {
                try
                {
                    var info = McpServer.GetHmiProgramInfo(softwarePath);
                    return $"Name={info.Name}; Type={info.ProgramType}; Screens={string.Join(",", info.Screens ?? Array.Empty<string>())}";
                }
                catch (Exception ex)
                {
                    return "ERR: " + (ex.InnerException?.Message ?? ex.Message);
                }
            }

            static string[] SafeStringList(Func<ResponseStringList> fn)
            {
                try
                {
                    return fn().Items?.ToArray() ?? Array.Empty<string>();
                }
                catch (Exception ex)
                {
                    return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) };
                }
            }
        }

        private static void RunProbeKtp700BasicHmiTags(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_HmiTags_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;

            Directory.CreateDirectory(projectDirectory);
            LogDiag($"KTP700 Basic HMI tags probe: directory={projectDirectory}, project={projectName}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var add = McpServer.AddHardwareCatalogDeviceWithProbe("KTP700 Basic PN", "HMI_KTP700_1", "6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            LogDiag($"KTP700 add: ok={add.Ok}, used={add.CandidateUsed?.TypeIdentifier}, error={add.Error}");

            var info = McpServer.GetHmiProgramInfo("HMI_RT_1");
            LogDiag($"HMI info: name={info.Name}, type={info.ProgramType}, screens={string.Join(",", info.Screens ?? Array.Empty<string>())}");

            var apiHints = SafeClassicTagApiHints();
            string? importProbeResult = null;
            try
            {
                var sampleImportPath = Path.Combine(Path.GetTempPath(), "ClassicHmiTagTable_" + Guid.NewGuid().ToString("N") + ".xml");
                WriteClassicHmiTagTableProbeXml(sampleImportPath, "Motor_HMI_Tags");
                var importRes = McpServer.ImportHmiTagTable("HMI_RT_1", "", sampleImportPath);
                var ok = importRes.Meta?["success"]?.GetValue<bool>() == true;
                var err = importRes.Meta?["error"]?.ToString() ?? "";
                var exportInfo = "";
                if (ok)
                {
                    try
                    {
                        var roundtripPath = Path.Combine(projectDirectory, projectName + "_Motor_HMI_Tags_roundtrip.xml");
                        var exRes = McpServer.ExportHmiTagTable("HMI_RT_1", "Motor_HMI_Tags", roundtripPath);
                        exportInfo = $" :: roundtrip={roundtripPath} :: exportSuccess={exRes.Meta?["success"]?.GetValue<bool>() == true}";
                    }
                    catch (Exception ex)
                    {
                        exportInfo = " :: roundtripERR=" + (ex.InnerException?.Message ?? ex.Message);
                    }
                }
                importProbeResult = $"{(ok ? "OK" : "FAIL")} :: {importRes.Message}{(string.IsNullOrWhiteSpace(err) ? "" : " :: " + err)} :: file={sampleImportPath}{exportInfo}";
            }
            catch (Exception ex)
            {
                importProbeResult = "ERR :: " + (ex.InnerException?.Message ?? ex.Message);
            }

            var tagResults = new System.Collections.Generic.List<string>();
            TryEnsureTagTable("Motor_HMI_Tags");
            TryEnsureTag("Motor_Start", "Bool", "Motor.Start", "HMI_Connection_1", "%M0.0");
            TryEnsureTag("Motor_Stop", "Bool", "Motor.Stop", "HMI_Connection_1", "%M0.1");
            TryEnsureTag("Motor_Run", "Bool", "Motor.Run", "HMI_Connection_1", "%M0.2");
            TryEnsureTag("Motor_Fault", "Bool", "Motor.Fault", "HMI_Connection_1", "%M0.3");
            TryEnsureTag("Counter", "Int", "Counter", "HMI_Connection_1", "%MW2");

            var attributeResults = new System.Collections.Generic.List<string>();
            TryReadClassicTagAttributes("Motor_Start");
            TryReadClassicTagAttributes("Motor_Stop");
            TryReadClassicTagAttributes("Motor_Run");
            TryReadClassicTagAttributes("Motor_Fault");
            TryReadClassicTagAttributes("Counter");

            var tables = SafeStringList(() => McpServer.GetHmiTagTables("HMI_RT_1"));
            var tags = SafeStringList(() => McpServer.GetHmiTags("HMI_RT_1", "Motor_HMI_Tags"));
            var descTable = SafeDescribe(() => McpServer.DescribeHmiTagTable("HMI_RT_1", "Motor_HMI_Tags"));
            var descTag = SafeDescribe(() => McpServer.DescribeHmiTag("HMI_RT_1", "Motor_HMI_Tags", "Motor_Start"));
            var tree = McpServer.GetProjectTree();
            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");

            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: KTP700 Basic Classic/Basic HMI tag table/tag creation");
            sb.AppendLine("HMI Device: OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0");
            sb.AppendLine($"HMI Info: Name={info.Name}; Type={info.ProgramType}");
            sb.AppendLine();
            sb.AppendLine("Classic API Hints:");
            sb.AppendLine(apiHints);
            sb.AppendLine();
            sb.AppendLine("Import Probe:");
            sb.AppendLine(importProbeResult ?? "");
            sb.AppendLine();
            sb.AppendLine("Ensure Results:");
            foreach (var r in tagResults) sb.AppendLine(r);
            sb.AppendLine();
            sb.AppendLine("Attribute Results:");
            foreach (var r in attributeResults) sb.AppendLine(r);
            sb.AppendLine();
            sb.AppendLine("TagTables:");
            foreach (var t in tables) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("Tags in Motor_HMI_Tags:");
            foreach (var t in tags) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("Describe TagTable:");
            sb.AppendLine(descTable);
            sb.AppendLine();
            sb.AppendLine("Describe Motor_Start:");
            sb.AppendLine(descTag);
            sb.AppendLine();
            sb.AppendLine("ProjectTree:");
            sb.AppendLine(tree.Tree ?? tree.Message ?? "");
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("KTP700 Basic HMI tags probe report written: " + reportPath);

            void TryEnsureTagTable(string tableName)
            {
                try
                {
                    var res = McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", tableName);
                    var ok = res.Meta?["success"]?.GetValue<bool>() == true;
                    var err = res.Meta?["error"]?.ToString() ?? "";
                    tagResults.Add($"TagTable {tableName}: {(ok ? "OK" : "FAIL")} :: {res.Message}{(string.IsNullOrWhiteSpace(err) ? "" : " :: " + err)}");
                }
                catch (Exception ex)
                {
                    tagResults.Add($"TagTable {tableName}: ERR :: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            void TryEnsureTag(string tagName, string dataType, string plcTag, string connectionName, string address)
            {
                try
                {
                    var res = McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Motor_HMI_Tags", tagName, dataType, "PLC_1", plcTag, connectionName, address);
                    var ok = res.Meta?["success"]?.GetValue<bool>() == true;
                    var err = res.Meta?["error"]?.ToString() ?? "";
                    tagResults.Add($"Tag {tagName}: {(ok ? "OK" : "FAIL")} :: {res.Message}{(string.IsNullOrWhiteSpace(err) ? "" : " :: " + err)}");
                }
                catch (Exception ex)
                {
                    tagResults.Add($"Tag {tagName}: ERR :: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            void TryReadClassicTagAttributes(string tagName)
            {
                var path = $"HMI_RT_1:Motor_HMI_Tags:{tagName}";
                foreach (var attr in new[] { "DataType", "AccessMode", "PlcTag", "Connection", "ControllerDataType", "AcquisitionCycle", "MaxLength", "Address" })
                {
                    try
                    {
                        var read = McpServer.InvokeObject("HmiTag", path, "GetAttribute", new JsonArray(attr)).Value?.ToString() ?? "";
                        attributeResults.Add($"{tagName}.{attr}: {read}");
                    }
                    catch (Exception ex)
                    {
                        attributeResults.Add($"{tagName}.{attr}: ERR :: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }

            static string[] SafeStringList(Func<ResponseStringList> fn)
            {
                try { return fn().Items?.ToArray() ?? Array.Empty<string>(); }
                catch (Exception ex) { return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) }; }
            }

            static string SafeDescribe(Func<ResponseObjectDescribe> fn)
            {
                try
                {
                    var res = fn();
                    var members = res.Members == null ? "" : string.Join(Environment.NewLine, res.Members.Take(80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}"));
                    return $"{res.Message}\nKind={res.ObjectKind}; Path={res.ObjectPath}; Type={res.TypeName}\n{members}";
                }
                catch (Exception ex)
                {
                    return "ERR: " + (ex.InnerException?.Message ?? ex.Message);
                }
            }

            static string SafeClassicTagApiHints()
            {
                try
                {
                    var xmlPath = @"D:\app\TIA21\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.WinCC.xml";
                    if (!File.Exists(xmlPath)) return "WinCC XML doc not found";
                    var text = File.ReadAllText(xmlPath, Encoding.UTF8);
                    string Slice(string marker, int take = 10)
                    {
                        var idx = text.IndexOf(marker, StringComparison.Ordinal);
                        if (idx < 0) return marker + " :: not found";
                        var lines = text.Substring(idx).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Take(take);
                        return string.Join(Environment.NewLine, lines);
                    }
                    return Slice("<member name=\"T:Siemens.Engineering.Hmi.Tag.TagTableComposition\">", 18)
                        + Environment.NewLine + Environment.NewLine
                        + Slice("<member name=\"T:Siemens.Engineering.Hmi.Tag.TagComposition\">", 18);
                }
                catch (Exception ex)
                {
                    return "ERR :: " + ex.Message;
                }
            }

            static void WriteClassicHmiTagTableProbeXml(string path, string tableName)
            {
                File.WriteAllText(path, $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo>
    <Created>2000-01-01T00:00:00.0000000Z</Created>
    <ExportSetting>None</ExportSetting>
    <InstalledProducts />
  </DocumentInfo>
  <Hmi.Tag.TagTable ID=""0"">
    <AttributeList>
      <Name>{SecurityElement.Escape(tableName)}</Name>
    </AttributeList>
    <ObjectList>
{ClassicHmiTagXml("1", "Motor_Start", "Bool", "1")}
{ClassicHmiTagXml("2", "Motor_Stop", "Bool", "1")}
{ClassicHmiTagXml("3", "Motor_Run", "Bool", "1")}
{ClassicHmiTagXml("4", "Motor_Fault", "Bool", "1")}
{ClassicHmiTagXml("5", "Counter", "Int", "2")}
    </ObjectList>
  </Hmi.Tag.TagTable>
</Document>
", Encoding.UTF8);
            }

            static string ClassicHmiTagXml(string id, string name, string dataType, string length)
            {
                return $@"      <Hmi.Tag.Tag ID=""{id}"" CompositionName=""Tags"">
        <AttributeList>
          <Length>{SecurityElement.Escape(length)}</Length>
          <Name>{SecurityElement.Escape(name)}</Name>
        </AttributeList>
        <LinkList>
          <DataType TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(dataType)}</Name>
          </DataType>
          <HmiDataType TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(dataType)}</Name>
          </HmiDataType>
        </LinkList>
      </Hmi.Tag.Tag>";
            }
        }

        private static void RunProbeKtp700BasicHmiConnection(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_HmiConnection_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;
            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var reportLines = new System.Collections.Generic.List<string>();
            void Report(string line)
            {
                reportLines.Add(line);
                try { File.WriteAllLines(reportPath, reportLines, Encoding.UTF8); } catch { }
            }

            Directory.CreateDirectory(projectDirectory);
            LogDiag($"KTP700 Basic HMI connection probe: directory={projectDirectory}, project={projectName}");
            Report("Project: " + projectName);
            Report("Probe: KTP700 Basic Classic HMI connection discovery/import/export");
            Report("PLC: S7-1211C DC/DC/DC 6ES7211-1AE40-0XB0/V4.7");
            Report("HMI: KTP700 Basic PN 6AV2 123-2GB03-0AX0/17.0.0.0");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var addPlc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            LogDiag($"PLC add: ok={addPlc.Ok}, used={addPlc.MlfbUsed}/{addPlc.VersionUsed}, error={addPlc.Error}");
            foreach (var attempt in addPlc.Attempts ?? Array.Empty<string>())
                LogDiag("  PLC attempt: " + attempt);

            var addHmi = McpServer.AddHardwareCatalogDeviceWithProbe("KTP700 Basic PN", "HMI_KTP700_1", "6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            LogDiag($"KTP700 add: ok={addHmi.Ok}, used={addHmi.CandidateUsed?.TypeIdentifier}, error={addHmi.Error}");
            foreach (var attempt in addHmi.Attempts ?? Array.Empty<string>())
                LogDiag("  KTP700 attempt: " + attempt);

            var hmiInfo = McpServer.GetHmiProgramInfo("HMI_RT_1");
            LogDiag($"HMI info: name={hmiInfo.Name}, type={hmiInfo.ProgramType}, screens={string.Join(",", hmiInfo.Screens ?? Array.Empty<string>())}");
            Report($"HMI Info: Name={hmiInfo.Name}; Type={hmiInfo.ProgramType}");

            var projectTree = McpServer.GetProjectTree();
            var connectionsBefore = SafeStringList(() => McpServer.GetHmiConnections("HMI_RT_1"));
            var connectionPropertyDescribe = SafeDescribe(() => McpServer.DescribeObjectProperty("Software", "HMI_RT_1", "Connections", "", 120));
            var connectionChildren = SafeChildren(() => McpServer.ListObjectChildren("Software", "HMI_RT_1", "Connections", "", 50));
            Report("");
            Report("Connections Before:");
            foreach (var item in connectionsBefore) Report(item);
            Report("");
            Report("Describe Connections:");
            foreach (var line in connectionPropertyDescribe.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)) Report(line);
            Report("");
            Report("ListObjectChildren(Connections):");
            foreach (var item in connectionChildren) Report(item);

            string creationProbe;
            string roundtripPath = Path.Combine(projectDirectory, projectName + "_Connection_roundtrip.xml");
            try
            {
                creationProbe = McpServer.Portal.ProbeClassicHmiConnectionCreation("HMI_RT_1", "HMI_Connection_1", roundtripPath);
            }
            catch (Exception ex)
            {
                creationProbe = "ERR :: " + (ex.InnerException?.Message ?? ex.Message);
            }

            var connectionsAfter = SafeStringList(() => McpServer.GetHmiConnections("HMI_RT_1"));
            string saveMessage;
            try
            {
                var save = McpServer.SaveProject();
                saveMessage = save.Message ?? "Project saved";
                LogDiag(saveMessage);
            }
            catch (Exception ex)
            {
                saveMessage = "SAVE_ERR :: " + (ex.InnerException?.Message ?? ex.Message);
                LogDiag(saveMessage);
            }

            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: KTP700 Basic Classic HMI connection discovery/import/export");
            sb.AppendLine("PLC: S7-1211C DC/DC/DC 6ES7211-1AE40-0XB0/V4.7");
            sb.AppendLine("HMI: KTP700 Basic PN 6AV2 123-2GB03-0AX0/17.0.0.0");
            sb.AppendLine($"HMI Info: Name={hmiInfo.Name}; Type={hmiInfo.ProgramType}");
            sb.AppendLine();
            sb.AppendLine("Connections Before:");
            foreach (var item in connectionsBefore) sb.AppendLine(item);
            sb.AppendLine();
            sb.AppendLine("ListObjectChildren(Connections):");
            foreach (var item in connectionChildren) sb.AppendLine(item);
            sb.AppendLine();
            sb.AppendLine("Describe Connections:");
            sb.AppendLine(connectionPropertyDescribe);
            sb.AppendLine();
            sb.AppendLine("Creation Probe:");
            sb.AppendLine(creationProbe);
            sb.AppendLine();
            sb.AppendLine("Connections After:");
            foreach (var item in connectionsAfter) sb.AppendLine(item);
            sb.AppendLine();
            sb.AppendLine("Save:");
            sb.AppendLine(saveMessage);
            sb.AppendLine();
            sb.AppendLine("ProjectTree:");
            sb.AppendLine(projectTree.Tree ?? projectTree.Message ?? "");
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("KTP700 Basic HMI connection probe report written: " + reportPath);

            static string[] SafeStringList(Func<ResponseStringList> fn)
            {
                try { return fn().Items?.ToArray() ?? Array.Empty<string>(); }
                catch (Exception ex) { return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) }; }
            }

            static string[] SafeChildren(Func<ResponseObjectChildren> fn)
            {
                try { return fn().Items?.ToArray() ?? Array.Empty<string>(); }
                catch (Exception ex) { return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) }; }
            }

            static string SafeDescribe(Func<ResponseObjectDescribe> fn)
            {
                try
                {
                    var res = fn();
                    var members = res.Members == null ? "" : string.Join(Environment.NewLine, res.Members.Take(120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}"));
                    return $"{res.Message}\nKind={res.ObjectKind}; Path={res.ObjectPath}; Type={res.TypeName}\n{members}";
                }
                catch (Exception ex)
                {
                    return "ERR: " + (ex.InnerException?.Message ?? ex.Message);
                }
            }

        }

        private static void RunProbeKtp700BasicNetworking(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_Networking_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;
            Directory.CreateDirectory(projectDirectory);

            LogDiag($"KTP700 Basic networking probe: directory={projectDirectory}, project={projectName}");
            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var addPlc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            LogDiag($"PLC add: ok={addPlc.Ok}, used={addPlc.MlfbUsed}/{addPlc.VersionUsed}, error={addPlc.Error}");
            foreach (var attempt in addPlc.Attempts ?? Array.Empty<string>()) LogDiag("  PLC attempt: " + attempt);

            var addHmi = McpServer.AddHardwareCatalogDeviceWithProbe("KTP700 Basic PN", "HMI_KTP700_1", "6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            LogDiag($"KTP700 add: ok={addHmi.Ok}, used={addHmi.CandidateUsed?.TypeIdentifier}, error={addHmi.Error}");
            foreach (var attempt in addHmi.Attempts ?? Array.Empty<string>()) LogDiag("  KTP700 attempt: " + attempt);

            var beforeConnections = SafeStringList(() => McpServer.GetHmiConnections("HMI_RT_1"));
            var networkProbe = ProbeKtp700NetworkBestEffort();
            var hwConnectionProbe = McpServer.Portal.ProbeCreateHardwareHmiConnection("PLC_1", "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1", "HMI_Connection_1", options.CreateHardwareHmiConnection);
            var hmiNetworkExposureProbe = McpServer.Portal.ProbeDeviceNetworkExposure("HMI_KTP700_1");
            var hmiIeNetworkExposureProbe = McpServer.Portal.ProbeDeviceNetworkExposure("HMI_KTP700_1/HMI_KTP700_1.IE_CP_1");
            var afterConnections = SafeStringList(() => McpServer.GetHmiConnections("HMI_RT_1"));
            var projectTree = McpServer.GetProjectTree();

            string saveMessage;
            try
            {
                var save = McpServer.SaveProject();
                saveMessage = save.Message ?? "Project saved";
                LogDiag(saveMessage);
            }
            catch (Exception ex)
            {
                saveMessage = "SAVE_ERR :: " + (ex.InnerException?.Message ?? ex.Message);
                LogDiag(saveMessage);
            }

            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: KTP700 Basic PLC/HMI Profinet node and subnet connection");
            sb.AppendLine("PLC: S7-1211C DC/DC/DC 6ES7211-1AE40-0XB0/V4.7");
            sb.AppendLine("HMI: KTP700 Basic PN 6AV2 123-2GB03-0AX0/17.0.0.0");
            sb.AppendLine();
            sb.AppendLine("Connections Before:");
            foreach (var c in beforeConnections) sb.AppendLine(c);
            sb.AppendLine();
            sb.AppendLine("Network Probe:");
            sb.AppendLine(networkProbe);
            sb.AppendLine();
            sb.AppendLine("Hardware HMI Connection Probe:");
            sb.AppendLine(hwConnectionProbe);
            sb.AppendLine();
            sb.AppendLine("HMI Network Exposure Probe:");
            sb.AppendLine(hmiNetworkExposureProbe);
            sb.AppendLine();
            sb.AppendLine("HMI IE_CP Network Exposure Probe:");
            sb.AppendLine(hmiIeNetworkExposureProbe);
            sb.AppendLine();
            sb.AppendLine("Connections After:");
            foreach (var c in afterConnections) sb.AppendLine(c);
            sb.AppendLine();
            sb.AppendLine("Save:");
            sb.AppendLine(saveMessage);
            sb.AppendLine();
            sb.AppendLine("ProjectTree:");
            sb.AppendLine(projectTree.Tree ?? projectTree.Message ?? "");
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("KTP700 Basic networking probe report written: " + reportPath);

            static string[] SafeStringList(Func<ResponseStringList> fn)
            {
                try { return fn().Items?.ToArray() ?? Array.Empty<string>(); }
                catch (Exception ex) { return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) }; }
            }

            string ProbeKtp700NetworkBestEffort()
            {
                var attempts = new[]
                {
                    "HMI_KTP700_1",
                    "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1",
                    "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1/PROFINET Interface_1",
                    "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1/PROFINET Interface_1/Port_1"
                };

                var sb = new StringBuilder();
                foreach (var hmiRoot in attempts)
                {
                    sb.AppendLine("Attempt hmiRoot=" + hmiRoot);
                    var probe = McpServer.Portal.ProbeConnectDeviceNodesToSubnet("PLC_1", hmiRoot, "PN_IE_1");
                    sb.AppendLine(probe);
                    if (probe.IndexOf("HMI ConnectToSubnet: OK", StringComparison.OrdinalIgnoreCase) >= 0)
                        break;
                }

                return sb.ToString();
            }
        }

        private static void RunProbeCurrentKtp700HardwareHmiConnection(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_Networking_R8"
                : options.ProjectName!;
            Directory.CreateDirectory(projectDirectory);

            LogDiag($"Current KTP700 HW HMI connection probe: directory={projectDirectory}, project={projectName}");
            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            try
            {
                var attach = McpServer.AttachToOpenProject(projectName);
                LogDiag(attach.Message ?? "Attach completed");
            }
            catch (Exception ex)
            {
                LogDiag("Attach failed, trying to open project file: " + (ex.InnerException?.Message ?? ex.Message));
                var projectPath = Path.Combine(projectDirectory, projectName, projectName + ".ap21");
                var open = McpServer.OpenProject(projectPath);
                LogDiag(open.Message ?? "Open completed");
            }

            var hwConnectionProbe = McpServer.Portal.ProbeCreateHardwareHmiConnection(
                "PLC_1",
                "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1",
                "HMI_Connection_1",
                options.CreateHardwareHmiConnection,
                options.DeepHardwareHmiConnectionScan);
            var connectionsAfter = SafeStringList(() => McpServer.GetHmiConnections("HMI_RT_1"));

            var reportPath = Path.Combine(projectDirectory, projectName + "_CURRENT_HW_CONNECTION_PROBE.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: Attach current/open project and scan/create KTP700 Basic hardware HMI connection");
            sb.AppendLine("CreateHardwareHmiConnection: " + options.CreateHardwareHmiConnection);
            sb.AppendLine("DeepHardwareHmiConnectionScan: " + options.DeepHardwareHmiConnectionScan);
            sb.AppendLine();
            sb.AppendLine("Hardware HMI Connection Probe:");
            sb.AppendLine(hwConnectionProbe);
            sb.AppendLine();
            sb.AppendLine("Classic HMI Connections after probe:");
            foreach (var item in connectionsAfter) sb.AppendLine(item);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("Current KTP700 HW HMI connection probe report written: " + reportPath);

            static string[] SafeStringList(Func<ResponseStringList> fn)
            {
                try { return fn().Items?.ToArray() ?? Array.Empty<string>(); }
                catch (Exception ex) { return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) }; }
            }
        }

        private static void RunListPortalProcessProjects(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            Directory.CreateDirectory(projectDirectory);

            LogDiag("Listing TIA Portal processes/projects");
            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            var list = McpServer.ListPortalProcessProjects();

            var reportPath = Path.Combine(projectDirectory, "TIA_PORTAL_PROCESS_PROJECTS_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Probe: TIA Portal process/project listing");
            foreach (var item in list.Items ?? Array.Empty<string>()) sb.AppendLine(item);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("TIA Portal process/project report written: " + reportPath);
        }

        private static async Task RunCapabilitySelfTest(CliOptions options)
        {
            var response = await McpServer.RunCapabilitySelfTest(
                connectIfNeeded: options.CapabilitySelfTestConnect,
                includeProjectTree: options.CapabilitySelfTestProjectTree,
                inspectPortalProcesses: options.CapabilitySelfTestInspectProcesses);
            var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            Console.WriteLine(json);
        }

        private static async Task RunGenerateAcceptanceReport(CliOptions options)
        {
            var response = await McpServer.GenerateAcceptanceReport(
                outputDirectory: options.AcceptanceReportDirectory ?? string.Empty,
                connectIfNeeded: options.CapabilitySelfTestConnect,
                includeProjectTree: options.CapabilitySelfTestProjectTree,
                inspectPortalProcesses: options.CapabilitySelfTestInspectProcesses);
            var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            Console.WriteLine(json);
        }

        private static void RunGenerateErrorReport(CliOptions options)
        {
            var response = McpServer.GenerateErrorReport(
                errorCode: options.ErrorReportCode ?? "UnknownError",
                summary: options.ErrorReportSummary ?? "No summary provided.",
                detail: options.ErrorReportDetail ?? string.Empty,
                recommendedNextActions: options.ErrorReportActions ?? string.Empty,
                outputDirectory: options.ErrorReportDirectory ?? string.Empty);
            var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            Console.WriteLine(json);
        }

        private static void RunProbeHardwareHmiConnectionOwnerCandidates(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_Networking_R8"
                : options.ProjectName!;
            Directory.CreateDirectory(projectDirectory);

            LogDiag($"Hardware HMI connection owner candidate probe: directory={projectDirectory}, project={projectName}");
            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            var attach = McpServer.AttachToOpenProject(projectName);
            LogDiag(attach.Message ?? "Attach completed");

            var list = McpServer.ProbeHardwareHmiConnectionOwnerCandidates("PLC_1", "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1", options.DeepHardwareHmiConnectionScan);
            var reportPath = Path.Combine(projectDirectory, projectName + "_HW_CONNECTION_OWNER_CANDIDATES.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: Hardware HMI connection owner candidates");
            sb.AppendLine("DeepHardwareHmiConnectionScan: " + options.DeepHardwareHmiConnectionScan);
            sb.AppendLine();
            foreach (var item in list.Items ?? Array.Empty<string>()) sb.AppendLine(item);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("Hardware HMI connection owner candidate report written: " + reportPath);
        }

        private static void RunProbeHardwareHmiConnectionWhitelistedServices(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_Networking_R8"
                : options.ProjectName!;
            Directory.CreateDirectory(projectDirectory);

            LogDiag($"Hardware HMI connection whitelisted service probe: directory={projectDirectory}, project={projectName}");
            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            try
            {
                var attach = McpServer.AttachToOpenProject(projectName);
                LogDiag(attach.Message ?? "Attach completed");
            }
            catch (Exception ex)
            {
                LogDiag("Attach failed, trying to open project file: " + (ex.InnerException?.Message ?? ex.Message));
                var projectPath = Path.Combine(projectDirectory, projectName, projectName + ".ap21");
                var open = McpServer.OpenProject(projectPath);
                LogDiag(open.Message ?? "Open completed");
            }

            var list = McpServer.ProbeHardwareHmiConnectionWhitelistedServices("PLC_1", "HMI_KTP700_1/HMI_KTP700_1.IE_CP_1", options.DeepHardwareHmiConnectionScan);
            var reportPath = Path.Combine(projectDirectory, projectName + "_HW_CONNECTION_WHITELISTED_SERVICES.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: Hardware HMI connection whitelisted services");
            sb.AppendLine("DeepHardwareHmiConnectionScan: " + options.DeepHardwareHmiConnectionScan);
            sb.AppendLine();
            foreach (var item in list.Items ?? Array.Empty<string>()) sb.AppendLine(item);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("Hardware HMI connection whitelisted service report written: " + reportPath);
        }

        private static void RunProbeKtp700BasicHmiSymbolicTags(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_KTP700_Basic_HmiSymbolicTags_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
                : options.ProjectName!;
            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Ktp700SymbolicTags_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(importDir);
            WriteMotorMinimalPlcXml(importDir);

            LogDiag($"KTP700 Basic symbolic HMI tags probe: directory={projectDirectory}, project={projectName}, importDir={importDir}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var addPlc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            LogDiag($"PLC add: ok={addPlc.Ok}, used={addPlc.MlfbUsed}/{addPlc.VersionUsed}, error={addPlc.Error}");
            foreach (var attempt in addPlc.Attempts ?? Array.Empty<string>())
                LogDiag("  PLC attempt: " + attempt);

            var addHmi = McpServer.AddHardwareCatalogDeviceWithProbe("KTP700 Basic PN", "HMI_KTP700_1", "6AV2 123-2GB03-0AX0 17.0.0.0 PN");
            LogDiag($"KTP700 add: ok={addHmi.Ok}, used={addHmi.CandidateUsed?.TypeIdentifier}, error={addHmi.Error}");
            foreach (var attempt in addHmi.Attempts ?? Array.Empty<string>())
                LogDiag("  KTP700 attempt: " + attempt);

            var hmiInfo = McpServer.GetHmiProgramInfo("HMI_RT_1");
            LogDiag($"HMI info: name={hmiInfo.Name}, type={hmiInfo.ProgramType}, screens={string.Join(",", hmiInfo.Screens ?? Array.Empty<string>())}");

            var plcImport = McpServer.ImportPlcProgramFromDirectory("PLC_1", importDir, compileAfter: true, stopOnImportFailure: false);
            LogDiag($"PLC import: types={string.Join(",", plcImport.ImportedTypes ?? Array.Empty<string>())}, blocks={string.Join(",", plcImport.ImportedBlocks ?? Array.Empty<string>())}, failed={plcImport.Failed?.Count() ?? 0}");
            foreach (var failure in plcImport.Failed ?? Array.Empty<ImportFailure>())
                LogDiag($"PLC import failure: {failure.Path} :: {failure.Error}");
            if (plcImport.Compile != null)
                LogDiag($"PLC import compile: {plcImport.Compile.State}, errors={plcImport.Compile.ErrorCount}, warnings={plcImport.Compile.WarningCount}");

            var symbolicTagPath = Path.Combine(importDir, "ClassicHmiTagTable_Symbolic.xml");
            WriteClassicHmiSymbolicTagTableProbeXml(symbolicTagPath, "Motor_HMI_Tags", "HMI_Connection_1");

            string importMessage;
            string roundtripPath = Path.Combine(projectDirectory, projectName + "_Motor_HMI_Tags_roundtrip.xml");
            try
            {
                var importRes = McpServer.ImportHmiTagTable("HMI_RT_1", "", symbolicTagPath);
                var ok = importRes.Meta?["success"]?.GetValue<bool>() == true;
                var err = importRes.Meta?["error"]?.ToString() ?? "";
                importMessage = $"{(ok ? "OK" : "FAIL")} :: {importRes.Message}{(string.IsNullOrWhiteSpace(err) ? "" : " :: " + err)}";
                if (ok)
                {
                    var exportRes = McpServer.ExportHmiTagTable("HMI_RT_1", "Motor_HMI_Tags", roundtripPath);
                    importMessage += $" :: exportSuccess={exportRes.Meta?["success"]?.GetValue<bool>() == true} :: roundtrip={roundtripPath}";
                }
            }
            catch (Exception ex)
            {
                importMessage = "ERR :: " + (ex.InnerException?.Message ?? ex.Message);
            }

            var tables = SafeStringList(() => McpServer.GetHmiTagTables("HMI_RT_1"));
            var tags = SafeStringList(() => McpServer.GetHmiTags("HMI_RT_1", "Motor_HMI_Tags"));
            var connections = SafeStringList(() => McpServer.GetHmiConnections("HMI_RT_1"));
            string saveMessage;
            try
            {
                var save = McpServer.SaveProject();
                saveMessage = save.Message ?? "Project saved";
                LogDiag(saveMessage);
            }
            catch (Exception ex)
            {
                saveMessage = "SAVE_ERR :: " + (ex.InnerException?.Message ?? ex.Message);
                LogDiag(saveMessage);
            }

            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Probe: KTP700 Basic Classic HMI symbolic tag-table import");
            sb.AppendLine("PLC: S7-1211C DC/DC/DC 6ES7211-1AE40-0XB0/V4.7");
            sb.AppendLine("HMI: KTP700 Basic PN 6AV2 123-2GB03-0AX0/17.0.0.0");
            sb.AppendLine($"HMI Info: Name={hmiInfo.Name}; Type={hmiInfo.ProgramType}");
            sb.AppendLine();
            sb.AppendLine("Import XML:");
            sb.AppendLine(symbolicTagPath);
            sb.AppendLine();
            sb.AppendLine("Import Result:");
            sb.AppendLine(importMessage);
            sb.AppendLine();
            sb.AppendLine("Connections:");
            foreach (var c in connections) sb.AppendLine(c);
            sb.AppendLine();
            sb.AppendLine("TagTables:");
            foreach (var t in tables) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("Tags:");
            foreach (var t in tags) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("Save:");
            sb.AppendLine(saveMessage);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("KTP700 Basic symbolic HMI tags probe report written: " + reportPath);

            static string[] SafeStringList(Func<ResponseStringList> fn)
            {
                try { return fn().Items?.ToArray() ?? Array.Empty<string>(); }
                catch (Exception ex) { return new[] { "ERR: " + (ex.InnerException?.Message ?? ex.Message) }; }
            }
        }

        private static void RunValidatePlcSclSyntax(CliOptions options)
        {
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_FlowLight_Test_20260427_1605"
                : options.ProjectName!;
            var softwarePath = "PLC_1";
            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_SclSyntax_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDir);
            WritePlcSyntaxValidationXml(importDir);
            WritePlcSyntaxIecFbXml(importDir);
            var sclSourcePath = Path.Combine(importDir, "MCP_Syntax_Source.scl");
            WritePlcSyntaxValidationScl(sclSourcePath);

            LogDiag($"PLC SCL syntax validation: project={projectName}, software={softwarePath}, importDir={importDir}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");

            var attach = McpServer.AttachToOpenProject(projectName);
            LogDiag(attach.Message ?? "Attach completed");

            var import = McpServer.ImportPlcProgramFromDirectory(softwarePath, importDir, compileAfter: true, stopOnImportFailure: false);
            LogDiag($"PLC syntax import: blocks={string.Join(",", import.ImportedBlocks ?? Array.Empty<string>())}, failed={import.Failed?.Count() ?? 0}");
            foreach (var f in import.Failed ?? Array.Empty<ImportFailure>()) LogDiag($"PLC syntax import failure: {f.Path} :: {f.Error}");

            if (import.Compile != null)
            {
                LogDiag($"PLC syntax import compile: state={import.Compile.State}, errors={import.Compile.ErrorCount}, warnings={import.Compile.WarningCount}");
            }

            var sourceImported = false;
            try
            {
                var sourceImport = McpServer.ImportPlcExternalSource(softwarePath, "", sclSourcePath);
                sourceImported = sourceImport.Meta?["success"]?.GetValue<bool>() == true;
                LogDiag($"PLC SCL source import: {sourceImport.Message}; success={sourceImported}");
            }
            catch (Exception ex)
            {
                LogDiag("PLC SCL source import failed: " + ex.Message);
            }

            if (sourceImported)
            {
                var sourceGenerated = false;
                try
                {
                    var sourceGenerate = McpServer.GenerateBlocksFromExternalSource(softwarePath, "MCP_Syntax_Source");
                    sourceGenerated = sourceGenerate.Meta?["success"]?.GetValue<bool>() == true;
                    LogDiag($"PLC SCL source generate: {sourceGenerate.Message}; success={sourceGenerated}");
                }
                catch (Exception ex)
                {
                    LogDiag("PLC SCL source generate failed: " + ex.Message);
                }

                if (sourceGenerated)
                {
                    var sourceCompile = McpServer.CompileAndDiagnosePlc(softwarePath);
                    LogDiag($"PLC SCL source compile: state={sourceCompile.State}, errors={sourceCompile.ErrorCount}, warnings={sourceCompile.WarningCount}");
                    foreach (var e in sourceCompile.Errors ?? Array.Empty<string>()) LogDiag("PLC SCL source error: " + e);
                    foreach (var w in sourceCompile.Warnings ?? Array.Empty<string>()) LogDiag("PLC SCL source warning: " + w);
                }
            }

            var compile = McpServer.CompileAndDiagnosePlc(softwarePath);
            LogDiag($"PLC syntax validation compile: state={compile.State}, errors={compile.ErrorCount}, warnings={compile.WarningCount}");
            foreach (var e in compile.Errors ?? Array.Empty<string>()) LogDiag("PLC syntax validation error: " + e);
            foreach (var w in compile.Warnings ?? Array.Empty<string>()) LogDiag("PLC syntax validation warning: " + w);

            if ((compile.ErrorCount ?? 0) > 0 || (import.Failed?.Any() ?? false))
            {
                throw new InvalidOperationException($"PLC SCL syntax validation failed. ImportDir: {importDir}");
            }

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");
            LogDiag("PLC SCL syntax validation XML kept at: " + importDir);
        }

        private static void RunMotorMinimalTest(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_Cursor_Test_V21"
                : options.ProjectName!;

            Directory.CreateDirectory(projectDirectory);
            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_MotorMinimal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDir);
            WriteMotorMinimalPlcXml(importDir);

            LogDiag($"Motor minimal test: directory={projectDirectory}, project={projectName}, importDir={importDir}");
            string[] plcAttempts = Array.Empty<string>();
            string[] hmiAttempts = Array.Empty<string>();
            string hmiReadbackSummary = "";
            string hmiDesignJson = "";
            string hmiConnectionSummary = "";
            string networkProbeSummary = "";
            string hardwareDeviation = "Generated with a verified Unified HMI runtime device. KTP700 Basic hardware insertion is verified on this machine, but Classic/Basic HMI connection creation and safe PLC-variable binding are still not fully automated through the current MCP path, so the end-to-end demo remains on Unified.";
            string hmiDesignApplySummary = "";

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            var state = McpServer.GetState();
            LogDiag($"State before create: connected={state.IsConnected}, project={state.Project}, session={state.Session}");

            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");
            LogDiag("Project tree after create:");
            LogDiag(McpServer.GetProjectTree().Tree ?? "");

            var plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            LogDiag($"PLC add DC/DC/DC preferred: ok={plc.Ok}, used={plc.MlfbUsed}/{plc.VersionUsed}, error={plc.Error}");
            plcAttempts = plc.Attempts?.ToArray() ?? Array.Empty<string>();
            foreach (var attempt in plcAttempts) LogDiag("  PLC attempt: " + attempt);
            if (plc.Ok != true)
            {
                throw new InvalidOperationException("Failed to add S7-1211C DC/DC/DC. Last error: " + plc.Error);
            }

            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            LogDiag($"HMI add Unified fallback: ok={hmi.Ok}, used={hmi.MlfbUsed}/{hmi.VersionUsed}, error={hmi.Error}");
            hmiAttempts = hmi.Attempts?.ToArray() ?? Array.Empty<string>();
            foreach (var attempt in hmiAttempts) LogDiag("  HMI attempt: " + attempt);

            networkProbeSummary = ProbeMotorNetworkBestEffort();
            LogDiag("Motor minimal network probe:");
            LogDiag(networkProbeSummary);

            var treeAfterHardware = McpServer.GetProjectTree();
            LogDiag("Project tree after hardware:");
            LogDiag(treeAfterHardware.Tree ?? treeAfterHardware.Message ?? "");

            var import = McpServer.ImportPlcProgramFromDirectory("PLC_1", importDir, compileAfter: true, stopOnImportFailure: false);
            LogDiag($"PLC import: types={string.Join(",", import.ImportedTypes ?? Array.Empty<string>())}, tags={string.Join(",", import.ImportedTagTables ?? Array.Empty<string>())}, blocks={string.Join(",", import.ImportedBlocks ?? Array.Empty<string>())}, failed={import.Failed?.Count() ?? 0}");
            foreach (var failure in import.Failed ?? Array.Empty<ImportFailure>()) LogDiag($"PLC import failure: {failure.Path} :: {failure.Error}");
            if (import.Compile != null) LogDiag($"PLC import compile: {import.Compile.State}, errors={import.Compile.ErrorCount}, warnings={import.Compile.WarningCount}");

            var compile = McpServer.CompileAndDiagnosePlc("PLC_1");
            LogDiag($"Final PLC compile: state={compile.State}, errors={compile.ErrorCount}, warnings={compile.WarningCount}");
            foreach (var e in compile.Errors ?? Array.Empty<string>()) LogDiag("PLC compile error: " + e);
            foreach (var w in compile.Warnings ?? Array.Empty<string>()) LogDiag("PLC compile warning: " + w);
            if ((compile.ErrorCount ?? 0) > 0)
            {
                throw new InvalidOperationException("PLC compile failed. See log/importDir: " + importDir);
            }

            if (hmi.Ok == true)
            {
                ConfigureMotorHmiBestEffort();
            }
            else
            {
                throw new InvalidOperationException("Failed to add verified Unified HMI hardware. Last error: " + hmi.Error);
            }

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");
            TryWriteText(Path.Combine(importDir, "MotorUnifiedDesign.json"), hmiDesignJson);
            WriteMotorMinimalReport(projectDirectory, projectName, importDir, treeAfterHardware.Tree ?? "", compile, plcAttempts, hmiAttempts, hmiReadbackSummary, hmiDesignJson, hardwareDeviation, hmiConnectionSummary, networkProbeSummary);
            SyncMotorMinimalArtifacts(projectName, projectDirectory, importDir);
            LogDiag("Motor minimal test report written.");

            string ProbeMotorNetworkBestEffort()
            {
                var attempts = new[]
                {
                    ("PLC_1", "HMI_RT_1"),
                    ("PLC_1", "HMI_RT_1.IE_CP_1"),
                    ("PLC_1/PROFINET 接口_1", "HMI_RT_1.IE_CP_1"),
                    ("PLC_1", "HMI_RT_1/HMI_RT_1.IE_CP_1")
                };

                var sb = new StringBuilder();
                foreach (var attempt in attempts)
                {
                    sb.AppendLine($"Attempt plcRoot={attempt.Item1}, hmiRoot={attempt.Item2}");
                    try
                    {
                        var probe = McpServer.Portal.ProbeConnectDeviceNodesToSubnet(attempt.Item1, attempt.Item2, "PN_IE_1");
                        sb.AppendLine(probe);
                        if (probe.IndexOf("HMI ConnectToSubnet: OK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            probe.IndexOf("Selected HMI node:", StringComparison.OrdinalIgnoreCase) >= 0 && probe.IndexOf("<none>", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("Probe error: " + (ex.InnerException?.Message ?? ex.Message));
                    }
                }

                return sb.ToString();
            }

            void ConfigureMotorHmiBestEffort()
            {
                try
                {
                    var info = McpServer.GetHmiProgramInfo("HMI_RT_1");
                    LogDiag($"HMI info: name={info.Name}, type={info.ProgramType}, screens={string.Join(",", info.Screens ?? Array.Empty<string>())}");

                    var connectionName = "HMI_Connection_1";
                    var conn = McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1");
                    LogDiag(conn.Message ?? "HMI connection ensured");
                    hmiConnectionSummary = ReadHmiConnectionSummary(connectionName);
                    LogDiag("HMI connection readback: " + hmiConnectionSummary);

                    McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "Motor_HMI_Tags");
                    EnsureHmiTag("Motor_Start", "Bool", "DB1_MotorData.Motor.Start", "%M0.0");
                    EnsureHmiTag("Motor_Stop", "Bool", "DB1_MotorData.Motor.Stop", "%M0.1");
                    EnsureHmiTag("Motor_Run", "Bool", "DB1_MotorData.Motor.Run", "%M0.2");
                    EnsureHmiTag("Motor_Fault", "Bool", "DB1_MotorData.Motor.Fault", "%M0.3");
                    EnsureHmiTag("Counter", "Int", "DB1_MotorData.Counter", "%MW2");

                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", "Main", 800, 480);
                    var design = BuildMotorUnifiedHmiDesignJson();
                    hmiDesignJson = design;
                    var designApply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", "Main", design);
                    LogDiag(designApply.Message ?? "Applied motor HMI design");
                    if (designApply.Meta != null)
                    {
                        var changed = designApply.Meta["changed"]?.ToJsonString() ?? "";
                        var failed = designApply.Meta["failed"]?.ToJsonString() ?? "";
                        hmiDesignApplySummary = "Changed=" + changed + "; Failed=" + failed;
                        LogDiag("HMI design apply meta: " + hmiDesignApplySummary);
                    }

                    TryBindButton("Btn_Start", "Motor_Start");
                    TryBindButton("Btn_Stop", "Motor_Stop");
                    TryBindDyn("Lamp_Run", "Visible", "Motor_Run", "Bool", "DB1_MotorData.Motor.Run");
                    TryBindDyn("Lamp_Fault", "Visible", "Motor_Fault", "Bool", "DB1_MotorData.Motor.Fault");
                    TryBindDyn("IO_Counter", "ProcessValue", "Counter", "Int", "DB1_MotorData.Counter");

                    var screens = McpServer.GetHmiScreens("HMI_RT_1");
                    var tables = McpServer.GetHmiTagTables("HMI_RT_1");
                    var tags = McpServer.GetHmiTags("HMI_RT_1", "Motor_HMI_Tags");
                    LogDiag($"HMI readback: screens={string.Join(",", screens.Items ?? Array.Empty<string>())}; tables={string.Join(",", tables.Items ?? Array.Empty<string>())}; tags={string.Join(",", tags.Items ?? Array.Empty<string>())}");
                    hmiReadbackSummary = "Screens=" + string.Join(",", screens.Items ?? Array.Empty<string>())
                        + "; TagTables=" + string.Join(",", tables.Items ?? Array.Empty<string>())
                        + "; Tags=" + string.Join(",", tags.Items ?? Array.Empty<string>())
                        + "; Connection=" + hmiConnectionSummary
                        + "; Bindings=Btn_Start->Motor_Start(events), Btn_Stop->Motor_Stop(events), Lamp_Run.Visible->Motor_Run, Lamp_Fault.Visible->Motor_Fault, IO_Counter.ProcessValue->Counter"
                        + "; DesignApply=" + hmiDesignApplySummary;

                    void EnsureHmiTag(string tagName, string dataType, string plcTag, string absoluteAddress)
                    {
                        McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Motor_HMI_Tags", tagName, dataType, "PLC_1", plcTag, connectionName, absoluteAddress);
                        var tagSummary = ReadHmiTagSummary(tagName);
                        var symbolicOk = tagSummary.IndexOf("Connection=HMI_Connection_1", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            tagSummary.IndexOf("PlcTag=" + plcTag, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!symbolicOk)
                        {
                            LogDiag($"HMI tag {tagName} symbolic binding did not read back; applying verified absolute address {absoluteAddress}. Before={tagSummary}");
                            SetHmiTagAttribute(tagName, "Connection", connectionName);
                            SetHmiTagAttribute(tagName, "DataType", dataType);
                            SetHmiTagAttribute(tagName, "AccessMode", "AbsoluteAccess");
                            SetHmiTagAttribute(tagName, "Address", absoluteAddress);
                            tagSummary = ReadHmiTagSummary(tagName);
                        }

                        LogDiag($"HMI tag {tagName}: {tagSummary}");
                        if (tagSummary.IndexOf("Connection=HMI_Connection_1", StringComparison.OrdinalIgnoreCase) < 0 ||
                            tagSummary.IndexOf("Address=" + absoluteAddress, StringComparison.OrdinalIgnoreCase) < 0 && !symbolicOk)
                        {
                            throw new InvalidOperationException($"HMI tag '{tagName}' binding verification failed. {tagSummary}");
                        }
                    }

                    void SetHmiTagAttribute(string tagName, string attr, string value)
                    {
                        McpServer.InvokeObject("HmiTag", $"HMI_RT_1:Motor_HMI_Tags:{tagName}", "SetAttribute", new JsonArray(attr, value), "", true);
                    }

                    string ReadHmiTagSummary(string tagName)
                    {
                        string Attr(string attr)
                        {
                            try
                            {
                                return McpServer.InvokeObject("HmiTag", $"HMI_RT_1:Motor_HMI_Tags:{tagName}", "GetAttribute", new JsonArray(attr)).Value?.ToString() ?? "";
                            }
                            catch
                            {
                                return "";
                            }
                        }

                        var plcTagValue = Attr("PlcTag");
                        if (string.IsNullOrWhiteSpace(plcTagValue))
                            plcTagValue = Attr("ControllerTag");

                        return $"Connection={Attr("Connection")}; AccessMode={Attr("AccessMode")}; AddressAccessMode={Attr("AddressAccessMode")}; PlcName={Attr("PlcName")}; PlcTag={plcTagValue}; Address={Attr("Address")}; LogicalAddress={Attr("LogicalAddress")}; DataType={Attr("DataType")}";
                    }

                    string ReadHmiConnectionSummary(string ensuredConnectionName)
                    {
                        string P(string path)
                        {
                            try { return McpServer.GetObjectProperty("HmiConnection", $"HMI_RT_1:{ensuredConnectionName}", path).Value?.ToString() ?? ""; }
                            catch { return ""; }
                        }

                        string A(string attr)
                        {
                            try { return McpServer.InvokeObject("HmiConnection", $"HMI_RT_1:{ensuredConnectionName}", "GetAttribute", new JsonArray(attr)).Value?.ToString() ?? ""; }
                            catch { return ""; }
                        }

                        return $"Name={P("Name")}; CommunicationDriver={P("CommunicationDriver")}; Partner={P("Partner")}; Station={P("Station")}; Node={P("Node")}; DriverAttr={A("CommunicationDriver")}";
                    }

                    void TryBindButton(string buttonName, string tagName)
                    {
                        var eventOk = TryEnsureButtonMomentaryEvents(buttonName, tagName);
                        try
                        {
                            LogDiag(McpServer.BindUnifiedHmiButtonPressedTag("HMI_RT_1", "Main", buttonName, tagName).Message ?? $"Bound {buttonName}");
                        }
                        catch (Exception ex)
                        {
                            LogDiag($"Pressed-state fallback skipped for {buttonName}: {ex.InnerException?.Message ?? ex.Message}");
                        }

                        if (!eventOk)
                            throw new InvalidOperationException($"Button '{buttonName}' has no visible HMI event. Command buttons must expose an event script.");
                    }

                    bool TryEnsureButtonMomentaryEvents(string buttonName, string tagName)
                    {
                        var pairs = new[]
                        {
                            new[] { "Down", "Up" },
                            new[] { "Press", "Release" },
                            new[] { "Pressed", "Released" },
                            new[] { "MouseDown", "MouseUp" }
                        };

                        foreach (var pair in pairs)
                        {
                            if (TryApplyButtonAction(buttonName, pair[0], "set-bit", tagName) &&
                                TryApplyButtonAction(buttonName, pair[1], "reset-bit", tagName))
                            {
                                LogDiag($"Button event pair success: {buttonName}.{pair[0]}=set-bit, {buttonName}.{pair[1]}=reset-bit -> {tagName}");
                                return true;
                            }
                        }

                        if (TryApplyButtonAction(buttonName, "Tapped", "set-bit", tagName) ||
                            TryApplyButtonAction(buttonName, "Click", "set-bit", tagName))
                        {
                            LogDiag($"Button event fallback success: {buttonName}.Tapped/Click set-bit -> {tagName}");
                            return true;
                        }

                        LogDiag($"Button event unresolved for {buttonName}.");
                        return false;
                    }

                    bool TryApplyButtonAction(string buttonName, string eventType, string actionKind, string tagName)
                    {
                        try
                        {
                            var action = McpServer.EnsureUnifiedHmiButtonAction("HMI_RT_1", "Main", buttonName, eventType, actionKind, tagName);
                            var ok = action.Meta?["applyStatus"]?.ToString()?.Equals("applied", StringComparison.OrdinalIgnoreCase) == true;
                            LogDiag($"{buttonName}.{eventType} {actionKind}: {(ok ? "OK" : "FAIL")} :: {action.Message}");
                            return ok;
                        }
                        catch (Exception ex)
                        {
                            LogDiag($"{buttonName}.{eventType} {actionKind}: ERR :: {ex.InnerException?.Message ?? ex.Message}");
                            return false;
                        }
                    }

                    void TryBindDyn(string itemName, string propertyName, string tagName, string dataType, string plcTag)
                    {
                        LogDiag(McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", "Main", itemName, propertyName, tagName, dataType, plcTag, "").Message ?? $"Bound {itemName}.{propertyName}");
                    }

                    string BuildMotorUnifiedHmiDesignJson()
                    {
                        var root = new JsonObject
                        {
                            ["screen"] = new JsonObject
                            {
                                ["BackColor"] = "0xFFF4F6F8"
                            }
                        };

                        JsonObject Item(string type, string name, int left, int top, int width, int height, string? text = null, JsonObject? props = null, JsonObject? font = null, string? textProperty = null)
                        {
                            var obj = new JsonObject
                            {
                                ["type"] = type,
                                ["name"] = name,
                                ["left"] = left,
                                ["top"] = top,
                                ["width"] = width,
                                ["height"] = height
                            };
                            if (!string.IsNullOrWhiteSpace(text)) obj["text"] = text;
                            if (props != null) obj["properties"] = props;
                            if (font != null) obj["font"] = font;
                            if (!string.IsNullOrWhiteSpace(textProperty)) obj["textProperty"] = textProperty;
                            return obj;
                        }

                        var items = new JsonArray
                        {
                            Item("Rectangle", "HeaderBar", 0, 0, 800, 64, null, new JsonObject
                            {
                                ["BackColor"] = "0xFF111827"
                            }),
                            Item("Text", "Title", 24, 14, 340, 30, "Motor Control Demo", new JsonObject
                            {
                                ["ForeColor"] = "0xFFFFFFFF"
                            }, new JsonObject
                            {
                                ["Size"] = 24
                            }),
                            Item("Text", "Subtitle", 510, 20, 260, 20, "S7-1211C + Unified Runtime", new JsonObject
                            {
                                ["ForeColor"] = "0xFFD1D5DB"
                            }, new JsonObject
                            {
                                ["Size"] = 12
                            }),
                            Item("Rectangle", "CommandPanel", 24, 88, 360, 176, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFFFFFFF",
                                ["BorderColor"] = "0xFFD7DEE5",
                                ["BorderWidth"] = 1
                            }),
                            Item("Text", "CommandTitle", 44, 106, 200, 24, "Commands", new JsonObject
                            {
                                ["ForeColor"] = "0xFF111827"
                            }, new JsonObject
                            {
                                ["Size"] = 16
                            }),
                            Item("Text", "StartHint", 44, 136, 140, 18, "Press to start", new JsonObject
                            {
                                ["ForeColor"] = "0xFF64748B"
                            }, new JsonObject
                            {
                                ["Size"] = 11
                            }),
                            Item("Button", "Btn_Start", 44, 160, 144, 52, "START", new JsonObject
                            {
                                ["BackColor"] = "0xFF16A34A",
                                ["ForeColor"] = "0xFFFFFFFF",
                                ["BorderColor"] = "0xFF15803D",
                                ["BorderWidth"] = 1
                            }, new JsonObject
                            {
                                ["Size"] = 16
                            }),
                            Item("Text", "StopHint", 212, 136, 140, 18, "Pulse to stop", new JsonObject
                            {
                                ["ForeColor"] = "0xFF64748B"
                            }, new JsonObject
                            {
                                ["Size"] = 11
                            }),
                            Item("Button", "Btn_Stop", 212, 160, 144, 52, "STOP", new JsonObject
                            {
                                ["BackColor"] = "0xFFDC2626",
                                ["ForeColor"] = "0xFFFFFFFF",
                                ["BorderColor"] = "0xFFB91C1C",
                                ["BorderWidth"] = 1
                            }, new JsonObject
                            {
                                ["Size"] = 16
                            }),
                            Item("Rectangle", "StatusPanel", 416, 88, 360, 176, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFFFFFFF",
                                ["BorderColor"] = "0xFFD7DEE5",
                                ["BorderWidth"] = 1
                            }),
                            Item("Text", "StatusTitle", 436, 106, 200, 24, "Status", new JsonObject
                            {
                                ["ForeColor"] = "0xFF111827"
                            }, new JsonObject
                            {
                                ["Size"] = 16
                            }),
                            Item("Text", "RunLabel", 436, 136, 100, 18, "Motor run", new JsonObject
                            {
                                ["ForeColor"] = "0xFF64748B"
                            }, new JsonObject
                            {
                                ["Size"] = 11
                            }),
                            Item("Rectangle", "Lamp_Run", 436, 160, 132, 52, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFDCFCE7",
                                ["BorderColor"] = "0xFF86EFAC",
                                ["BorderWidth"] = 1
                            }),
                            Item("Text", "Lamp_Run_Text", 470, 176, 64, 20, "RUN", new JsonObject
                            {
                                ["ForeColor"] = "0xFF166534"
                            }, new JsonObject
                            {
                                ["Size"] = 14
                            }),
                            Item("Text", "FaultLabel", 604, 136, 100, 18, "Motor fault", new JsonObject
                            {
                                ["ForeColor"] = "0xFF64748B"
                            }, new JsonObject
                            {
                                ["Size"] = 11
                            }),
                            Item("Rectangle", "Lamp_Fault", 604, 160, 132, 52, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFFEE2E2",
                                ["BorderColor"] = "0xFFFCA5A5",
                                ["BorderWidth"] = 1
                            }),
                            Item("Text", "Lamp_Fault_Text", 626, 176, 88, 20, "FAULT", new JsonObject
                            {
                                ["ForeColor"] = "0xFFB91C1C"
                            }, new JsonObject
                            {
                                ["Size"] = 14
                            }),
                            Item("Rectangle", "CounterPanel", 24, 286, 752, 104, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFFFFFFF",
                                ["BorderColor"] = "0xFFD7DEE5",
                                ["BorderWidth"] = 1
                            }),
                            Item("Text", "CounterTitle", 44, 302, 160, 24, "Counter", new JsonObject
                            {
                                ["ForeColor"] = "0xFF111827"
                            }, new JsonObject
                            {
                                ["Size"] = 16
                            }),
                            Item("Text", "CounterLabel", 44, 342, 180, 22, "1-second pulse count", new JsonObject
                            {
                                ["ForeColor"] = "0xFF4B5563"
                            }, new JsonObject
                            {
                                ["Size"] = 14
                            }),
                            Item("IOField", "IO_Counter", 248, 332, 184, 42, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFFFFFFF",
                                ["ForeColor"] = "0xFF111827",
                                ["BorderColor"] = "0xFF94A3B8",
                                ["BorderWidth"] = 1
                            }, new JsonObject
                            {
                                ["Size"] = 16
                            }),
                            Item("Rectangle", "FooterPanel", 24, 410, 752, 42, null, new JsonObject
                            {
                                ["BackColor"] = "0xFFF8FAFC",
                                ["BorderColor"] = "0xFFE5E7EB",
                                ["BorderWidth"] = 1
                            }),
                            Item("Text", "FooterText", 40, 422, 720, 18, "Bindings: START, STOP, RUN, FAULT, COUNTER", new JsonObject
                            {
                                ["ForeColor"] = "0xFF6B7280"
                            }, new JsonObject
                            {
                                ["Size"] = 11
                            })
                        };

                        root["items"] = items;
                        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                    }

                }
                catch (Exception ex)
                {
                    LogDiag("HMI best-effort failed: " + ex);
                    hmiReadbackSummary = "HMI best-effort failed: " + ex.Message;
                }
            }

        }

    }
}
