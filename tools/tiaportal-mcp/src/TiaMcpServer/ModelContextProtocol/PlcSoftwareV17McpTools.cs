using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public static class PlcSoftwareV17McpTools
    {
        [McpServerTool(Name = "Bootstrap"), Description("[L0][V17-Phase1] 返回 V17 PLC-Software MCP 使用指引和当前状态。")]
        public static Task<ResponseBootstrap> Bootstrap() => McpServer.Bootstrap();

        [McpServerTool(Name = "RunCapabilitySelfTest"), Description("[L0][V17-Phase1] 执行只读能力自检。")]
        public static Task<ResponseCapabilitySelfTest> RunCapabilitySelfTest(
            [Description("为 true 时，如未连接则先附着或启动 TIA Portal。")] bool connectIfNeeded = false,
            [Description("为 true 时，项目已打开则包含 GetProjectTree 输出。")] bool includeProjectTree = false,
            [Description("为 true 时，枚举 TIA Portal 进程和工程信息。")] bool inspectPortalProcesses = false,
            [Description("期望 PLC softwarePath。")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("兼容保留参数。")] string expectedHmiSoftwarePath = "HMI_RT_1")
            => McpServer.RunCapabilitySelfTest(connectIfNeeded, includeProjectTree, inspectPortalProcesses, expectedPlcSoftwarePath, expectedHmiSoftwarePath);

        [McpServerTool(Name = "Connect"), Description("[L1][V17-Phase1] 连接或附着 TIA Portal。")]
        public static ResponseConnect Connect() => McpServer.Connect();

        [McpServerTool(Name = "Disconnect"), Description("[L1][V17-Phase1] 断开 MCP 与 TIA Portal 的 Openness 连接。")]
        public static ResponseDisconnect Disconnect() => McpServer.Disconnect();

        [McpServerTool(Name = "EnsureOpennessUserGroup"), Description("[L1][V17-Phase1] 检查并提示加入 Siemens TIA Openness 用户组。")]
        public static Task<ResponseMessage> EnsureOpennessUserGroup() => McpServer.EnsureOpennessUserGroup();

        [McpServerTool(Name = "ListPortalProcessProjects"), Description("[L1][V17-Phase1] 列出可见 TIA Portal 进程和工程。")]
        public static ResponseStringList ListPortalProcessProjects() => McpServer.ListPortalProcessProjects();

        [McpServerTool(Name = "GetState"), Description("[L0][V17-Phase1] 获取连接和当前工程状态。")]
        public static ResponseState GetState() => McpServer.GetState();

        [McpServerTool(Name = "GetProject"), Description("[L1][V17-Phase1] 列出当前可见工程。")]
        public static ResponseGetProjects GetProject() => McpServer.GetProjects();

        [McpServerTool(Name = "OpenProject"), Description("[L1][V17-Phase1] 打开本地 TIA 工程。")]
        public static ResponseOpenProject OpenProject(
            [Description("工程 .ap17 或 .als17 路径。")] string path)
            => McpServer.OpenProject(path);

        [McpServerTool(Name = "AttachToOpenProject"), Description("[L1][V17-Phase1] 按工程名附着已打开工程。")]
        public static ResponseMessage AttachToOpenProject(
            [Description("TIA Portal 中显示的工程名。")] string projectName)
            => McpServer.AttachToOpenProject(projectName);

        [McpServerTool(Name = "CreateProject"), Description("[L1][V17-Phase1] 创建新的 TIA 工程。")]
        public static ResponseMessage CreateProject(
            [Description("工程目录。")] string directoryPath,
            [Description("工程名称。")] string projectName)
            => McpServer.CreateProject(directoryPath, projectName);

        [McpServerTool(Name = "SaveProject"), Description("[L1][V17-Phase1] 保存当前工程。")]
        public static ResponseSaveProject SaveProject() => McpServer.SaveProject();

        [McpServerTool(Name = "CloseProject"), Description("[L1][V17-Phase1] 关闭当前工程。")]
        public static ResponseCloseProject CloseProject() => McpServer.CloseProject();

        [McpServerTool(Name = "GetProjectTree"), Description("[L1][V17-Phase1] 获取项目树，用于发现 PLC softwarePath。")]
        public static ResponseProjectTree GetProjectTree() => McpServer.GetProjectTree();

        [McpServerTool(Name = "SearchHardwareCatalog"), Description("[L1][V17-Phase1] 搜索硬件目录。")]
        public static ResponseHardwareCatalogSearch SearchHardwareCatalog(
            [Description("MLFB、设备名或关键字。")] string keyword,
            [Description("最大返回数量。")] int limit = 50)
            => McpServer.SearchHardwareCatalog(keyword, limit);

        [McpServerTool(Name = "AddDeviceWithFallback"), Description("[L1][V17-Phase1] 按 MLFB 和 fallback 逻辑添加 PLC。")]
        public static ResponseDeviceProbe AddDeviceWithFallback(
            [Description("优先 MLFB。")] string preferredMlfb,
            [Description("优先版本。")] string preferredVersion,
            [Description("工程内设备名。")] string deviceName,
            [Description("设备族，例如 S7-1200。")] string family = "S7-1500")
            => McpServer.AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);

        [McpServerTool(Name = "BuildPlcUdtXml"), Description("[L2][V17-Phase1] 离线构建 UDT XML。")]
        public static ResponseXmlBuild BuildPlcUdtXml([Description("UDT JSON。")] string udtJson)
            => McpServer.BuildPlcUdtXml(udtJson);

        [McpServerTool(Name = "BuildPlcTagTableXml"), Description("[L2][V17-Phase1] 离线构建 PLC tag table XML。")]
        public static ResponseXmlBuild BuildPlcTagTableXml([Description("变量表 JSON。")] string tagTableJson)
            => McpServer.BuildPlcTagTableXml(tagTableJson);

        [McpServerTool(Name = "BuildPlcGlobalDbXml"), Description("[L2][V17-Phase1] 离线构建 GlobalDB XML。")]
        public static ResponseXmlBuild BuildPlcGlobalDbXml([Description("GlobalDB JSON。")] string globalDbJson)
            => McpServer.BuildPlcGlobalDbXml(globalDbJson);

        [McpServerTool(Name = "BuildStructuredTextXml"), Description("[L2][V17-Phase1] 离线构建 StructuredText XML。")]
        public static ResponseXmlBuild BuildStructuredTextXml(
            [Description("StructuredText JSON。")] string structuredTextJson,
            [Description("为 true 时仅返回内部 XML。")] bool innerOnly = false)
            => McpServer.BuildStructuredTextXml(structuredTextJson, innerOnly);

        [McpServerTool(Name = "BuildFlgNetCallXml"), Description("[L2][V17-Phase1] 离线构建 FlgNet 调用 XML。")]
        public static ResponseXmlBuild BuildFlgNetCallXml([Description("FlgNet JSON。")] string flgNetJson)
            => McpServer.BuildFlgNetCallXml(flgNetJson);

        [McpServerTool(Name = "ComposePlcFcBlockXml"), Description("[L2][V17-Phase1] 离线组合 FC 块 XML。")]
        public static ResponseXmlBuild ComposePlcFcBlockXml([Description("FC 块 JSON。")] string fcBlockJson)
            => McpServer.ComposePlcFcBlockXml(fcBlockJson);

        [McpServerTool(Name = "ComposePlcFbBlockXml"), Description("[L2][V17-Phase1] 离线组合 FB 块 XML。")]
        public static ResponseXmlBuild ComposePlcFbBlockXml([Description("FB 块 JSON。")] string fbBlockJson)
            => McpServer.ComposePlcFbBlockXml(fbBlockJson);

        [McpServerTool(Name = "ComposePlcLadFcBlockXml"), Description("[L2][V17-Phase1] 离线组合 LAD FC 块 XML。")]
        public static ResponseXmlBuild ComposePlcLadFcBlockXml([Description("LAD FC 块 JSON。")] string ladFcBlockJson)
            => McpServer.ComposePlcLadFcBlockXml(ladFcBlockJson);

        [McpServerTool(Name = "BuildPlcSymbolManifestFromXmlPath"), Description("[L2][V17-Phase1] 从 PLC XML 文件或目录生成符号清单。")]
        public static ResponseJsonReport BuildPlcSymbolManifestFromXmlPath([Description("XML 文件或目录路径。")] string path)
            => McpServer.BuildPlcSymbolManifestFromXmlPath(path);

        [McpServerTool(Name = "GetSoftwareInfo"), Description("[L1][V17-Phase1] 获取 PLC 软件对象信息。")]
        public static ResponseSoftwareInfo GetSoftwareInfo([Description("PLC softwarePath，例如 PLC_1。")] string softwarePath)
            => McpServer.GetSoftwareInfo(softwarePath);

        [McpServerTool(Name = "PlcBuildAndImport"), Description("[L1][V17-Phase1] 结构化构建并导入 PLC 对象。")]
        public static ResponsePlcProgramImport PlcBuildAndImport(
            [Description("PLC softwarePath，例如 PLC_1。")] string softwarePath,
            [Description("类型：udt|tagtable|globaldb|fc|fb。")] string kind,
            [Description("结构化 JSON。")] string json,
            [Description("UDT 类型组路径。")] string typeGroupPath = "",
            [Description("变量表文件夹路径。")] string tagFolderPath = "",
            [Description("块组路径。")] string blockGroupPath = "",
            [Description("导入后是否编译。")] bool compileAfter = true,
            [Description("为 true 时只生成文件和计划，不导入。")] bool dryRun = true)
            => McpServer.PlcBuildAndImport(softwarePath, kind, json, typeGroupPath, tagFolderPath, blockGroupPath, compileAfter, dryRun);

        [McpServerTool(Name = "GetPlcExternalSources"), Description("[L2][V17-Phase1] 获取 PLC 外部源列表。")]
        public static ResponseStringList GetPlcExternalSources([Description("PLC softwarePath。")] string softwarePath)
            => McpServer.GetPlcExternalSources(softwarePath);

        [McpServerTool(Name = "GetPlcTagTables"), Description("[L2][V17-Phase1] 获取 PLC 变量表列表。")]
        public static ResponseStringList GetPlcTagTables([Description("PLC softwarePath。")] string softwarePath)
            => McpServer.GetPlcTagTables(softwarePath);

        [McpServerTool(Name = "ExportPlcTagTable"), Description("[L2][V17-Phase1] 导出一个 PLC 变量表。")]
        public static ResponseExportFile ExportPlcTagTable(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("变量表名称。")] string tagTableName,
            [Description("导出 XML 路径。")] string exportPath)
            => McpServer.ExportPlcTagTable(softwarePath, tagTableName, exportPath);

        [McpServerTool(Name = "ImportPlcTagTable"), Description("[L1][V17-Phase1] 导入一个 PLC 变量表。")]
        public static ResponseMessage ImportPlcTagTable(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("变量表组路径，根路径传空。")] string folderPath,
            [Description("导入 XML 路径。")] string importPath)
            => McpServer.ImportPlcTagTable(softwarePath, folderPath, importPath);

        [McpServerTool(Name = "GetPlcWatchTables"), Description("[L2][V17-Phase1] 获取 PLC 监控表列表。")]
        public static ResponseStringList GetPlcWatchTables([Description("PLC softwarePath。")] string softwarePath)
            => McpServer.GetPlcWatchTables(softwarePath);

        [McpServerTool(Name = "ExportPlcWatchTable"), Description("[L2][V17-Phase1] 导出一个 PLC 监控表。")]
        public static ResponseExportFile ExportPlcWatchTable(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("监控表名称。")] string watchTableName,
            [Description("导出 XML 路径。")] string exportPath)
            => McpServer.ExportPlcWatchTable(softwarePath, watchTableName, exportPath);

        [McpServerTool(Name = "ImportPlcExternalSource"), Description("[L2][V17-Phase1] 导入一个 PLC 外部源文件。")]
        public static ResponseMessage ImportPlcExternalSource(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("外部源组路径，根路径传空。")] string groupPath,
            [Description("外部源文件路径。")] string filePath)
            => McpServer.ImportPlcExternalSource(softwarePath, groupPath, filePath);

        [McpServerTool(Name = "DeletePlcExternalSource"), Description("[L2][V17-Phase1] 删除一个 PLC 外部源。")]
        public static ResponseMessage DeletePlcExternalSource(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("外部源名称。")] string externalSourceName)
            => McpServer.DeletePlcExternalSource(softwarePath, externalSourceName);

        [McpServerTool(Name = "GenerateBlocksFromExternalSource"), Description("[L2][V17-Phase1] 从 PLC 外部源生成块。")]
        public static ResponseMessage GenerateBlocksFromExternalSource(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("外部源名称。")] string externalSourceName)
            => McpServer.GenerateBlocksFromExternalSource(softwarePath, externalSourceName);

        [McpServerTool(Name = "CompileSoftware"), Description("[L1][V17-Phase1] 编译 PLC 软件。")]
        public static ResponseCompile CompileSoftware(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("安全密码，默认空。")] string password = "")
            => McpServer.CompileSoftware(softwarePath, password);

        [McpServerTool(Name = "CompileAndDiagnosePlc"), Description("[L1][V17-Phase1] 编译 PLC 并返回结构化诊断。")]
        public static ResponseCompileDiagnose CompileAndDiagnosePlc(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("安全密码，默认空。")] string password = "")
            => McpServer.CompileAndDiagnosePlc(softwarePath, password);

        [McpServerTool(Name = "GetSoftwareTree"), Description("[L1][V17-Phase1] 获取 PLC 软件树。")]
        public static ResponseSoftwareTree GetSoftwareTree([Description("PLC softwarePath。")] string softwarePath)
            => McpServer.GetSoftwareTree(softwarePath);

        [McpServerTool(Name = "GetBlockInfo"), Description("[L2][V17-Phase1] 获取块详情。")]
        public static ResponseBlockInfo GetBlockInfo(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("块完整路径。")] string blockPath)
            => McpServer.GetBlockInfo(softwarePath, blockPath);

        [McpServerTool(Name = "GetBlocks"), Description("[L2][V17-Phase1] 获取块列表。")]
        public static ResponseBlocks GetBlocks(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("块名正则过滤。")] string regexName = "")
            => McpServer.GetBlocks(softwarePath, regexName);

        [McpServerTool(Name = "GetBlocksWithHierarchy"), Description("[L2][V17-Phase1] 获取带层级的块列表。")]
        public static ResponseBlocksWithHierarchy GetBlocksWithHierarchy([Description("PLC softwarePath。")] string softwarePath)
            => McpServer.GetBlocksWithHierarchy(softwarePath);

        [McpServerTool(Name = "ExportBlock"), Description("[L2][V17-Phase1] 导出单个块 XML。")]
        public static ResponseExportBlock ExportBlock(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("块完整路径。")] string blockPath,
            [Description("导出路径。")] string exportPath,
            [Description("是否保留层级路径。")] bool preservePath = false)
            => McpServer.ExportBlock(softwarePath, blockPath, exportPath, preservePath);

        [McpServerTool(Name = "ImportBlock"), Description("[L1][V17-Phase1] 导入单个块 XML。")]
        public static ResponseImportBlock ImportBlock(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("块组路径，根路径传空。")] string groupPath,
            [Description("导入 XML 路径。")] string importPath)
            => McpServer.ImportBlock(softwarePath, groupPath, importPath);

        [McpServerTool(Name = "ImportBlocksFromDirectory"), Description("[L2][V17-Phase1] 从目录批量导入块 XML。")]
        public static ResponseImportBatch ImportBlocksFromDirectory(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("块组路径，根路径传空。")] string groupPath,
            [Description("XML 目录。")] string dir,
            [Description("文件名正则过滤。")] string regexName = "",
            [Description("为 true 时覆盖。")] bool overwrite = true)
            => McpServer.ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, overwrite);

        [McpServerTool(Name = "ImportPlcProgramFromDirectory"), Description("[L2][V17-Phase1] 从目录按依赖顺序导入 PLC 程序。")]
        public static ResponsePlcProgramImport ImportPlcProgramFromDirectory(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("源目录。")] string sourceDir,
            [Description("类型组路径。")] string typeGroupPath = "",
            [Description("变量表组路径。")] string tagFolderPath = "",
            [Description("TO 组路径。")] string technologyFolderPath = "",
            [Description("块组路径。")] string blockGroupPath = "",
            [Description("文件名正则过滤。")] string regexName = "",
            [Description("导入后是否编译。")] bool compileAfter = true,
            [Description("首次导入失败后是否停止。")] bool stopOnImportFailure = false,
            [Description("为 true 时仅分类不导入。")] bool dryRun = false)
            => McpServer.ImportPlcProgramFromDirectory(softwarePath, sourceDir, typeGroupPath, tagFolderPath, technologyFolderPath, blockGroupPath, regexName, compileAfter, stopOnImportFailure, dryRun);

        [McpServerTool(Name = "ExportBlocks"), Description("[L2][V17-Phase1] 批量导出块 XML。")]
        public static Task<ResponseExportBlocks> ExportBlocks(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("导出目录。")] string exportPath,
            [Description("块名正则过滤。")] string regexName = "",
            [Description("是否保留层级路径。")] bool preservePath = false)
            => McpServer.ExportBlocks(server, context, softwarePath, exportPath, regexName, preservePath);

        [McpServerTool(Name = "GetTypeInfo"), Description("[L2][V17-Phase1] 获取 PLC 类型详情。")]
        public static ResponseTypeInfo GetTypeInfo(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("类型完整路径。")] string typePath)
            => McpServer.GetTypeInfo(softwarePath, typePath);

        [McpServerTool(Name = "GetTypes"), Description("[L2][V17-Phase1] 获取 PLC 类型列表。")]
        public static ResponseTypes GetTypes(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("类型名正则过滤。")] string regexName = "")
            => McpServer.GetTypes(softwarePath, regexName);

        [McpServerTool(Name = "ExportType"), Description("[L2][V17-Phase1] 导出单个 PLC 类型 XML。")]
        public static ResponseExportType ExportType(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("导出目录。")] string exportPath,
            [Description("类型完整路径。")] string typePath,
            [Description("是否保留层级路径。")] bool preservePath = false)
            => McpServer.ExportType(softwarePath, exportPath, typePath, preservePath);

        [McpServerTool(Name = "ImportType"), Description("[L1][V17-Phase1] 导入单个 PLC 类型 XML。")]
        public static ResponseImportType ImportType(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("类型组路径，根路径传空。")] string groupPath,
            [Description("导入 XML 路径。")] string importPath)
            => McpServer.ImportType(softwarePath, groupPath, importPath);

        [McpServerTool(Name = "ExportTypes"), Description("[L2][V17-Phase1] 批量导出 PLC 类型 XML。")]
        public static Task<ResponseExportTypes> ExportTypes(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("导出目录。")] string exportPath,
            [Description("类型名正则过滤。")] string regexName = "",
            [Description("是否保留层级路径。")] bool preservePath = false)
            => McpServer.ExportTypes(server, context, softwarePath, exportPath, regexName, preservePath);

        [McpServerTool(Name = "GetTechnologyObjects"), Description("[L2][V17-Phase1] 获取 PLC Technology Object 列表。")]
        public static ResponseTechnologyObjectList GetTechnologyObjects([Description("PLC softwarePath。")] string softwarePath)
            => McpServer.GetTechnologyObjects(softwarePath);

        [McpServerTool(Name = "ExportTechnologyObject"), Description("[L2][V17-Phase1] 导出单个 Technology Object XML。")]
        public static ResponseMessage ExportTechnologyObject(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("TO 名称。")] string toName,
            [Description("导出 XML 路径。")] string exportPath)
            => McpServer.ExportTechnologyObject(softwarePath, toName, exportPath);

        [McpServerTool(Name = "ImportTechnologyObject"), Description("[L2][V17-Phase1] 导入一个 Technology Object XML。")]
        public static ResponseMessage ImportTechnologyObject(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("TO 组路径，根路径传空。")] string folderPath,
            [Description("导入 XML 路径。")] string importPath)
            => McpServer.ImportTechnologyObject(softwarePath, folderPath, importPath);

        [McpServerTool(Name = "ImportTechnologyObjectsFromDirectory"), Description("[L2][V17-Phase1] 从目录批量导入 Technology Object XML。")]
        public static ResponseImportBatch ImportTechnologyObjectsFromDirectory(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("TO 组路径，根路径传空。")] string folderPath,
            [Description("XML 目录。")] string dir,
            [Description("文件名正则过滤。")] string regexName = "",
            [Description("为 true 时覆盖。")] bool overwrite = true)
            => McpServer.ImportTechnologyObjectsFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);

        [McpServerTool(Name = "ExportAsDocuments"), Description("[L2][V17-Phase1][Gated] 文档导出仅 TIA Portal V20+ 支持，V17 返回明确门禁。")]
        public static ResponseExportAsDocuments ExportAsDocuments(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("块完整路径。")] string blockPath,
            [Description("导出目录。")] string exportPath,
            [Description("是否保留层级路径。")] bool preservePath = false)
            => McpServer.ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath);

        [McpServerTool(Name = "ExportBlocksAsDocuments"), Description("[L2][V17-Phase1][Gated] 批量文档导出仅 TIA Portal V20+ 支持，V17 返回明确门禁。")]
        public static Task<ResponseExportBlocksAsDocuments> ExportBlocksAsDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("导出目录。")] string exportPath,
            [Description("块名正则过滤。")] string regexName = "",
            [Description("是否保留层级路径。")] bool preservePath = false)
            => McpServer.ExportBlocksAsDocuments(server, context, softwarePath, exportPath, regexName, preservePath);

        [McpServerTool(Name = "ImportFromDocuments"), Description("[L2][V17-Phase1][Gated] 文档导入仅 TIA Portal V20+ 支持，V17 返回明确门禁。")]
        public static ResponseImportFromDocuments ImportFromDocuments(
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("目标块组路径。")] string groupPath,
            [Description("文档目录。")] string importPath,
            [Description("不含扩展名的文件名。")] string fileNameWithoutExtension,
            [Description("导入选项。")] string importOption = "Override")
            => McpServer.ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, importOption);

        [McpServerTool(Name = "ImportBlocksFromDocuments"), Description("[L2][V17-Phase1][Gated] 批量文档导入仅 TIA Portal V20+ 支持，V17 返回明确门禁。")]
        public static Task<ResponseImportBlocksFromDocuments> ImportBlocksFromDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("PLC softwarePath。")] string softwarePath,
            [Description("目标块组路径。")] string groupPath,
            [Description("文档目录。")] string importPath,
            [Description("文件名正则过滤。")] string regexName = "",
            [Description("导入选项。")] string importOption = "Override")
            => McpServer.ImportBlocksFromDocuments(server, context, softwarePath, groupPath, importPath, regexName, importOption);

        [McpServerTool(Name = "SeedProjectFromReference"), Description("[L2][V17-Phase1] 从参考目录回灌 PLC 种子资源。")]
        public static ResponseSeed SeedProjectFromReference(
            [Description("PLC softwarePath。")] string plcSoftwarePath,
            [Description("兼容保留参数。")] string hmiSoftwarePath,
            [Description("参考目录。")] string referenceDir,
            [Description("占位符 JSON。")] JsonObject? placeholders = null)
            => McpServer.SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders);
    }
}
