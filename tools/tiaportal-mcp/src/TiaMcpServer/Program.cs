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
using TiaMcpServer.License;

namespace TiaMcpServer
{
    public partial class Program
    {
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

                // Default logging to stderr (mode 1) when the user doesn't pass --logging,
                // so errors are visible out of the box. Users can opt out with --logging 0
                // (treated as "no logging" by the switch statements below).
                if (options.Logging == null)
                {
                    options.Logging = 1;
                    LogDiag("Logging defaulted to stderr (--logging 1). Pass --logging 0 to silence, 2 for Debug output, 3 for EventLog.");
                }

                // --show-machine-id: print hardware fingerprint and exit.
                // Must happen BEFORE any TIA-specific initialization.
                if (options.ShowMachineId)
                {
                    var machineId = MachineId.Get();
                    Console.WriteLine(machineId);
                    LogDiag($"MachineId: {machineId}");
                    return;
                }

                // Wire CLI --tia-portal-location into the assembly resolver. Must happen BEFORE
                // DetectTiaMajorVersion so the override participates in version detection.
                if (!string.IsNullOrWhiteSpace(options.TiaPortalLocation))
                {
                    Engineering.TiaPortalLocationOverride = options.TiaPortalLocation;
                    LogDiag($"TIA Portal location (from CLI): {options.TiaPortalLocation}");
                }

                int tiaMajorVersion;
                bool tiaVersionReliable; // explicit CLI arg or a positive registry detection (not the blind fallback)
                if (options.TiaMajorVersion.HasValue)
                {
                    tiaMajorVersion = options.TiaMajorVersion.Value;
                    tiaVersionReliable = true;
                    LogDiag($"TIA major version (from CLI): {tiaMajorVersion}");
                }
                else
                {
                    var detected = Engineering.DetectTiaMajorVersion();
                    tiaMajorVersion = detected ?? 21;
                    tiaVersionReliable = detected.HasValue;
                    LogDiag(detected.HasValue
                        ? $"TIA major version (auto-detected): {tiaMajorVersion}"
                        : $"TIA major version (default fallback): {tiaMajorVersion} — install not detected, specify --tia-major-version if wrong");
                }
                Engineering.TiaMajorVersion = tiaMajorVersion;

                // Version-aware self-routing (issue #8): this exe's IL is bound to one TIA major
                // version; when the machine actually wants a different one, re-exec the sibling
                // exe built for it instead of crashing at the first Siemens assembly load.
                // stdio is inherited, so MCP hosts and CLI callers are unaffected.
                if (tiaVersionReliable && tiaMajorVersion != EngineRouter.CompiledTiaMajorVersion)
                {
                    if (EngineRouter.TryRedirect(tiaMajorVersion, args, LogDiag, out int routedExit))
                    {
                        Environment.Exit(routedExit);
                        return;
                    }
                    LogDiag($"WARN: TIA V{tiaMajorVersion} requested but this exe is built for V{EngineRouter.CompiledTiaMajorVersion} " +
                            $"and no V{tiaMajorVersion} sibling exe was found next to it — Siemens assembly load will likely fail. " +
                            $"Run the V{tiaMajorVersion} exe from the bundle, or pass --tia-major-version {EngineRouter.CompiledTiaMajorVersion} to force.");
                }

                // 静态自检也会枚举 MCP 工具特性，方法签名里引用的 Siemens 程序集需要先能被解析。
                // 这里只注册程序集解析器，不初始化 Openness，也不连接或打开 TIA 项目。
                AppDomain.CurrentDomain.AssemblyResolve += Engineering.Resolver;

                // Headless by default for fast startup; --with-ui launches the full GUI for inspection.
                // Flag lives on Engineering (Siemens-free) so setting it here doesn't force the CLR to
                // load the Portal type (its Siemens.Engineering fields) at Main's JIT time.
                Engineering.LaunchWithUserInterface = options.PortalWithUserInterface;
                LogDiag(options.PortalWithUserInterface
                    ? "TIA Portal will launch WITH user interface (--with-ui); slower cold start."
                    : "TIA Portal will launch headless (WithoutUserInterface) for faster startup; pass --with-ui to show the GUI.");

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

