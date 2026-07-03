using Siemens.Engineering;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseMessage
    {
        public string? Message { get; set; }
        public JsonObject? Meta { get; set; }
    }

    public class ImportFailure
    {
        public string? Path { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseAttributes : ResponseMessage
    {
        public IEnumerable<Attribute>? Attributes { get; set; }
    }

    public class ResponseSoftwareInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseDeviceInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseDeviceItemInfo : ResponseAttributes
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseBlockInfo : ResponseAttributes
    {
        //public string? Path { get; set; }
        public string? TypeName { get; set; }
        public string? Name { get; set; }
        public string? Namespace { get; set; }
        public string? ProgrammingLanguage { get; set; }
        public string? MemoryLayout { get; set; }
        public bool? IsConsistent { get; set; }
        public string? HeaderName { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public bool? IsKnowHowProtected { get; set; }
        public string? Description { get; set; }
    }
    public class ResponseBlocksWithHierarchy : ResponseMessage
    {
        public BlockGroupInfo? Root { get; set; }
    }

    public class ResponseTypeInfo : ResponseAttributes
    {
        //public string? Path { get; set; }
        public string? Name { get; set; }
        public string? TypeName { get; set; }
        public string? Namespace { get; set; }
        public bool? IsConsistent { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public bool? IsKnowHowProtected { get; set; }
        public string? Description { get; set; }
    }

    public class ResponseProjectInfo : ResponseAttributes
    {
        //public string? Path { get; set; }
        public string? Name { get; set; }
    }

    public class ResponseConnect : ResponseMessage
    {
    }

    public class ResponseDisconnect : ResponseMessage
    {
    }

    public class ResponseState : ResponseMessage
    {
        public bool? IsConnected { get; set; }
        public string? Project { get; set; }
        public string? Session { get; set; }
    }

    public class CapabilitySelfTestItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? Detail { get; set; }
    }

    public class ResponseCapabilitySelfTest : ResponseMessage
    {
        public bool? Ok { get; set; }
        public bool? IncludeProjectTree { get; set; }
        public IEnumerable<CapabilitySelfTestItem>? Items { get; set; }
        public string? ProjectTree { get; set; }
    }

    /// <summary>
    /// Single-call orientation result used by AI clients to discover environment,
    /// connection state, what tool to call next, and known platform limitations.
    /// </summary>
    public class ResponseBootstrap : ResponseMessage
    {
        public bool? Ready { get; set; }
        public BootstrapEnvironment? Environment { get; set; }
        public BootstrapPortal? Portal { get; set; }
        public string? RecommendedNextTool { get; set; }
        public string? RecommendedReason { get; set; }
        /// <summary>
        /// Compact operating rules returned on the very first call so even hosts that never load
        /// SKILL.md still steer a less-capable model away from the common ordering/parameter mistakes.
        /// </summary>
        public IEnumerable<string>? OperatingRules { get; set; }
        public IEnumerable<string>? KnownLimitations { get; set; }
        public BootstrapToolLayers? ToolLayers { get; set; }
        public string? SkillFile { get; set; }
        public string? ServerVersion { get; set; }
        public IEnumerable<TiaMcpServer.Siemens.CapabilityInfo>? Capabilities { get; set; }
    }

    public class BootstrapEnvironment
    {
        public int? TiaVersionInUse { get; set; }
        public int? TiaVersionDetected { get; set; }
        public bool? OpennessGroupOk { get; set; }
        public string? TiaInstallPath { get; set; }
        public string? Transport { get; set; }
    }

    public class BootstrapPortal
    {
        public bool? Connected { get; set; }
        public string? ProjectName { get; set; }
        public string? SessionName { get; set; }
        public string? LastConnectError { get; set; }
    }

    public class BootstrapToolLayers
    {
        public IEnumerable<string>? L0 { get; set; }
        public IEnumerable<string>? L1 { get; set; }
        public int? L2Count { get; set; }
    }

    public class ResponseDoctor : ResponseMessage
    {
        public bool? Ready { get; set; }
        public IEnumerable<DoctorCheck>? Checks { get; set; }
        public string? RecommendedNextTool { get; set; }
        public string? Summary { get; set; }
    }

    public class DoctorCheck
    {
        public string? Name { get; set; }
        public bool Ok { get; set; }
        public string? Detail { get; set; }
        public string? Fix { get; set; }
    }

    public class ResponseSafetySelfTest : ResponseMessage
    {
        public bool? Ok { get; set; }
        public IEnumerable<CapabilitySelfTestItem>? Items { get; set; }
        public IEnumerable<string>? Policy { get; set; }
    }

    public class ResponseAcceptanceReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? OperationId { get; set; }
        public string? OutputDirectory { get; set; }
        public string? MarkdownPath { get; set; }
        public string? JsonPath { get; set; }
        public ResponseCapabilitySelfTest? SelfTest { get; set; }
        public ResponseSafetySelfTest? SafetySelfTest { get; set; }
    }

    public class ResponseErrorReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? OperationId { get; set; }
        public string? ErrorCode { get; set; }
        public string? Severity { get; set; }
        public string? Summary { get; set; }
        public string? OutputDirectory { get; set; }
        public string? MarkdownPath { get; set; }
        public string? JsonPath { get; set; }
        public IEnumerable<string>? RecommendedNextActions { get; set; }
    }

    public class ResponseGetProjects : ResponseMessage
    {
        public IEnumerable<ResponseProjectInfo>? Items { get; set; }
    }

    public class ResponseOpenProject : ResponseMessage
    {
    }

    public class ResponseSaveProject : ResponseMessage
    {
    }

    public class ResponseSaveAsProject : ResponseMessage
    {
    }

    public class ResponseCloseProject : ResponseMessage
    {
    }

    public class ResponseTree : ResponseMessage
    {
        public string? Tree { get; set; }
    }

    public class ResponseProjectTree : ResponseMessage
    {
        public string? Tree { get; set; }
    }

    public class ResponseSoftwareTree : ResponseMessage
    {
        public string? Tree { get; set; }
    }

    public class ResponseHmiProgramInfo : ResponseMessage
    {
        public string? Name { get; set; }
        public string? ProgramType { get; set; } // Classic/Unified/Unknown
        public IEnumerable<string>? Screens { get; set; }
    }

    public class ResponseStringList : ResponseMessage
    {
        public IEnumerable<string>? Items { get; set; }
    }

    public class ResponseExportFile : ResponseMessage
    {
        public string? ExportPath { get; set; }
    }

    public class ResponseBatchExport : ResponseMessage
    {
        public IEnumerable<string>? Exported { get; set; }
        public IEnumerable<string>? Failed { get; set; }
    }

    public class ResponseTempExport : ResponseMessage
    {
        public string? TempDir { get; set; }
        public IEnumerable<string>? Paths { get; set; }
    }

    public class ResponseCrossReferences : ResponseMessage
    {
        public IEnumerable<CrossReferenceEntry>? Items { get; set; }
    }

    public class ResponseNetworkInfo : ResponseMessage
    {
        public string? DeviceItemName { get; set; }
        public IEnumerable<NetworkAttribute>? Attributes { get; set; }
    }

    public class ResponseObjectDescribe : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? TypeName { get; set; }
        public IEnumerable<ObjectMember>? Members { get; set; }
    }

    public class ResponseObjectValue : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? ValueType { get; set; }
        public object? Value { get; set; }
    }

    public class ResponseObjectChildren : ResponseMessage
    {
        public string? ObjectKind { get; set; }
        public string? ObjectPath { get; set; }
        public string? Collection { get; set; }
        public IEnumerable<string>? Items { get; set; }
    }

    public class ResponseJsonReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public JsonObject? Data { get; set; }

        // Optional well-known surfaces. Builders/validators may populate any subset
        // of these in addition to Data so AI clients can rely on a stable contract
        // for the most common use cases (errors, warnings, where the artifact landed).
        // Always check these before parsing Data.
        public string[]? Errors { get; set; }
        public string[]? Warnings { get; set; }
        public string? OutputPath { get; set; }
        public string[]? OutputFiles { get; set; }
    }

    public class ResponseGlobalLibraryProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? LibraryPath { get; set; }
        public string? ResolvedLibraryFile { get; set; }
        public string? LibraryType { get; set; }
        public IEnumerable<string>? Members { get; set; }
        public IEnumerable<string>? MasterCopies { get; set; }
        public IEnumerable<string>? Types { get; set; }
        public IEnumerable<string>? Folders { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public JsonObject? Raw { get; set; }
    }

    public class ResponseGlobalLibraryImport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? LibraryPath { get; set; }
        public string? ResolvedLibraryFile { get; set; }
        public string? MasterCopyName { get; set; }
        public string? HmiSoftwarePath { get; set; }
        public string? ScreenName { get; set; }
        public string? ImportedItemName { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public IEnumerable<string>? ReadbackItems { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public JsonObject? Raw { get; set; }
    }

    public class ResponseDevices : ResponseMessage
    {
        public IEnumerable<ResponseDeviceInfo>? Items { get; set; }
    }

    public class ResponseImportBatch : ResponseMessage
    {
        public IEnumerable<string>? Imported { get; set; }
        public IEnumerable<ImportFailure>? Failed { get; set; }
    }

    public class ResponseSeed : ResponseMessage
    {
        public IEnumerable<string>? Imported { get; set; }
        public IEnumerable<ImportFailure>? Failed { get; set; }
        public JsonObject? Placeholders { get; set; }
        public string? TempDir { get; set; }
    }

    public class ResponseDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? DeviceName { get; set; }
        public string? Family { get; set; }
        public string? MlfbUsed { get; set; }
        public string? VersionUsed { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class GsdDeviceCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? Vendor { get; set; }
        public string? ProductFamily { get; set; }
        public string? MainFamily { get; set; }
        public string? DapId { get; set; }
        public string? DapName { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public string? GsdmlPath { get; set; }
        public int? Score { get; set; }
    }

    public class HardwareCatalogCandidate
    {
        public string? Source { get; set; }
        public string? Keyword { get; set; }
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public bool? Insertable { get; set; }
        public int? Score { get; set; }
    }

    public class ResponseHardwareCatalogSearch : ResponseMessage
    {
        public string? Keyword { get; set; }
        public int? Count { get; set; }
        public IEnumerable<HardwareCatalogCandidate>? Items { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseHardwareCatalogDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? Keyword { get; set; }
        public string? DeviceName { get; set; }
        public string? PreferredText { get; set; }
        public HardwareCatalogCandidate? CandidateUsed { get; set; }
        public IEnumerable<HardwareCatalogCandidate>? Candidates { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseGsdDeviceSearch : ResponseMessage
    {
        public string? Keyword { get; set; }
        public int? Count { get; set; }
        public IEnumerable<GsdDeviceCandidate>? Items { get; set; }
    }

    public class ResponseGsdDeviceProbe : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? Keyword { get; set; }
        public string? DeviceName { get; set; }
        public string? PreferredDap { get; set; }
        public GsdDeviceCandidate? CandidateUsed { get; set; }
        public IEnumerable<GsdDeviceCandidate>? Candidates { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public string? Error { get; set; }
    }

    public class ResponseCompile : ResponseMessage
    {
        public string? State { get; set; }
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public IEnumerable<string>? Messages { get; set; }
    }

    public class ResponsePlcProgramImport : ResponseMessage
    {
        public bool? DryRun { get; set; }
        public string? BuildKind { get; set; }
        public string? GeneratedDirectory { get; set; }
        public IEnumerable<string>? WrittenFiles { get; set; }
        public IEnumerable<string>? DiscoveredTypes { get; set; }
        public IEnumerable<string>? DiscoveredTagTables { get; set; }
        public IEnumerable<string>? DiscoveredTechnologyObjects { get; set; }
        public IEnumerable<string>? DiscoveredBlocks { get; set; }
        public IEnumerable<string>? ImportedTypes { get; set; }
        public IEnumerable<string>? ImportedTagTables { get; set; }
        public IEnumerable<string>? ImportedTechnologyObjects { get; set; }
        public IEnumerable<string>? ImportedBlocks { get; set; }
        public IEnumerable<ImportFailure>? Failed { get; set; }
        public ResponseCompile? Compile { get; set; }
        public string? CapabilityDecision { get; set; }
        public IEnumerable<string>? CapabilityWarnings { get; set; }
        public IEnumerable<string>? RecommendedNextActions { get; set; }
    }

    public class ResponsePlcProgramExport : ResponseMessage
    {
        public string? ExportDir { get; set; }
        public IEnumerable<string>? ExportedTypes { get; set; }
        public IEnumerable<string>? ExportedTagTables { get; set; }
        public IEnumerable<string>? ExportedTechnologyObjects { get; set; }
        public IEnumerable<string>? ExportedBlocks { get; set; }
        public IEnumerable<ImportFailure>? Failed { get; set; }
    }

    public class ResponseCompileDiagnose : ResponseMessage
    {
        public string? State { get; set; }
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public IEnumerable<string>? Errors { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public IEnumerable<string>? Info { get; set; }
        public IEnumerable<string>? RawMessages { get; set; }
    }

    public class ResponseRepairAndCompile : ResponseMessage
    {
        public bool? Imported { get; set; }
        public string? ImportError { get; set; }
        public ResponseCompileDiagnose? Compile { get; set; }
        public IEnumerable<string>? Suggestions { get; set; }
    }
    
    public class ResponseBlocks : ResponseMessage
    {
        public IEnumerable<ResponseBlockInfo>? Items { get; set; }
    }

    public class ResponseExportBlock : ResponseMessage
    {
    }

    public class ResponseExportDeviceAml : ResponseMessage
    {
        public string? DeviceName { get; set; }
        public string? FilePath { get; set; }
        public bool Success { get; set; }
        public string? State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public IEnumerable<string>? Messages { get; set; }
    }

    public class ResponseImportBlock : ResponseMessage
    {
    }

    public class ResponseExportBlocks : ResponseMessage
    {
        public IEnumerable<ResponseBlockInfo>? Items { get; set; }
        public IEnumerable<ResponseBlockInfo>? Inconsistent { get; set; }
    }

    public class ResponseTypes : ResponseMessage
    {
        public IEnumerable<ResponseTypeInfo>? Items { get; set; }
    }

    public class ResponseExportType : ResponseMessage
    {
    }

    public class ResponseImportType : ResponseMessage
    {
    }

    public class ResponseExportTypes : ResponseMessage
    {
        public IEnumerable<ResponseTypeInfo>? Items { get; set; }
        public IEnumerable<ResponseTypeInfo>? Inconsistent { get; set; }
    }

    public class ResponseExportAsDocuments : ResponseMessage
    {
    }

    public class ResponseExportBlocksAsDocuments : ResponseMessage
    {
        public IEnumerable<ResponseBlockInfo>? Items { get; set; }
    }

    public class ResponseImportFromDocuments : ResponseMessage
    {
    }

    public class ScaffoldStep
    {
        public string? Step { get; set; }
        public string? Status { get; set; } // ok | failed | skipped
        public string? Detail { get; set; }
    }

    public class ResponseScaffold : ResponseMessage
    {
        public bool Ok { get; set; }
        public string? ProjectName { get; set; }
        public string? DirectoryPath { get; set; }
        public string? CompileState { get; set; }
        public int? CompileErrorCount { get; set; }
        public int? CompileWarningCount { get; set; }
        public List<ScaffoldStep> Steps { get; set; } = new List<ScaffoldStep>();
    }

    public class ResponseImportBlocksFromDocuments : ResponseMessage
    {
        public IEnumerable<ResponseBlockInfo>? Items { get; set; }
    }

    public class ResponseDownload : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? State { get; set; }          // Success | Information | Warning | Error
        public int? ErrorCount { get; set; }
        public int? WarningCount { get; set; }
        public string[]? Errors { get; set; }
        public string[]? Warnings { get; set; }
    }

    public class ResponseCheckDownload : ResponseMessage
    {
        public bool? Ready { get; set; }
        public bool? HasDownloadProvider { get; set; }
        public bool? HasConfiguration { get; set; }
        public bool? IsConsistent { get; set; }
        public string[]? Issues { get; set; }
    }

    public class ResponseOnlineState : ResponseMessage
    {
        // OnlineState values: Offline, Connecting, Online, Incompatible, NotReachable, Protected, Disconnecting
        public string? State { get; set; }
        public bool? IsOnline { get; set; }
        public bool? IsReachable { get; set; }
    }

    public class CompareEntry
    {
        public string? Path { get; set; }
        public string? LeftName { get; set; }
        public string? RightName { get; set; }
        public string? Status { get; set; }
        public string? Details { get; set; }
    }

    public class ResponseCompare : ResponseMessage
    {
        public bool? IsOnline { get; set; }
        public CompareEntry[]? Entries { get; set; }
        public Dictionary<string, int>? Summary { get; set; }
        public bool? Truncated { get; set; }
    }

    public class TechnologyObjectInfo
    {
        public string? Name { get; set; }
        public string? OfSystemLibElement { get; set; }   // e.g. "TO_PositioningAxis"
        public string? OfSystemLibVersion { get; set; }   // e.g. "V8.0"
        public string? TypeHint { get; set; }              // fallback when OfSystemLibElement is absent
    }

    public class ResponseTechnologyObjectList : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? SoftwarePath { get; set; }
        public int? Count { get; set; }
        public TechnologyObjectInfo[]? Items { get; set; }
    }

    /// <summary>
    /// Response shape for offline XML / fragment builders (Build*Xml, Compose*Xml).
    /// Adds a typed <see cref="Xml"/> surface so AI clients can read the generated
    /// XML directly without parsing the legacy <see cref="ResponseJsonReport.Data"/>
    /// JsonObject.
    /// </summary>
    public class ResponseXmlBuild : ResponseJsonReport
    {
        public string? Xml { get; set; }
    }
}
