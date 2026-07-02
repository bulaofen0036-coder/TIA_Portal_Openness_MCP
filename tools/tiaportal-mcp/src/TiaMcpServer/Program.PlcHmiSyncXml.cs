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
    // Partial: plc/hmi sync + xml writers. Extracted from Program.cs (god-file split); behavior unchanged.
    public partial class Program
    {
        private static void RunValidatePlcHmiSyncMinimal(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_PLC_HMI_Sync_Min_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;

            Directory.CreateDirectory(projectDirectory);
            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_PlcHmiSync_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDir);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "plc_hmi_sync_minimal.md");
            var jsonReportPath = Path.Combine(reportDir, "plc_hmi_sync_minimal.json");

            var expected = new[]
            {
                new SyncTag("Sync_Start", "Bool", "%M10.0", "ButtonPressed", "Btn_Start", "PressedStateTags"),
                new SyncTag("Sync_Run", "Bool", "%M10.1", "LampVisible", "Lamp_Run", "Visible"),
                new SyncTag("Sync_Count", "Int", "%MW20", "IOFieldProcessValue", "IO_Count", "ProcessValue")
            };

            WritePlcHmiSyncMinimalPlcXml(importDir, expected);
            LogDiag($"PLC/HMI sync minimal validation: directory={projectDirectory}, project={projectName}, importDir={importDir}");

            McpServer.Connect();
            McpServer.CreateProject(projectDirectory, projectName);
            var plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            if (plc.Ok != true) throw new InvalidOperationException("Failed to add PLC for PLC/HMI sync validation: " + plc.Error);
            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            if (hmi.Ok != true) throw new InvalidOperationException("Failed to add Unified HMI for PLC/HMI sync validation: " + hmi.Error);

            var import = McpServer.ImportPlcProgramFromDirectory("PLC_1", importDir, compileAfter: true, stopOnImportFailure: true);
            var compile = McpServer.CompileAndDiagnosePlc("PLC_1");
            if ((compile.ErrorCount ?? 0) > 0 || (import.Failed?.Any() ?? false))
            {
                WritePlcHmiSyncReport(reportPath, jsonReportPath, projectName, projectDirectory, importDir, false, "", expected, import, compile, new List<Dictionary<string, object?>>(), "PLC import or compile failed.");
                throw new InvalidOperationException("PLC/HMI sync minimal validation failed during PLC compile. Report: " + reportPath);
            }

            var exportedTagTable = Path.Combine(reportDir, "Sync_Minimal_Tags_export.xml");
            var exportOk = false;
            try
            {
                McpServer.ExportPlcTagTable("PLC_1", "Sync_Minimal_Tags", exportedTagTable);
                exportOk = File.Exists(exportedTagTable);
            }
            catch (Exception ex)
            {
                LogDiag("PLC sync tag table export failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
            var plcReadback = exportOk ? ReadPlcTagTableExport(exportedTagTable) : new Dictionary<string, (string DataType, string Address)>(StringComparer.OrdinalIgnoreCase);
            var plcTables = McpServer.GetPlcTagTables("PLC_1").Items?.ToArray() ?? Array.Empty<string>();
            var plcTableOk = plcTables.Any(t => string.Equals(t, "Sync_Minimal_Tags", StringComparison.OrdinalIgnoreCase));

            var connectionName = "HMI_Connection_1";
            var connectionReadback = McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1").Message ?? "";
            McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "Sync_HMI_Tags");
            foreach (var tag in expected)
            {
                McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Sync_HMI_Tags", tag.Name, tag.DataType, "PLC_1", tag.Name, connectionName, tag.Address);
                SetHmiTagAbsolute("Sync_HMI_Tags", tag.Name, tag.DataType, connectionName, tag.Address);
            }

            McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", "Sync_Main", 800, 480);
            McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", "Sync_Main", BuildPlcHmiSyncMinimalDesignJson());
            McpServer.BindUnifiedHmiButtonPressedTag("HMI_RT_1", "Sync_Main", "Btn_Start", "Sync_Start");
            McpServer.EnsureUnifiedHmiButtonEventHandler("HMI_RT_1", "Sync_Main", "Btn_Start", "Tapped");
            McpServer.SetUnifiedHmiButtonEventScriptCode("HMI_RT_1", "Sync_Main", "Btn_Start", "Tapped", "HMIRuntime.Tags.SysFct.SetBitInTag(\"Sync_Start\", 0);", "", false);
            McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", "Sync_Main", "Lamp_Run", "Visible", "Sync_Run", "Bool", "Sync_Run", "");
            McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", "Sync_Main", "IO_Count", "ProcessValue", "Sync_Count", "Int", "Sync_Count", "");

            var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
            var hmiTables = McpServer.GetHmiTagTables("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
            var hmiTags = McpServer.GetHmiTags("HMI_RT_1", "Sync_HMI_Tags").Items?.ToArray() ?? Array.Empty<string>();
            var rows = new List<Dictionary<string, object?>>();
            foreach (var tag in expected)
            {
                var plcHas = plcReadback.TryGetValue(tag.Name, out var plcTag);
                var hmiSummary = ReadHmiTagSummary("Sync_HMI_Tags", tag.Name);
                var hmiAddress = ExtractSummaryField(hmiSummary, "Address");
                if (string.IsNullOrWhiteSpace(hmiAddress))
                    hmiAddress = ExtractSummaryField(hmiSummary, "LogicalAddress");
                var hmiDataType = ExtractSummaryField(hmiSummary, "DataType");
                var hmiConnection = ExtractSummaryField(hmiSummary, "Connection");
                var row = new Dictionary<string, object?>
                {
                    ["name"] = tag.Name,
                    ["expectedDataType"] = tag.DataType,
                    ["expectedAddress"] = tag.Address,
                    ["plcTableReadback"] = plcHas,
                    ["plcDataType"] = plcHas ? plcTag.DataType : "",
                    ["plcAddress"] = plcHas ? plcTag.Address : "",
                    ["hmiTagReadback"] = hmiTags.Any(t => string.Equals(t, tag.Name, StringComparison.OrdinalIgnoreCase)),
                    ["hmiConnection"] = hmiConnection,
                    ["hmiDataType"] = hmiDataType,
                    ["hmiAddress"] = hmiAddress,
                    ["addressSynced"] = plcHas && string.Equals(plcTag.Address, tag.Address, StringComparison.OrdinalIgnoreCase) && string.Equals(hmiAddress, tag.Address, StringComparison.OrdinalIgnoreCase),
                    ["dataTypeSynced"] = plcHas && string.Equals(plcTag.DataType, tag.DataType, StringComparison.OrdinalIgnoreCase) && string.Equals(hmiDataType, tag.DataType, StringComparison.OrdinalIgnoreCase),
                    ["control"] = tag.ControlName,
                    ["controlBinding"] = tag.BindingProperty,
                    ["hmiTagSummary"] = hmiSummary
                };
                rows.Add(row);
            }

            var btnDesc = McpServer.DescribeHmiScreenItem("HMI_RT_1", "Sync_Main", "Btn_Start", 120);
            var lampDesc = McpServer.DescribeHmiScreenItem("HMI_RT_1", "Sync_Main", "Lamp_Run", 120);
            var ioDesc = McpServer.DescribeHmiScreenItem("HMI_RT_1", "Sync_Main", "IO_Count", 120);
            var eventDesc = McpServer.DescribeUnifiedHmiButtonEventScript("HMI_RT_1", "Sync_Main", "Btn_Start", "Tapped", 120);
            var controlsOk = hmiTags.Length >= expected.Length;
            var rowsOk = rows.All(r => Equals(r["addressSynced"], true) && Equals(r["dataTypeSynced"], true) && Equals(r["hmiTagReadback"], true));
            var passed = exportOk && rowsOk && controlsOk;

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");
            WritePlcHmiSyncReport(reportPath, jsonReportPath, projectName, projectDirectory, importDir, passed, connectionReadback, expected, import, compile, rows, passed ? "" : "Readback mismatch in PLC tags, HMI tags, or screen controls.");
            if (!passed)
            {
                throw new InvalidOperationException("PLC/HMI sync minimal validation failed. Report: " + reportPath);
            }
        }

        private sealed class SyncTag
        {
            public SyncTag(string name, string dataType, string address, string role, string controlName, string bindingProperty)
            {
                Name = name;
                DataType = dataType;
                Address = address;
                Role = role;
                ControlName = controlName;
                BindingProperty = bindingProperty;
            }

            public string Name { get; }
            public string DataType { get; }
            public string Address { get; }
            public string Role { get; }
            public string ControlName { get; }
            public string BindingProperty { get; }
        }

        private static void WritePlcHmiSyncMinimalPlcXml(string dir, IEnumerable<SyncTag> tags)
        {
            var objectList = new StringBuilder();
            var id = 1;
            foreach (var tag in tags)
            {
                objectList.AppendLine($@"      <SW.Tags.PlcTag ID=""{id++}"" CompositionName=""Tags""><AttributeList><DataTypeName>{SecurityElement.Escape(tag.DataType)}</DataTypeName><LogicalAddress>{SecurityElement.Escape(tag.Address)}</LogicalAddress><Name>{SecurityElement.Escape(tag.Name)}</Name></AttributeList></SW.Tags.PlcTag>");
            }

            File.WriteAllText(Path.Combine(dir, "Sync_Minimal_Tags.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Tags.PlcTagTable ID=""0"">
    <AttributeList><Name>Sync_Minimal_Tags</Name></AttributeList>
    <ObjectList>
{objectList}    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>", Encoding.UTF8);
        }

        private static Dictionary<string, (string DataType, string Address)> ReadPlcTagTableExport(string exportPath)
        {
            var result = new Dictionary<string, (string DataType, string Address)>(StringComparer.OrdinalIgnoreCase);
            var doc = new XmlDocument();
            doc.XmlResolver = null; // 安全：禁用外部实体/DTD 解析，防 XXE
            doc.Load(exportPath);
            foreach (XmlElement node in doc.GetElementsByTagName("SW.Tags.PlcTag"))
            {
                var name = node.SelectSingleNode(".//*[local-name()='Name']")?.InnerText ?? "";
                var type = node.SelectSingleNode(".//*[local-name()='DataTypeName']")?.InnerText ?? "";
                var address = node.SelectSingleNode(".//*[local-name()='LogicalAddress']")?.InnerText ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[name] = (type, address);
                }
            }

            return result;
        }

        private static void SetHmiTagAbsolute(string tableName, string tagName, string dataType, string connection, string address)
        {
            McpServer.InvokeObject("HmiTag", $"HMI_RT_1:{tableName}:{tagName}", "SetAttribute", new JsonArray("Connection", connection), "", true);
            McpServer.InvokeObject("HmiTag", $"HMI_RT_1:{tableName}:{tagName}", "SetAttribute", new JsonArray("DataType", dataType), "", true);
            McpServer.InvokeObject("HmiTag", $"HMI_RT_1:{tableName}:{tagName}", "SetAttribute", new JsonArray("AccessMode", "AbsoluteAccess"), "", true);
            McpServer.InvokeObject("HmiTag", $"HMI_RT_1:{tableName}:{tagName}", "SetAttribute", new JsonArray("Address", address), "", true);
        }

        private static string ReadHmiTagSummary(string tableName, string tagName)
        {
            string Attr(string attr)
            {
                try
                {
                    return McpServer.InvokeObject("HmiTag", $"HMI_RT_1:{tableName}:{tagName}", "GetAttribute", new JsonArray(attr)).Value?.ToString() ?? "";
                }
                catch
                {
                    return "";
                }
            }

            var plcTagValue = Attr("PlcTag");
            if (string.IsNullOrWhiteSpace(plcTagValue)) plcTagValue = Attr("ControllerTag");
            return $"Connection={Attr("Connection")}; AccessMode={Attr("AccessMode")}; AddressAccessMode={Attr("AddressAccessMode")}; PlcName={Attr("PlcName")}; PlcTag={plcTagValue}; Address={Attr("Address")}; LogicalAddress={Attr("LogicalAddress")}; DataType={Attr("DataType")}";
        }

        private static string ExtractSummaryField(string summary, string field)
        {
            foreach (var part in summary.Split(';'))
            {
                var trimmed = part.Trim();
                var prefix = field + "=";
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(prefix.Length);
                }
            }

            return "";
        }

        private static string BuildPlcHmiSyncMinimalDesignJson()
        {
            var root = new JsonObject
            {
                ["screen"] = new JsonObject { ["BackColor"] = "0xFFF6F7F9" },
                ["items"] = new JsonArray
                {
                    new JsonObject { ["type"] = "Rectangle", ["name"] = "Header", ["left"] = 0, ["top"] = 0, ["width"] = 800, ["height"] = 62, ["properties"] = new JsonObject { ["BackColor"] = "0xFF172033" } },
                    new JsonObject { ["type"] = "Text", ["name"] = "Title", ["left"] = 24, ["top"] = 16, ["width"] = 360, ["height"] = 28, ["text"] = "PLC HMI Sync Minimal", ["properties"] = new JsonObject { ["ForeColor"] = "0xFFFFFFFF" }, ["font"] = new JsonObject { ["Size"] = 22 } },
                    new JsonObject { ["type"] = "Button", ["name"] = "Btn_Start", ["left"] = 48, ["top"] = 110, ["width"] = 180, ["height"] = 64, ["text"] = "Start" },
                    new JsonObject { ["type"] = "Rectangle", ["name"] = "Lamp_Run", ["left"] = 288, ["top"] = 110, ["width"] = 80, ["height"] = 64, ["text"] = "Run", ["properties"] = new JsonObject { ["BackColor"] = "0xFF22C55E" } },
                    new JsonObject { ["type"] = "Text", ["name"] = "CountLabel", ["left"] = 48, ["top"] = 230, ["width"] = 180, ["height"] = 28, ["text"] = "Count" },
                    new JsonObject { ["type"] = "IOField", ["name"] = "IO_Count", ["left"] = 288, ["top"] = 220, ["width"] = 180, ["height"] = 48 }
                }
            };
            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        private static void WritePlcHmiSyncReport(string mdPath, string jsonPath, string projectName, string projectDirectory, string importDir, bool passed, string connectionReadback, SyncTag[] expected, ResponsePlcProgramImport import, ResponseCompileDiagnose compile, List<Dictionary<string, object?>> rows, string error)
        {
            var md = new StringBuilder();
            md.AppendLine("# PLC/HMI Sync Minimal Validation");
            md.AppendLine();
            md.AppendLine("- Project: `" + projectName + "`");
            md.AppendLine("- ProjectDirectory: `" + projectDirectory + "`");
            md.AppendLine("- ImportDir: `" + importDir + "`");
            md.AppendLine("- Result: `" + (passed ? "PASS" : "FAIL") + "`");
            md.AppendLine("- PLC Compile: `errors=" + compile.ErrorCount + ", warnings=" + compile.WarningCount + ", state=" + compile.State + "`");
            if (!string.IsNullOrWhiteSpace(connectionReadback)) md.AppendLine("- HMI Connection: `" + connectionReadback.Replace("`", "'") + "`");
            if (!string.IsNullOrWhiteSpace(error)) md.AppendLine("- Error: `" + error.Replace("`", "'") + "`");
            md.AppendLine();
            md.AppendLine("| PLC variable | Type | PLC address | HMI address | HMI connection | Control binding | Synced |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                md.AppendLine("| " + r["name"] + " | " + r["expectedDataType"] + " | " + r["plcAddress"] + " | " + r["hmiAddress"] + " | " + r["hmiConnection"] + " | " + r["control"] + "." + r["controlBinding"] + " | " + (Equals(r["addressSynced"], true) && Equals(r["dataTypeSynced"], true)) + " |");
            }
            if (rows.Count == 0)
            {
                foreach (var tag in expected)
                {
                    md.AppendLine("| " + tag.Name + " | " + tag.DataType + " | " + tag.Address + " |  |  | " + tag.ControlName + "." + tag.BindingProperty + " | False |");
                }
            }
            File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                projectName,
                projectDirectory,
                importDir,
                passed,
                error,
                connectionReadback,
                compile = new { compile.State, compile.ErrorCount, compile.WarningCount, compile.Errors, compile.Warnings },
                import = new { import.ImportedTagTables, import.ImportedBlocks, import.ImportedTypes, import.Failed },
                expected,
                results = rows
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }

        private static void RunValidatePlcChineseCommentsMinimal(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_PLC_CN_Comments_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;

            Directory.CreateDirectory(projectDirectory);
            var importDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_ChineseComments_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDir);
            WriteMotorMinimalPlcXml(importDir);

            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "plc_chinese_comments_minimal.md");
            var jsonReportPath = Path.Combine(reportDir, "plc_chinese_comments_minimal.json");

            LogDiag($"PLC Chinese comments validation: directory={projectDirectory}, project={projectName}, importDir={importDir}");
            McpServer.Connect();
            McpServer.CreateProject(projectDirectory, projectName);
            var plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            if (plc.Ok != true) throw new InvalidOperationException("Failed to add PLC for Chinese comments validation: " + plc.Error);

            var import = McpServer.ImportPlcProgramFromDirectory("PLC_1", importDir, compileAfter: true, stopOnImportFailure: true);
            var compile = McpServer.CompileAndDiagnosePlc("PLC_1");

            var blocks = McpServer.GetBlocks("PLC_1", "Motor").Items?.ToArray() ?? Array.Empty<ResponseBlockInfo>();
            LogDiag("Chinese comments blocks readback: " + string.Join(",", blocks.Select(b => b.Name)));

            var exportDir = Path.Combine(reportDir, "exported_readback");
            Directory.CreateDirectory(exportDir);
            var tagExport = Path.Combine(exportDir, "Motor_IO_Tags.xml");
            McpServer.ExportPlcTagTable("PLC_1", "Motor_IO_Tags", tagExport);
            var blockExport = McpServer.ExportBlocksToTemp("PLC_1", "FB1_LAD_Motor|FB2_SCL_Count", false);
            if (!string.IsNullOrWhiteSpace(blockExport.TempDir) && Directory.Exists(blockExport.TempDir))
            {
                foreach (var file in Directory.GetFiles(blockExport.TempDir, "*.xml", SearchOption.AllDirectories))
                {
                    File.Copy(file, Path.Combine(exportDir, Path.GetFileName(file)), true);
                }
            }

            var exportedFiles = Directory.GetFiles(exportDir, "*.xml", SearchOption.AllDirectories);
            var allExportText = string.Join(Environment.NewLine, exportedFiles.Select(p => File.ReadAllText(p, Encoding.UTF8)));
            var checks = new Dictionary<string, bool>
            {
                ["tagComment"] = allExportText.Contains("启动按钮，HMI或现场按钮写入"),
                ["ladBlockComment"] = allExportText.Contains("LAD电机控制功能块，包含中文块注释"),
                ["ladNetworkTitle"] = allExportText.Contains("启动运行自保持"),
                ["ladNetworkComment"] = allExportText.Contains("启动条件成立时置位运行状态"),
                ["sclBlockComment"] = allExportText.Contains("SCL计数功能块，包含中文块注释"),
                ["sclNetworkTitle"] = allExportText.Contains("一秒节拍计数"),
                ["sclNetworkComment"] = allExportText.Contains("定时器到达后计数加一")
            };
            var importedBlockNames = import.ImportedBlocks ?? Array.Empty<string>();
            var exportedFileNames = exportedFiles.Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            var passed = (compile.ErrorCount ?? 0) == 0
                && importedBlockNames.Any(n => string.Equals(n, "FB1_LAD_Motor", StringComparison.OrdinalIgnoreCase))
                && importedBlockNames.Any(n => string.Equals(n, "FB2_SCL_Count", StringComparison.OrdinalIgnoreCase))
                && exportedFileNames.Any(n => string.Equals(n, "FB1_LAD_Motor", StringComparison.OrdinalIgnoreCase))
                && exportedFileNames.Any(n => string.Equals(n, "FB2_SCL_Count", StringComparison.OrdinalIgnoreCase))
                && checks.Values.All(v => v);
            McpServer.SaveProject();
            WriteChineseCommentsReport(reportPath, jsonReportPath, projectName, projectDirectory, importDir, exportDir, passed, import, compile, checks, exportedFiles);
            if (!passed)
            {
                throw new InvalidOperationException("PLC Chinese comments validation failed. Report: " + reportPath);
            }
        }

        private static string MlText(string id, string composition, string text)
        {
            return $@"<MultilingualText ID=""{id}"" CompositionName=""{composition}""><ObjectList><MultilingualTextItem ID=""{id}_1"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>{SecurityElement.Escape(text)}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>";
        }

        private static void WritePlcChineseCommentsMinimalXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "CN_Comment_Tags.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Tags.PlcTagTable ID=""0"">
    <AttributeList><Name>CN_Comment_Tags</Name></AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID=""1"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M30.0</LogicalAddress><Name>CN_Start</Name></AttributeList><ObjectList>{MlText("2", "Comment", "启动按钮，HMI 或现场按钮写入")}</ObjectList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""3"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M30.1</LogicalAddress><Name>CN_Stop</Name></AttributeList><ObjectList>{MlText("4", "Comment", "停止按钮，优先切断运行保持")}</ObjectList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""5"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M30.2</LogicalAddress><Name>CN_Run</Name></AttributeList><ObjectList>{MlText("6", "Comment", "运行状态输出，供HMI指示灯显示")}</ObjectList></SW.Tags.PlcTag>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>", Encoding.UTF8);

            File.WriteAllText(Path.Combine(dir, "FB_CN_LAD_Comment.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5""><Section Name=""Input""><Member Name=""Start"" Datatype=""Bool"" /><Member Name=""Stop"" Datatype=""Bool"" /></Section><Section Name=""Output""><Member Name=""Run"" Datatype=""Bool"" /></Section><Section Name=""InOut"" /><Section Name=""Static"" /><Section Name=""Temp"" /><Section Name=""Constant"" /></Sections></Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>FB_CN_LAD_Comment</Name><Namespace /><Number>31</Number><ProgrammingLanguage>LAD</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      {MlText("1", "Comment", "LAD最小中文注释功能块：演示块注释、网络标题和网络注释可导入并读回。")}
      <SW.Blocks.CompileUnit ID=""3"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource><FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5""><Parts><Access Scope=""LocalVariable"" UId=""21""><Symbol UId=""22""><Component Name=""Start"" UId=""23"" /></Symbol></Access><Access Scope=""LocalVariable"" UId=""24""><Symbol UId=""25""><Component Name=""Stop"" UId=""26"" /></Symbol></Access><Access Scope=""LocalVariable"" UId=""27""><Symbol UId=""28""><Component Name=""Run"" UId=""29"" /></Symbol></Access><Part Name=""Contact"" UId=""30"" /><Part Name=""Contact"" UId=""31""><Negated Name=""operand"" /></Part><Part Name=""Coil"" UId=""32"" /></Parts><Wires><Wire><Powerrail /><NameCon UId=""30"" Name=""in"" /></Wire><Wire><IdentCon UId=""21"" /><NameCon UId=""30"" Name=""operand"" /></Wire><Wire><NameCon UId=""30"" Name=""out"" /><NameCon UId=""31"" Name=""in"" /></Wire><Wire><IdentCon UId=""24"" /><NameCon UId=""31"" Name=""operand"" /></Wire><Wire><NameCon UId=""31"" Name=""out"" /><NameCon UId=""32"" Name=""in"" /></Wire><Wire><IdentCon UId=""27"" /><NameCon UId=""32"" Name=""operand"" /></Wire></Wires></FlgNet></NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>{MlText("4", "Comment", "按下启动并且没有停止时，置位运行状态。")}{MlText("5", "Title", "启动保持回路")}</ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>", Encoding.UTF8);

            var h = CreateStructuredTextXmlHelpers();
            var st = new StringBuilder();
            h.Line(st, h.Local("Limited"), h.Blank(1), h.Tok(":="), h.Blank(1), $@"<Access Scope=""Call"" UId=""101""><Instruction Name=""LIMIT"" UId=""102""><Token Text=""("" UId=""103"" /><Parameter Name=""MN"" UId=""104""><Token Text="":="" UId=""105"" />{h.Const("0")}</Parameter><Token Text="","" UId=""106"" /><Parameter Name=""IN"" UId=""107""><Token Text="":="" UId=""108"" />{h.Local("Raw")}</Parameter><Token Text="","" UId=""109"" /><Parameter Name=""MX"" UId=""110""><Token Text="":="" UId=""111"" />{h.Const("100")}</Parameter><Token Text="")"" UId=""112"" /></Instruction></Access>", h.Tok(";"));

            File.WriteAllText(Path.Combine(dir, "FC_CN_SCL_Comment.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FC ID=""0"">
    <AttributeList>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5""><Section Name=""Input""><Member Name=""Raw"" Datatype=""Int"" /></Section><Section Name=""Output""><Member Name=""Limited"" Datatype=""Int"" /></Section><Section Name=""InOut"" /><Section Name=""Temp"" /><Section Name=""Constant"" /><Section Name=""Return""><Member Name=""Ret_Val"" Datatype=""Void"" /></Section></Sections></Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>FC_CN_SCL_Comment</Name><Namespace /><Number>32</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      {MlText("1", "Comment", "SCL最小中文注释功能：演示SCL网络中文标题和中文注释。")}
      <SW.Blocks.CompileUnit ID=""3"" CompositionName=""CompileUnits""><AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">{st}</StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList>{MlText("4", "Comment", "将输入计数限制在0到100之间，避免HMI显示越界值。")}{MlText("5", "Title", "计数值限幅")}</ObjectList></SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FC>
</Document>", Encoding.UTF8);
        }

        private static void WriteChineseCommentsReport(string mdPath, string jsonPath, string projectName, string projectDirectory, string importDir, string exportDir, bool passed, ResponsePlcProgramImport import, ResponseCompileDiagnose compile, Dictionary<string, bool> checks, string[] exportedFiles)
        {
            var md = new StringBuilder();
            md.AppendLine("# PLC Chinese Comments Minimal Validation");
            md.AppendLine();
            md.AppendLine("- Project: `" + projectName + "`");
            md.AppendLine("- Result: `" + (passed ? "PASS" : "FAIL") + "`");
            md.AppendLine("- PLC Compile: `errors=" + compile.ErrorCount + ", warnings=" + compile.WarningCount + ", state=" + compile.State + "`");
            md.AppendLine("- ImportDir: `" + importDir + "`");
            md.AppendLine("- ExportDir: `" + exportDir + "`");
            md.AppendLine();
            md.AppendLine("| Check | Present after TIA export |");
            md.AppendLine("|---|---|");
            foreach (var kv in checks) md.AppendLine("| " + kv.Key + " | " + kv.Value + " |");
            File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new { projectName, projectDirectory, importDir, exportDir, passed, compile = new { compile.State, compile.ErrorCount, compile.WarningCount, compile.Errors, compile.Warnings }, import = new { import.ImportedTagTables, import.ImportedBlocks, import.ImportedTypes, import.Failed }, checks, exportedFiles }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }

        private static void WriteMotorMinimalPlcXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "UDT_Motor.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Types.PlcStruct ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""None"">
            <Member Name=""Start"" Datatype=""Bool"" />
            <Member Name=""Stop"" Datatype=""Bool"" />
            <Member Name=""Run"" Datatype=""Bool"" />
            <Member Name=""Fault"" Datatype=""Bool"" />
          </Section>
        </Sections>
      </Interface>
      <Name>UDT_Motor</Name>
      <Namespace />
    </AttributeList>
  </SW.Types.PlcStruct>
</Document>", Encoding.UTF8);

            File.WriteAllText(Path.Combine(dir, "DB1_MotorData.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.GlobalDB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Static"">
            <Member Name=""Motor"" Datatype=""&quot;UDT_Motor&quot;"" />
            <Member Name=""Counter"" Datatype=""Int""><StartValue>0</StartValue></Member>
            <Member Name=""ManualEnable"" Datatype=""Bool""><StartValue>true</StartValue></Member>
          </Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>DB1_MotorData</Name>
      <Namespace />
      <Number>1</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>", Encoding.UTF8);

            File.WriteAllText(Path.Combine(dir, "Motor_IO_Tags.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Tags.PlcTagTable ID=""0"">
    <AttributeList><Name>Motor_IO_Tags</Name></AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID=""1"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Motor_Start</Name></AttributeList><ObjectList>{MlText("101", "Comment", "启动按钮，HMI或现场按钮写入")}</ObjectList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""2"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.1</LogicalAddress><Name>Motor_Stop</Name></AttributeList><ObjectList>{MlText("102", "Comment", "停止按钮，优先切断运行保持")}</ObjectList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""3"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.2</LogicalAddress><Name>Motor_Run</Name></AttributeList><ObjectList>{MlText("103", "Comment", "运行状态输出，供HMI指示灯显示")}</ObjectList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""4"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.3</LogicalAddress><Name>Motor_Fault</Name></AttributeList><ObjectList>{MlText("104", "Comment", "故障状态输入，触发运行复位")}</ObjectList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""5"" CompositionName=""Tags""><AttributeList><DataTypeName>Int</DataTypeName><LogicalAddress>%MW2</LogicalAddress><Name>Counter</Name></AttributeList><ObjectList>{MlText("105", "Comment", "一秒节拍累计值，供HMI数值框显示")}</ObjectList></SW.Tags.PlcTag>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>", Encoding.UTF8);

            WriteMotorFb1LadXml(dir);
            WriteMotorFb2SclXml(dir);
            WriteMotorFb1InstanceDbXml(dir);
            WriteMotorFb2InstanceDbXml(dir);
            WriteMotorOb1SclXml(dir);
        }

        private static void WriteMotorMinimalReport(string projectDirectory, string projectName, string importDir, string projectTree, ResponseCompileDiagnose compile, string[] plcAttempts, string[] hmiAttempts, string hmiReadbackSummary, string hmiDesignJson, string hardwareDeviation, string hmiConnectionSummary, string networkProbeSummary)
        {
            var reportPath = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Project: " + projectName);
            sb.AppendLine("Directory: " + projectDirectory);
            sb.AppendLine("ImportDir: " + importDir);
            sb.AppendLine("Hardware: S7-1211C DC/DC/DC + WinCC Unified HMI runtime device");
            sb.AppendLine("Deviation: " + hardwareDeviation);
            sb.AppendLine();
            sb.AppendLine("Compile:");
            sb.AppendLine("State=" + compile.State);
            sb.AppendLine("Errors=" + compile.ErrorCount);
            sb.AppendLine("Warnings=" + compile.WarningCount);
            var compileErrors = compile.Errors?.ToArray() ?? Array.Empty<string>();
            var compileWarnings = compile.Warnings?.ToArray() ?? Array.Empty<string>();
            if (compileErrors.Length > 0)
                foreach (var error in compileErrors)
                    sb.AppendLine("Error: " + error);
            if (compileWarnings.Length > 0)
                foreach (var warning in compileWarnings)
                    sb.AppendLine("Warning: " + warning);
            sb.AppendLine();
            sb.AppendLine("PLC Device Attempts:");
            foreach (var attempt in plcAttempts)
                sb.AppendLine(attempt);
            sb.AppendLine();
            sb.AppendLine("HMI Device Attempts:");
            foreach (var attempt in hmiAttempts)
                sb.AppendLine(attempt);
            sb.AppendLine();
            sb.AppendLine("PLC/HMI Network Probe:");
            sb.AppendLine(networkProbeSummary);
            sb.AppendLine();
            sb.AppendLine("HMI Connection Readback:");
            sb.AppendLine(hmiConnectionSummary);
            sb.AppendLine();
            sb.AppendLine("Structure:");
            sb.AppendLine(projectTree);
            sb.AppendLine();
            sb.AppendLine("UDT Definition:");
            sb.AppendLine("UDT_Motor { Start: Bool; Stop: Bool; Run: Bool; Fault: Bool; }");
            sb.AppendLine();
            sb.AppendLine("DB Definition:");
            sb.AppendLine(@"DB1_MotorData { Motor: UDT_Motor; Counter: Int; ManualEnable: Bool; }");
            sb.AppendLine();
            sb.AppendLine("FB1_LAD_Motor Logic:");
            sb.AppendLine("True LAD FlgNet: ManualEnable and Start set Motor.Run; Stop/Fault/ManualDisable reset Motor.Run.");
            sb.AppendLine();
            sb.AppendLine("FB2_SCL_Count Code:");
            sb.AppendLine("1s TON tick increments Counter; Counter >= 32767 resets to 0.");
            sb.AppendLine();
            sb.AppendLine("HMI Binding:");
            sb.AppendLine("Btn_Start events -> set/reset HMI tag Motor_Start -> %M0.0 -> DB1_MotorData.Motor.Start");
            sb.AppendLine("Btn_Stop events -> set/reset HMI tag Motor_Stop -> %M0.1 -> DB1_MotorData.Motor.Stop");
            sb.AppendLine("Lamp_Run -> HMI tag Motor_Run -> %M0.2 <- DB1_MotorData.Motor.Run");
            sb.AppendLine("Lamp_Fault -> HMI tag Motor_Fault -> %M0.3 -> DB1_MotorData.Motor.Fault");
            sb.AppendLine("IO_Counter -> HMI tag Counter -> %MW2 <- DB1_MotorData.Counter");
            sb.AppendLine();
            sb.AppendLine("HMI Readback:");
            sb.AppendLine(hmiReadbackSummary);
            sb.AppendLine();
            sb.AppendLine("HMI Layout Summary:");
            sb.AppendLine("Screen 800x480 with header, command panel, status panel, counter panel, and footer legend.");
            sb.AppendLine();
            sb.AppendLine("HMI Design JSON:");
            sb.AppendLine(hmiDesignJson);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteMotorFb1LadXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "FB1_LAD_Motor.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""ManualEnable"" Datatype=""Bool"" /></Section>
          <Section Name=""Output"" />
          <Section Name=""InOut""><Member Name=""Motor"" Datatype=""&quot;UDT_Motor&quot;"" /></Section>
          <Section Name=""Static"" />
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>FB1_LAD_Motor</Name>
      <Namespace />
      <Number>1</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID=""A1"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""A2"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>LAD电机控制功能块，包含中文块注释、网络标题和网络说明。</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LocalVariable"" UId=""21""><Symbol><Component Name=""ManualEnable"" /></Symbol></Access>
                <Access Scope=""LocalVariable"" UId=""22""><Symbol><Component Name=""Motor"" /><Component Name=""Start"" /></Symbol></Access>
                <Access Scope=""LocalVariable"" UId=""23""><Symbol><Component Name=""Motor"" /><Component Name=""Run"" /></Symbol></Access>
                <Part Name=""Contact"" UId=""24"" />
                <Part Name=""Contact"" UId=""25"" />
                <Part Name=""SCoil"" UId=""26"" />
              </Parts>
              <Wires>
                <Wire UId=""27""><Powerrail /><NameCon UId=""24"" Name=""in"" /></Wire>
                <Wire UId=""28""><IdentCon UId=""21"" /><NameCon UId=""24"" Name=""operand"" /></Wire>
                <Wire UId=""29""><NameCon UId=""24"" Name=""out"" /><NameCon UId=""25"" Name=""in"" /></Wire>
                <Wire UId=""30""><IdentCon UId=""22"" /><NameCon UId=""25"" Name=""operand"" /></Wire>
                <Wire UId=""31""><NameCon UId=""25"" Name=""out"" /><NameCon UId=""26"" Name=""in"" /></Wire>
                <Wire UId=""32""><IdentCon UId=""23"" /><NameCon UId=""26"" Name=""operand"" /></Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""A3"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""A4"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>启动条件成立时置位运行状态。</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
          <MultilingualText ID=""A5"" CompositionName=""Title""><ObjectList><MultilingualTextItem ID=""A6"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>启动运行自保持</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <SW.Blocks.CompileUnit ID=""2"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
              <Parts>
                <Access Scope=""LocalVariable"" UId=""21""><Symbol><Component Name=""Motor"" /><Component Name=""Stop"" /></Symbol></Access>
                <Access Scope=""LocalVariable"" UId=""22""><Symbol><Component Name=""Motor"" /><Component Name=""Fault"" /></Symbol></Access>
                <Access Scope=""LocalVariable"" UId=""23""><Symbol><Component Name=""ManualEnable"" /></Symbol></Access>
                <Access Scope=""LocalVariable"" UId=""24""><Symbol><Component Name=""Motor"" /><Component Name=""Run"" /></Symbol></Access>
                <Part Name=""Contact"" UId=""25"" />
                <Part Name=""Contact"" UId=""26"" />
                <Part Name=""Contact"" UId=""27""><Negated Name=""operand"" /></Part>
                <Part Name=""O"" UId=""28""><TemplateValue Name=""Card"" Type=""Cardinality"">3</TemplateValue></Part>
                <Part Name=""RCoil"" UId=""29"" />
              </Parts>
              <Wires>
                <Wire UId=""30""><Powerrail /><NameCon UId=""25"" Name=""in"" /><NameCon UId=""26"" Name=""in"" /><NameCon UId=""27"" Name=""in"" /></Wire>
                <Wire UId=""31""><IdentCon UId=""21"" /><NameCon UId=""25"" Name=""operand"" /></Wire>
                <Wire UId=""32""><IdentCon UId=""22"" /><NameCon UId=""26"" Name=""operand"" /></Wire>
                <Wire UId=""33""><IdentCon UId=""23"" /><NameCon UId=""27"" Name=""operand"" /></Wire>
                <Wire UId=""34""><NameCon UId=""25"" Name=""out"" /><NameCon UId=""28"" Name=""in1"" /></Wire>
                <Wire UId=""35""><NameCon UId=""26"" Name=""out"" /><NameCon UId=""28"" Name=""in2"" /></Wire>
                <Wire UId=""36""><NameCon UId=""27"" Name=""out"" /><NameCon UId=""28"" Name=""in3"" /></Wire>
                <Wire UId=""37""><NameCon UId=""28"" Name=""out"" /><NameCon UId=""29"" Name=""in"" /></Wire>
                <Wire UId=""38""><IdentCon UId=""24"" /><NameCon UId=""29"" Name=""operand"" /></Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID=""A7"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""A8"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>停止、故障或手动使能取消时复位运行状态。</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
          <MultilingualText ID=""A9"" CompositionName=""Title""><ObjectList><MultilingualTextItem ID=""AA"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>停止故障复位</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>", Encoding.UTF8);
        }

        private static void WriteMotorFb1InstanceDbXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "IDB_FB1_LAD_Motor.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.InstanceDB ID=""0"">
    <AttributeList>
      <InstanceOfName>FB1_LAD_Motor</InstanceOfName>
      <InstanceOfType>FB</InstanceOfType>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""ManualEnable"" Datatype=""Bool"" /></Section>
          <Section Name=""Output"" />
          <Section Name=""InOut""><Member Name=""Motor"" Datatype=""&quot;UDT_Motor&quot;"" /></Section>
          <Section Name=""Static"" />
        </Sections>
      </Interface>
      <Name>IDB_FB1_LAD_Motor</Name>
      <Namespace />
      <Number>101</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
    <ObjectList />
  </SW.Blocks.InstanceDB>
</Document>", Encoding.UTF8);
        }

        private static void WriteMotorFb2InstanceDbXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "IDB_FB2_SCL_Count.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.InstanceDB ID=""0"">
    <AttributeList>
      <InstanceOfName>FB2_SCL_Count</InstanceOfName>
      <InstanceOfType>FB</InstanceOfType>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input"" />
          <Section Name=""Output"" />
          <Section Name=""InOut""><Member Name=""Counter"" Datatype=""Int"" /></Section>
          <Section Name=""Static""><Member Name=""Tick_1s"" Datatype=""TON_TIME"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member></Section>
        </Sections>
      </Interface>
      <Name>IDB_FB2_SCL_Count</Name>
      <Namespace />
      <Number>102</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
    <ObjectList />
  </SW.Blocks.InstanceDB>
</Document>", Encoding.UTF8);
        }

        private static (Func<string, string> Tok, Func<int, string> Blank, Func<string> NL, Func<string, string> Local, Func<string, string> Const, Func<string, string, string> LocalField, Func<string, string> TypedConst, Func<string, (string Name, string Value)[], string> InstanceCall, StructuredTextLine Line) CreateStructuredTextXmlHelpers()
        {
            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Local(string name) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" /></Symbol></Access>";
            string Const(string value) => $"<Access Scope=\"LiteralConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";
            string LocalField(string name, string field) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" />{Tok(".")}<Component Name=\"{System.Security.SecurityElement.Escape(field)}\" UId=\"{U()}\" /></Symbol></Access>";
            string TypedConst(string value) => $"<Access Scope=\"TypedConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";
            string Param(string name, string value) => $"<Parameter Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\">{Tok(":=")}{value}</Parameter>";
            string InstanceCall(string instance, params (string Name, string Value)[] args)
            {
                var b = new StringBuilder();
                b.Append(Local(instance));
                b.Append($"<Access Scope=\"Call\" UId=\"{U()}\"><Instruction UId=\"{U()}\">");
                b.Append(Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(Tok(","));
                    b.Append(Param(args[i].Name, args[i].Value));
                }
                b.Append(Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }
            void Line(StringBuilder st, params string[] parts)
            {
                foreach (var p in parts) st.AppendLine(p);
                st.AppendLine(NL());
            }
            return (Tok, Blank, NL, Local, Const, LocalField, TypedConst, InstanceCall, Line);
        }

        private static void WriteMotorFb1SclXml(string dir)
        {
            var h = CreateStructuredTextXmlHelpers();
            var st = new StringBuilder();
            h.Line(st, h.Tok("IF"), h.Blank(1), h.Tok("NOT"), h.Blank(1), h.Local("ManualEnable"), h.Blank(1), h.Tok("OR"), h.Blank(1), h.LocalField("Motor", "Fault"), h.Blank(1), h.Tok("OR"), h.Blank(1), h.LocalField("Motor", "Stop"), h.Blank(1), h.Tok("THEN"));
            h.Line(st, h.Blank(2), h.LocalField("Motor", "Run"), h.Blank(1), h.Tok(":="), h.Blank(1), h.Const("FALSE"), h.Tok(";"));
            h.Line(st, h.Tok("ELSIF"), h.Blank(1), h.LocalField("Motor", "Start"), h.Blank(1), h.Tok("THEN"));
            h.Line(st, h.Blank(2), h.LocalField("Motor", "Run"), h.Blank(1), h.Tok(":="), h.Blank(1), h.Const("TRUE"), h.Tok(";"));
            h.Line(st, h.Tok("END_IF"), h.Tok(";"));

            File.WriteAllText(Path.Combine(dir, "FB1_LAD_Motor.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""ManualEnable"" Datatype=""Bool"" /></Section>
          <Section Name=""Output"" />
          <Section Name=""InOut""><Member Name=""Motor"" Datatype=""&quot;UDT_Motor&quot;"" /></Section>
          <Section Name=""Static"" />
          <Section Name=""Temp"" />
          <Section Name=""Constant"" />
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>FB1_LAD_Motor</Name><Namespace /><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>", Encoding.UTF8);
        }

        private static void WriteMotorFb2SclXml(string dir)
        {
            var h = CreateStructuredTextXmlHelpers();
            var st = new StringBuilder();
            h.Line(st, h.InstanceCall("Tick_1s", new[] { ("IN", h.Const("TRUE")), ("PT", h.TypedConst("T#1S")) }), h.Tok(";"));
            h.Line(st, h.Tok("IF"), h.Blank(1), h.LocalField("Tick_1s", "Q"), h.Blank(1), h.Tok("THEN"));
            h.Line(st, h.Blank(2), h.Tok("IF"), h.Blank(1), h.Local("Counter"), h.Blank(1), h.Tok(">="), h.Blank(1), h.Const("32767"), h.Blank(1), h.Tok("THEN"));
            h.Line(st, h.Blank(4), h.Local("Counter"), h.Blank(1), h.Tok(":="), h.Blank(1), h.Const("0"), h.Tok(";"));
            h.Line(st, h.Blank(2), h.Tok("ELSE"));
            h.Line(st, h.Blank(4), h.Local("Counter"), h.Blank(1), h.Tok(":="), h.Blank(1), h.Local("Counter"), h.Blank(1), h.Tok("+"), h.Blank(1), h.Const("1"), h.Tok(";"));
            h.Line(st, h.Blank(2), h.Tok("END_IF"), h.Tok(";"));
            h.Line(st, h.Blank(2), h.InstanceCall("Tick_1s", new[] { ("IN", h.Const("FALSE")), ("PT", h.TypedConst("T#1S")) }), h.Tok(";"));
            h.Line(st, h.Tok("END_IF"), h.Tok(";"));

            File.WriteAllText(Path.Combine(dir, "FB2_SCL_Count.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input"" />
          <Section Name=""Output"" />
          <Section Name=""InOut""><Member Name=""Counter"" Datatype=""Int"" /></Section>
          <Section Name=""Static""><Member Name=""Tick_1s"" Datatype=""TON_TIME"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member></Section>
          <Section Name=""Temp"" />
          <Section Name=""Constant"" />
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>FB2_SCL_Count</Name><Namespace /><Number>2</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID=""B1"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""B2"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>SCL计数功能块，包含中文块注释、网络标题和网络说明。</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
        <ObjectList>
          <MultilingualText ID=""B3"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""B4"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>定时器到达后计数加一，达到上限后清零。</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
          <MultilingualText ID=""B5"" CompositionName=""Title""><ObjectList><MultilingualTextItem ID=""B6"" CompositionName=""Items""><AttributeList><Culture>zh-CN</Culture><Text>一秒节拍计数</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>", Encoding.UTF8);
        }

        private static void WriteMotorOb1SclXml(string dir)
        {
            var h = CreateStructuredTextXmlHelpers();
            var st = new StringBuilder();
            var uid = 1000;
            string U() => (uid++).ToString();
            string Db(string member) => $@"<Access Scope=""GlobalVariable"" UId=""{U()}""><Symbol UId=""{U()}""><Component Name=""DB1_MotorData"" UId=""{U()}""><BooleanAttribute Name=""HasQuotes"" UId=""{U()}"">true</BooleanAttribute></Component><Token Text=""."" UId=""{U()}"" /><Component Name=""{member}"" UId=""{U()}"" /></Symbol></Access>";
            string DbMotor(string member) => $@"<Access Scope=""GlobalVariable"" UId=""{U()}""><Symbol UId=""{U()}""><Component Name=""DB1_MotorData"" UId=""{U()}""><BooleanAttribute Name=""HasQuotes"" UId=""{U()}"">true</BooleanAttribute></Component><Token Text=""."" UId=""{U()}"" /><Component Name=""Motor"" UId=""{U()}"" /><Token Text=""."" UId=""{U()}"" /><Component Name=""{member}"" UId=""{U()}"" /></Symbol></Access>";
            string PlcTag(string name) => $@"<Access Scope=""GlobalVariable"" UId=""{U()}""><Symbol UId=""{U()}""><Component Name=""{name}"" UId=""{U()}"" /></Symbol></Access>";
            string GlobalInstanceCall(string instanceName, params (string Name, string Value)[] args)
            {
                var b = new StringBuilder();
                b.Append($@"<Access Scope=""GlobalVariable"" UId=""{U()}""><Symbol UId=""{U()}""><Component Name=""{instanceName}"" UId=""{U()}""><BooleanAttribute Name=""HasQuotes"" UId=""{U()}"">true</BooleanAttribute></Component></Symbol></Access>");
                b.Append($@"<Access Scope=""Call"" UId=""{U()}""><Instruction UId=""{U()}"">");
                b.Append(h.Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(h.Tok(","));
                    b.Append($@"<Parameter Name=""{System.Security.SecurityElement.Escape(args[i].Name)}"" UId=""{U()}"">");
                    b.Append(h.Tok(":="));
                    b.Append(args[i].Value);
                    b.Append("</Parameter>");
                }
                b.Append(h.Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }

            h.Line(st, DbMotor("Start"), h.Blank(1), h.Tok(":="), h.Blank(1), PlcTag("Motor_Start"), h.Tok(";"));
            h.Line(st, DbMotor("Stop"), h.Blank(1), h.Tok(":="), h.Blank(1), PlcTag("Motor_Stop"), h.Tok(";"));
            h.Line(st, DbMotor("Fault"), h.Blank(1), h.Tok(":="), h.Blank(1), PlcTag("Motor_Fault"), h.Tok(";"));
            h.Line(st, GlobalInstanceCall("IDB_FB1_LAD_Motor", ("ManualEnable", Db("ManualEnable")), ("Motor", Db("Motor"))), h.Tok(";"));
            h.Line(st, GlobalInstanceCall("IDB_FB2_SCL_Count", ("Counter", Db("Counter"))), h.Tok(";"));
            h.Line(st, PlcTag("Motor_Run"), h.Blank(1), h.Tok(":="), h.Blank(1), DbMotor("Run"), h.Tok(";"));
            h.Line(st, PlcTag("Counter"), h.Blank(1), h.Tok(":="), h.Blank(1), Db("Counter"), h.Tok(";"));

            File.WriteAllText(Path.Combine(dir, "Main.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.OB ID=""0"">
    <AttributeList>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5""><Section Name=""Input""><Member Name=""Initial_Call"" Datatype=""Bool"" Informative=""true"" /><Member Name=""Remanence"" Datatype=""Bool"" Informative=""true"" /></Section><Section Name=""Temp"" /><Section Name=""Constant"" /></Sections></Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>Main</Name><Namespace /><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SecondaryType>ProgramCycle</SecondaryType><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.OB>
</Document>", Encoding.UTF8);
        }

    }
}