                if (Engineering.TiaMajorVersion >= 20)
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
                        LogDiag($"FIX: TIA Portal V{Engineering.TiaMajorVersion} (with the Openness option) was not found on this machine. Install it, or pass --tia-major-version <n> matching the installed version, or set the TiaPortalLocation environment variable to the install path. Run `tia.cmd doctor` for a full check.");
                        LogDiag($"修复：本机未找到 TIA Portal V{Engineering.TiaMajorVersion}（含 Openness 组件）。请安装对应版本，或用 --tia-major-version 指定已装版本，或设置 TiaPortalLocation 环境变量指向安装目录。可运行 tia.cmd doctor 一键体检。");
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

                    // ── License validation ────────────────────────────
                    {
                        var (licenseOk, licenseMsg) = await LicenseValidator.Validate(
                            options.LicenseKey,
                            options.LicenseServerUrl ?? "http://47.96.87.46");
                        LogDiag(licenseMsg);
                        if (!licenseOk)
                        {
                            LogDiag("LICENSE DENIED — exiting (code 7)");
                            Environment.Exit(7);
                        }
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
                    LogDiag("FIX: run this exe with `doctor` (e.g. tia.cmd doctor --fix) or add your Windows user to the local group 'Siemens TIA Openness' (lusrmgr.msc), then sign out/in and restart the AI client.");
                    LogDiag("修复：运行 tia.cmd doctor --fix，或手动把当前 Windows 用户加入本地组 'Siemens TIA Openness'（lusrmgr.msc），注销重登后重启 AI 客户端。");
                    Environment.ExitCode = 2;
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
                    var mcp = builder.Services
                        .AddMcpServer(o =>
                        {
                            // Injected into the model's context by the host at initialize time —
                            // reaches EVERY MCP client, including ones that never load SKILL.md.
                            o.ServerInstructions = ModelContextProtocol.McpGuides.ServerInstructions;
                        })
                        .WithStdioServerTransport();
                    // TIA_MCP_PROFILE=lite → only [L0]/[L1] essentials (weak models / capped hosts).
                    if (ModelContextProtocol.McpServer.IsLiteProfile())
                        mcp.WithTools(ModelContextProtocol.McpServer.GetLiteTools());
                    else
                        mcp.WithToolsFromAssembly();
                    mcp.WithPromptsFromAssembly();
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

                var mcpHttp = builder.Services
                    .AddMcpServer(o =>
                    {
                        o.ServerInstructions = ModelContextProtocol.McpGuides.ServerInstructions;
                    })
                    .WithStreamServerTransport(httpToMcp, mcpToHttp);
                if (ModelContextProtocol.McpServer.IsLiteProfile())
                    mcpHttp.WithTools(ModelContextProtocol.McpServer.GetLiteTools());
                else
                    mcpHttp.WithToolsFromAssembly();
                mcpHttp.WithPromptsFromAssembly();

                builder.Services.AddSingleton<TiaMcpServer.Siemens.Portal>();

                var host = builder.Build();
                McpServer.SetServiceProvider(host.Services);
                McpServer.Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer");

                await host.RunAsync();
            });

            await HttpMcpServer.Run(options, httpToMcp, mcpToHttp, LogDiag).ConfigureAwait(false);
            await mcpTask.ConfigureAwait(false);
        }

        // methods for 'cli probe/test commands' — moved to Program.CliProbes.cs
        // methods for 'report/analysis builders' — moved to Program.ReportBuilders.cs
        // methods for 'hmi template validation' — moved to Program.HmiTemplates.cs
        // methods for 'plc/hmi sync + xml writers' — moved to Program.PlcHmiSyncXml.cs
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
