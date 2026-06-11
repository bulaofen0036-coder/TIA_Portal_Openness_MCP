using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer
{
    public class Program
    {
        private const string PlcSoftwareV17Phase1ToolProfile = "plc-software-v17-phase1";
        private static readonly string DiagLogPath = Path.Combine(Path.GetTempPath(), "TiaMcpServer.log");
        private static readonly string DiagLogPathLocal = Path.Combine(AppContext.BaseDirectory, "TiaMcpServer.startup.log");
        private delegate void StructuredTextLine(StringBuilder st, params string[] parts);

        public static async Task Main(string[] args)
        {
            try
            {
                // Force stdin/stdout to UTF-8 (no BOM). Without this, on zh-CN Windows the
                // default Console encoding is GBK (CP936), which mangles Chinese project
                // names, comments, HMI labels, and any non-ASCII characters in JSON-RPC.
                try
                {
                    var utf8NoBom = new UTF8Encoding(false);
                    Console.InputEncoding  = utf8NoBom;
                    Console.OutputEncoding = utf8NoBom;
                }
                catch (Exception encEx)
                {
                    LogDiag("WARN: failed to set Console UTF-8 encoding: " + encEx.Message);
                }

                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromBaseDir;

                LogDiag($"=== {DateTime.Now:O} PID={System.Diagnostics.Process.GetCurrentProcess().Id} ===");
                LogDiag($"BaseDir: {AppContext.BaseDirectory}");
                LogDiag($"Exe: {Assembly.GetExecutingAssembly().Location}");
                LogDiag($"Args: {string.Join(" ", args)}");

                var options = CliOptions.ParseArgs(args);
                options.ToolProfile = ResolveToolProfile(options.ToolProfile);
                LogDiag(string.IsNullOrWhiteSpace(options.ToolProfile)
                    ? "Tool profile: full (default)"
                    : $"Tool profile: {options.ToolProfile}");

                // Default logging to stderr (mode 1) when the user doesn't pass --logging,
                // so errors are visible out of the box. Users can opt out with --logging 0
                // (treated as "no logging" by the switch statements below).
                if (options.Logging == null)
                {
                    options.Logging = 1;
                    LogDiag("Logging defaulted to stderr (--logging 1). Pass --logging 0 to silence, 2 for Debug output, 3 for EventLog.");
                }

                // Wire CLI --tia-portal-location into the assembly resolver. Must happen BEFORE
                // DetectTiaMajorVersion so the override participates in version detection.
                if (!string.IsNullOrWhiteSpace(options.TiaPortalLocation))
                {
                    Engineering.TiaPortalLocationOverride = options.TiaPortalLocation;
                    LogDiag($"TIA Portal location (from CLI): {options.TiaPortalLocation}");
                }

                int tiaMajorVersion;
                if (options.TiaMajorVersion.HasValue)
                {
                    tiaMajorVersion = options.TiaMajorVersion.Value;
                    LogDiag($"TIA major version (from CLI): {tiaMajorVersion}");
                }
                else
                {
                    var detected = Engineering.DetectTiaMajorVersion();
                    tiaMajorVersion = detected ?? 21;
                    LogDiag(detected.HasValue
                        ? $"TIA major version (auto-detected): {tiaMajorVersion}"
                        : $"TIA major version (default fallback): {tiaMajorVersion} — install not detected, specify --tia-major-version if wrong");
                }
                Engineering.TiaMajorVersion = tiaMajorVersion;

                // 静态自检也会枚举 MCP 工具特性，方法签名里引用的 Siemens 程序集需要先能被解析。
                // 这里只注册程序集解析器，不初始化 Openness，也不连接或打开 TIA 项目。
                AppDomain.CurrentDomain.AssemblyResolve += Engineering.Resolver;

                // V17 defaults to GUI-first because attach/launch may require an interactive Openness
                // confirmation. --without-ui is retained as an explicit advanced override.
                Engineering.ForceWithoutUserInterface = options.PortalWithoutUserInterface;
                Engineering.LaunchWithUserInterface = options.PortalWithUserInterface
                    || (Engineering.TiaMajorVersion == 17 && !options.PortalWithoutUserInterface);
                if (Engineering.ForceWithoutUserInterface)
                {
                    LogDiag("TIA Portal will launch headless (WithoutUserInterface) due to explicit --without-ui override.");
                }
                else if (Engineering.LaunchWithUserInterface)
                {
                    LogDiag(Engineering.TiaMajorVersion == 17
                        ? "TIA Portal V17 defaults to WITH user interface for interactive-safe Connect."
                        : "TIA Portal will launch WITH user interface (--with-ui); slower cold start.");
                }
                else
                {
                    LogDiag("TIA Portal will launch headless (WithoutUserInterface) for faster startup; pass --with-ui to show the GUI.");
                }

                // CLI verb dispatch: `tia gen|patch|compile|export|import|describe|prewarm|schema|version`.
                // Engine config (assembly resolver, version, headless) is already applied above, so verb
                // handlers can connect immediately. Falls through to MCP host when args[0] isn't a verb.
                if (args.Length > 0 && Cli.CliCommands.IsVerb(args[0]))
                {
                    Environment.Exit(Cli.CliCommands.Run(args));
                    return;
                }

                if (options.AnalyzeReferenceAssets)
                {
                    RunAnalyzeReferenceAssets(options);
                    return;
                }

                if (options.AnalyzeGlobalLibraryPackage)
                {
                    RunAnalyzeGlobalLibraryPackage(options);
                    return;
                }

                if (options.AnalyzeHmiTemplateReference)
                {
                    RunAnalyzeHmiTemplateReference(options);
                    return;
                }

                if (options.AnalyzeHmiComponentCatalog)
                {
                    RunAnalyzeHmiComponentCatalog(options);
                    return;
                }

                if (options.RunHmiActionScriptRecipeProbe)
                {
                    RunHmiActionScriptRecipeProbe(options);
                    return;
                }

                if (options.RunHmiActionScriptRecipeSafetySelfTest)
                {
                    RunHmiActionScriptRecipeSafetySelfTest();
                    return;
                }

                if (options.RunHmiTemplateLayoutProbe)
                {
                    RunHmiTemplateLayoutProbe(options);
                    return;
                }

                if (options.RunClassicHmiMinimalPackageProbe)
                {
                    RunClassicHmiMinimalPackageProbe(options);
                    return;
                }

                if (options.RunClassicHmiOfflineSuite)
                {
                    RunClassicHmiOfflineSuite(options);
                    return;
                }

                if (options.RunClassicHmiTemporaryImportPreflight)
                {
                    RunClassicHmiTemporaryImportPreflight(options);
                    return;
                }

                if (options.RunPlcSymbolManifestProbe)
                {
                    RunPlcSymbolManifestProbe(options);
                    return;
                }

                if (options.RunOfflineReleaseSuite)
                {
                    RunOfflineReleaseSuite(options);
                    return;
                }

                if (options.RunV2PlanCompletionAudit)
                {
                    RunV2PlanCompletionAudit(options);
                    return;
                }

                if (options.RebuildReleaseHandoff)
                {
                    RunRebuildReleaseHandoff(options);
                    return;
                }

                if (options.RunHmiTemplatePlcSyncPrecheckSuite)
                {
                    RunHmiTemplatePlcSyncPrecheckSuite(options);
                    return;
                }

                if (options.AnalyzeHmiTemplatePlcMapping)
                {
                    RunAnalyzeHmiTemplatePlcMapping(options);
                    return;
                }

                if (options.GenerateHmiTemplateMappingSkeleton)
                {
                    RunGenerateHmiTemplateMappingSkeleton(options);
                    return;
                }

                if (options.GenerateHmiTemplateSyncPrecheck &&
                    string.IsNullOrWhiteSpace(options.PlcTagTableRegex) &&
                    Math.Max(0, options.MaxPlcTagTablesToExport ?? 0) == 0)
                {
                    RunGenerateHmiTemplateSyncPrecheck(options);
                    return;
                }

                if (options.GeneratePlcBuilderFixtureReadiness)
                {
                    RunGeneratePlcBuilderFixtureReadiness(options);
                    return;
                }

                if (options.RunPlcBuilderOfflineSuite)
                {
                    RunPlcBuilderOfflineSuite(options);
                    return;
                }

                if (options.RunPlcTagTableBuilderProbe)
                {
                    RunPlcTagTableBuilderProbe(options);
                    return;
                }

                if (options.RunPlcUdtBuilderProbe)
                {
                    RunPlcUdtBuilderProbe(options);
                    return;
                }

                if (options.RunStructuredTextBuilderProbe)
                {
                    RunStructuredTextBuilderProbe(options);
                    return;
                }

                if (options.RunPlcFcBlockComposerProbe)
                {
                    RunPlcFcBlockComposerProbe(options);
                    return;
                }

                if (options.RunPlcGlobalDbBuilderProbe)
                {
                    RunPlcGlobalDbBuilderProbe(options);
                    return;
                }

                if (options.RunFlgNetCallBuilderProbe)
                {
                    RunFlgNetCallBuilderProbe(options);
                    return;
                }

                if (options.ValidateMappedHmiTemplateBindings &&
                    options.MappedHmiTemplateOfflineOnly)
                {
                    RunValidateMappedHmiTemplateBindings(options);
                    return;
                }

                if (options.RunOnlineMonitoringSafetySelfTest)
                {
                    RunOnlineMonitoringSafetySelfTest();
                    return;
                }

                if (Engineering.TiaMajorVersion >= 17)
                {
                    try
                    {
                        LogDiag($"Initializing TIA Openness API for V{Engineering.TiaMajorVersion}");
                        Openness.Initialize(Engineering.TiaMajorVersion);
                        LogDiag("TIA Openness API initialized");
                    }
                    catch (FileNotFoundException ex)
                    {
                        LogDiag("Openness.Initialize failed: FileNotFoundException");
                        LogDiag($"FileName: {ex.FileName}");
                        if (!string.IsNullOrWhiteSpace(ex.FusionLog))
                        {
                            LogDiag("FusionLog:");
                            LogDiag(ex.FusionLog);
                        }
                        throw;
                    }
                    catch (BadImageFormatException ex)
                    {
                        LogDiag("Openness.Initialize failed: BadImageFormatException (x86/x64 mismatch or corrupt dll)");
                        LogDiag(ex.ToString());
                        throw;
                    }
                }

                if (options.RunInternalPortalProbe)
                {
                    RunInternalPortalProbe(options);
                    return;
                }

                // Ensure user is in user group 'Siemens TIA Openness'.
                LogDiag("Checking Windows group membership: Siemens TIA Openness");
                var opennessUserOk = await Openness.IsUserInGroup();
                LogDiag($"Siemens TIA Openness group membership: {opennessUserOk}");
                if (opennessUserOk)
                {
                    if (options.RunFlowLightTest)
                    {
                        RunFlowLightTest(options);
                        return;
                    }

                    if (options.FixCurrentFlowBinding)
                    {
                        RunFixCurrentFlowBinding(options);
                        return;
                    }

                    if (options.ProbeS71200Device)
                    {
                        RunProbeS71200Device(options);
                        return;
                    }

                    if (options.Add1511CToCurrentProject)
                    {
                        RunAdd1511CToCurrentProject(options);
                        return;
                    }

                    if (options.ValidatePlcSclSyntax)
                    {
                        RunValidatePlcSclSyntax(options);
                        return;
                    }

                    if (options.RunMotorMinimalTest)
                    {
                        RunMotorMinimalTest(options);
                        return;
                    }

                    if (options.ValidateUnifiedHmiTemplates)
                    {
                        RunValidateUnifiedHmiTemplates(options);
                        return;
                    }

                    if (options.ValidateUnifiedHmiActionSyntaxCheck)
                    {
                        RunValidateUnifiedHmiActionSyntaxCheck(options);
                        return;
                    }

                    if (options.ValidateUnifiedHmiTemplateBindings)
                    {
                        RunValidateUnifiedHmiTemplateBindings(options);
                        return;
                    }

                    if (options.ValidateMappedHmiTemplateBindings)
                    {
                        RunValidateMappedHmiTemplateBindings(options);
                        return;
                    }

                    if (options.ValidatePlcHmiSyncMinimal)
                    {
                        RunValidatePlcHmiSyncMinimal(options);
                        return;
                    }

                    if (options.ValidatePlcChineseCommentsMinimal)
                    {
                        RunValidatePlcChineseCommentsMinimal(options);
                        return;
                    }

                    if (options.ProbeKtp700Basic)
                    {
                        RunProbeKtp700Basic(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiImport)
                    {
                        RunProbeKtp700BasicHmiImport(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiTags)
                    {
                        RunProbeKtp700BasicHmiTags(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiConnection)
                    {
                        RunProbeKtp700BasicHmiConnection(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicHmiSymbolicTags)
                    {
                        RunProbeKtp700BasicHmiSymbolicTags(options);
                        return;
                    }

                    if (options.ProbeKtp700BasicNetworking)
                    {
                        RunProbeKtp700BasicNetworking(options);
                        return;
                    }

                    if (options.ProbeCurrentKtp700HardwareHmiConnection)
                    {
                        RunProbeCurrentKtp700HardwareHmiConnection(options);
                        return;
                    }

                    if (options.ListPortalProcessProjects)
                    {
                        RunListPortalProcessProjects(options);
                        return;
                    }

                    if (options.RunCapabilitySelfTest)
                    {
                        await RunCapabilitySelfTest(options);
                        return;
                    }

                    if (options.GenerateAcceptanceReport)
                    {
                        await RunGenerateAcceptanceReport(options);
                        return;
                    }

                    if (options.GenerateErrorReport)
                    {
                        RunGenerateErrorReport(options);
                        return;
                    }

                    if (options.GenerateMonitoringReadOnlyReport)
                    {
                        RunGenerateMonitoringReadOnlyReport(options);
                        return;
                    }

                    if (options.GenerateGlobalLibraryProbeReport)
                    {
                        RunGenerateGlobalLibraryProbeReport(options);
                        return;
                    }

                    if (options.ValidateGlobalLibraryMasterCopyImport)
                    {
                        RunValidateGlobalLibraryMasterCopyImport(options);
                        return;
                    }

                    if (options.GenerateHmiTemplateSyncPrecheck)
                    {
                        RunGenerateHmiTemplateSyncPrecheck(options);
                        return;
                    }

                    if (options.ProbeHardwareHmiConnectionOwnerCandidates)
                    {
                        RunProbeHardwareHmiConnectionOwnerCandidates(options);
                        return;
                    }

                    if (options.ProbeHardwareHmiConnectionWhitelistedServices)
                    {
                        RunProbeHardwareHmiConnectionWhitelistedServices(options);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(options.SearchGsdKeyword))
                    {
                        RunSearchGsd(options);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(options.SearchHardwareCatalogKeyword))
                    {
                        RunSearchHardwareCatalog(options);
                        return;
                    }

                    if (string.Equals(options.Transport, "http", StringComparison.OrdinalIgnoreCase))
                    {
                        await RunHttpHost(options);
                    }
                    else
                    {
                        await RunStdioHost(options);
                    }
                }
                else
                {
                    LogDiag("User is not in the required group 'Siemens TIA Openness'. Exiting.");
                }
            }
            catch (Exception ex)
            {
                LogDiag("FATAL:");
                LogExceptionSafe(ex);
                if (ex is ReflectionTypeLoadException rtle && rtle.LoaderExceptions != null)
                {
                    foreach (var le in rtle.LoaderExceptions)
                    {
                        if (le == null) continue;
                        LogDiag("LoaderException:");
                        LogExceptionSafe(le);
                    }
                }
                // Re-throw so host surfaces failure, but we still have the log on disk.
                throw;
            }
        }

        public static async Task RunStdioHost(CliOptions? options)
        {
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            if (builder != null)
            {
                if (options != null && options.Logging != null)
                {
                    switch (options.Logging)
                    {
                        case 1:
                            // ATTENTION: For STDIO, logs must go to stderr!
                            builder.Logging.AddConsole(options =>
                            {
                                options.LogToStandardErrorThreshold = LogLevel.Trace;
                            });
                            break;

                        case 2:
                            // Visual Studio Debug Output / Sysinternals.DebugView
                            builder.Logging.AddDebug();
                            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
                            builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);
                            builder.Logging.AddFilter("TiaMcpServer", LogLevel.Debug);

                            // Log Level for Debug Output
                            builder.Logging.SetMinimumLevel(LogLevel.Debug);
                            break;

                        case 3:
                            // Windows Event Log
                            builder.Logging.AddEventLog();
                            break;

                        default:
                            // no logging
                            break;
                    }
                }

                try
                {
                    var mcpBuilder = builder.Services
                        .AddMcpServer()
                        .WithStdioServerTransport();
                    RegisterProfileTools(mcpBuilder, options)
                        .WithPromptsFromAssembly();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    LogDiag("WithToolsFromAssembly failed: ReflectionTypeLoadException");
                    LogDiag(ex.ToString());
                    if (ex.LoaderExceptions != null)
                    {
                        foreach (var le in ex.LoaderExceptions)
                        {
                            if (le == null) continue;
                            LogDiag("LoaderException:");
                            LogDiag(le.ToString());
                        }
                    }
                    throw;
                }

                // Register the Portal service for dependency injection
                builder.Services.AddSingleton<Portal>();

                var host = builder.Build();

                // Set the service provider for the MCP server, to retrieve Portal with injected logger
                McpServer.SetServiceProvider(host.Services);

                // Set the logger for the MCP server
                McpServer.Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer");

                // log a bit of information about the server start
                if (options != null && options.Logging != null && options.Logging > 0)
                {
                    var logger = host.Services.GetRequiredService<ILogger<Program>>();

                    logger.LogInformation($"=== TIA Portal MCP Server '{DateTime.Now.ToShortTimeString()}' ===");

                    switch (options.Logging)
                    {
                        case 1:
                            logger.LogInformation("Logging to stderr");
                            break;
                        case 2:
                            logger.LogInformation("Logging to debug output");
                            break;
                        case 3:
                            logger.LogInformation("Logging to Windows event log");
                            break;
                    }
                }

                await host.RunAsync();
            }

        }

        public static async Task RunHttpHost(CliOptions? options)
        {
            // Two blocking streams form the bidirectional channel between HTTP and the MCP server.
            var httpToMcp = new McpBlockingStream();
            var mcpToHttp = new McpBlockingStream();

            var mcpTask = Task.Run(async () =>
            {
                var builder = Host.CreateEmptyApplicationBuilder(settings: null);

                if (options?.Logging != null)
                {
                    switch (options.Logging)
                    {
                        case 1:
                            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                            break;
                        case 2:
                            builder.Logging.AddDebug();
                            builder.Logging.SetMinimumLevel(LogLevel.Debug);
                            break;
                        case 3:
                            builder.Logging.AddEventLog();
                            break;
                    }
                }

                var mcpBuilder = builder.Services
                    .AddMcpServer()
                    .WithStreamServerTransport(httpToMcp, mcpToHttp);
                RegisterProfileTools(mcpBuilder, options)
                    .WithPromptsFromAssembly();

                builder.Services.AddSingleton<TiaMcpServer.Siemens.Portal>();

                var host = builder.Build();
                McpServer.SetServiceProvider(host.Services);
                McpServer.Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer");

                await host.RunAsync();
            });

            await HttpMcpServer.Run(options, httpToMcp, mcpToHttp, LogDiag).ConfigureAwait(false);
            await mcpTask.ConfigureAwait(false);
        }

        private static string ResolveToolProfile(string? cliProfile)
        {
            var profile = string.IsNullOrWhiteSpace(cliProfile)
                ? Environment.GetEnvironmentVariable("TIA_MCP_TOOL_PROFILE")
                : cliProfile;
            return string.IsNullOrWhiteSpace(profile) ? string.Empty : profile.Trim();
        }

        private static bool IsPlcSoftwareV17Phase1Profile(CliOptions? options)
        {
            return string.Equals(options?.ToolProfile, PlcSoftwareV17Phase1ToolProfile, StringComparison.OrdinalIgnoreCase);
        }

        private static IMcpServerBuilder RegisterProfileTools(IMcpServerBuilder builder, CliOptions? options)
        {
            if (IsPlcSoftwareV17Phase1Profile(options))
            {
                LogDiag("Registering PLC-Software V17 phase1 tool surface only.");
                return builder.WithTools(new[] { typeof(PlcSoftwareV17McpTools) });
            }

            return builder.WithToolsFromAssembly();
        }

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
            var list = McpServer.ListPortalProcessProjects();

            var reportPath = Path.Combine(projectDirectory, "TIA_PORTAL_PROCESS_PROJECTS_REPORT.txt");
            var sb = new StringBuilder();
            sb.AppendLine("Probe: TIA Portal process/project listing");
            foreach (var item in list.Items ?? Array.Empty<string>()) sb.AppendLine(item);
            File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            LogDiag("TIA Portal process/project report written: " + reportPath);
        }

        private static void RunInternalPortalProbe(CliOptions options)
        {
            var action = (options.InternalPortalProbeAction ?? string.Empty).Trim().ToLowerInvariant();
            var portal = new Portal();

            object result;
            switch (action)
            {
                case "attach_pid":
                    result = RunStaProbe(() =>
                    {
                        if (!options.InternalPortalProbePid.HasValue)
                            throw new InvalidOperationException("internal portal probe requires --internal-portal-probe-pid for attach_pid");

                        var proc = global::Siemens.Engineering.TiaPortal.GetProcesses().FirstOrDefault(p => p.Id == options.InternalPortalProbePid.Value);
                        if (proc == null)
                            throw new InvalidOperationException($"TIA Portal process not found: PID={options.InternalPortalProbePid.Value}");

                        using var attached = proc.Attach();
                        return new
                        {
                            action = action,
                            success = attached != null,
                            processId = options.InternalPortalProbePid.Value,
                            apartmentState = Thread.CurrentThread.GetApartmentState().ToString()
                        };
                    });
                    break;

                case "inspect_attached_portal":
                    result = RunStaProbe(() =>
                    {
                        if (!options.InternalPortalProbePid.HasValue)
                            throw new InvalidOperationException("internal portal probe requires --internal-portal-probe-pid for inspect_attached_portal");

                        var proc = global::Siemens.Engineering.TiaPortal.GetProcesses().FirstOrDefault(p => p.Id == options.InternalPortalProbePid.Value);
                        if (proc == null)
                            throw new InvalidOperationException($"TIA Portal process not found: PID={options.InternalPortalProbePid.Value}");

                        using var attached = proc.Attach();
                        bool hasSessions = false;
                        bool hasProjects = false;
                        try
                        {
                            hasSessions = attached.LocalSessions.Any();
                        }
                        catch { }

                        try
                        {
                            hasProjects = attached.Projects.Any();
                        }
                        catch { }

                        return new
                        {
                            action = action,
                            success = true,
                            processId = options.InternalPortalProbePid.Value,
                            hasSessions,
                            hasProjects,
                            sessionProjectNames = Array.Empty<string>(),
                            projectNames = Array.Empty<string>(),
                            apartmentState = Thread.CurrentThread.GetApartmentState().ToString()
                        };
                    });
                    break;

                case "launch_with_ui":
                case "launch_without_ui":
                    result = RunStaProbe(() =>
                    {
                        var mode = action == "launch_with_ui"
                            ? global::Siemens.Engineering.TiaPortalMode.WithUserInterface
                            : global::Siemens.Engineering.TiaPortalMode.WithoutUserInterface;
                        using var launched = new global::Siemens.Engineering.TiaPortal(mode);
                        return new
                        {
                            action = action,
                            success = launched != null,
                            launchMode = mode.ToString(),
                            apartmentState = Thread.CurrentThread.GetApartmentState().ToString()
                        };
                    });
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported --internal-portal-probe-action: '{options.InternalPortalProbeAction}'");
            }

            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Console.Out.WriteLine(json);
        }

        private static object RunStaProbe(Func<object> fn)
        {
            object? result = null;
            Exception? error = null;
            var worker = new Thread(() =>
            {
                try { result = fn(); }
                catch (Exception ex) { error = ex; }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = false;
            worker.Start();
            worker.Join();

            if (error != null)
            {
                return new
                {
                    action = "probe",
                    success = false,
                    errorType = error.GetType().FullName,
                    errorMessage = error.Message,
                    apartmentState = ApartmentState.STA.ToString()
                };
            }

            return result ?? new
            {
                action = "probe",
                success = false,
                errorType = "System.InvalidOperationException",
                errorMessage = "STA probe returned null result.",
                apartmentState = ApartmentState.STA.ToString()
            };
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

        private static string MakeSafeReportFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }
            return sb.ToString();
        }

        private static void AppendJsonArrayPreview(StringBuilder md, JsonArray? array, string title, int limit)
        {
            md.AppendLine("## " + title);
            if (array == null || array.Count == 0)
            {
                md.AppendLine("- <none>");
                md.AppendLine();
                return;
            }

            foreach (var item in array.Take(limit))
            {
                md.AppendLine("- " + item);
            }
            if (array.Count > limit)
            {
                md.AppendLine("- ... " + (array.Count - limit) + " more");
            }
            md.AppendLine();
        }

        private static void AppendSectionSummary(StringBuilder md, JsonObject? parent, string key, string title)
        {
            var sections = parent?["sections"] as JsonObject;
            var section = sections?[key] as JsonObject;
            if (section == null) return;

            md.AppendLine();
            md.AppendLine("### " + title);
            md.AppendLine("- Exists: " + section["exists"]);
            md.AppendLine("- File count: " + section["fileCount"]);
            md.AppendLine("- Total bytes: " + section["totalBytes"]);
            if (section["samples"] is JsonArray samples && samples.Count > 0)
            {
                md.AppendLine("- Largest samples:");
                foreach (var item in samples.Take(5))
                {
                    var obj = item as JsonObject;
                    md.AppendLine("  - " + obj?["relativePath"] + " (" + obj?["bytes"] + " bytes)");
                }
            }
        }

        private static void SyncMotorMinimalArtifacts(string projectName, string projectDirectory, string importDir)
        {
            try
            {
                var packRoot = Path.Combine(Directory.GetCurrentDirectory(), "TIA_MCP_AI_PACK");
                var refRoot = Path.Combine(packRoot, "references", "motor_minimal_latest_fixed");
                var reportsRoot = Path.Combine(packRoot, "references", "reports");
                Directory.CreateDirectory(refRoot);
                Directory.CreateDirectory(reportsRoot);

                foreach (var file in Directory.GetFiles(importDir))
                {
                    var dest = Path.Combine(refRoot, Path.GetFileName(file));
                    File.Copy(file, dest, true);
                }

                var report = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
                if (File.Exists(report))
                {
                    File.Copy(report, Path.Combine(reportsRoot, projectName + "_REPORT.txt"), true);
                    File.Copy(report, Path.Combine(refRoot, projectName + "_REPORT.txt"), true);
                }

                var notes = new StringBuilder();
                notes.AppendLine("# Latest Motor Minimal Fixed Run");
                notes.AppendLine();
                notes.AppendLine("Project: " + projectName);
                notes.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                notes.AppendLine("Source project directory: " + projectDirectory);
                notes.AppendLine("Generator: tools\\tiaportal-mcp\\src\\TiaMcpServer\\Program.cs --run-motor-minimal-test");
                notes.AppendLine();
                notes.AppendLine("Verification focus:");
                notes.AppendLine("- PLC/HMI Profinet node connection probe is recorded in the report.");
                notes.AppendLine("- HMI connection CommunicationDriver/Partner/Station/Node readback is recorded.");
                notes.AppendLine("- HMI tags try symbolic PLC binding first; if TIA V21 does not read back PlcTag, the generator applies and verifies real absolute PLC addresses.");
                notes.AppendLine("- Buttons include pressed-state binding; STOP also attempts a click event script.");
                TryWriteText(Path.Combine(refRoot, "README.md"), notes.ToString());
            }
            catch (Exception ex)
            {
                LogDiag("SyncMotorMinimalArtifacts failed: " + ex.Message);
            }
        }

        private static void WritePlcSyntaxValidationXml(string dir)
        {
            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Local(string name) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" /></Symbol></Access>";
            string Const(string value) => $"<Access Scope=\"LiteralConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";
            string Call(string name, params string[] args)
            {
                var b = new StringBuilder();
                b.Append($"<Access Scope=\"Call\" UId=\"{U()}\"><Instruction Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\">");
                b.Append(Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(Tok(","));
                    b.Append($"<NamelessParameter UId=\"{U()}\">");
                    b.Append(args[i]);
                    b.Append("</NamelessParameter>");
                }
                b.Append(Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }
            string CallNamed(string name, params (string Name, string Value)[] args)
            {
                var b = new StringBuilder();
                b.Append($"<Access Scope=\"Call\" UId=\"{U()}\"><Instruction Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\">");
                b.Append(Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(Tok(","));
                    b.Append($"<Parameter Name=\"{System.Security.SecurityElement.Escape(args[i].Name)}\" UId=\"{U()}\">");
                    b.Append(Blank());
                    b.Append(Tok(":="));
                    b.Append(Blank());
                    b.Append(args[i].Value);
                    b.Append("</Parameter>");
                }
                b.Append(Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }
            var st = new StringBuilder();
            void Line(params string[] parts) { foreach (var p in parts) st.AppendLine(p); st.AppendLine(NL()); }

            Line(Local("OutBool"), Blank(), Tok(":="), Blank(), Local("Enable"), Blank(), Tok("AND"), Blank(), Tok("("), Local("InInt"), Blank(), Tok(">="), Blank(), Const("0"), Tok(")"), Blank(), Tok("OR"), Blank(), Tok("("), Local("Mode"), Blank(), Tok("="), Blank(), Const("2"), Tok(")"), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), Local("InInt"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("INT_TO_DINT", Local("OutInt")), Tok(";"));
            Line(Local("OutReal"), Blank(), Tok(":="), Blank(), Call("DINT_TO_REAL", Local("OutDInt")), Blank(), Tok("/"), Blank(), Const("10.0"), Tok(";"));
            Line(Local("OutWord"), Blank(), Tok(":="), Blank(), Const("16#1234"), Tok(";"));
            Line(Local("OutReal"), Blank(), Tok(":="), Blank(), Call("ABS", Local("InReal")), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("MIN", ("IN1", Local("OutInt")), ("IN2", Const("5")), ("IN3", Const("10"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("MAX", ("IN1", Local("OutInt")), ("IN2", Const("5")), ("IN3", Const("10"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("LIMIT", ("MN", Const("-100")), ("IN", Local("OutInt")), ("MX", Const("100"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("SEL", ("G", Local("Enable")), ("IN0", Const("0")), ("IN1", Local("OutInt"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("MUX", ("K", Const("1")), ("IN0", Const("0")), ("IN1", Const("10")), ("IN2", Const("20"))), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("ROUND", Local("InReal")), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("TRUNC", Local("InReal")), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("REAL_TO_DINT", Local("InReal")), Tok(";"));
            Line(Local("OutWord"), Blank(), Tok(":="), Blank(), CallNamed("SHL", ("IN", Local("OutWord")), ("N", Const("1"))), Tok(";"));
            Line(Local("OutWord"), Blank(), Tok(":="), Blank(), CallNamed("SHR", ("IN", Local("OutWord")), ("N", Const("1"))), Tok(";"));

            Line(Tok("IF"), Blank(), Tok("NOT"), Blank(), Local("Enable"), Blank(), Tok("THEN"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("ELSIF"), Blank(), Local("Mode"), Blank(), Tok("="), Blank(), Const("1"), Blank(), Tok("THEN"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("10"), Tok(";"));
            Line(Tok("ELSE"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("20"), Tok(";"));
            Line(Tok("END_IF"), Tok(";"));

            Line(Tok("CASE"), Blank(), Local("Mode"), Blank(), Tok("OF"));
            Line(Blank(2), Const("0"), Tok(":"), Blank(), Local("OutInt"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Blank(2), Const("1"), Tok(","), Blank(), Const("2"), Tok(":"), Blank(), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Blank(2), Const("3"), Tok(".."), Const("5"), Tok(":"), Blank(), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("3"), Tok(";"));
            Line(Tok("ELSE"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Const("-1"), Tok(";"));
            Line(Tok("END_CASE"), Tok(";"));

            Line(Local("acc"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("FOR"), Blank(), Local("i"), Blank(), Tok(":="), Blank(), Const("0"), Blank(), Tok("TO"), Blank(), Const("9"), Blank(), Tok("DO"));
            Line(Blank(2), Local("acc"), Blank(), Tok(":="), Blank(), Local("acc"), Blank(), Tok("+"), Blank(), Local("i"), Tok(";"));
            Line(Tok("END_FOR"), Tok(";"));

            Line(Local("i"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("WHILE"), Blank(), Local("i"), Blank(), Tok("<"), Blank(), Const("3"), Blank(), Tok("DO"));
            Line(Blank(2), Local("acc"), Blank(), Tok(":="), Blank(), Local("acc"), Blank(), Tok("+"), Blank(), Local("i"), Tok(";"));
            Line(Blank(2), Local("i"), Blank(), Tok(":="), Blank(), Local("i"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Tok("END_WHILE"), Tok(";"));

            Line(Tok("REPEAT"));
            Line(Blank(2), Local("acc"), Blank(), Tok(":="), Blank(), Local("acc"), Blank(), Tok("-"), Blank(), Const("1"), Tok(";"));
            Line(Tok("UNTIL"), Blank(), Local("acc"), Blank(), Tok("<="), Blank(), Const("0"));
            Line(Tok("END_REPEAT"), Tok(";"));

            File.WriteAllText(Path.Combine(dir, "MCP_Syntax_FC.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FC ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""Enable"" Datatype=""Bool"" /><Member Name=""Mode"" Datatype=""Int"" /><Member Name=""InInt"" Datatype=""Int"" /><Member Name=""InReal"" Datatype=""Real"" /></Section>
          <Section Name=""Output""><Member Name=""OutBool"" Datatype=""Bool"" /><Member Name=""OutInt"" Datatype=""Int"" /><Member Name=""OutDInt"" Datatype=""DInt"" /><Member Name=""OutReal"" Datatype=""Real"" /><Member Name=""OutWord"" Datatype=""Word"" /></Section>
          <Section Name=""InOut"" />
          <Section Name=""Temp""><Member Name=""i"" Datatype=""Int"" /><Member Name=""acc"" Datatype=""Int"" /></Section>
          <Section Name=""Constant"" />
          <Section Name=""Return""><Member Name=""Ret_Val"" Datatype=""Void"" /></Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>MCP_Syntax_FC</Name><Namespace /><Number>12001</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FC>
</Document>", Encoding.UTF8);
        }

        private static void WritePlcSyntaxIecFbXml(string dir)
        {
            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Local(string name) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" /></Symbol></Access>";
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

            var st = new StringBuilder();
            void Line(params string[] parts) { foreach (var p in parts) st.AppendLine(p); st.AppendLine(NL()); }

            Line(InstanceCall("rEdge", ("CLK", Local("Pulse"))), Tok(";"));
            Line(InstanceCall("fEdge", ("CLK", Local("Pulse"))), Tok(";"));
            Line(Local("Rising"), Blank(), Tok(":="), Blank(), LocalField("rEdge", "Q"), Tok(";"));
            Line(Local("Falling"), Blank(), Tok(":="), Blank(), LocalField("fEdge", "Q"), Tok(";"));
            Line(InstanceCall("tOn", ("IN", Local("Enable")), ("PT", TypedConst("T#2S"))), Tok(";"));
            Line(Local("TimerDone"), Blank(), Tok(":="), Blank(), LocalField("tOn", "Q"), Tok(";"));
            Line(Local("Elapsed"), Blank(), Tok(":="), Blank(), LocalField("tOn", "ET"), Tok(";"));

            File.WriteAllText(Path.Combine(dir, "MCP_Syntax_Iec_FB.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <HeaderVersion>1.0</HeaderVersion>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""Enable"" Datatype=""Bool"" /><Member Name=""Pulse"" Datatype=""Bool"" /></Section>
          <Section Name=""Output""><Member Name=""Rising"" Datatype=""Bool"" /><Member Name=""Falling"" Datatype=""Bool"" /><Member Name=""TimerDone"" Datatype=""Bool"" /><Member Name=""Elapsed"" Datatype=""Time"" /></Section>
          <Section Name=""InOut"" />
          <Section Name=""Static"">
            <Member Name=""rEdge"" Datatype=""R_TRIG"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member>
            <Member Name=""fEdge"" Datatype=""F_TRIG"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member>
            <Member Name=""tOn"" Datatype=""TON_TIME"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member>
          </Section>
          <Section Name=""Temp"" />
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>MCP_Syntax_Iec_FB</Name><Namespace /><Number>12002</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
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

        private static void WritePlcSyntaxValidationScl(string path)
        {
            File.WriteAllText(path, @"TYPE ""MCP_Syntax_UDT""
VERSION : 0.1
   STRUCT
      Flag : Bool;
      Count : Int;
      Level : Real;
      Stamp : Time;
   END_STRUCT;
END_TYPE

FUNCTION ""MCP_Syntax_CalcInt"" : Int
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   A : Int;
   B : Int;
END_VAR
BEGIN
   ""MCP_Syntax_CalcInt"" := #A + #B;
END_FUNCTION

FUNCTION ""MCP_Syntax_Basics"" : Void
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   Enable : Bool;
   Mode : Int;
   InInt : Int;
   InReal : Real;
END_VAR
VAR_OUTPUT
   OutBool : Bool;
   OutInt : Int;
   OutDInt : DInt;
   OutReal : Real;
   OutWord : Word;
END_VAR
VAR_TEMP
   i : Int;
   acc : Int;
   localArray : Array[0..9] of Int;
   data : ""MCP_Syntax_UDT"";
END_VAR
BEGIN
   // Assignment, boolean logic, comparison, arithmetic, and type conversion.
   #OutBool := #Enable AND (#InInt >= 0) OR (#Mode = 2);
   #OutInt := #InInt + 1;
   #OutDInt := INT_TO_DINT(#OutInt);
   #OutReal := DINT_TO_REAL(#OutDInt) / 10.0;
   #OutWord := INT_TO_WORD(#OutInt);

   // Common scalar functions.
   #OutInt := LIMIT(MN := -100, IN := #OutInt, MX := 100);
   #OutReal := ABS(#InReal);

   // IF / ELSIF / ELSE.
   IF NOT #Enable THEN
      #OutInt := 0;
   ELSIF #Mode = 1 THEN
      #OutInt := #OutInt + 10;
   ELSE
      #OutInt := #OutInt + 20;
   END_IF;

   // CASE with single values, value list, and range.
   CASE #Mode OF
      0:
         #OutInt := 0;
      1, 2:
         #OutInt := #OutInt + 1;
      3..5:
         #OutInt := #OutInt + 3;
   ELSE
      #OutInt := -1;
   END_CASE;

   // FOR loop and array indexing.
   #acc := 0;
   FOR #i := 0 TO 9 DO
      #localArray[#i] := #i;
      #acc := #acc + #localArray[#i];
   END_FOR;

   // WHILE loop.
   #i := 0;
   WHILE #i < 3 DO
      #acc := #acc + #i;
      #i := #i + 1;
   END_WHILE;

   // REPEAT loop.
   REPEAT
      #acc := #acc - 1;
   UNTIL #acc <= 0
   END_REPEAT;

   // UDT/STRUCT field access.
   #data.Flag := #OutBool;
   #data.Count := #OutInt;
   #data.Level := #OutReal;
   #data.Stamp := T#1s;
END_FUNCTION

FUNCTION_BLOCK ""MCP_Syntax_Iec""
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   Enable : Bool;
   Reset : Bool;
   Pulse : Bool;
END_VAR
VAR_OUTPUT
   Rising : Bool;
   Falling : Bool;
   TimerDone : Bool;
   Elapsed : Time;
   CountValue : Int;
END_VAR
VAR
   tOn : TON;
   cUp : CTU;
   rEdge : R_TRIG;
   fEdge : F_TRIG;
END_VAR
BEGIN
   // IEC multi-instance calls. Inputs use :=, outputs are read from instance members.
   #rEdge(CLK := #Pulse);
   #fEdge(CLK := #Pulse);
   #Rising := #rEdge.Q;
   #Falling := #fEdge.Q;

   #tOn(IN := #Enable, PT := T#2s);
   #TimerDone := #tOn.Q;
   #Elapsed := #tOn.ET;

   #cUp(CU := #rEdge.Q, R := #Reset, PV := 10);
   #CountValue := #cUp.CV;
END_FUNCTION_BLOCK

FUNCTION_BLOCK ""MCP_Syntax_Caller""
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   Enable : Bool;
   Reset : Bool;
   Pulse : Bool;
   A : Int;
   B : Int;
END_VAR
VAR_OUTPUT
   Done : Bool;
   Sum : Int;
   CountValue : Int;
END_VAR
VAR
   iec : ""MCP_Syntax_Iec"";
END_VAR
BEGIN
   // Function call with named parameters and return value.
   #Sum := ""MCP_Syntax_CalcInt""(A := #A, B := #B);

   // FB multi-instance call with output parameter assignment.
   #iec(Enable := #Enable,
        Reset := #Reset,
        Pulse := #Pulse,
        TimerDone => #Done,
        CountValue => #CountValue);
END_FUNCTION_BLOCK

DATA_BLOCK ""MCP_Syntax_DB""
{ S7_Optimized_Access := 'TRUE' }
VAR
   Enable : Bool := TRUE;
   Value : Int := 10;
   Data : ""MCP_Syntax_UDT"";
   Caller : ""MCP_Syntax_Caller"";
END_VAR
BEGIN
END_DATA_BLOCK
", Encoding.UTF8);
        }

        private static void WriteFlowLightPlcXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "FlowLightTags.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Tags.PlcTagTable ID=""0"">
    <AttributeList><Name>FlowLightTags</Name></AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID=""1"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Flow_Enable</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""2"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.5</LogicalAddress><Name>Clock_1Hz</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""3"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M1.0</LogicalAddress><Name>Clock_Last</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""4"" CompositionName=""Tags""><AttributeList><DataTypeName>Int</DataTypeName><LogicalAddress>%MW2</LogicalAddress><Name>Flow_Step</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""5"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.0</LogicalAddress><Name>Light_1</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""6"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.1</LogicalAddress><Name>Light_2</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""7"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.2</LogicalAddress><Name>Light_3</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""8"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.3</LogicalAddress><Name>Light_4</Name></AttributeList></SW.Tags.PlcTag>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>");

            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Global(string name) => $"<Access Scope=\"GlobalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\"><BooleanAttribute Name=\"HasQuotes\" UId=\"{U()}\">true</BooleanAttribute></Component></Symbol></Access>";
            string Const(string value) => $"<Access Scope=\"LiteralConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";

            var st = new System.Text.StringBuilder();
            void Line(params string[] parts) { foreach (var p in parts) st.AppendLine(p); st.AppendLine(NL()); }

            Line(Tok("IF"), Blank(), Tok("NOT"), Blank(), Global("Flow_Enable"), Blank(), Tok("THEN"));
            Line(Blank(2), Global("Flow_Step"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("ELSIF"), Blank(), Global("Clock_1Hz"), Blank(), Tok("AND"), Blank(), Tok("NOT"), Blank(), Global("Clock_Last"), Blank(), Tok("THEN"));
            Line(Blank(2), Tok("IF"), Blank(), Global("Flow_Step"), Blank(), Tok(">="), Blank(), Const("3"), Blank(), Tok("THEN"));
            Line(Blank(4), Global("Flow_Step"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Blank(2), Tok("ELSE"));
            Line(Blank(4), Global("Flow_Step"), Blank(), Tok(":="), Blank(), Global("Flow_Step"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Blank(2), Tok("END_IF"), Tok(";"));
            Line(Tok("END_IF"), Tok(";"));
            Line(Global("Clock_Last"), Blank(), Tok(":="), Blank(), Global("Clock_1Hz"), Tok(";"));
            for (var i = 1; i <= 4; i++)
            {
                Line(Global($"Light_{i}"), Blank(), Tok(":="), Blank(), Global("Flow_Enable"), Blank(), Tok("AND"), Blank(), Tok("("), Global("Flow_Step"), Blank(), Tok("="), Blank(), Const((i - 1).ToString()), Tok(")"), Tok(";"));
            }

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
</Document>");
        }

        private static void WriteClassicHmiSymbolicTagTableProbeXml(string path, string tableName, string connectionName)
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
{ClassicHmiSymbolicTagXml("1", "Motor_Start", "Bool", "1", connectionName, "DB1_MotorData.Motor.Start")}
{ClassicHmiSymbolicTagXml("2", "Motor_Stop", "Bool", "1", connectionName, "DB1_MotorData.Motor.Stop")}
{ClassicHmiSymbolicTagXml("3", "Motor_Run", "Bool", "1", connectionName, "DB1_MotorData.Motor.Run")}
{ClassicHmiSymbolicTagXml("4", "Motor_Fault", "Bool", "1", connectionName, "DB1_MotorData.Motor.Fault")}
{ClassicHmiSymbolicTagXml("5", "Counter", "Int", "2", connectionName, "DB1_MotorData.Counter")}
    </ObjectList>
  </Hmi.Tag.TagTable>
</Document>
", Encoding.UTF8);
        }

        private static string ClassicHmiSymbolicTagXml(string id, string name, string dataType, string length, string connectionName, string controllerTag)
        {
            return $@"      <Hmi.Tag.Tag ID=""{SecurityElement.Escape(id)}"" CompositionName=""Tags"">
        <AttributeList>
          <AcquisitionTriggerMode>Visible</AcquisitionTriggerMode>
          <AddressAccessMode>Symbolic</AddressAccessMode>
          <Length>{SecurityElement.Escape(length)}</Length>
          <LogicalAddress />
          <Name>{SecurityElement.Escape(name)}</Name>
        </AttributeList>
        <LinkList>
          <AcquisitionCycle TargetID=""@OpenLink"">
            <Name>1 s</Name>
          </AcquisitionCycle>
          <Connection TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(connectionName)}</Name>
          </Connection>
          <ControllerTag TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(controllerTag)}</Name>
          </ControllerTag>
          <DataType TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(dataType)}</Name>
          </DataType>
          <HmiDataType TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(dataType)}</Name>
          </HmiDataType>
        </LinkList>
      </Hmi.Tag.Tag>";
        }

        private static void RunValidateUnifiedHmiTemplates(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_HMI_Template_Validation_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var templateDirectory = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "hmi_templates")
                : options.HmiTemplateDirectory!;

            Directory.CreateDirectory(projectDirectory);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "unified_hmi_template_validation.md");
            var jsonReportPath = Path.Combine(reportDir, "unified_hmi_template_validation.json");

            LogDiag($"Unified HMI template validation: directory={projectDirectory}, project={projectName}, templates={templateDirectory}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            LogDiag($"HMI add for template validation: ok={hmi.Ok}, used={hmi.MlfbUsed}/{hmi.VersionUsed}, error={hmi.Error}");
            if (hmi.Ok != true)
            {
                throw new InvalidOperationException("Failed to add Unified HMI for template validation. Last error: " + hmi.Error);
            }

            var templateFiles = Directory.Exists(templateDirectory)
                ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(path).Equals("MotorUnifiedDesign_DemoSlate.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            if (templateFiles.Length == 0)
            {
                throw new InvalidOperationException("No Unified HMI template JSON files found in: " + templateDirectory);
            }

            var results = new List<Dictionary<string, object?>>();
            foreach (var templateFile in templateFiles)
            {
                var screenName = "Tpl_" + Regex.Replace(Path.GetFileNameWithoutExtension(templateFile), @"[^A-Za-z0-9_]+", "_");
                if (screenName.StartsWith("Tpl_unified_", StringComparison.OrdinalIgnoreCase))
                {
                    screenName = "Tpl_" + screenName.Substring("Tpl_unified_".Length);
                }
                if (screenName.Length > 44) screenName = screenName.Substring(0, 44);

                var result = new Dictionary<string, object?>
                {
                    ["template"] = templateFile,
                    ["screen"] = screenName,
                    ["applied"] = false,
                    ["screenReadback"] = false,
                    ["error"] = ""
                };

                try
                {
                    var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(templateFile, 800, 480);
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480);
                    var apply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", screenName, designJson);
                    var applyFailureCount = CountHmiApplyFailures(apply.Meta);
                    result["applied"] = true;
                    result["message"] = apply.Message ?? "";
                    result["meta"] = apply.Meta?.ToJsonString() ?? "";
                    result["applyFailures"] = applyFailureCount;
                    if (applyFailureCount > 0)
                    {
                        result["error"] = "ApplyUnifiedHmiScreenDesignJson reported failed writes: " + applyFailureCount;
                    }

                    var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                    result["screenReadback"] = screens.Any(s => string.Equals(s, screenName, StringComparison.OrdinalIgnoreCase));
                    result["screenCount"] = screens.Length;
                    LogDiag($"Template {Path.GetFileName(templateFile)}: readback={result["screenReadback"]}, screen={screenName}");
                }
                catch (Exception ex)
                {
                    result["error"] = ex.InnerException?.Message ?? ex.Message;
                    LogDiag($"Template {Path.GetFileName(templateFile)} failed: {result["error"]}");
                }

                results.Add(result);
            }

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");

            var failed = results.Where(r => !Equals(r["applied"], true) || !Equals(r["screenReadback"], true) || (r.TryGetValue("applyFailures", out var af) && Convert.ToInt32(af ?? 0) > 0) || !string.IsNullOrWhiteSpace(r["error"]?.ToString())).ToList();
            var markdown = new StringBuilder();
            markdown.AppendLine("# Unified HMI Template Validation");
            markdown.AppendLine();
            markdown.AppendLine("- Project: `" + projectName + "`");
            markdown.AppendLine("- ProjectDirectory: `" + projectDirectory + "`");
            markdown.AppendLine("- TemplateDirectory: `" + templateDirectory + "`");
            markdown.AppendLine("- Result: `" + (failed.Count == 0 ? "PASS" : "FAIL") + "`");
            markdown.AppendLine();
            markdown.AppendLine("| Template | Screen | Applied | Readback | Apply failures | Error |");
            markdown.AppendLine("|---|---|---|---|---:|---|");
            foreach (var r in results)
            {
                markdown.AppendLine("| " + Path.GetFileName(r["template"]?.ToString() ?? "") + " | " + r["screen"] + " | " + r["applied"] + " | " + r["screenReadback"] + " | " + (r.TryGetValue("applyFailures", out var applyFailures) ? applyFailures : 0) + " | " + (r["error"]?.ToString() ?? "").Replace("|", "\\|") + " |");
            }
            File.WriteAllText(reportPath, markdown.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonReportPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                projectName,
                projectDirectory,
                templateDirectory,
                passed = failed.Count == 0,
                results
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            LogDiag("Unified HMI template validation report: " + reportPath);
            if (failed.Count > 0)
            {
                throw new InvalidOperationException("Unified HMI template validation failed. Report: " + reportPath);
            }
        }

        private static void RunValidateUnifiedHmiTemplateBindings(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_HMI_Template_Binding_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var templateDirectory = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "hmi_templates")
                : options.HmiTemplateDirectory!;
            Directory.CreateDirectory(projectDirectory);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "unified_hmi_template_binding_validation.md");
            var jsonReportPath = Path.Combine(reportDir, "unified_hmi_template_binding_validation.json");

            LogDiag($"Unified HMI template binding validation: directory={projectDirectory}, project={projectName}, templates={templateDirectory}");
            McpServer.Connect();
            McpServer.CreateProject(projectDirectory, projectName);
            var plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            if (plc.Ok != true) throw new InvalidOperationException("Failed to add PLC for HMI binding validation: " + plc.Error);
            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            if (hmi.Ok != true) throw new InvalidOperationException("Failed to add Unified HMI for binding validation: " + hmi.Error);

            var connectionName = "HMI_Connection_1";
            McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1");
            McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "Template_Binding_Tags");

            var templates = Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = new List<Dictionary<string, object?>>();

            foreach (var template in templates)
            {
                var screenName = "Bind_" + Regex.Replace(Path.GetFileNameWithoutExtension(template).Replace("unified_", ""), @"[^A-Za-z0-9_]+", "_");
                if (screenName.Length > 44) screenName = screenName.Substring(0, 44);
                var currentTags = Array.Empty<HmiTemplateTagSpec>();
                var result = new Dictionary<string, object?>
                {
                    ["template"] = template,
                    ["screen"] = screenName,
                    ["screenReadback"] = false,
                    ["hmiTagsCreated"] = 0,
                    ["bindingsAttempted"] = 0,
                    ["bindingsSucceeded"] = 0,
                    ["eventsAttempted"] = 0,
                    ["eventsSucceeded"] = 0,
                    ["eventReadbacks"] = 0,
                    ["errors"] = new List<string>()
                };

                try
                {
                    var json = File.ReadAllText(template, Encoding.UTF8);
                    var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
                    var requiredTags = ReadHmiTemplateTags(root);
                    currentTags = requiredTags;
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480);
                    var apply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", screenName, HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(template, 800, 480));
                    var applyFailures = CountHmiApplyFailures(apply.Meta);
                    result["applyFailures"] = applyFailures;
                    if (applyFailures > 0)
                    {
                        ((List<string>)result["errors"]!).Add("ApplyUnifiedHmiScreenDesignJson reported failed writes: " + applyFailures);
                        results.Add(result);
                        continue;
                    }
                    var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                    result["screenReadback"] = screens.Any(s => string.Equals(s, screenName, StringComparison.OrdinalIgnoreCase));

                    foreach (var tag in requiredTags)
                    {
                        McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Template_Binding_Tags", tag.Name, tag.DataType, "PLC_1", tag.PlcTag, connectionName, tag.Address);
                        result["hmiTagsCreated"] = (int)result["hmiTagsCreated"]! + 1;
                    }

                    var tagNames = requiredTags.Select(x => x.Name).ToArray();
                    var items = (root["Items"] as JsonArray ?? root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
                    foreach (var item in items)
                    {
                        var type = item["Type"]?.ToString() ?? item["type"]?.ToString() ?? "";
                        var name = item["Name"]?.ToString() ?? item["name"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var dyn in ReadTemplateDynamizations(item))
                        {
                            if (tagNames.Contains(dyn.Tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryBind(name, dyn.PropertyName, dyn.Tag);
                            }
                        }

                        foreach (var action in ReadTemplateItemActions(item))
                        {
                            var tag = action.TargetTag;
                            if (string.IsNullOrWhiteSpace(tag))
                            {
                                tag = ExtractFirstRuntimeTag(action.Script);
                            }
                            if (tagNames.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryButtonEvent(name, action.Event, tag, action.Script);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add(ex.InnerException?.Message ?? ex.Message);
                }

                results.Add(result);

                void TryBind(string itemName, string propertyName, string tagName)
                {
                    result["bindingsAttempted"] = (int)result["bindingsAttempted"]! + 1;
                    try
                    {
                        var tag = currentTags.FirstOrDefault(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase));
                        McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", screenName, itemName, propertyName, tagName, tag?.DataType ?? "Bool", tag?.PlcTag ?? "", "");
                        McpServer.DescribeHmiScreenItem("HMI_RT_1", screenName, itemName, 80);
                        result["bindingsSucceeded"] = (int)result["bindingsSucceeded"]! + 1;
                    }
                    catch (Exception ex)
                    {
                        ((List<string>)result["errors"]!).Add($"{itemName}.{propertyName}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                void TryButtonEvent(string buttonName, string eventType, string tagName, string scriptCode)
                {
                    result["eventsAttempted"] = (int)result["eventsAttempted"]! + 1;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(eventType)) eventType = "Tapped";
                        if (string.IsNullOrWhiteSpace(scriptCode))
                        {
                            scriptCode = $"HMIRuntime.Tags.SysFct.SetBitInTag(\"{tagName}\", 0);";
                        }
                        McpServer.EnsureUnifiedHmiButtonEventHandler("HMI_RT_1", screenName, buttonName, eventType);
                        McpServer.SetUnifiedHmiButtonEventScriptCode("HMI_RT_1", screenName, buttonName, eventType, scriptCode, "", false);
                        var readback = McpServer.DescribeUnifiedHmiButtonEventScript("HMI_RT_1", screenName, buttonName, eventType, 80);
                        if ((readback.Members?.Any() ?? false) || !string.IsNullOrWhiteSpace(readback.Message))
                        {
                            result["eventReadbacks"] = (int)result["eventReadbacks"]! + 1;
                        }
                        result["eventsSucceeded"] = (int)result["eventsSucceeded"]! + 1;
                    }
                    catch (Exception ex)
                    {
                        ((List<string>)result["errors"]!).Add($"{buttonName}.{eventType}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }

            McpServer.SaveProject();
            var failed = results.Where(r => !(bool)r["screenReadback"]! || (r.TryGetValue("applyFailures", out var af) && Convert.ToInt32(af ?? 0) > 0) || (int)r["hmiTagsCreated"]! == 0 || (int)r["bindingsAttempted"]! != (int)r["bindingsSucceeded"]! || (int)r["eventsAttempted"]! != (int)r["eventsSucceeded"]!).ToList();
            WriteBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, failed.Count == 0, results);
            if (failed.Count > 0)
            {
                throw new InvalidOperationException("Unified HMI template binding validation failed. Report: " + reportPath);
            }

            void WriteBindingReport(string mdPath, string jsonPath, string projName, string projDir, string tplDir, bool passed, List<Dictionary<string, object?>> rows)
            {
                var md = new StringBuilder();
                md.AppendLine("# Unified HMI Template Binding Validation");
                md.AppendLine();
                md.AppendLine("- Project: `" + projName + "`");
                md.AppendLine("- ProjectDirectory: `" + projDir + "`");
                md.AppendLine("- TemplateDirectory: `" + tplDir + "`");
                md.AppendLine("- Result: `" + (passed ? "PASS" : "FAIL") + "`");
                md.AppendLine();
                md.AppendLine("| Template | Screen | Apply failures | Tags | Bindings | Events | Event Readback | Errors |");
                md.AppendLine("|---|---|---:|---:|---:|---:|---:|---|");
                foreach (var r in rows)
                {
                    var errs = string.Join("<br>", ((List<string>)r["errors"]!).Select(e => e.Replace("|", "\\|")));
                    md.AppendLine("| " + Path.GetFileName(r["template"]?.ToString() ?? "") + " | " + r["screen"] + " | " + (r.TryGetValue("applyFailures", out var applyFailures) ? applyFailures : 0) + " | " + r["hmiTagsCreated"] + " | " + r["bindingsSucceeded"] + "/" + r["bindingsAttempted"] + " | " + r["eventsSucceeded"] + "/" + r["eventsAttempted"] + " | " + r["eventReadbacks"] + " | " + errs + " |");
                }
                File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new { projectName = projName, projectDirectory = projDir, templateDirectory = tplDir, passed, results = rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }

        private sealed class HmiTemplateTagSpec
        {
            public string Name { get; set; } = "";
            public string DataType { get; set; } = "Bool";
            public string PlcTag { get; set; } = "";
            public string Address { get; set; } = "";
        }

        private sealed class HmiTemplateDynamizationSpec
        {
            public string PropertyName { get; set; } = "";
            public string Tag { get; set; } = "";
        }

        private sealed class HmiTemplateActionSpec
        {
            public string Event { get; set; } = "Tapped";
            public string TargetTag { get; set; } = "";
            public string Script { get; set; } = "";
        }

        private static HmiTemplateTagSpec[] ReadHmiTemplateTags(JsonObject root)
        {
            var required = root["RequiredTags"] as JsonArray ?? root["requiredTags"] as JsonArray ?? new JsonArray();
            return required.OfType<JsonObject>()
                .Select(tag => new HmiTemplateTagSpec
                {
                    Name = tag["Name"]?.ToString() ?? tag["name"]?.ToString() ?? "",
                    DataType = tag["DataType"]?.ToString() ?? tag["dataType"]?.ToString() ?? "Bool",
                    PlcTag = tag["PlcTag"]?.ToString() ?? tag["plcTag"]?.ToString() ?? "",
                    Address = tag["Address"]?.ToString() ?? tag["address"]?.ToString() ?? ""
                })
                .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
                .ToArray();
        }

        private static HmiTemplateDynamizationSpec[] ReadTemplateDynamizations(JsonObject item)
        {
            var result = new List<HmiTemplateDynamizationSpec>();
            var dyns = item["Dynamizations"] as JsonObject ?? item["dynamizations"] as JsonObject;
            if (dyns == null) return result.ToArray();

            foreach (var dyn in dyns)
            {
                if (dyn.Value is not JsonObject cfg) continue;
                var property = dyn.Key;
                var suffix = ".TagDynamization";
                if (property.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    property = property.Substring(0, property.Length - suffix.Length);
                }

                var tag = cfg["Tag"]?.ToString() ?? cfg["tag"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(property) || string.IsNullOrWhiteSpace(tag)) continue;
                result.Add(new HmiTemplateDynamizationSpec { PropertyName = property, Tag = tag });
            }

            return result.ToArray();
        }

        private static HmiTemplateActionSpec[] ReadTemplateItemActions(JsonObject item)
        {
            var actions = item["Actions"] as JsonArray ?? item["actions"] as JsonArray ?? new JsonArray();
            return actions.OfType<JsonObject>()
                .Select(action => new HmiTemplateActionSpec
                {
                    Event = action["Event"]?.ToString() ?? action["event"]?.ToString() ?? "Tapped",
                    TargetTag = action["TargetTag"]?.ToString() ?? action["targetTag"]?.ToString() ?? "",
                    Script = action["Script"]?.ToString() ?? action["script"]?.ToString() ?? ""
                })
                .ToArray();
        }

        private static string ExtractFirstRuntimeTag(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return "";
            var match = Regex.Match(script, @"Tags\.SysFct\.\w+\(\s*""([^""]+)""", RegexOptions.IgnoreCase);
            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : "";
        }

        private static int CountHmiApplyFailures(JsonObject? meta)
        {
            if (meta == null) return 0;
            if (meta["success"] is JsonValue successValue && successValue.TryGetValue<bool>(out var success) && !success) return 1;
            if (meta["failed"] is JsonArray failed) return failed.Count;
            return 0;
        }

        private static void RunValidateMappedHmiTemplateBindings(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_Mapped_HMI_Template_Binding_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var templateDirectory = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var plcExportDirectory = options.PlcExportDirectory ?? "";
            var mappingPath = options.HmiTemplateMappingPath ?? "";
            var tiaStepTimeoutSeconds = Math.Max(15, options.TiaStepTimeoutSeconds ?? 90);

            if (string.IsNullOrWhiteSpace(mappingPath))
            {
                throw new InvalidOperationException("--hmi-template-mapping-path is required for mapped HMI template binding validation.");
            }

            Directory.CreateDirectory(projectDirectory);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var mappedTemplateDir = Path.Combine(reportDir, "mapped_templates");
            Directory.CreateDirectory(mappedTemplateDir);
            var reportPath = Path.Combine(reportDir, "mapped_hmi_template_binding_validation.md");
            var jsonReportPath = Path.Combine(reportDir, "mapped_hmi_template_binding_validation.json");

            var templates = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, "", "");
            var templateArray = templates["templates"] as JsonArray ?? new JsonArray();
            var mappingFile = LoadHmiTemplateMappingFile(mappingPath);
            var effectiveTemplates = ApplyHmiTemplateMapping(templateArray, mappingFile);
            var plcExportCatalog = AnalyzePlcExportDirectory(plcExportDirectory);
            var plcSymbolCatalog = BuildPlcSymbolCatalog(plcExportCatalog);
            var plcSymbols = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in (plcExportCatalog["symbols"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                plcSymbols.Add(symbol);
            }
            var precheck = BuildHmiTemplateSyncPrecheck(effectiveTemplates, plcSymbols, plcSymbolCatalog);
            var readyTemplateNames = new HashSet<string>(
                precheck
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["status"]?.ToString(), "plc-symbols-present", StringComparison.OrdinalIgnoreCase)
                                && TemplateAllRequiredTagsMapped(effectiveTemplates, x["templateName"]?.ToString() ?? ""))
                    .Select(x => x["templateName"]?.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase);

            var templateFiles = Directory.Exists(templateDirectory)
                ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            var mappedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<Dictionary<string, object?>>();

            foreach (var templateNode in effectiveTemplates.OfType<JsonObject>())
            {
                var templateName = templateNode["templateName"]?.ToString() ?? "";
                var templateFile = templateNode["file"]?.ToString() ?? "";
                var precheckRow = precheck.OfType<JsonObject>().FirstOrDefault(x => string.Equals(x["templateName"]?.ToString(), templateName, StringComparison.OrdinalIgnoreCase));
                if (!readyTemplateNames.Contains(templateName))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["templateName"] = templateName,
                        ["template"] = templateFile,
                        ["screen"] = "",
                        ["status"] = "skipped",
                        ["reason"] = TemplateAllRequiredTagsMapped(effectiveTemplates, templateName)
                            ? "PLC symbol precheck failed."
                            : "RequiredTags are not fully mapped by explicit mapping file.",
                        ["precheckStatus"] = precheckRow?["status"]?.ToString() ?? "",
                        ["missingPlcSymbols"] = (precheckRow?["missingRootSymbols"] as JsonArray)?.Count ?? 0,
                        ["hmiTagsCreated"] = 0,
                        ["bindingsAttempted"] = 0,
                        ["bindingsSucceeded"] = 0,
                        ["eventsAttempted"] = 0,
                        ["eventsSucceeded"] = 0,
                        ["eventReadbacks"] = 0,
                        ["errors"] = new List<string>()
                    });
                    continue;
                }

                var originalPath = templateFiles.FirstOrDefault(path => string.Equals(path, templateFile, StringComparison.OrdinalIgnoreCase))
                                   ?? templateFiles.FirstOrDefault(path => string.Equals(ReadTemplateNameFromFile(path), templateName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(originalPath))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["templateName"] = templateName,
                        ["template"] = templateFile,
                        ["screen"] = "",
                        ["status"] = "failed",
                        ["reason"] = "Template file not found.",
                        ["precheckStatus"] = precheckRow?["status"]?.ToString() ?? "",
                        ["missingPlcSymbols"] = 0,
                        ["hmiTagsCreated"] = 0,
                        ["bindingsAttempted"] = 0,
                        ["bindingsSucceeded"] = 0,
                        ["eventsAttempted"] = 0,
                        ["eventsSucceeded"] = 0,
                        ["eventReadbacks"] = 0,
                        ["errors"] = new List<string> { "Template file not found for " + templateName }
                    });
                    continue;
                }

                var mappedPath = Path.Combine(mappedTemplateDir, Path.GetFileNameWithoutExtension(originalPath) + ".mapped.json");
                WriteMappedTemplateFile(originalPath, templateNode, mappedPath);
                mappedFiles[templateName] = mappedPath;
                results.Add(new Dictionary<string, object?>
                {
                    ["templateName"] = templateName,
                    ["template"] = mappedPath,
                    ["screen"] = "",
                    ["status"] = options.MappedHmiTemplateOfflineOnly ? "offline-gate-pass" : "ready-for-tia-validation",
                    ["reason"] = "Explicit mapping and PLC export full-symbol precheck passed.",
                    ["precheckStatus"] = precheckRow?["status"]?.ToString() ?? "",
                    ["missingPlcSymbols"] = 0,
                    ["hmiTagsCreated"] = 0,
                    ["bindingsAttempted"] = 0,
                    ["bindingsSucceeded"] = 0,
                    ["eventsAttempted"] = 0,
                    ["eventsSucceeded"] = 0,
                    ["eventReadbacks"] = 0,
                    ["errors"] = new List<string>()
                });
            }

            if (mappedFiles.Count == 0)
            {
                WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, "No fully mapped template passed PLC symbol precheck.", mappingFile, plcExportCatalog, precheck, results);
                throw new InvalidOperationException("No fully mapped template passed PLC symbol precheck. Report: " + reportPath);
            }

            if (options.MappedHmiTemplateOfflineOnly)
            {
                WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, true, "Offline mapped-template gate passed. TIA temporary-project validation was intentionally skipped.", mappingFile, plcExportCatalog, precheck, results);
                return;
            }

            WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, "PLC export precheck passed for mapped templates; TIA temporary-project validation is about to start.", mappingFile, plcExportCatalog, precheck, results);
            LogDiag($"Mapped HMI template binding validation: project={projectName}, mappedTemplates={mappedFiles.Count}");
            if (!TryRunMappedTiaStep("Connect", tiaStepTimeoutSeconds, () => McpServer.Connect(), results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at TIA Connect. Report: " + reportPath);
            }
            if (!TryRunMappedTiaStep("CreateProject", tiaStepTimeoutSeconds, () => McpServer.CreateProject(projectDirectory, projectName), results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at CreateProject. Report: " + reportPath);
            }
            ResponseDeviceProbe plc = null!;
            if (!TryRunMappedTiaStep("Add PLC", tiaStepTimeoutSeconds, () =>
                {
                    plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
                    if (plc.Ok != true) throw new InvalidOperationException("Failed to add PLC for mapped HMI binding validation: " + plc.Error);
                    return plc;
                }, results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at Add PLC. Report: " + reportPath);
            }
            LogDiag($"Mapped HMI template binding validation: Add PLC completed ok={plc.Ok}, used={plc.MlfbUsed}/{plc.VersionUsed}");
            ResponseDeviceProbe hmi = null!;
            if (!TryRunMappedTiaStep("Add HMI", tiaStepTimeoutSeconds, () =>
                {
                    hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
                    if (hmi.Ok != true) throw new InvalidOperationException("Failed to add Unified HMI for mapped HMI binding validation: " + hmi.Error);
                    return hmi;
                }, results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at Add HMI. Report: " + reportPath);
            }
            LogDiag($"Mapped HMI template binding validation: Add HMI completed ok={hmi.Ok}, used={hmi.MlfbUsed}/{hmi.VersionUsed}");

            var connectionName = "Mapped_HMI_Connection_1";
            if (!TryRunMappedTiaStep("Ensure HMI connection/tag table", tiaStepTimeoutSeconds, () =>
                {
                    McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1");
                    McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "Mapped_Template_Tags");
                    return "ok";
                }, results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at HMI connection/tag table setup. Report: " + reportPath);
            }

            foreach (var kv in mappedFiles)
            {
                ValidateOneMappedTemplate(kv.Key, kv.Value, connectionName, results);
            }

            McpServer.SaveProject();
            var failed = results.Where(r => string.Equals(r["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase)).ToList();
            var passed = failed.Count == 0 && results.Any(r => string.Equals(r["status"]?.ToString(), "validated", StringComparison.OrdinalIgnoreCase));
            WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, passed, "", mappingFile, plcExportCatalog, precheck, results);
            if (!passed)
            {
                throw new InvalidOperationException("Mapped HMI template binding validation failed. Report: " + reportPath);
            }

            void ValidateOneMappedTemplate(string templateName, string templateFile, string connectionNameLocal, List<Dictionary<string, object?>> rows)
            {
                rows.RemoveAll(r => string.Equals(r["templateName"]?.ToString(), templateName, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(r["status"]?.ToString(), "ready-for-tia-validation", StringComparison.OrdinalIgnoreCase));
                var screenName = "Map_" + Regex.Replace(templateName, @"[^A-Za-z0-9_]+", "_");
                if (screenName.Length > 44) screenName = screenName.Substring(0, 44);
                var currentTags = Array.Empty<HmiTemplateTagSpec>();
                var result = new Dictionary<string, object?>
                {
                    ["templateName"] = templateName,
                    ["template"] = templateFile,
                    ["screen"] = screenName,
                    ["status"] = "validated",
                    ["reason"] = "Explicit mapping and PLC export precheck passed.",
                    ["precheckStatus"] = "plc-symbols-present",
                    ["missingPlcSymbols"] = 0,
                    ["screenReadback"] = false,
                    ["applyFailures"] = 0,
                    ["hmiTagsCreated"] = 0,
                    ["bindingsAttempted"] = 0,
                    ["bindingsSucceeded"] = 0,
                    ["eventsAttempted"] = 0,
                    ["eventsSucceeded"] = 0,
                    ["eventReadbacks"] = 0,
                    ["errors"] = new List<string>()
                };

                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(templateFile, Encoding.UTF8))?.AsObject() ?? new JsonObject();
                    currentTags = ReadHmiTemplateTags(root);
                    LogDiag($"Mapped HMI template binding validation: Ensure screen start template={templateName}, screen={screenName}");
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480);
                    LogDiag($"Mapped HMI template binding validation: Apply screen start template={templateName}, screen={screenName}");
                    var apply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", screenName, HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(templateFile, 800, 480));
                    var applyFailures = CountHmiApplyFailures(apply.Meta);
                    result["applyFailures"] = applyFailures;
                    if (applyFailures > 0)
                    {
                        ((List<string>)result["errors"]!).Add("ApplyUnifiedHmiScreenDesignJson reported failed writes: " + applyFailures);
                    }

                    var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                    result["screenReadback"] = screens.Any(s => string.Equals(s, screenName, StringComparison.OrdinalIgnoreCase));
                    LogDiag($"Mapped HMI template binding validation: Screen readback template={templateName}, readback={result["screenReadback"]}, applyFailures={applyFailures}");

                    foreach (var tag in currentTags)
                    {
                        LogDiag($"Mapped HMI template binding validation: Ensure HMI tag {tag.Name}->{tag.PlcTag}");
                        McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Mapped_Template_Tags", tag.Name, tag.DataType, "PLC_1", tag.PlcTag, connectionNameLocal, tag.Address);
                        result["hmiTagsCreated"] = (int)result["hmiTagsCreated"]! + 1;
                    }

                    var tagNames = currentTags.Select(x => x.Name).ToArray();
                    var items = (root["Items"] as JsonArray ?? root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
                    foreach (var item in items)
                    {
                        var name = item["Name"]?.ToString() ?? item["name"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var dyn in ReadTemplateDynamizations(item))
                        {
                            if (tagNames.Contains(dyn.Tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryBindMapped(result, currentTags, screenName, name, dyn.PropertyName, dyn.Tag);
                            }
                        }

                        foreach (var action in ReadTemplateItemActions(item))
                        {
                            var tag = action.TargetTag;
                            if (string.IsNullOrWhiteSpace(tag))
                            {
                                tag = ExtractFirstRuntimeTag(action.Script);
                            }
                            if (tagNames.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryButtonEventMapped(result, screenName, name, action.Event, tag, action.Script);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add(ex.InnerException?.Message ?? ex.Message);
                }

                var errors = (List<string>)result["errors"]!;
                if (!Equals(result["screenReadback"], true)
                    || Convert.ToInt32(result["applyFailures"] ?? 0) > 0
                    || Convert.ToInt32(result["hmiTagsCreated"] ?? 0) == 0
                    || Convert.ToInt32(result["bindingsAttempted"] ?? 0) != Convert.ToInt32(result["bindingsSucceeded"] ?? 0)
                    || Convert.ToInt32(result["eventsAttempted"] ?? 0) != Convert.ToInt32(result["eventsSucceeded"] ?? 0)
                    || errors.Count > 0)
                {
                    result["status"] = "failed";
                }
                rows.Add(result);
            }

            void TryBindMapped(Dictionary<string, object?> result, HmiTemplateTagSpec[] currentTags, string screenName, string itemName, string propertyName, string tagName)
            {
                result["bindingsAttempted"] = (int)result["bindingsAttempted"]! + 1;
                try
                {
                    var tag = currentTags.FirstOrDefault(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase));
                    McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", screenName, itemName, propertyName, tagName, tag?.DataType ?? "Bool", tag?.PlcTag ?? "", "");
                    McpServer.DescribeHmiScreenItem("HMI_RT_1", screenName, itemName, 80);
                    result["bindingsSucceeded"] = (int)result["bindingsSucceeded"]! + 1;
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add($"{itemName}.{propertyName}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            void TryButtonEventMapped(Dictionary<string, object?> result, string screenName, string buttonName, string eventType, string tagName, string scriptCode)
            {
                result["eventsAttempted"] = (int)result["eventsAttempted"]! + 1;
                try
                {
                    if (string.IsNullOrWhiteSpace(eventType)) eventType = "Tapped";
                    if (string.IsNullOrWhiteSpace(scriptCode))
                    {
                        scriptCode = $"HMIRuntime.Tags.SysFct.SetBitInTag(\"{tagName}\", 0);";
                    }
                    McpServer.EnsureUnifiedHmiButtonEventHandler("HMI_RT_1", screenName, buttonName, eventType);
                    McpServer.SetUnifiedHmiButtonEventScriptCode("HMI_RT_1", screenName, buttonName, eventType, scriptCode, "", false);
                    var readback = McpServer.DescribeUnifiedHmiButtonEventScript("HMI_RT_1", screenName, buttonName, eventType, 80);
                    if ((readback.Members?.Any() ?? false) || !string.IsNullOrWhiteSpace(readback.Message))
                    {
                        result["eventReadbacks"] = (int)result["eventReadbacks"]! + 1;
                    }
                    result["eventsSucceeded"] = (int)result["eventsSucceeded"]! + 1;
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add($"{buttonName}.{eventType}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        private static bool TemplateAllRequiredTagsMapped(JsonArray effectiveTemplates, string templateName)
        {
            var template = effectiveTemplates
                .OfType<JsonObject>()
                .FirstOrDefault(x => string.Equals(x["templateName"]?.ToString(), templateName, StringComparison.OrdinalIgnoreCase));
            if (template == null) return false;
            var required = template["requiredTags"] as JsonArray ?? new JsonArray();
            if (required.Count == 0) return false;
            return required
                .OfType<JsonObject>()
                .All(tag => string.Equals(tag["MappedBy"]?.ToString(), "hmi-template-mapping-file", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(tag["PlcTag"]?.ToString()));
        }

        private static string ReadTemplateNameFromFile(string path)
        {
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                return json?["TemplateName"]?.ToString() ?? Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(path);
            }
        }

        private static void WriteMappedTemplateFile(string originalPath, JsonObject effectiveTemplate, string outputPath)
        {
            var root = JsonNode.Parse(File.ReadAllText(originalPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidOperationException("HMI template JSON root must be an object: " + originalPath);
            var mappedByTag = (effectiveTemplate["requiredTags"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToDictionary(x => x["Name"]?.ToString() ?? "", x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var tagNode in root["RequiredTags"] as JsonArray ?? new JsonArray())
            {
                if (tagNode is not JsonObject tag) continue;
                var name = tag["Name"]?.ToString() ?? "";
                if (!mappedByTag.TryGetValue(name, out var mappedTag)) continue;
                var mapped = mappedTag["PlcTag"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(mapped)) continue;
                tag["OriginalPlcTag"] = tag["PlcTag"]?.ToString() ?? "";
                tag["PlcTag"] = mapped;
                tag["MappedBy"] = "hmi-template-mapping-file";
                tag["MappingStatus"] = mappedTag["MappingStatus"]?.ToString() ?? "";
                tag["MappingSource"] = mappedTag["MappingSource"]?.ToString() ?? "";
                tag["DeterministicRule"] = mappedTag["DeterministicRule"]?.ToString() ?? "";
                tag["RuleEvidence"] = mappedTag["RuleEvidence"]?.ToString() ?? "";
                tag["Comment"] = "中文说明：此变量来自显式映射文件，已先通过离线 PLC 导出符号预检。";
            }
            File.WriteAllText(outputPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
        }

        private static bool TryRunMappedTiaStep<T>(
            string stepName,
            int timeoutSeconds,
            Func<T> action,
            List<Dictionary<string, object?>> rows,
            string reportPath,
            string jsonReportPath,
            string projectName,
            string projectDirectory,
            string templateDirectory,
            string mappingPath,
            string plcExportDirectory,
            string mappedTemplateDir,
            JsonObject mappingFile,
            JsonObject plcExportCatalog,
            JsonArray precheck)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(15, timeoutSeconds));
            LogDiag($"Mapped HMI template binding validation: {stepName} start, timeoutSeconds={timeout.TotalSeconds:0}");
            try
            {
                var task = Task.Run(action);
                if (!task.Wait(timeout))
                {
                    AddMappedTiaStepFailure(rows, stepName, "timeout", $"TIA step timed out after {timeout.TotalSeconds:0} seconds.");
                    WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, $"TIA temporary-project validation stopped at `{stepName}` because the step timed out.", mappingFile, plcExportCatalog, precheck, rows);
                    LogDiag($"Mapped HMI template binding validation: {stepName} timeout after {timeout.TotalSeconds:0}s");
                    return false;
                }

                if (task.IsFaulted)
                {
                    var message = task.Exception?.GetBaseException().Message ?? "Unknown TIA step failure.";
                    AddMappedTiaStepFailure(rows, stepName, "failed", message);
                    WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, $"TIA temporary-project validation failed at `{stepName}`.", mappingFile, plcExportCatalog, precheck, rows);
                    LogDiag($"Mapped HMI template binding validation: {stepName} failed: {message}");
                    return false;
                }

                LogDiag($"Mapped HMI template binding validation: {stepName} completed");
                return true;
            }
            catch (Exception ex)
            {
                var message = ex.InnerException?.Message ?? ex.Message;
                AddMappedTiaStepFailure(rows, stepName, "failed", message);
                WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, $"TIA temporary-project validation failed at `{stepName}`.", mappingFile, plcExportCatalog, precheck, rows);
                LogDiag($"Mapped HMI template binding validation: {stepName} failed: {message}");
                return false;
            }
        }

        private static void AddMappedTiaStepFailure(List<Dictionary<string, object?>> rows, string stepName, string status, string detail)
        {
            rows.RemoveAll(r => string.Equals(r["templateName"]?.ToString(), "__tia_step__", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(r["screen"]?.ToString(), stepName, StringComparison.OrdinalIgnoreCase));
            rows.Add(new Dictionary<string, object?>
            {
                ["templateName"] = "__tia_step__",
                ["template"] = "",
                ["screen"] = stepName,
                ["status"] = status,
                ["reason"] = detail,
                ["precheckStatus"] = "",
                ["missingPlcSymbols"] = 0,
                ["hmiTagsCreated"] = 0,
                ["bindingsAttempted"] = 0,
                ["bindingsSucceeded"] = 0,
                ["eventsAttempted"] = 0,
                ["eventsSucceeded"] = 0,
                ["eventReadbacks"] = 0,
                ["errors"] = new List<string> { detail }
            });
        }

        private static void WriteMappedBindingReport(
            string mdPath,
            string jsonPath,
            string projectName,
            string projectDirectory,
            string templateDirectory,
            string mappingPath,
            string plcExportDirectory,
            string mappedTemplateDirectory,
            bool passed,
            string failureReason,
            JsonObject mappingFile,
            JsonObject plcExportCatalog,
            JsonArray precheck,
            List<Dictionary<string, object?>> rows)
        {
            var md = new StringBuilder();
            md.AppendLine("# Mapped HMI Template Binding Validation");
            md.AppendLine();
            md.AppendLine("- Project: `" + projectName + "`");
            md.AppendLine("- ProjectDirectory: `" + projectDirectory + "`");
            md.AppendLine("- TemplateDirectory: `" + templateDirectory + "`");
            md.AppendLine("- MappingFile: `" + mappingPath + "`");
            md.AppendLine("- PlcExportDirectory: `" + plcExportDirectory + "`");
            md.AppendLine("- MappedTemplateDirectory: `" + mappedTemplateDirectory + "`");
            md.AppendLine("- Result: `" + (passed ? "PASS" : "FAIL") + "`");
            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                md.AppendLine("- " + (passed ? "Note" : "FailureReason") + ": " + failureReason);
            }
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- Only explicit `MappedPlcTag` entries are used; candidate suggestions are ignored.");
            md.AppendLine("- PLC symbols must pass offline full-symbol precheck before HMI tags/events are created.");
            md.AppendLine("- This validation writes only a temporary validation project and does not modify the delivery package.");
            md.AppendLine();
            md.AppendLine("## Inputs");
            md.AppendLine("- Mapping loaded: " + mappingFile["loaded"]);
            md.AppendLine("- Effective mappings: " + ((mappingFile["entries"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- PLC export files scanned: " + plcExportCatalog["filesScanned"]);
            md.AppendLine("- PLC symbols: " + plcExportCatalog["symbolCount"]);
            md.AppendLine();
            md.AppendLine("## PLC Precheck");
            foreach (var node in precheck.OfType<JsonObject>())
            {
                md.AppendLine("- " + node["templateName"] + ": " + node["status"] + ", missing=" + ((node["missingRootSymbols"] as JsonArray)?.Count ?? 0));
            }
            md.AppendLine();
            md.AppendLine("## HMI Validation");
            md.AppendLine("| Template | Status | Screen | Tags | Bindings | Events | Event Readback | Reason | Errors |");
            md.AppendLine("|---|---|---|---:|---:|---:|---:|---|---|");
            foreach (var r in rows)
            {
                var errs = r.TryGetValue("errors", out var errObj) && errObj is List<string> errList
                    ? string.Join("<br>", errList.Select(e => e.Replace("|", "\\|")))
                    : "";
                md.AppendLine("| " + EscapeMarkdownCell(r["templateName"]?.ToString() ?? "") +
                              " | " + EscapeMarkdownCell(r["status"]?.ToString() ?? "") +
                              " | " + EscapeMarkdownCell(r["screen"]?.ToString() ?? "") +
                              " | " + r["hmiTagsCreated"] +
                              " | " + r["bindingsSucceeded"] + "/" + r["bindingsAttempted"] +
                              " | " + r["eventsSucceeded"] + "/" + r["eventsAttempted"] +
                              " | " + r["eventReadbacks"] +
                              " | " + EscapeMarkdownCell(r["reason"]?.ToString() ?? "") +
                              " | " + errs + " |");
            }
            md.AppendLine();
            md.AppendLine("## Release Readiness Gate");
            md.AppendLine("- A template is reusable only after it has explicit mapping, PLC symbol precheck, HMI tag creation, screen apply, dynamization binding, event binding, and readback evidence.");
            md.AppendLine("- Templates listed as skipped still need mapping rules or manual mapping before delivery-package synchronization.");

            File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                projectName,
                projectDirectory,
                templateDirectory,
                mappingPath,
                plcExportDirectory,
                mappedTemplateDirectory,
                passed,
                failureReason,
                mappingFile,
                plcExportCatalog,
                precheck,
                results = rows
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }

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

        private static Assembly? ResolveFromBaseDir(object? sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                var name = requested.Name ?? string.Empty;
                if (!name.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var candidate = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void LogDiag(string message)
        {
            // Console may be swallowed by host; always persist to %TEMP%.
            try { Console.Error.WriteLine(message); } catch { }
            try { File.AppendAllText(DiagLogPath, message + Environment.NewLine); } catch { }
            try { File.AppendAllText(DiagLogPathLocal, message + Environment.NewLine); } catch { }
        }

        private static void LogExceptionSafe(Exception ex)
        {
            try
            {
                LogDiag(ex.GetType().FullName + ": " + (ex.Message ?? ""));
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    LogDiag(ex.StackTrace);
                }
                if (ex.InnerException != null)
                {
                    LogDiag("InnerException:");
                    LogExceptionSafe(ex.InnerException);
                }
            }
            catch
            {
                try { LogDiag("Exception logging failed; original exception type: " + ex.GetType().FullName); } catch { }
            }
        }
    }
}
