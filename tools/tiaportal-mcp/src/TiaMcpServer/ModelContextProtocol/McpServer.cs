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
    [McpServerToolType]
    public static partial class McpServer
    {
        private static IServiceProvider? _services;
        private static Portal? _portal;

        public static ILogger? Logger { get; set; }

        public static Portal Portal
        {
            get
            {
                if (_services !=null)
                {
                    return _services.GetRequiredService<Portal>();
                }
                else
                {
                    if (_portal == null)
                    {
                        _portal = new Portal();
                    }
                    return _portal;
                }
            }
            set
            {
                _portal = value ?? throw new ArgumentNullException(nameof(value), "Portal cannot be null");
            }
        }

        public static void SetServiceProvider(IServiceProvider services)
        {
            _services = services;
        }

        #region portal

        [McpServerTool(Name = "Connect"), Description("[L1][Portal] Connect to a running TIA Portal instance or start a new one. MUST be the first tool called in every session. On success, state becomes Connected=true. If TIA Portal is not installed or the user is not in the 'Siemens TIA Openness' Windows group, this will fail — run EnsureOpennessUserGroup first.")]
        public static ResponseConnect Connect()
        {
            Logger?.LogInformation("Connecting to TIA Portal...");

            try
            {
                // ConnectPortal 失败时抛 PortalException（结构化错误码），下方 catch 统一映射到 McpException
                Portal.ConnectPortal();
                return new ResponseConnect
                {
                    Message = "Connected to TIA-Portal",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed to connect to TIA-Portal [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error connecting to TIA-Portal: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListPortalProcessProjects"), Description("[L1][Portal]List running TIA Portal processes and the projects/sessions visible in each process.")]
        public static ResponseStringList ListPortalProcessProjects()
        {
            try
            {
                var items = Portal.ListPortalProcessProjects();
                return new ResponseStringList
                {
                    Message = "TIA Portal processes inspected",
                    Items = items,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing TIA Portal processes: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureOpennessUserGroup"), Description("[L1][Portal]Ensure current Windows user is in TIA Openness user group (may prompt UI). Returns success=true when membership is OK.")]
        public static async Task<ResponseMessage> EnsureOpennessUserGroup()
        {
            try
            {
                var ok = await Siemens.Openness.IsUserInGroup();
                return new ResponseMessage
                {
                    Message = ok ? "Openness user group OK" : "Openness user group NOT OK",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring Openness user group: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "Disconnect"), Description("[L1][Portal] Disconnect from TIA Portal and release the Openness handle. Call after all project work is done. Any unsaved changes will be lost — call SaveProject first if needed.")]
        public static ResponseDisconnect Disconnect()
        {
            try
            {
                if (Portal.DisconnectPortal())
                {
                    return new ResponseDisconnect
                    {
                        Message = "Disconnected from TIA-Portal",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException("Failed disconnecting from TIA-Portal", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error disconnecting from TIA-Portal: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region state

        [McpServerTool(Name = "GetState"), Description("[L0][Portal] Get current connection state: IsConnected, open Project name, and open Session name. Use this to check preconditions before other tools — if IsConnected=false, call Connect first; if Project is empty, call OpenProject or CreateProject.")]
        public static ResponseState GetState()
        {
            try
            {
                var state = Portal.GetState();

                if (state != null)
                {
                    return new ResponseState
                    {
                        Message = "TIA-Portal MCP server state retrieved",
                        IsConnected = state.IsConnected,
                        Project = state.Project,
                        Session = state.Session,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException("Failed to retrieve TIA-Portal MCP server state", McpErrorCode.InternalError);
                }
                

            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving TIA-Portal MCP server state: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region bootstrap

        [McpServerTool(Name = "Bootstrap"), Description("[L0][Bootstrap] FIRST tool any AI model should call. Read-only single-call orientation: returns TIA version, Openness group status, current connection/project state, the recommended next tool, the L0/L1 tool roster, and known TIA Openness limitations. Does NOT connect to TIA Portal — call Connect afterwards based on RecommendedNextTool.")]
        public static async Task<ResponseBootstrap> Bootstrap()
        {
            try
            {
                var env = new BootstrapEnvironment
                {
                    TiaVersionInUse = Engineering.TiaMajorVersion == 0 ? (int?)null : Engineering.TiaMajorVersion,
                    TiaVersionDetected = Engineering.DetectTiaMajorVersion(),
                    TiaInstallPath = Environment.GetEnvironmentVariable("TiaPortalLocation"),
                    Transport = Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio",
                };

                try { env.OpennessGroupOk = await Siemens.Openness.IsUserInGroup(); }
                catch { env.OpennessGroupOk = false; }

                var portalDto = new BootstrapPortal();
                try
                {
                    var st = Portal.GetState();
                    portalDto.Connected = st?.IsConnected;
                    portalDto.ProjectName = st?.Project;
                    portalDto.SessionName = st?.Session;
                }
                catch { portalDto.Connected = false; }
                portalDto.LastConnectError = Portal.LastConnectError;

                string nextTool;
                string reason;
                if (env.OpennessGroupOk != true)
                {
                    nextTool = "EnsureOpennessUserGroup";
                    reason = "Current user is not in 'Siemens TIA Openness' Windows group; cannot use Openness API.";
                }
                else if (env.TiaVersionInUse == null && env.TiaVersionDetected == null)
                {
                    nextTool = "(install TIA Portal)";
                    reason = "No TIA Portal installation detected. Install V18+ and set TiaPortalLocation env var.";
                }
                else if (portalDto.Connected != true)
                {
                    nextTool = "Connect";
                    reason = "Not connected to TIA Portal yet. Connect first; if a project is already open in TIA UI, then call AttachToOpenProject.";
                }
                else if (string.IsNullOrWhiteSpace(portalDto.ProjectName) || portalDto.ProjectName == "-")
                {
                    nextTool = "AttachToOpenProject";
                    reason = "Connected to portal but no project bound. Use AttachToOpenProject if a project is already open in TIA UI, or OpenProject/CreateProject otherwise.";
                }
                else
                {
                    nextTool = "GetProjectTree";
                    reason = "Project is open. Inspect the tree before any write operation.";
                }

                var layers = new BootstrapToolLayers
                {
                    L0 = new[] { "Bootstrap", "GetState", "RunCapabilitySelfTest" },
                    L1 = new[]
                    {
                        "Connect", "Disconnect", "AttachToOpenProject", "OpenProject", "CreateProject",
                        "SaveProject", "CloseProject", "GetProjectTree", "GetSoftwareTree",
                        "PlcBuildAndImport", "CompileSoftware", "DownloadToPlc", "GoOnline", "GoOffline"
                    },
                    L2Count = GetMcpToolNames().Count(),
                };

                var limits = new[]
                {
                    "Openness API CANNOT: read/change CPU RUN-STOP mode (use OPC UA), read fault buffer, ClearForces, selective per-block download.",
                    "Force/Watch table values become effective only after the project is online and the table trigger fires.",
                    "Safety F-CPU compile is not exposed in PublicAPI; user must trigger it in TIA UI.",
                };

                bool ready = env.OpennessGroupOk == true && (env.TiaVersionInUse != null || env.TiaVersionDetected != null);

                return new ResponseBootstrap
                {
                    Ready = ready,
                    Environment = env,
                    Portal = portalDto,
                    RecommendedNextTool = nextTool,
                    RecommendedReason = reason,
                    KnownLimitations = limits,
                    ToolLayers = layers,
                    SkillFile = "tools/tiaportal-mcp/skill/SKILL.md",
                    ServerVersion = typeof(McpServer).Assembly.GetName().Version?.ToString(),
                    Capabilities = Capability.Snapshot(),
                    Message = ready ? "TIA Portal MCP ready" : "TIA Portal MCP not ready — see RecommendedNextTool",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Bootstrap unexpected error: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region capability self-test

        [McpServerTool(Name = "RunCapabilitySelfTest"), Description("[L0][Diagnostics]Run a read-only MCP/TIA readiness self-test. It checks Openness group membership, connection state, visible portal processes, optional automation context, and optional project tree readback without writing to the project.")]
        public static async Task<ResponseCapabilitySelfTest> RunCapabilitySelfTest(
            [Description("When true, call Connect before checks if the server is not connected. This is read-only but attaches to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include GetProjectTree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA Portal process/project details. This may attach to running TIA processes and can be slow on a contended workstation.")] bool inspectPortalProcesses = false,
            [Description("Expected PLC software path for ValidateAutomationContext.")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("Expected HMI software path for ValidateAutomationContext.")] string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            var items = new List<CapabilitySelfTestItem>();
            string? projectTree = null;

            void Add(string id, string name, string status, string detail)
            {
                items.Add(new CapabilitySelfTestItem
                {
                    Id = id,
                    Name = name,
                    Status = status,
                    Detail = detail
                });
            }

            try
            {
                bool opennessOk;
                try
                {
                    opennessOk = await Siemens.Openness.IsUserInGroup();
                    Add("openness.user-group", "Siemens TIA Openness user group", opennessOk ? "pass" : "fail", opennessOk ? "Current user is in the Openness group." : "Current user is not in the Openness group, or membership could not be confirmed.");
                }
                catch (Exception ex)
                {
                    opennessOk = false;
                    Add("openness.user-group", "Siemens TIA Openness user group", "fail", ex.Message);
                }

                if (inspectPortalProcesses)
                {
                    try
                    {
                        var processProjects = Portal.ListPortalProcessProjects();
                        Add("tia.processes", "Visible TIA Portal processes", processProjects.Any() ? "pass" : "warn", string.Join(Environment.NewLine, processProjects));
                    }
                    catch (Exception ex)
                    {
                        Add("tia.processes", "Visible TIA Portal processes", "fail", ex.Message);
                    }
                }
                else
                {
                    Add("tia.processes", "Visible TIA Portal processes", "skip", "Process/project inspection skipped. Set inspectPortalProcesses=true to run it.");
                }

                var state = connectIfNeeded ? Portal.GetState() : null;
                var isConnected = state?.IsConnected == true;
                if (!isConnected && connectIfNeeded)
                {
                    try
                    {
                        isConnected = Portal.ConnectPortal();
                        state = Portal.GetState();
                    }
                    catch (Exception ex)
                    {
                        Add("tia.connect", "Connect to TIA Portal", "fail", ex.Message);
                    }
                }

                Add("tia.connection", "MCP connection state", isConnected ? "pass" : "warn", $"IsConnected={state?.IsConnected}; Project={state?.Project}; Session={state?.Session}");

                var hasProject = isConnected && state != null && (!string.IsNullOrWhiteSpace(state.Project) && state.Project != "-" || !string.IsNullOrWhiteSpace(state.Session) && state.Session != "-");
                if (hasProject)
                {
                    try
                    {
                        var validation = Portal.ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
                        var success = validation.Meta != null && validation.Meta.TryGetPropertyValue("success", out var value) && value != null && value.GetValue<bool>();
                        Add("project.automation-context", "Automation context validation", success ? "pass" : "warn", validation.Message ?? "ValidateAutomationContext returned no message.");
                    }
                    catch (Exception ex)
                    {
                        Add("project.automation-context", "Automation context validation", "fail", ex.Message);
                    }

                    if (includeProjectTree)
                    {
                        try
                        {
                            projectTree = Portal.GetProjectTree();
                            Add("project.tree", "Project tree readback", string.IsNullOrWhiteSpace(projectTree) ? "warn" : "pass", string.IsNullOrWhiteSpace(projectTree) ? "Project tree was empty." : "Project tree read successfully.");
                        }
                        catch (Exception ex)
                        {
                            Add("project.tree", "Project tree readback", "fail", ex.Message);
                        }
                    }
                }
                else
                {
                    Add("project.open", "Open project/session", "warn", "No open project or local session is attached. Project-specific checks were skipped.");
                }

                var ok = items.All(i => i.Status == "pass" || i.Status == "warn" || i.Status == "skip");
                return new ResponseCapabilitySelfTest
                {
                    Ok = ok,
                    IncludeProjectTree = includeProjectTree,
                    Items = items,
                    ProjectTree = projectTree,
                    Message = ok ? "Capability self-test completed" : "Capability self-test completed with failures",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["connectIfNeeded"] = connectIfNeeded,
                        ["checkedItems"] = items.Count
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running capability self-test: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunOnlineMonitoringSafetySelfTest"), Description("[L0][Diagnostics]Run a static, read-only safety self-test for online monitoring guardrails. It does not connect to TIA Portal, open projects, modify watch tables, write PLC values, or expose forced-value operations.")]
        public static ResponseSafetySelfTest RunOnlineMonitoringSafetySelfTest()
        {
            var items = new List<CapabilitySelfTestItem>();

            void Add(string id, string name, bool pass, string detail)
            {
                items.Add(new CapabilitySelfTestItem
                {
                    Id = id,
                    Name = name,
                    Status = pass ? "pass" : "fail",
                    Detail = detail
                });
            }

            try
            {
                var toolNames = GetMcpToolNames();
                var toolNameList = toolNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                // Read-only getters (Get*) may legitimately reference force tables (e.g. GetPlcForceTables
                // lists names). The safety red line is that no force-EXECUTION/WRITE tool is exposed.
                var forbiddenToolNames = toolNameList
                    .Where(x => x.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0
                             && !x.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                Add(
                    "safety.no-force-tools",
                    "No force-write MCP tools exposed",
                    forbiddenToolNames.Count == 0,
                    forbiddenToolNames.Count == 0
                        ? $"Checked {toolNameList.Count} MCP tools; no force-write tool exposed (read-only force-table getters allowed)."
                        : "Forbidden force-write tool names: " + string.Join(", ", forbiddenToolNames));

                var requiredTools = new[]
                {
                    "GetPlcWatchTables",
                    "ExportPlcWatchTable",
                    "ExportPlcWatchTablesToDirectory",
                    "ProbePlcMonitorOnlineCapabilities",
                    "PlanOnlineReadOnlyMonitoring",
                    "RunOnlineMonitoringSafetySelfTest"
                };
                var missingTools = requiredTools
                    .Where(x => !toolNameList.Contains(x, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                Add(
                    "safety.required-readonly-tools",
                    "Required read-only monitoring tools are present",
                    missingTools.Count == 0,
                    missingTools.Count == 0
                        ? "Read-only watch-table discovery/export/probe/self-test tools are present."
                        : "Missing required tools: " + string.Join(", ", missingTools));

                var portalType = typeof(Portal);
                var guardMethod = portalType.GetMethod("GetHardDeniedReflectionReason", BindingFlags.NonPublic | BindingFlags.Static);
                Add(
                    "safety.reflection-hard-deny",
                    "Reflection hard-deny guard exists",
                    guardMethod != null,
                    guardMethod != null
                        ? "Portal reflection bridge has a private hard-deny guard."
                        : "Portal reflection bridge hard-deny guard was not found.");

                if (guardMethod != null)
                {
                    var forceDenied = InvokeReflectionDenyGuard(guardMethod, "Block", "PLC_1/Main", "ForceValue");
                    var watchCreateDenied = InvokeReflectionDenyGuard(guardMethod, "WatchTable", "PLC_1/Watch", "Create");
                    var watchWriteDenied = InvokeReflectionDenyGuard(guardMethod, "Monitor", "PLC_1/Watch", "WriteValue");
                    var readAllowed = InvokeReflectionDenyGuard(guardMethod, "Block", "PLC_1/Main", "GetAttribute");
                    Add(
                        "safety.reflection-hard-deny-semantics",
                        "Reflection hard-deny guard blocks unsafe operations",
                        !string.IsNullOrWhiteSpace(forceDenied) &&
                        !string.IsNullOrWhiteSpace(watchCreateDenied) &&
                        !string.IsNullOrWhiteSpace(watchWriteDenied) &&
                        string.IsNullOrWhiteSpace(readAllowed),
                        $"forceDenied={DescribeGuardResult(forceDenied)}; watchCreateDenied={DescribeGuardResult(watchCreateDenied)}; watchWriteDenied={DescribeGuardResult(watchWriteDenied)}; normalReadAllowed={string.IsNullOrWhiteSpace(readAllowed)}.");
                }

                var describeService = portalType.GetMethod("DescribeService", BindingFlags.Public | BindingFlags.Instance);
                var invokeService = portalType.GetMethod("InvokeService", BindingFlags.Public | BindingFlags.Instance);
                var invokeObject = portalType.GetMethod("InvokeObject", BindingFlags.Public | BindingFlags.Instance);
                Add(
                    "safety.reflection-entrypoints",
                    "Reflection entrypoints available for guarded inspection",
                    describeService != null && invokeService != null && invokeObject != null,
                    $"DescribeService={(describeService != null ? "present" : "missing")}; InvokeService={(invokeService != null ? "present" : "missing")}; InvokeObject={(invokeObject != null ? "present" : "missing")}.");

                var probeMethod = portalType.GetMethod("ProbePlcMonitorOnlineCapabilities", BindingFlags.Public | BindingFlags.Instance);
                Add(
                    "safety.online-probe-readonly",
                    "Online capability probe is discovery-only",
                    probeMethod != null,
                    probeMethod != null
                        ? "Probe method exists. It is intended for API-surface discovery only, not online transition or value writes."
                        : "ProbePlcMonitorOnlineCapabilities was not found.");

                var ok = items.All(i => i.Status == "pass");
                var policy = GetOnlineMonitoringSafetyPolicy();
                return new ResponseSafetySelfTest
                {
                    Ok = ok,
                    Items = items,
                    Policy = policy,
                    Message = ok ? "Online monitoring safety self-test passed" : "Online monitoring safety self-test failed",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["checkedTools"] = toolNameList.Count
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running online monitoring safety self-test: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static List<string> GetMcpToolNames()
        {
            var names = new List<string>();
            foreach (var method in typeof(McpServer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (var attribute in method.CustomAttributes)
                {
                    if (!string.Equals(attribute.AttributeType.Name, "McpServerToolAttribute", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var name = attribute.NamedArguments
                        .FirstOrDefault(x => string.Equals(x.MemberName, "Name", StringComparison.Ordinal))
                        .TypedValue.Value?.ToString();
                    names.Add(string.IsNullOrWhiteSpace(name) ? method.Name : name!);
                }
            }
            return names;
        }

        private static string? InvokeReflectionDenyGuard(MethodInfo guardMethod, string resultKind, string resultPath, string methodName)
        {
            return guardMethod.Invoke(null, new object[] { new object(), resultKind, resultPath, methodName }) as string;
        }

        private static string DescribeGuardResult(string? result)
        {
            return string.IsNullOrWhiteSpace(result) ? "allow" : "deny";
        }

        private static IReadOnlyList<string> GetOnlineMonitoringSafetyPolicy()
        {
            return new[]
            {
                "在线监视只允许读取变量当前状态/当前值。",
                "在线模式不允许修改监控表、监视表或表内对象。",
                "不允许通过 MCP 暴露、调用或绕过任何强制表/强制相关操作。",
                "通用反射入口必须拦截强制相关服务，并拦截在线/监视/监控表面的写入、创建、删除、下载、启停和上下线切换动作。",
                "新增监视能力必须先探测 API 形状，再用最小实例读回验证；未验证前只能标记为探测能力。"
            };
        }

        #endregion

        #region acceptance report

        [McpServerTool(Name = "GenerateAcceptanceReport"), Description("[L0][Reports]Generate a read-only acceptance report for the current MCP/TIA environment. The default mode does not attach to TIA or write to the project; it writes Markdown/JSON report files to outputDirectory.")]
        public static async Task<ResponseAcceptanceReport> GenerateAcceptanceReport(
            [Description("Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%.")] string outputDirectory = "",
            [Description("When true, call Connect during self-test if the server is not connected. This may attach to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include project tree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA process/project details. This may be slow if TIA is contended.")] bool inspectPortalProcesses = false,
            [Description("Optional report title.")] string title = "TIA MCP Acceptance Report")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    outputDirectory = Path.Combine(Path.GetTempPath(), "TiaMcpReports");
                }

                Directory.CreateDirectory(outputDirectory);
                var operationId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var markdownPath = Path.Combine(outputDirectory, $"tia_mcp_acceptance_{operationId}.md");
                var jsonPath = Path.Combine(outputDirectory, $"tia_mcp_acceptance_{operationId}.json");

                var selfTest = await RunCapabilitySelfTest(
                    connectIfNeeded: connectIfNeeded,
                    includeProjectTree: includeProjectTree,
                    inspectPortalProcesses: inspectPortalProcesses);
                var safetySelfTest = RunOnlineMonitoringSafetySelfTest();

                var markdown = BuildAcceptanceReportMarkdown(title, operationId, selfTest, safetySelfTest);
                File.WriteAllText(markdownPath, markdown);

                var response = new ResponseAcceptanceReport
                {
                    Ok = selfTest.Ok == true && safetySelfTest.Ok == true,
                    OperationId = operationId,
                    OutputDirectory = outputDirectory,
                    MarkdownPath = markdownPath,
                    JsonPath = jsonPath,
                    SelfTest = selfTest,
                    SafetySelfTest = safetySelfTest,
                    Message = selfTest.Ok == true && safetySelfTest.Ok == true ? "Acceptance report generated" : "Acceptance report generated with failures",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = selfTest.Ok == true && safetySelfTest.Ok == true
                    }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(jsonPath, json);

                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating acceptance report: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static string BuildAcceptanceReportMarkdown(string title, string operationId, ResponseCapabilitySelfTest selfTest, ResponseSafetySelfTest safetySelfTest)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# " + (string.IsNullOrWhiteSpace(title) ? "TIA MCP Acceptance Report" : title));
            sb.AppendLine();
            sb.AppendLine("- OperationId: `" + operationId + "`");
            sb.AppendLine("- GeneratedAt: `" + DateTime.Now.ToString("O") + "`");
            sb.AppendLine("- Overall: `" + (selfTest.Ok == true && safetySelfTest.Ok == true ? "PASS" : "CHECK") + "`");
            sb.AppendLine();
            sb.AppendLine("## Self Test");
            sb.AppendLine();
            sb.AppendLine("| Id | Status | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var item in selfTest.Items ?? Array.Empty<CapabilitySelfTestItem>())
            {
                sb.AppendLine("| " + EscapeMarkdownTable(item.Id) + " | " + EscapeMarkdownTable(item.Status) + " | " + EscapeMarkdownTable(item.Detail) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Online Monitoring Safety");
            sb.AppendLine();
            sb.AppendLine("| Id | Status | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var item in safetySelfTest.Items ?? Array.Empty<CapabilitySelfTestItem>())
            {
                sb.AppendLine("| " + EscapeMarkdownTable(item.Id) + " | " + EscapeMarkdownTable(item.Status) + " | " + EscapeMarkdownTable(item.Detail) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("### Safety Policy");
            sb.AppendLine();
            foreach (var policy in safetySelfTest.Policy ?? Array.Empty<string>())
            {
                sb.AppendLine("- " + policy);
            }

            if (!string.IsNullOrWhiteSpace(selfTest.ProjectTree))
            {
                sb.AppendLine();
                sb.AppendLine("## Project Tree");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(selfTest.ProjectTree);
                sb.AppendLine("```");
            }

            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine("- This report is read-only and does not prove PLC compile or HMI binding unless those checks are explicitly added to the workflow.");
            sb.AppendLine("- Treat `warn` and `skip` entries as deployment notes, not product-ready validation.");
            return sb.ToString();
        }

        private static string EscapeMarkdownTable(string? value)
        {
            return (value ?? string.Empty)
                .Replace("|", "\\|")
                .Replace("\r", " ")
                .Replace("\n", "<br>");
        }

        #endregion

        #region error report

        [McpServerTool(Name = "GenerateErrorReport"), Description("[L0][Reports]Generate a standardized Markdown/JSON error report. This is file/report generation only; it does not touch TIA Portal or modify projects.")]
        public static ResponseErrorReport GenerateErrorReport(
            [Description("Machine-readable error code, for example CompileError, HmiBindingError, TiaSessionContention, UnexpectedOpennessError.")] string errorCode,
            [Description("Short human-readable summary.")] string summary,
            [Description("Detailed error text, stack trace, compile message, or diagnostic context.")] string detail = "",
            [Description("Comma-separated next actions.")] string recommendedNextActions = "",
            [Description("Severity: info, warn, error, critical.")] string severity = "error",
            [Description("Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%.")] string outputDirectory = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    outputDirectory = Path.Combine(Path.GetTempPath(), "TiaMcpReports", "errors");
                }

                Directory.CreateDirectory(outputDirectory);
                var operationId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var safeCode = Regex.Replace(string.IsNullOrWhiteSpace(errorCode) ? "UnknownError" : errorCode.Trim(), @"[^A-Za-z0-9_.-]+", "_");
                var markdownPath = Path.Combine(outputDirectory, $"tia_mcp_error_{safeCode}_{operationId}.md");
                var jsonPath = Path.Combine(outputDirectory, $"tia_mcp_error_{safeCode}_{operationId}.json");
                var actions = SplitRecommendedActions(recommendedNextActions);
                if (actions.Count == 0)
                {
                    actions = GetDefaultRecommendedActions(errorCode);
                }

                var response = new ResponseErrorReport
                {
                    Ok = true,
                    OperationId = operationId,
                    ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "UnknownError" : errorCode.Trim(),
                    Severity = string.IsNullOrWhiteSpace(severity) ? "error" : severity.Trim(),
                    Summary = summary,
                    OutputDirectory = outputDirectory,
                    MarkdownPath = markdownPath,
                    JsonPath = jsonPath,
                    RecommendedNextActions = actions,
                    Message = "Error report generated",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };

                File.WriteAllText(markdownPath, BuildErrorReportMarkdown(response, detail));
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    response.OperationId,
                    response.ErrorCode,
                    response.Severity,
                    response.Summary,
                    Detail = detail,
                    response.RecommendedNextActions,
                    GeneratedAt = DateTime.Now.ToString("O")
                }, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(jsonPath, json);

                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating error report: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static List<string> SplitRecommendedActions(string recommendedNextActions)
        {
            if (string.IsNullOrWhiteSpace(recommendedNextActions))
            {
                return new List<string>();
            }

            return recommendedNextActions
                .Split(new[] { '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static List<string> GetDefaultRecommendedActions(string? errorCode)
        {
            switch ((errorCode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "invalidparams":
                    return new List<string> { "Check required parameters and path spelling.", "Read live project tree before retrying.", "Use a resolver tool when the target path is ambiguous." };
                case "notconnected":
                    return new List<string> { "Run Connect.", "Check TIA Portal is installed and accessible.", "Run RunCapabilitySelfTest in minimal mode." };
                case "projectnotopen":
                    return new List<string> { "AttachToOpenProject or OpenProject before writing.", "Run GetState and GetProjectTree.", "Avoid opening a project already opened by another session." };
                case "preconditionfailed":
                    return new List<string> { "Run the required preflight sequence.", "Read back the target object before writing.", "Use dry-run where available." };
                case "notfound":
                    return new List<string> { "Search the live project tree for the target.", "Use full qualified paths for blocks/types.", "Report candidate matches instead of guessing." };
                case "ambiguouspath":
                    return new List<string> { "List candidates and choose one exact path.", "Avoid single block names when groups may contain duplicates.", "Use GetBlocksWithHierarchy before exporting/importing blocks." };
                case "unsupportedtiaversion":
                    return new List<string> { "Confirm TIA Portal V21 is installed.", "Restart the server with --tia-major-version 21.", "Check installed Openness assemblies." };
                case "opennesspermissiondenied":
                    return new List<string> { "Add the user to Siemens TIA Openness group.", "Sign out or restart after changing group membership.", "Run scripts/check-environment.ps1." };
                case "hardwarecatalognotfound":
                    return new List<string> { "Run SearchHardwareCatalog or SearchInstalledGsdDevices.", "Use MLFB/order number and installed catalog version.", "Do not fall back from third-party hardware to Siemens devices." };
                case "importschemaerror":
                    return new List<string> { "Validate XML is well formed.", "Compare against a same-version TIA export.", "Do not mix SCL source syntax with Openness XML syntax." };
                case "compileerror":
                    return new List<string> { "Export the failed block/type for inspection.", "Search existing tags, DBs, UDTs, and block interfaces before adding variables.", "Fix the smallest object and run CompileAndDiagnosePlc again." };
                case "hmibindingerror":
                    return new List<string> { "Read HMI screens, tag tables, tags, and connections.", "Verify the HMI tag is PLC-backed, not only internal.", "Read back dynamization and button script properties after binding." };
                case "reflectionriskblocked":
                    return new List<string> { "Describe the object/service first.", "Confirm method signature and parameter types.", "Use allowWrite only after read-only discovery and backup/export." };
                case "saveblocked":
                    return new List<string> { "Compile or validate before saving.", "Review warnings/failures in the report.", "Save only after readback succeeds unless the user explicitly asks otherwise." };
                case "tiasessioncontention":
                    return new List<string> { "Do not run write-capable CLI probes in parallel with an active MCP session.", "Use the already-running MCP server or restart it cleanly.", "Stop only the probe process you launched." };
                case "unexpectedopennesserror":
                    return new List<string> { "Capture the native exception details.", "Classify the failure before retrying.", "Prefer a small sacrificial project probe before touching a real project." };
                default:
                    return new List<string> { "Capture the exact tool, parameters, and native error.", "Run readback diagnostics before retrying.", "Generate an acceptance or environment report if the failure may be machine-specific." };
            }
        }

        // Best-effort "Did you mean …?" suffix for a not-found block name. Only fires for a
        // bare name (no '/'), where a typo is the likely cause. Returns "" on any failure.
        private static string BuildBlockDidYouMean(string softwarePath, string blockPath)
        {
            if (string.IsNullOrEmpty(blockPath) || blockPath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(blockPath);
                var blocks = Portal.GetBlocks(softwarePath, $"^{escaped}$");
                if (blocks == null || blocks.Count == 0)
                    blocks = Portal.GetBlocks(softwarePath, escaped);

                var candidates = blocks
                    .Take(10)
                    .Select(b => Portal.GetBlockPath(b))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Siemens.Guard.DidYouMean(candidates);
            }
            catch
            {
                return string.Empty;
            }
        }

        // Best-effort "Did you mean …?" suffix for a not-found type name. See BuildBlockDidYouMean.
        private static string BuildTypeDidYouMean(string softwarePath, string typePath)
        {
            if (string.IsNullOrEmpty(typePath) || typePath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(typePath);
                var types = Portal.GetTypes(softwarePath, $"^{escaped}$");
                if (types == null || types.Count == 0)
                    types = Portal.GetTypes(softwarePath, escaped);

                var candidates = types
                    .Take(10)
                    .Select(t => t.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Siemens.Guard.DidYouMean(candidates);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildErrorReportMarkdown(ResponseErrorReport report, string detail)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# TIA MCP Error Report");
            sb.AppendLine();
            sb.AppendLine("- OperationId: `" + report.OperationId + "`");
            sb.AppendLine("- GeneratedAt: `" + DateTime.Now.ToString("O") + "`");
            sb.AppendLine("- ErrorCode: `" + report.ErrorCode + "`");
            sb.AppendLine("- Severity: `" + report.Severity + "`");
            sb.AppendLine("- Summary: " + (string.IsNullOrWhiteSpace(report.Summary) ? "(none)" : report.Summary));
            sb.AppendLine();
            sb.AppendLine("## Detail");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(detail ?? string.Empty);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Recommended Next Actions");
            sb.AppendLine();
            var actions = report.RecommendedNextActions?.ToList() ?? new List<string>();
            if (actions.Count == 0)
            {
                sb.AppendLine("- No recommended action was provided.");
            }
            else
            {
                foreach (var action in actions)
                {
                    sb.AppendLine("- " + action);
                }
            }
            return sb.ToString();
        }

        #endregion

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
                throw new McpException($"Unexpected error retrieving open projects: {ex.Message}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error opening project '{path}': {ex.Message}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error attaching to open project '{projectName}': {ex.Message}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error creating project: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        // NOTE: Deprecated demo tool removed.
        // In V21, prefer importing block XML via ImportBlock/ImportBlocksFromDirectory, then CompileSoftware.

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
                throw new McpException($"Unexpected error saving local project/session: {ex.Message}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error saving local project/session as '{newProjectPath}': {ex.Message}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error closing local project/session: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region devices

        [McpServerTool(Name = "GetProjectTree"), Description("[L1][Project] Get the full project device/software tree as ASCII art. Requires: Connect + OpenProject. ALWAYS call this first after opening a project to discover the exact softwarePath (e.g. 'PLC_1') and device paths needed by all other PLC/HMI/hardware tools. Returns device names, software nodes, and HMI nodes.")]
        public static ResponseProjectTree GetProjectTree()
        {
            try
            {
                var tree = Portal.GetProjectTree();

                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseProjectTree
                    {
                        Message = "Project tree retrieved",
                        Tree = "```\n" + tree + "\n```",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException("Failed retrieving project tree", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving project tree: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceInfo"), Description("[L2][Hardware]Get info from a device from the current project/session")]
        public static ResponseDeviceInfo GetDeviceInfo(
            [Description("devicePath: defines the path in the project structure to the device")] string devicePath)
        {
            try
            {
                var device = Portal.GetDevice(devicePath);

                if (device != null)
                {
                    var attributes = Helper.GetAttributeList(device);

                    return new ResponseDeviceInfo
                    {
                        Message = $"Device info retrieved from '{devicePath}'",
                        Name = device.Name,
                        Attributes = attributes,
                        Description = device.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Device not found at '{devicePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device info from '{devicePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceItemInfo"), Description("[L2][Hardware]Get info from a device item from the current project/session")]
        public static ResponseDeviceItemInfo GetDeviceItemInfo(
            [Description("deviceItemPath: defines the path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var deviceItem = Portal.GetDeviceItem(deviceItemPath);

                if (deviceItem != null)
                {
                    var attributes = Helper.GetAttributeList(deviceItem);

                    return new ResponseDeviceItemInfo
                    {
                        Message = $"Device item info retrieved from '{deviceItemPath}'",
                        Name = deviceItem.Name,
                        Attributes = attributes,
                        Description = deviceItem.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device item info from '{deviceItemPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceItemTree"), Description("[L2][Hardware]Get a subtree view for a device item (hardware components + sub device items)")]
        public static ResponseTree GetDeviceItemTree(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath,
            [Description("maxDepth: recursion depth, default 4")] int maxDepth = 4)
        {
            try
            {
                var tree = Portal.GetDeviceItemTree(deviceItemPath, maxDepth);
                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseTree
                    {
                        Message = $"Device item tree retrieved from '{deviceItemPath}'",
                        Tree = "```\n" + tree + "\n```",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device item tree from '{deviceItemPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceItemNetworkInfo"), Description("[L2][Hardware]Get network-related attributes for a device item (best-effort heuristic filter)")]
        public static ResponseNetworkInfo GetDeviceItemNetworkInfo(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var attrs = Portal.GetDeviceItemNetworkInfo(deviceItemPath);
                if (attrs != null)
                {
                    return new ResponseNetworkInfo
                    {
                        Message = $"Network info retrieved from '{deviceItemPath}'",
                        DeviceItemName = deviceItemPath.Split('/').LastOrDefault(),
                        Attributes = attrs,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Device item not found at '{deviceItemPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving network info from '{deviceItemPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetDeviceItemAttribute"), Description("[L2][Hardware]Set one exact DeviceItem attribute by name with type coercion and detailed error return. Use GetDeviceItemInfo/GetDeviceItemNetworkInfo first.")]
        public static ResponseMessage SetDeviceItemAttribute(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath,
            [Description("attributeName: exact attribute name from GetDeviceItemInfo/GetDeviceItemNetworkInfo")] string attributeName,
            [Description("value: new value as string; converted based on current attribute type")] string value)
        {
            try
            {
                return Portal.SetDeviceItemAttribute(deviceItemPath, attributeName, value);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting device item attribute '{attributeName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateAutomationContext"), Description("[L1][Diagnostics]Preflight current project for automation: devices, software, expected PLC/HMI paths, and project tree.")]
        public static ResponseMessage ValidateAutomationContext(
            [Description("expectedPlcSoftwarePath: expected PLC software path, empty to skip")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("expectedHmiSoftwarePath: expected HMI software path, empty to skip")] string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            try
            {
                return Portal.ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating automation context: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ConnectDeviceNodesToProfinetSubnet"), Description("[L1][Hardware] PREFERRED for PROFINET network setup. Finds the first IE/PROFINET node under two devices, creates/reuses a subnet on the first, connects the second, and returns readback evidence. Requires: Connect + OpenProject + both devices added. Typical: firstRootPath='PLC_1', secondRootPath='HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'. Verified for S7-1200 + KTP700 Basic PN.")]
        public static ResponseMessage ConnectDeviceNodesToProfinetSubnet(
            [Description("firstRootPath: first device/device-item root, usually PLC root, e.g. 'PLC_1'")] string firstRootPath,
            [Description("secondRootPath: second device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string secondRootPath,
            [Description("subnetName: subnet name to create when the first node is not already connected, e.g. 'PN_IE_1'")] string subnetName = "PN_IE_1")
        {
            try
            {
                var report = Portal.ProbeConnectDeviceNodesToSubnet(firstRootPath, secondRootPath, subnetName);
                var success =
                    report.IndexOf("ConnectToSubnet: OK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (report.IndexOf("already connected to subnet", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     report.IndexOf("connectedSubnet=<none>", StringComparison.OrdinalIgnoreCase) < 0);

                return new ResponseMessage
                {
                    Message = success
                        ? "Device nodes connected to PROFINET subnet"
                        : "Device node PROFINET subnet connection did not complete",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = success,
                        ["firstRootPath"] = firstRootPath,
                        ["secondRootPath"] = secondRootPath,
                        ["subnetName"] = subnetName,
                        ["report"] = report
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error connecting device nodes to PROFINET subnet: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanHardwareNetworkConfiguration"), Description("[L2][Hardware][Offline] Validate a hardware network operation plan without connecting to TIA Portal or modifying a project. Use this before EnsureSubnet/AttachDeviceNodeToSubnet/SetCpuCommonSettings; rejects guessed paths, unsafe subnet types, invalid IP/mask/gateway, and CPU settings without exactAttributes.")]
        public static ResponseJsonReport PlanHardwareNetworkConfiguration(
            [Description("planJson: JSON with operations[]. Supported operation types: EnsureSubnet, AttachDeviceNodeToSubnet, SetCpuCommonSettings. This is offline-only and performs validation only.")] string planJson)
        {
            try
            {
                var report = HardwareNetworkPlanValidator.Validate(planJson);
                return new ResponseJsonReport
                {
                    Ok = report["ok"]?.GetValue<bool>() == true,
                    Data = report,
                    Message = report["ok"]?.GetValue<bool>() == true
                        ? "Hardware network plan is valid"
                        : "Hardware network plan has validation errors",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = report["ok"]?.GetValue<bool>() == true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating hardware network plan: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureSubnet"), Description("[L2][Hardware] Ensure an Industrial Ethernet/PROFINET subnet by anchoring on a real deviceItemPath from GetProjectTree/GetDeviceItemTree. Applies only through TIA Openness, then returns readback evidence (node path, subnet name, interface path). Does not guess paths.")]
        public static ResponseMessage EnsureSubnet(
            [Description("anchorDeviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree; used to create/reuse the subnet from its first PROFINET node.")] string anchorDeviceItemPath,
            [Description("subnetType: IndustrialEthernet/PROFINET/PN/IE only.")] string subnetType,
            [Description("subnetName: exact subnet name to create or read back, e.g. PN_IE_1.")] string subnetName)
        {
            try
            {
                return Portal.EnsureSubnet(anchorDeviceItemPath, subnetType, subnetName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring subnet '{subnetName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AttachDeviceNodeToSubnet"), Description("[L2][Hardware] Attach one real device network node to an existing PROFINET subnet and return readback evidence. deviceItemPath must come from GetProjectTree/GetDeviceItemTree, interfaceIndex selects a discovered Industrial Ethernet/PROFINET node, and online/force operations are never used.")]
        public static ResponseMessage AttachDeviceNodeToSubnet(
            [Description("deviceItemPath: real device/device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string deviceItemPath,
            [Description("interfaceIndex: zero-based index among discovered Industrial Ethernet/PROFINET nodes under deviceItemPath.")] int interfaceIndex,
            [Description("subnetName: existing subnet name to attach to.")] string subnetName,
            [Description("anchorDeviceItemPath: optional real device-item path used to EnsureSubnet first when subnetName is not found.")] string anchorDeviceItemPath = "")
        {
            try
            {
                return Portal.AttachDeviceNodeToSubnet(deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error attaching device node to subnet '{subnetName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetCpuCommonSettings"), Description("[L2][Hardware] Set CPU common settings using exactAttributes JSON only after reading exact attribute names from GetDeviceItemInfo/GetDeviceItemNetworkInfo. The tool writes only those exact attributes, rejects missing/non-writable attributes, and returns readback evidence.")]
        public static ResponseMessage SetCpuCommonSettings(
            [Description("cpuPath: real CPU device-item path resolved from GetProjectTree/GetDeviceItemTree.")] string cpuPath,
            [Description("settingsJson: JSON object { \"exactAttributes\": { \"ExactAttributeNameFromReadback\": \"value\" } }. No aliases or guessed attribute names are accepted.")] string settingsJson)
        {
            try
            {
                return Portal.SetCpuCommonSettings(cpuPath, settingsJson);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting CPU common settings for '{cpuPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbeHardwareHmiConnectionOwnerCandidates"), Description("[L2][Hardware]Enumerate candidate owner objects for hardware HMI connection creation without calling GetService on each high-level object.")]
        public static ResponseStringList ProbeHardwareHmiConnectionOwnerCandidates(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include ancestor/project/device-level candidates; when false only direct node/interface/device-item candidates")] bool deepScan = true)
        {
            try
            {
                var items = Portal.ProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection owner candidates enumerated",
                    Items = items,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing hardware HMI connection owner candidates: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbeHardwareHmiConnectionWhitelistedServices"), Description("[L2][Hardware]Read-only scan of whitelisted services on safe hardware HMI connection owner candidates. Does not create connections and skips high-level project/composition objects to avoid hangs.")]
        public static ResponseStringList ProbeHardwareHmiConnectionWhitelistedServices(
            [Description("plcRootPath: PLC device/device-item root, e.g. 'PLC_1'")] string plcRootPath,
            [Description("hmiRootPath: HMI device/device-item root, e.g. 'HMI_KTP700_1/HMI_KTP700_1.IE_CP_1'")] string hmiRootPath,
            [Description("deepScan: when true include safe ancestors; when false only direct node/interface/device-item candidates")] bool deepScan = true)
        {
            try
            {
                var items = Portal.ProbeHardwareHmiConnectionWhitelistedServices(plcRootPath, hmiRootPath, deepScan);
                return new ResponseStringList
                {
                    Message = "Hardware HMI connection whitelisted services scanned",
                    Items = items,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error scanning hardware HMI connection whitelisted services: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        // NOTE: Deprecated demo tools removed (GenerateStartStopBlocks / EnsureUnifiedMainScreen).

        [McpServerTool(Name = "GetDevices"), Description("[L1][Hardware] List all hardware devices (PLCs, HMI panels, drives) in the project with their attributes. Requires: Connect + OpenProject. Prefer GetProjectTree for a visual overview; use this when you need the exact device Name and attribute values for subsequent operations like AddDevice or SetDeviceItemAttribute.")]
        public static ResponseDevices GetDevices()
        {
            try
            {
                var list = Portal.GetDevices();
                var responseList = new List<ResponseDeviceInfo>();

                if (list != null)
                {
                    foreach (var device in list)
                    {
                        if (device != null)
                        {
                            var attributes = Helper.GetAttributeList(device);
                            responseList.Add(new ResponseDeviceInfo
                            {
                                Name = device.Name,
                                Attributes = attributes,
                                Description = device.ToString()
                            });
                        }
                    }

                    return new ResponseDevices
                    {
                        Message = "Devices retrieved",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving devices", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving devices: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddDevice"), Description("[L2][Hardware] Add one hardware device using an exact MLFB/order number and version from the TIA hardware catalog. Requires: Connect + OpenProject. Use SearchHardwareCatalog first to find the exact MLFB/version. For unknown or approximate device names, use AddDeviceWithFallback or AddHardwareCatalogDeviceWithProbe instead.")]
        public static ResponseMessage AddDevice(
            [Description("orderNumber: MLFB/order number from the TIA hardware catalog, e.g. '6ES7211-1BE40-0XB0' or '6ES7 211-1BE40-0XB0'")] string orderNumber,
            [Description("version: catalog device version, e.g. 'V4.7', 'V3.1', or empty to let TIA try defaults")] string version,
            [Description("deviceName: name in project tree")] string deviceName)
        {
            try
            {
                var dev = Portal.AddDevice(orderNumber, version, deviceName);
                return new ResponseMessage
                {
                    Message = $"Device '{deviceName}' added",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["name"] = dev.Name
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed to add device '{deviceName}' ({orderNumber} {version}) [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding device: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddDeviceWithFallback"), Description("[L1][Hardware] PREFERRED for natural-language device insertion. Adds a Siemens hardware device by probing the installed TIA catalog with fallback versions. Requires: Connect + OpenProject. Example: 'add CPU 1211C AC/DC/RLY' → family='S7-1200'. Families: S7-1200, S7-1500, WinCCUnifiedPC. For third-party/GSD devices use AddGsdDeviceWithProbe.")]
        public static ResponseDeviceProbe AddDeviceWithFallback(
            [Description("preferredMlfb: preferred MLFB/order number, e.g. '6ES7211-1BE40-0XB0'; empty uses family fallback list")] string preferredMlfb,
            [Description("preferredVersion: preferred catalog version, e.g. 'V4.7'; empty probes known/default versions")] string preferredVersion,
            [Description("deviceName: name in project tree")] string deviceName,
            [Description("family: fallback hint, e.g. 'S7-1200', 'S7-1500', or 'WinCCUnifiedPC'")] string family = "S7-1500")
        {
            try
            {
                var res = Portal.AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);
                if (res.Device == null)
                {
                    return new ResponseDeviceProbe
                    {
                        Message = $"Failed to add device '{deviceName}' with fallback probes",
                        Ok = false,
                        DeviceName = deviceName,
                        Family = family,
                        Attempts = res.Attempts,
                        Error = res.Error,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                    };
                }

                return new ResponseDeviceProbe
                {
                    Message = $"Device '{deviceName}' added (fallback)",
                    Ok = true,
                    DeviceName = deviceName,
                    Family = family,
                    MlfbUsed = res.MlfbUsed,
                    VersionUsed = res.VersionUsed,
                    Attempts = res.Attempts,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["name"] = res.Device.Name }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding device with fallback: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SearchInstalledGsdDevices"), Description("[L2][Hardware]Search installed TIA V21 hardware catalog and local GSDML files for third-party PROFINET/GSD devices such as AFM60A, ATV320, or DL100. Use this before inserting a non-Siemens device.")]
        public static ResponseGsdDeviceSearch SearchInstalledGsdDevices(
            [Description("keyword: device/vendor/order/DAP keyword, e.g. 'AFM60A', 'ATV320', 'DL100', 'SICK AFM60A'")] string keyword,
            [Description("limit: maximum candidates to return")] int limit = 50)
        {
            try
            {
                var items = Portal.SearchInstalledGsdDevices(keyword, limit);
                return new ResponseGsdDeviceSearch
                {
                    Message = $"Found {items.Count} installed GSD/catalog candidate(s) for '{keyword}'",
                    Keyword = keyword,
                    Count = items.Count,
                    Items = items,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error searching installed GSD devices: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SearchHardwareCatalog"), Description("[L1][Hardware]Search the installed TIA Portal hardware catalog for Siemens or third-party catalog entries by keyword, MLFB/order number, device family, or description. Use this before adding hardware when the exact TypeIdentifier is unknown, especially HMI panels such as KTP700 Basic.")]
        public static ResponseHardwareCatalogSearch SearchHardwareCatalog(
            [Description("keyword: MLFB/order number/device/family text, e.g. 'KTP700', '6AV2123', '1211C DC/DC/DC', '6ES7211-1AE40'")] string keyword,
            [Description("limit: maximum candidates to return")] int limit = 50)
        {
            try
            {
                var items = Portal.SearchHardwareCatalog(keyword, limit);
                var success = items.Count > 0;
                return new ResponseHardwareCatalogSearch
                {
                    Message = $"Found {items.Count} hardware catalog candidate(s) for '{keyword}'",
                    Keyword = keyword,
                    Count = items.Count,
                    Items = items,
                    Error = success ? null : $"No matching hardware catalog candidates for '{keyword}'",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = success
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed searching hardware catalog for '{keyword}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error searching hardware catalog: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddGsdDeviceWithProbe"), Description("[L2][Hardware]Add a third-party GSD/GSDML hardware device by first searching the installed TIA hardware catalog, ranking candidates, and inserting with the exact catalog TypeIdentifier. Does not fall back to unrelated Siemens devices.")]
        public static ResponseGsdDeviceProbe AddGsdDeviceWithProbe(
            [Description("keyword: device/vendor/order/DAP keyword, e.g. 'AFM60A', 'ATV320', 'DL100'")] string keyword,
            [Description("deviceName: name in project tree, e.g. 'ENC_AFM60A_1'")] string deviceName,
            [Description("preferredDap: optional DAP/version hint, e.g. 'DAP V2.0', 'DAP 1', or empty")] string preferredDap = "")
        {
            try
            {
                var res = Portal.AddGsdDeviceWithProbe(keyword, deviceName, preferredDap);
                if (res.Device == null)
                {
                    return new ResponseGsdDeviceProbe
                    {
                        Message = $"Failed to add GSD device '{deviceName}' for '{keyword}'",
                        Ok = false,
                        Keyword = keyword,
                        DeviceName = deviceName,
                        PreferredDap = preferredDap,
                        Candidates = res.Candidates,
                        Attempts = res.Attempts,
                        Error = res.Error,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                    };
                }

                return new ResponseGsdDeviceProbe
                {
                    Message = $"GSD device '{deviceName}' added",
                    Ok = true,
                    Keyword = keyword,
                    DeviceName = deviceName,
                    PreferredDap = preferredDap,
                    CandidateUsed = res.Candidate,
                    Candidates = res.Candidates,
                    Attempts = res.Attempts,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["name"] = res.Device.Name
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding GSD device with probe: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AddHardwareCatalogDeviceWithProbe"), Description("[L2][Hardware]Add a hardware device by searching the installed TIA hardware catalog, ranking insertable TypeIdentifier candidates, and inserting the best match. Use for Siemens devices/HMI panels when exact TypeIdentifier is unknown, e.g. KTP700 Basic PN.")]
        public static ResponseHardwareCatalogDeviceProbe AddHardwareCatalogDeviceWithProbe(
            [Description("keyword: MLFB/order number/device/family text, e.g. 'KTP700 Basic PN', '6AV2123-2GB03', 'S7-1211C DC/DC/DC'")] string keyword,
            [Description("deviceName: name in project tree, e.g. 'HMI_KTP700_1'")] string deviceName,
            [Description("preferredText: optional ranking hint, e.g. 'PN 17.0.0.0 landscape', '6AV2 123-2GB03-0AX0'")] string preferredText = "")
        {
            try
            {
                var res = Portal.AddHardwareCatalogDeviceWithProbe(keyword, deviceName, preferredText);
                if (res.Device == null)
                {
                    return new ResponseHardwareCatalogDeviceProbe
                    {
                        Message = $"Failed to add hardware catalog device '{deviceName}' for '{keyword}'",
                        Ok = false,
                        Keyword = keyword,
                        DeviceName = deviceName,
                        PreferredText = preferredText,
                        Candidates = res.Candidates,
                        Attempts = res.Attempts,
                        Error = res.Error,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                    };
                }

                return new ResponseHardwareCatalogDeviceProbe
                {
                    Message = $"Hardware catalog device '{deviceName}' added",
                    Ok = true,
                    Keyword = keyword,
                    DeviceName = deviceName,
                    PreferredText = preferredText,
                    CandidateUsed = res.Candidate,
                    Candidates = res.Candidates,
                    Attempts = res.Attempts,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["name"] = res.Device.Name }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error adding hardware catalog device with probe: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region plc software

        [McpServerTool(Name = "GetSoftwareInfo"), Description("[L1][PLC-Software] Get PLC software properties (language, version, block counts). Requires: Connect + OpenProject. softwarePath comes from GetProjectTree (e.g. 'PLC_1'). Use GetSoftwareTree for the full block hierarchy.")]
        public static ResponseSoftwareInfo GetSoftwareInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var software = Portal.GetPlcSoftware(softwarePath);
                if (software != null)
                {

                    var attributes = Helper.GetAttributeList(software);

                    return new ResponseSoftwareInfo
                    {
                        Message = $"Software info retrieved from '{softwarePath}'",
                        Name = software.Name,
                        Attributes = attributes,
                        Description = software.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Software not found at '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software info from '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiProgramInfo"), Description("[L2][HMI] Get HMI software type (Classic/Basic/Unified), version, and list of all screen names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'HMI_RT_1'). Use to confirm HMI type before choosing Classic vs Unified tool variants.")]
        public static ResponseHmiProgramInfo GetHmiProgramInfo(
            [Description("softwarePath: path in the project structure to the HMI software (see GetProjectTree)")] string softwarePath)
        {
            try
            {
                var info = Portal.GetHmiProgramInfo(softwarePath);
                if (info != null)
                {
                    return new ResponseHmiProgramInfo
                    {
                        Message = $"HMI program info retrieved from '{softwarePath}'",
                        Name = info.Value.Name,
                        ProgramType = info.Value.ProgramType,
                        Screens = info.Value.Screens,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }

                throw new McpException($"HMI program not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving HMI program info from '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiSoftware"), Description("[L2][HMI]Describe the HMI software object (members/methods) via reflection. Useful to discover Export/Import/Create APIs.")]
        public static ResponseObjectDescribe DescribeHmiSoftware(
            [Description("softwarePath: path in the project structure to the HMI software (e.g. 'HMI_RT_1')")] string softwarePath,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiSoftware(softwarePath, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI software '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiScreen"), Description("[L2][HMI]Describe one HMI screen object (members/methods) by name under an HMI software.")]
        public static ResponseObjectDescribe DescribeHmiScreen(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("screenName: screen name, e.g. 'Main'")] string screenName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiScreen(softwarePath, screenName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI screen '{screenName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiTagTable"), Description("[L2][HMI]Describe one HMI tag table object (members/methods) by name under an HMI software.")]
        public static ResponseObjectDescribe DescribeHmiTagTable(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("tagTableName: tag table name")] string tagTableName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiTagTable(softwarePath, tagTableName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI tag table '{tagTableName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiTag"), Description("[L2][HMI]Describe one HMI tag object (members/methods) by name under an HMI tag table.")]
        public static ResponseObjectDescribe DescribeHmiTag(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("tagTableName: tag table name")] string tagTableName,
            [Description("tagName: tag name")] string tagName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiTag(softwarePath, tagTableName, tagName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI tag '{tagName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiScreenItem"), Description("[L2][HMI]Describe one HMI screen item (widget) by name under an HMI screen.")]
        public static ResponseObjectDescribe DescribeHmiScreenItem(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("screenName: screen name, e.g. 'Main'")] string screenName,
            [Description("itemName: widget name, e.g. 'BTN_Start'")] string itemName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiScreenItem(softwarePath, screenName, itemName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI screen item '{itemName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeObjectProperty"), Description("[L2][Reflection]Describe an object's nested property via reflection (members list). propertyPath supports dotted path.")]
        public static ResponseObjectDescribe DescribeObjectProperty(
            [Description("objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("objectPath: object path")] string objectPath,
            [Description("propertyPath: dotted property path, e.g. 'Connections' or 'PressedStateTags'")] string propertyPath,
            [Description("softwarePath: required for Block/Type")] string softwarePath = "",
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeObjectProperty(objectKind, objectPath, propertyPath, softwarePath, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing property '{propertyPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureStartStopUnifiedHmi"), Description("[L2][HMI-Unified] SHORTCUT for motor start/stop HMI. Ensures HMI_Connection_1 uses the correct PLC driver (1200/1500 vs 300/400 from CPU TypeIdentifier), 4 HMI tags (StartPB/StopPB/EStop/RunOut) with symbolic PLC binding, and a simple styled Main screen. Requires: Connect + OpenProject + PLC + Unified HMI. Call after EnsureUnifiedHmiScreen if you need a fixed screen size. Idempotent.")]
        public static ResponseMessage EnsureStartStopUnifiedHmi(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name (default 'Main')")] string screenName = "Main",
            [Description("tagTableName: target HMI tag table name (default '默认变量表')")] string tagTableName = "默认变量表",
            [Description("plcName: PLC software path / device name for connection + tag mapping (default 'PLC_1')")] string plcName = "PLC_1",
            [Description("connectionName: Unified HMI connection object name (default 'HMI_Connection_1')")] string connectionName = "HMI_Connection_1")
        {
            try
            {
                var res = Portal.EnsureStartStopUnifiedHmi(hmiSoftwarePath, screenName, tagTableName, plcName, connectionName);
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring unified HMI start/stop: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiScreen"), Description("[L2][HMI-Unified] Create or verify a WinCC Unified HMI screen exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. After creating a screen, add tags with EnsureUnifiedHmiTag, add controls with EnsureUnifiedHmiScreenItem, or apply a complete layout with ApplyUnifiedHmiScreenDesignJson.")]
        public static ResponseMessage EnsureUnifiedHmiScreen(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("width: optional screen width, 0 means keep current")] uint width = 0,
            [Description("height: optional screen height, 0 means keep current")] uint height = 0)
        {
            try
            {
                return Portal.EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, width, height);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI screen '{screenName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiTagTable"), Description("[L2][HMI-Unified] Create or verify a Unified HMI tag table exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. Create tag tables before adding tags with EnsureUnifiedHmiTag. Default tag table name is '默认变量表'.")]
        public static ResponseMessage EnsureUnifiedHmiTagTable(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("tagTableName: target HMI tag table name")] string tagTableName)
        {
            try
            {
                return Portal.EnsureUnifiedHmiTagTable(hmiSoftwarePath, tagTableName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI tag table '{tagTableName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiTag"), Description("[L2][HMI-Unified] Create or verify a Unified HMI external tag. For PLC-backed tags pass plcTag and address in the same call; the address must read back in Address/LogicalAddress, e.g. %DB200.DBX0.0. Requires: Connect + OpenProject + EnsureUnifiedHmiConnection + EnsureUnifiedHmiTagTable.")]
        public static ResponseMessage EnsureUnifiedHmiTag(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("tagTableName: target HMI tag table name")] string tagTableName,
            [Description("tagName: HMI tag name")] string tagName,
            [Description("hmiDataType: HMI data type, e.g. Bool, Int, Real, String")] string hmiDataType = "Bool",
            [Description("plcName: PLC name for symbolic binding")] string plcName = "PLC_1",
            [Description("plcTag: PLC tag name/path; empty means same as tagName")] string plcTag = "",
            [Description("connectionName: HMI connection name; empty keeps current/auto")] string connectionName = "",
            [Description("address: optional absolute PLC address, e.g. %DB200.DBX0.0. When supplied it is written as the verified HMI runtime address while plcTag remains available as the symbolic reference.")] string address = "")
        {
            try
            {
                return Portal.EnsureUnifiedHmiTag(hmiSoftwarePath, tagTableName, tagName, hmiDataType, plcName, plcTag, connectionName, address);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI tag '{tagName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiConnection"), Description("[L2][HMI-Unified] Create or verify the PLC↔HMI communication connection (HMI_Connection_1 by default). Requires: Connect + OpenProject + both PLC and Unified HMI devices. Must exist before PLC-backed HMI tags can exchange data. Call before EnsureUnifiedHmiTag with plcTag binding.")]
        public static ResponseObjectDescribe EnsureUnifiedHmiConnection(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("connectionName: HMI connection name")] string connectionName = "HMI_Connection_1",
            [Description("plcName: PLC software/device symbolic name")] string plcName = "PLC_1")
        {
            try
            {
                var res = Portal.EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI connection '{connectionName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiScreenItem"), Description("[L2][HMI-Unified] Create or verify a single Unified HMI control (button, lamp, IO field, etc.) on a screen. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. itemType: Button, Rectangle (lamp/indicator), IOField (value display/entry), or full CLR type name. For a complete screen layout use ApplyUnifiedHmiScreenDesignJson instead.")]
        public static ResponseMessage EnsureUnifiedHmiScreenItem(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: screen item name")] string itemName,
            [Description("itemType: Button, Rectangle/Lamp, IOField, or full CLR type name")] string itemType = "Button",
            [Description("left: X position")] int left = 0,
            [Description("top: Y position")] int top = 0,
            [Description("width: item width")] uint width = 120,
            [Description("height: item height")] uint height = 40,
            [Description("text: optional button/display text")] string text = "")
        {
            try
            {
                return Portal.EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, itemName, itemType, left, top, width, height, text);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI screen item '{itemName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiScreenDesignJson"), Description("[L2][HMI-Unified] PREFERRED for natural-language HMI design. Apply a complete JSON layout spec to a screen in one call: screen size + multiple controls (Button/Rectangle/IOField) with positions, text, and properties. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. Better than calling EnsureUnifiedHmiScreenItem multiple times. Use BuildUnifiedHmiLayoutDesignJson to generate the JSON from a grid description.")]
        public static ResponseMessage ApplyUnifiedHmiScreenDesignJson(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("designJson: JSON object with optional screen properties and items array")] string designJson)
        {
            try
            {
                return Portal.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, designJson);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI design to '{screenName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiThemeDesignJson"), Description("[L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a theme/palette. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiThemeDesignJson(
            [Description("themeJson: JSON {name?, palette:{Page?,Surface?,Text?,Border?,...}} with TIA ARGB colors like 0xFFF4F6F8.")] string themeJson)
        {
            try
            {
                var root = JsonNode.Parse(themeJson) as JsonObject
                    ?? throw new ArgumentException("themeJson root must be an object.");
                var design = HmiUnifiedThemeLayoutBuilder.BuildThemeDesign(root);
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = "Unified HMI theme design JSON built offline",
                    Data = design,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["offlineOnly"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid Unified HMI theme JSON: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiLayoutDesignJson"), Description("[L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a grid layout. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiLayoutDesignJson(
            [Description("layoutJson: JSON {grid?,left?,top?,gap?,columns?,cellWidth?,cellHeight?,items:[{name,type?,row?,col?,rowSpan?,colSpan?,text?,properties?}]}.")] string layoutJson)
        {
            try
            {
                var root = JsonNode.Parse(layoutJson) as JsonObject
                    ?? throw new ArgumentException("layoutJson root must be an object.");
                var design = HmiUnifiedThemeLayoutBuilder.BuildLayoutDesign(root);
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = "Unified HMI layout design JSON built offline",
                    Data = design,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["offlineOnly"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid Unified HMI layout JSON: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiTheme"), Description("[L2][HMI-Unified] Apply a theme/palette to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify with DescribeHmiScreenItem/readback before saving.")]
        public static ResponseMessage ApplyUnifiedHmiTheme(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("themeJson: JSON accepted by BuildUnifiedHmiThemeDesignJson.")] string themeJson)
        {
            try
            {
                var design = BuildUnifiedHmiThemeDesignJson(themeJson).Data?.ToJsonString() ?? "{}";
                return Portal.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI theme: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiLayout"), Description("[L2][HMI-Unified] Apply a grid layout to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify changed items with DescribeHmiScreenItem/readback before saving.")]
        public static ResponseMessage ApplyUnifiedHmiLayout(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("layoutJson: JSON accepted by BuildUnifiedHmiLayoutDesignJson.")] string layoutJson)
        {
            try
            {
                var design = BuildUnifiedHmiLayoutDesignJson(layoutJson).Data?.ToJsonString() ?? "{}";
                return Portal.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI layout: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildClassicHmiScreenXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI screen XML document from structured JSON. It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project.")]
        public static ResponseXmlBuild BuildClassicHmiScreenXml(
            [Description("designJson: JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus Name/Left/Top/Width/Height/Text/Properties.")] string designJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(ClassicHmiScreenXmlBuilder.BuildFromJson(designJson), "Classic HMI screen XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI screen XML offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcUdtXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 PLC UDT/PlcStruct XML document from structured JSON. Input: {members:[{name,datatype,externalWritable?,commentZhCn?}]}. It only returns XML; it does not connect to TIA Portal, import types, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcUdtXml(
            [Description("udtJson: JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, commentZhCn/comment.")] string udtJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildUdt(udtJson), "PLC UDT XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC UDT builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcTagTableXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 PLC tag table XML document from structured JSON. Input: {tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcTagTableXml(
            [Description("tagTableJson: JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, logicalAddress/address.")] string tagTableJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildTagTable(tagTableJson), "PLC tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC tag table builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcGlobalDbXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 PLC GlobalDB XML document from structured JSON. Input: {dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcGlobalDbXml(
            [Description("globalDbJson: JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[].")] string globalDbJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildGlobalDb(globalDbJson), "PLC GlobalDB XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC GlobalDB builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildStructuredTextXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: {operations:[{op:'if'|'else'|'endif'|'assignment'|'token'|'blank'|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildStructuredTextXml(
            [Description("structuredTextJson: JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token uses text.")] string structuredTextJson,
            [Description("innerOnly: true returns only inner XML for embedding into a block composer; false returns <StructuredText>.")] bool innerOnly = false)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildStructuredText(structuredTextJson, innerOnly), "PLC StructuredText XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid StructuredText builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildFlgNetCallXml"), Description("[L2][PLC-Builders][Offline] NARROW SCOPE: builds ONLY a LAD network that calls one FC with parameters. For general ladder (contacts/coils/SR/compare/Move/math) author S7DCL text and import with ImportBlocksFromDocuments — there is no XML builder for those, and hand-written FlgNet XML is the usual cause of import errors. Build a TIA V21 LAD FlgNet/v5 FC call network XML from structured JSON. Input: {callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?|symbol?|value?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildFlgNetCallXml(
            [Description("flgNetJson: JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use sourceKind='constant' and value.")] string flgNetJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildFlgNetCall(flgNetJson), "PLC FlgNet call XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid FlgNet call builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcFcBlockXml"), Description("[L2][PLC-Builders][Offline] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild ComposePlcFcBlockXml(
            [Description("fcBlockJson: JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or structuredText.operations[].")] string fcBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFcBlock(fcBlockJson), "PLC FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcFbBlockXml"), Description("[L2][PLC-Builders][Offline] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, create instance DBs, or modify projects.")]
        public static ResponseXmlBuild ComposePlcFbBlockXml(
            [Description("fbBlockJson: JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and structuredTextInnerXml or structuredText.operations[].")] string fbBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFbBlock(fbBlockJson), "PLC FB block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FB composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcLadFcBlockXml"), Description("[L2][PLC-Builders][Offline] NARROW SCOPE: every network must be an FC call; this cannot emit contacts/coils/SR/compare/Move/math. For general ladder, author S7DCL text (.s7dcl + .s7res) and import with ImportBlocksFromDocuments instead. Compose a TIA V21 LAD FC block XML containing one or more FlgNet/v5 FC-call networks. Each network is an FC call described as { callJson: { callName, parameters[] }, titleZhCn?, commentZhCn? }. Top-level: blockName, blockNumber, optional inputs/outputs members, optional commentZhCn / titleZhCn. Returns XML only; does not connect to TIA Portal or import. Pair with ImportBlock.")]
        public static ResponseXmlBuild ComposePlcLadFcBlockXml(
            [Description("ladFcBlockJson: JSON object with blockName, blockNumber, networks[] (each with callJson{callName,parameters[]}, optional titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn block-level.")] string ladFcBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeLadFcBlock(ladFcBlockJson), "PLC LAD FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC LAD FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "PlcBuildAndImport"), Description("[L1][PLC-Software] MAIN tool for creating new PLC blocks from natural language. Build one PLC artifact (UDT/tag table/GlobalDB/FC/FB) from structured JSON, then optionally import and compile. Use dryRun=true first to validate. Workflow: describe block in JSON → dryRun → review → dryRun=false to import. Replaces the multi-step Build*Xml + ImportBlock sequence.")]
        public static ResponsePlcProgramImport PlcBuildAndImport(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'. Required only when dryRun=false.")] string softwarePath,
            [Description("kind: udt|tagtable|globaldb|fc|fb")] string kind,
            [Description("json: structured JSON matching the corresponding BuildPlc* tool.")] string json,
            [Description("typeGroupPath: PLC data type group path for kind=udt.")] string typeGroupPath = "",
            [Description("tagFolderPath: PLC tag table group path for kind=tagtable.")] string tagFolderPath = "",
            [Description("blockGroupPath: PLC block group path for kind=globaldb|fc.")] string blockGroupPath = "",
            [Description("compileAfter: compile PLC after import when dryRun=false.")] bool compileAfter = true,
            [Description("dryRun: true builds XML and returns the import plan without importing/compiling.")] bool dryRun = true)
        {
            var failed = new List<ImportFailure>();
            var importedTypes = new List<string>();
            var importedTagTables = new List<string>();
            var importedBlocks = new List<string>();
            ResponseCompile? compile = null;

            try
            {
                var normalizedKind = NormalizePlcBuildKind(kind);
                var build = BuildPlcArtifact(normalizedKind, json);
                var xml = build["xml"]?.ToString() ?? "";
                var objectName = ResolveBuiltPlcObjectName(xml);
                var tempDir = Path.Combine(Path.GetTempPath(), "tia_mcp_plc_build_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                Directory.CreateDirectory(tempDir);
                var fileName = MakeSafeFileName(string.IsNullOrWhiteSpace(objectName) ? normalizedKind : objectName) + ".xml";
                var xmlPath = Path.Combine(tempDir, fileName);
                File.WriteAllText(xmlPath, xml, System.Text.Encoding.UTF8);

                var classifiedKind = ClassifyPlcXml(xmlPath, out var subKind, out var classifiedObjectName);
                if (classifiedKind == "unknown")
                {
                    failed.Add(new ImportFailure { Path = xmlPath, Error = "Generated XML could not be classified as a supported PLC XML artifact." });
                }

                var discoveredTypes = classifiedKind == "type" ? new List<string> { classifiedObjectName } : new List<string>();
                var discoveredTagTables = classifiedKind == "tagtable" ? new List<string> { classifiedObjectName } : new List<string>();
                var discoveredBlocks = classifiedKind == "block" ? new List<string> { classifiedObjectName } : new List<string>();

                if (!dryRun && failed.Count == 0)
                    compile = PlcBuildAndImportApply(softwarePath, typeGroupPath, tagFolderPath, blockGroupPath, compileAfter, xmlPath, classifiedKind, classifiedObjectName, importedTypes, importedTagTables, importedBlocks, failed);

                var response = BuildPlcProgramImportResponse(
                    tempDir,
                    dryRun,
                    discoveredTypes,
                    discoveredTagTables,
                    new List<string>(),
                    discoveredBlocks,
                    importedTypes,
                    importedTagTables,
                    new List<string>(),
                    importedBlocks,
                    failed,
                    compile);
                response.BuildKind = normalizedKind;
                response.GeneratedDirectory = tempDir;
                response.WrittenFiles = new[] { xmlPath };
                response.Message = dryRun
                    ? $"PLC build/import dry-run kind={normalizedKind}: generated '{xmlPath}', classified={classifiedKind}/{subKind}, failed={failed.Count}"
                    : $"PLC build/import kind={normalizedKind}: generated '{xmlPath}', importedTypes={importedTypes.Count}, importedTagTables={importedTagTables.Count}, importedBlocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}";
                response.Meta ??= new JsonObject();
                response.Meta["offlineBuildOk"] = build["ok"]?.GetValue<bool>() == true;
                response.Meta["classifiedKind"] = classifiedKind;
                response.Meta["classifiedSubKind"] = subKind;
                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running PlcBuildAndImport: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        private static ResponseCompile? PlcBuildAndImportApply(
            string softwarePath,
            string typeGroupPath,
            string tagFolderPath,
            string blockGroupPath,
            bool compileAfter,
            string xmlPath,
            string classifiedKind,
            string classifiedObjectName,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedBlocks,
            List<ImportFailure> failed)
        {
            if (classifiedKind == "type")
            {
                try { Portal.ImportType(softwarePath, typeGroupPath, xmlPath); importedTypes.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else if (classifiedKind == "tagtable")
            {
                try { Portal.ImportPlcTagTable(softwarePath, tagFolderPath, xmlPath); importedTagTables.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else if (classifiedKind == "block")
            {
                try { Portal.ImportBlock(softwarePath, blockGroupPath, xmlPath); importedBlocks.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else
            {
                failed.Add(new ImportFailure { Path = xmlPath, Error = "Unsupported classified kind: " + classifiedKind });
            }

            if (!compileAfter || failed.Count != 0)
                return null;

            try
            {
                var result = Portal.CompileSoftware(softwarePath);
                return BuildCompileResponse(softwarePath, result);
            }
            catch (PortalException pex)
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = $"[{pex.Code}] {pex.Message}" });
                return null;
            }
        }

        private static ResponseXmlBuild BuildOfflineXmlBuilderReport(JsonObject data, string successMessage)
        {
            var ok = data["ok"]?.GetValue<bool>() == true;
            var xml = data["xml"]?.GetValue<string>();

            // Builders use one of two error shapes:
            //   PlcBuilderToolJson:        ["error"] = string?
            //   ClassicHmi*XmlBuilder:     ["errors"] = JsonArray of string
            string[]? errorList = null;
            if (data["errors"] is JsonArray errArr)
            {
                errorList = errArr.Where(e => e != null).Select(e => e!.GetValue<string>()).ToArray();
                if (errorList.Length == 0) errorList = null;
            }
            else
            {
                var singleError = data["error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(singleError))
                    errorList = new[] { singleError! };
            }

            string[]? warningList = null;
            if (data["warnings"] is JsonArray warnArr)
            {
                warningList = warnArr.Where(w => w != null).Select(w => w!.GetValue<string>()).ToArray();
                if (warningList.Length == 0) warningList = null;
            }

            return new ResponseXmlBuild
            {
                Ok = ok,
                Message = ok ? successMessage : successMessage + " with validation findings",
                Data = data,
                Xml = xml,
                Errors = errorList,
                Warnings = warningList,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok,
                    ["offlineOnly"] = true
                }
            };
        }


        private static string NormalizePlcBuildKind(string kind)
        {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            return normalized switch
            {
                "udt" => "udt",
                "type" => "udt",
                "plcstruct" => "udt",
                "tagtable" => "tagtable",
                "plctagtable" => "tagtable",
                "globaldb" => "globaldb",
                "db" => "globaldb",
                "fc" => "fc",
                "function" => "fc",
                "fb" => "fb",
                "functionblock" => "fb",
                _ => throw new ArgumentException("Unsupported PLC build kind. Supported values: udt|tagtable|globaldb|fc|fb.")
            };
        }

        private static JsonObject BuildPlcArtifact(string kind, string json)
        {
            return kind switch
            {
                "udt" => PlcBuilderToolJson.BuildUdt(json),
                "tagtable" => PlcBuilderToolJson.BuildTagTable(json),
                "globaldb" => PlcBuilderToolJson.BuildGlobalDb(json),
                "fc" => PlcBuilderToolJson.ComposeFcBlock(json),
                "fb" => PlcBuilderToolJson.ComposeFbBlock(json),
                _ => throw new ArgumentException("Unsupported PLC build kind: " + kind)
            };
        }

        private static string ResolveBuiltPlcObjectName(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
                var attrs = obj?.Element("AttributeList");
                var name = attrs?.Element("Name")?.Value;
                return string.IsNullOrWhiteSpace(name) ? "" : name!.Trim();
            }
            catch
            {
                return "";
            }
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(name) ? "plc_artifact" : name.Trim())
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();
            var safe = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "plc_artifact" : safe;
        }

        private static ResponseCompile BuildCompileResponse(string softwarePath, object result)
        {
            var collected = new CompilerMessageCollectResult();
            try
            {
                var messagesValue = result.GetType().GetProperty("Messages")?.GetValue(result);
                collected = CollectCompilerMessages(messagesValue);
            }
            catch
            {
                // best effort only
            }

            var state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "";
            var errorCount = ReadIntProperty(result, "ErrorCount");
            var warningCount = ReadIntProperty(result, "WarningCount");
            return new ResponseCompile
            {
                Message = $"Software '{softwarePath}' compiled. State={state} Errors={errorCount} Warnings={warningCount}",
                State = state,
                ErrorCount = errorCount,
                WarningCount = warningCount,
                Messages = collected.Raw,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = !state.Equals("Error", StringComparison.OrdinalIgnoreCase),
                    ["errorDetailCount"] = collected.Errors.Count,
                    ["warningDetailCount"] = collected.Warnings.Count
                }
            };
        }

        private static int ReadIntProperty(object value, string propertyName)
        {
            var raw = value.GetType().GetProperty(propertyName)?.GetValue(value);
            if (raw is int i) return i;
            return int.TryParse(raw?.ToString(), out var parsed) ? parsed : 0;
        }

        [McpServerTool(Name = "BuildClassicHmiTagTableXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI tag table XML document from structured JSON. Supports plain HMI tags and symbolic PLC bindings through Connection + ControllerTag/PlcTag. It does not connect to TIA Portal, import tags, or modify projects.")]
        public static ResponseXmlBuild BuildClassicHmiTagTableXml(
            [Description("tableJson: JSON object with Name/TableName and Tags[]. Tag fields: Name, DataType, Length, optional Connection and ControllerTag/PlcTag.")] string tableJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(ClassicHmiTagTableXmlBuilder.BuildFromJson(tableJson), "Classic HMI tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI tag table XML offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildClassicHmiMinimalPackage"), Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package from structured JSON. It returns tag-table XML, screen XML, import order, and readiness checks that screen item tag references are declared in the tag table. It does not connect to TIA Portal, import files, or modify projects.")]
        public static ResponseJsonReport BuildClassicHmiMinimalPackage(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable. Screen items may reference HMI tags through Tag/HmiTag/ProcessValueTag or Properties.*Tag.")] string packageJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.BuildFromJson(packageJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package built offline" : "Classic HMI minimal package built with validation findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI minimal package offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WriteClassicHmiMinimalPackageFiles"), Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package and write tag-table XML, screen XML, and manifest JSON to an output directory. It does not connect to TIA Portal, import files, or modify projects.")]
        public static ResponseJsonReport WriteClassicHmiMinimalPackageFiles(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable.")] string packageJson,
            [Description("outputDirectory: directory where tag-table XML, screen XML, and manifest JSON will be written.")] string outputDirectory)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.WriteFiles(packageJson, outputDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;

                string[]? files = null;
                if (data["files"] is JsonArray arr)
                {
                    files = arr.Where(f => f != null).Select(f => f!.GetValue<string>()).ToArray();
                    if (files.Length == 0) files = null;
                }
                var outDir = data["outputDirectory"]?.GetValue<string>();

                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package files written offline" : "Classic HMI minimal package files written with validation findings",
                    Data = data,
                    OutputPath = outDir,
                    OutputFiles = files,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["fileCount"] = data["fileCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing Classic HMI minimal package files offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackageFiles"), Description("[L2][HMI-Classic]Offline-only helper: validate an already written Classic/Basic HMI minimal package folder or manifest. It reads manifest/XML, checks parseability and HMI tag references, and does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport ValidateClassicHmiMinimalPackageFiles(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFiles(path);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package files validated offline" : "Classic HMI minimal package file validation found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["missingTagCount"] = data["missingTagCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI minimal package files offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackagePlcSync"), Description("[L2][HMI-Classic]Offline-only helper: validate that Classic/Basic HMI tag-table ControllerTag/PlcTag bindings exist in a caller-provided exact PLC symbol list. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport ValidateClassicHmiMinimalPackagePlcSync(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path,
            [Description("plcSymbolsJson: JSON array of exact PLC symbols, or object with Symbols[]. Example: [\"DB1_MotorData.Motor.Start\"].")] string plcSymbolsJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(path, plcSymbolsJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package PLC symbol sync validated offline" : "Classic HMI minimal package PLC symbol sync validation found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["missingPlcSymbolCount"] = data["missingPlcSymbolCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI PLC symbol sync offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcSymbolManifestFromXmlPath"), Description("[L2][PLC-Builders]Offline-only helper: extract a PLC symbol manifest from PLC tag table and GlobalDB XML files or directories. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildPlcSymbolManifestFromXmlPath(
            [Description("path: XML file or directory containing PLC tag table / GlobalDB XML exports.")] string path)
        {
            try
            {
                var data = PlcSymbolManifestBuilder.BuildFromXmlPath(path);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "PLC symbol manifest built offline" : "PLC symbol manifest built with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["symbolCount"] = data["symbolCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building PLC symbol manifest offline: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiOfflineValidationSuite"), Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI validation suite covering PLC symbol extraction, HMI package generation, HMI tag references, and PLC-HMI sync positive/negative gates. It writes reports only to the requested report directory.")]
        public static ResponseJsonReport RunClassicHmiOfflineValidationSuite(
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = ClassicHmiOfflineValidationSuite.Run(reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI offline validation suite passed" : "Classic HMI offline validation suite found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI offline validation suite: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunOfflineReleaseValidationSuite"), Description("[L2][Validation]Offline-only helper: run the release smoke suite covering PLC Builder, Classic HMI, PLC symbol extraction, Unified HMI template layout, HMI action recipes, and online-monitoring safety guardrails. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport RunOfflineReleaseValidationSuite(
            [Description("workspaceRoot: repository/workspace root containing TMP_EXPORT, docs, and tools.")] string workspaceRoot,
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = OfflineReleaseValidationSuite.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Offline release validation suite passed" : "Offline release validation suite found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running offline release validation suite: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunV2PlanCompletionAudit"), Description("[L2][Validation]Offline-only strict audit for docs/TIA_MCP_常见操作全覆盖方案_V2_二次优化计划.md. It reports verified hard-gate percentage and blocks 100% claims when real TIA/online evidence is missing.")]
        public static ResponseJsonReport RunV2PlanCompletionAudit(
            [Description("workspaceRoot: repository/workspace root containing docs, tools, and reports.")] string workspaceRoot,
            [Description("reportDirectory: directory where V2 audit reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = V2PlanCompletionAuditor.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = "V2 plan completion audit finished",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running V2 plan completion audit: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseDiagnosticReport"), Description("[L2][Reports]Build an offline diagnostic report from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseDiagnosticReport(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var data = ReleaseDiagnosticReportBuilder.Build(root);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release diagnostic report built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release diagnostic report: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseRunbook"), Description("[L2][Reports]Build an offline first-user runbook from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseRunbook(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var diagnostics = root["diagnostics"] as JsonObject ?? ReleaseDiagnosticReportBuilder.Build(root);
                var data = ReleaseRunbookBuilder.Build(root, diagnostics);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release runbook built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release runbook: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseManifest"), Description("[L2][Reports]Build an offline machine-readable release manifest from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseManifest(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var diagnostics = root["diagnostics"] as JsonObject ?? ReleaseDiagnosticReportBuilder.Build(root);
                var runbook = root["runbook"] as JsonObject ?? ReleaseRunbookBuilder.Build(root, diagnostics);
                var data = ReleaseManifestBuilder.Build(root, diagnostics, runbook);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release manifest built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release manifest: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RebuildReleaseHandoffArtifacts"), Description("[L2][Reports]Rebuild diagnostics, runbook, and manifest files from an existing OfflineReleaseValidationSuite JSON report. Offline-only and does not connect to TIA Portal.")]
        public static ResponseJsonReport RebuildReleaseHandoffArtifacts(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath,
            [Description("outputDirectory: directory where rebuilt handoff artifacts will be written.")] string outputDirectory)
        {
            try
            {
                var data = ReleaseHandoffArtifactBuilder.RebuildFromSuiteJson(offlineReleaseSuiteJsonPath, outputDirectory);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release handoff artifacts rebuilt.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error rebuilding release handoff artifacts: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiTemporaryImportPreflight"), Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI temporary-import preflight. It checks TIA V21 environment, Openness group, package files, PLC-HMI sync, and emits an import/readback plan without connecting to TIA Portal or creating projects.")]
        public static ResponseJsonReport RunClassicHmiTemporaryImportPreflight(
            [Description("workspaceRoot: repository/workspace root.")] string workspaceRoot,
            [Description("reportDirectory: directory where preflight files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = ClassicHmiTemporaryImportPreflightSuite.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI temporary import preflight passed" : "Classic HMI temporary import preflight blocked",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI temporary import preflight: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunHmiTemplatePlcSyncPrecheckSuite"), Description("[L2][Validation]Offline-only helper: verify Unified HMI template RequiredTags against real PLC tag/DB-member XML symbols before any HMI binding. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport RunHmiTemplatePlcSyncPrecheckSuite(
            [Description("templateDirectory: directory containing Unified HMI template JSON files.")] string templateDirectory,
            [Description("plcXmlPath: PLC XML file or directory exported from TIA, containing tag tables and/or GlobalDB XML.")] string plcXmlPath,
            [Description("reportDirectory: directory where reports will be written.")] string reportDirectory,
            [Description("mappingFilePath: optional explicit mapping file produced by the mapping skeleton flow.")] string mappingFilePath = "")
        {
            try
            {
                var data = HmiTemplatePlcSyncPrecheckSuite.Run(templateDirectory, plcXmlPath, reportDirectory, mappingFilePath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template PLC sync precheck suite completed" : "HMI template PLC sync precheck suite found blocking issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI template PLC sync precheck suite: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignJson"), Description("[L2][HMI-Unified]Offline-only helper: convert one Unified HMI template JSON file into the execution JSON accepted by ApplyUnifiedHmiScreenDesignJson, with layout QA attached. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignJson(
            [Description("templateFile: full path to a Unified HMI template JSON file")] string templateFile,
            [Description("fallbackWidth: width used only if the template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if the template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
                {
                    var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                    var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                    return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
                });
                var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
                var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
                var itemCount = (designJson["items"] as JsonArray)?.Count ?? 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design JSON built offline" : "Unified HMI template execution design JSON built with blocking layout findings",
                    Data = new JsonObject
                    {
                        ["format"] = "tia-unified-hmi-template-apply-design-offline-v1",
                        ["timestamp"] = DateTime.Now.ToString("O"),
                        ["offlineOnly"] = true,
                        ["templateFile"] = templateFile,
                        ["ok"] = ok,
                        ["itemCount"] = itemCount,
                        ["layoutQa"] = layout,
                        ["applyDesign"] = designJson
                    },
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["itemCount"] = itemCount
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design JSON: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignManifest"), Description("[L2][HMI-Unified]Offline-only helper: build a directory-level manifest for Unified HMI templates. It summarizes layout QA and execution-design readiness for every unified_*.json template without returning full apply payloads. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignManifest(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("fallbackWidth: width used only if a template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if a template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var files = Directory.Exists(templateDirectory)
                    ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
                var rows = new JsonArray();
                var referenceAnalysis = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, "", "");
                var referenceRows = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .ToDictionary(
                        x => x["templateName"]?.ToString() ?? "",
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                var referenceRowsByFile = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Where(x => !string.IsNullOrWhiteSpace(x["file"]?.ToString()))
                    .ToDictionary(
                        x => Path.GetFullPath(x["file"]?.ToString() ?? ""),
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var templateName = Path.GetFileNameWithoutExtension(file);
                    referenceRowsByFile.TryGetValue(Path.GetFullPath(file), out var referenceRow);
                    if (referenceRow == null)
                    {
                        referenceRows.TryGetValue(templateName, out referenceRow);
                    }
                    rows.Add(BuildUnifiedHmiTemplateApplyDesignManifestRow(file, fallbackWidth, fallbackHeight, referenceRow));
                }

                var failed = rows.OfType<JsonObject>().Count(x => x["ok"]?.GetValue<bool>() != true);
                var totalItems = rows.OfType<JsonObject>().Sum(x => x["itemCount"]?.GetValue<int>() ?? 0);
                var root = new JsonObject
                {
                    ["format"] = "tia-unified-hmi-template-apply-design-manifest-v1",
                    ["timestamp"] = DateTime.Now.ToString("O"),
                    ["offlineOnly"] = true,
                    ["templateDirectory"] = templateDirectory,
                    ["templateCount"] = files.Length,
                    ["failed"] = failed,
                    ["totalItems"] = totalItems,
                    ["ok"] = failed == 0,
                    ["policy"] = new JsonObject
                    {
                        ["fullPayloadTool"] = "BuildUnifiedHmiTemplateApplyDesignJson",
                        ["applyTool"] = "ApplyUnifiedHmiScreenDesignJson",
                        ["rule"] = "Use this manifest as a pre-apply gate; inspect a full payload for any template before writing it to TIA."
                    },
                    ["templates"] = rows
                };

                var ok = failed == 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design manifest built offline" : "Unified HMI template execution design manifest has blocking findings",
                    Data = root,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["templateCount"] = files.Length,
                        ["failed"] = failed,
                        ["totalItems"] = totalItems
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design manifest: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static JsonObject BuildUnifiedHmiTemplateApplyDesignManifestRow(string templateFile, int fallbackWidth, int fallbackHeight, JsonObject? referenceRow)
        {
            var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
            {
                var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
            });
            var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
            var items = designJson["items"] as JsonArray ?? new JsonArray();
            var errors = layout["errors"] as JsonArray ?? new JsonArray();
            var warnings = layout["warnings"] as JsonArray ?? new JsonArray();
            var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
            var eventReadiness = BuildUnifiedHmiTemplateEventReadiness(referenceRow);
            var row = new JsonObject
            {
                ["templateFile"] = templateFile,
                ["templateName"] = layout["templateName"]?.DeepClone(),
                ["ok"] = ok,
                ["status"] = layout["status"]?.DeepClone(),
                ["width"] = designJson["width"]?.DeepClone(),
                ["height"] = designJson["height"]?.DeepClone(),
                ["itemCount"] = items.Count,
                ["errorCount"] = errors.Count,
                ["warningCount"] = warnings.Count,
                ["errors"] = new JsonArray(errors.Select(x => x?.DeepClone()).ToArray()),
                ["warnings"] = new JsonArray(warnings.Select(x => x?.DeepClone()).ToArray()),
                ["layoutDensity"] = layout["layoutDensity"]?.DeepClone(),
                ["applyDesignReady"] = layout["executionJsonChecked"]?.DeepClone(),
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["eventReadiness"] = eventReadiness,
                ["recommendedNextAction"] = ok
                    ? "Inspect full payload with BuildUnifiedHmiTemplateApplyDesignJson, then apply in a temporary TIA project before using a real project."
                    : "Fix blocking layout/template findings before applying to TIA."
            };
            row["eventRecommendedNextAction"] = eventReadiness["recommendedNextAction"]?.DeepClone();
            return row;
        }

        private static JsonObject BuildUnifiedHmiTemplateEventReadiness(JsonObject? referenceRow)
        {
            if (referenceRow == null)
            {
                return new JsonObject
                {
                    ["status"] = "not-analyzed",
                    ["effectiveActionCount"] = 0,
                    ["safeDeterministicActionCount"] = 0,
                    ["apiDiscoveryRequiredCount"] = 0,
                    ["highRiskActionCount"] = 0,
                    ["todoActionCount"] = 0,
                    ["missingTargetCount"] = 0,
                    ["duplicateActionCount"] = 0,
                    ["commandActionCount"] = 0,
                    ["navigationActionCount"] = 0,
                    ["recommendedNextAction"] = "Run HmiTemplateReferenceAnalyzer first; event and binding readiness could not be joined for this template."
                };
            }

            var summary = referenceRow["actionRecipeSummary"] as JsonObject ?? new JsonObject();
            var effectiveRecipes = summary["effectiveRecipes"] as JsonArray ?? new JsonArray();
            var generated = new JsonArray();
            var safeDeterministic = 0;
            var apiDiscovery = 0;
            var todo = 0;
            var blocked = 0;
            var command = 0;
            var navigation = 0;

            foreach (var recipeNode in effectiveRecipes.OfType<JsonObject>())
            {
                var targetTags = (recipeNode["targetTags"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "");
                var built = HmiActionScriptRecipeBuilder.Build(
                    recipeNode["recipeKind"]?.ToString() ?? "",
                    recipeNode["event"]?.ToString() ?? "",
                    targetTags,
                    recipeNode["targetScreen"]?.ToString() ?? "",
                    recipeNode["targetPopup"]?.ToString() ?? "");
                generated.Add(built);
                var kind = built["recipeKind"]?.ToString() ?? "";
                var safety = built["safetyLevel"]?.ToString() ?? "";
                if (string.Equals(safety, "command", StringComparison.OrdinalIgnoreCase)) command++;
                if (string.Equals(safety, "navigation", StringComparison.OrdinalIgnoreCase)) navigation++;
                if (built["requiresApiDiscovery"]?.GetValue<bool>() == true) apiDiscovery++;
                if (built["applyBlocked"]?.GetValue<bool>() == true || !string.IsNullOrWhiteSpace(built["applyBlockedReason"]?.ToString())) blocked++;
                if ((built["script"]?.ToString() ?? "").IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0) todo++;
                if (built["ok"]?.GetValue<bool>() == true
                    && built["requiresApiDiscovery"]?.GetValue<bool>() != true
                    && (kind.Equals("set-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("reset-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("toggle-bit", StringComparison.OrdinalIgnoreCase)))
                {
                    safeDeterministic++;
                }
            }

            var missingTargets = summary["missingTargets"] as JsonArray ?? new JsonArray();
            var duplicateActions = summary["duplicateActions"] as JsonArray ?? new JsonArray();
            var highRisk = GetManifestInt(summary, "highRiskWrites");
            var missingRequiredTags = summary["missingRequiredTags"] as JsonArray ?? new JsonArray();
            var status = "ready-for-temp-project-validation";
            var recommended = "Generate safe deterministic scripts, then verify HMI tags, PLC-side symbols, TIA SyntaxCheck, and readback in a temporary project.";

            if (missingRequiredTags.Count > 0 || missingTargets.Count > 0 || duplicateActions.Count > 0)
            {
                status = "needs-template-fix";
                recommended = "Fix missing action tags, missing target screens/popups, or duplicate actions before applying events.";
            }
            else if (highRisk > 0)
            {
                status = "blocked-by-high-risk-actions";
                recommended = "High-risk value writes require explicit operator confirmation, range validation, SyntaxCheck, and readback before any apply path is enabled.";
            }
            else if (apiDiscovery > 0 || todo > 0)
            {
                status = "needs-api-discovery";
                recommended = "Keep API-discovery actions blocked until the exact WinCC Unified V21 event/navigation/popup API is verified from TIA readback.";
            }

            return new JsonObject
            {
                ["status"] = status,
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["effectiveActionCount"] = GetManifestInt(summary, "effectiveActionCount"),
                ["safeDeterministicActionCount"] = safeDeterministic,
                ["apiDiscoveryRequiredCount"] = apiDiscovery,
                ["blockedActionCount"] = blocked,
                ["highRiskActionCount"] = highRisk,
                ["todoActionCount"] = todo,
                ["missingRequiredTagCount"] = missingRequiredTags.Count,
                ["missingTargetCount"] = missingTargets.Count,
                ["duplicateActionCount"] = duplicateActions.Count,
                ["commandActionCount"] = command,
                ["navigationActionCount"] = navigation,
                ["recommendedNextAction"] = recommended
            };
        }

        private static int GetManifestInt(JsonObject? obj, string name)
        {
            if (obj == null || obj[name] == null) return 0;
            return int.TryParse(obj[name]?.ToString(), out var value) ? value : 0;
        }

        [McpServerTool(Name = "BindUnifiedHmiButtonPressedTag"), Description("[L2][HMI-Unified]Bind a Unified HMI button PressedStateTags entry to an HMI tag (momentary press behavior, best-effort).")]
        public static ResponseMessage BindUnifiedHmiButtonPressedTag(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("tagName: HMI tag name to write while pressed")] string tagName)
        {
            try
            {
                return Portal.BindUnifiedHmiButtonPressedTag(hmiSoftwarePath, screenName, buttonName, tagName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error binding button '{buttonName}' to tag '{tagName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListUnifiedHmiApiTypes"), Description("[L2][HMI-Unified]List loaded WinCC Unified HMI API types/enums by name filter, useful for discovering event and dynamization types.")]
        public static ResponseStringList ListUnifiedHmiApiTypes(
            [Description("nameContains: case-insensitive substring filter, e.g. Dynamization or EventType")] string nameContains = "",
            [Description("limit: max returned type lines")] int limit = 500)
        {
            try
            {
                var items = Portal.ListUnifiedHmiApiTypes(nameContains, limit);
                return new ResponseStringList
                {
                    Message = $"Unified HMI API types listed (filter='{nameContains}')",
                    Items = items,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing Unified HMI API types: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiButtonEventHandler"), Description("[L2][HMI-Unified]Ensure a Unified HMI button event handler exists and return its API shape. eventType must match HmiButtonEventType.")]
        public static ResponseMessage EnsureUnifiedHmiButtonEventHandler(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: HmiButtonEventType value, e.g. Tapped, Down, Up; use ListUnifiedHmiApiTypes('HmiButtonEventType') to inspect")] string eventType)
        {
            try
            {
                return Portal.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring button event handler '{buttonName}.{eventType}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeUnifiedHmiButtonEventScript"), Description("[L2][HMI-Unified]Describe a Unified HMI button event handler Script property and its current object members/attributes.")]
        public static ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: enum value, e.g. Tapped, Down, Up")] string eventType,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing button event script '{buttonName}.{eventType}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetUnifiedHmiButtonEventScriptCode"), Description("[L2][HMI-Unified]Set ScriptCode on a Unified HMI button event ScriptDynamization and run SyntaxCheck.")]
        public static ResponseMessage SetUnifiedHmiButtonEventScriptCode(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: enum value, e.g. Tapped, Down, Up")] string eventType,
            [Description("scriptCode: JavaScript code for the event")] string scriptCode,
            [Description("globalDefinitionAreaScriptCode: optional global definitions for the script")] string globalDefinitionAreaScriptCode = "",
            [Description("async: whether the script is async")] bool async = false)
        {
            try
            {
                return Portal.SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, scriptCode, globalDefinitionAreaScriptCode, async);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting button event script '{buttonName}.{eventType}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiButtonActionScript"), Description("[L2][HMI-Unified]Build a safe Unified HMI button action script from a high-level action recipe without connecting to TIA.")]
        public static ResponseMessage BuildUnifiedHmiButtonActionScript(
            [Description("actionKind: set-bit, reset-bit, toggle-bit, open-popup, goto-screen, confirm-write")] string actionKind,
            [Description("eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released")] string eventType,
            [Description("targetTag: target HMI tag for set/reset/toggle actions")] string targetTag = "",
            [Description("targetScreen: target screen for goto-screen actions")] string targetScreen = "",
            [Description("targetPopup: target popup for open-popup actions")] string targetPopup = "")
        {
            try
            {
                var tags = string.IsNullOrWhiteSpace(targetTag)
                    ? Array.Empty<string>()
                    : new[] { targetTag };
                var recipe = HmiActionScriptRecipeBuilder.Build(actionKind, eventType, tags, targetScreen, targetPopup);
                return new ResponseMessage
                {
                    Message = recipe["ok"]?.GetValue<bool>() == true
                        ? "Unified HMI button action script recipe built."
                        : "Unified HMI button action script recipe has validation errors.",
                    Meta = recipe
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI button action script: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunHmiActionScriptRecipeSafetySelfTest"), Description("[L2][Diagnostics]Offline-only helper: prove deterministic HMI button action scripts are allowed only for safe set/reset/toggle bit recipes, while high-risk writes and unverified navigation/popup recipes are blocked.")]
        public static ResponseJsonReport RunHmiActionScriptRecipeSafetySelfTest()
        {
            try
            {
                var data = HmiActionScriptRecipeBuilder.RunSafetySelfTest();
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI action script recipe safety self-test passed" : "HMI action script recipe safety self-test failed",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI action script recipe safety self-test: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiButtonAction"), Description("[L2][HMI-Unified]Generate and apply a deterministic Unified HMI button action. Only set-bit/reset-bit/toggle-bit are applied; high-risk or TODO recipes are rejected.")]
        public static ResponseMessage EnsureUnifiedHmiButtonAction(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released")] string eventType,
            [Description("actionKind: set-bit, reset-bit, or toggle-bit")] string actionKind,
            [Description("targetTag: verified target HMI tag")] string targetTag)
        {
            try
            {
                var recipe = HmiActionScriptRecipeBuilder.Build(actionKind, eventType, new[] { targetTag });
                var kind = recipe["recipeKind"]?.ToString() ?? "";
                var script = recipe["script"]?.ToString() ?? "";
                var allowed = new[] { "set-bit", "reset-bit", "toggle-bit" };
                if (!allowed.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Only set-bit/reset-bit/toggle-bit recipes can be applied by this safe high-level tool.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected by safety policy.", Meta = recipe };
                }
                if (recipe["ok"]?.GetValue<bool>() != true || string.IsNullOrWhiteSpace(script) || script.IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Recipe has errors, empty script, or TODO placeholder.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected because the generated script is not directly applicable.", Meta = recipe };
                }

                var ensure = Portal.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);
                var set = Portal.SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, script, "", false);
                recipe["applyStatus"] = set.Meta?["success"]?.GetValue<bool>() == true ? "applied" : "apply-failed";
                recipe["ensureMessage"] = ensure.Message ?? "";
                recipe["setMessage"] = set.Message ?? "";
                recipe["setMeta"] = set.Meta?.DeepClone();
                return new ResponseMessage
                {
                    Message = "Unified HMI button action applied via generated recipe.",
                    Meta = recipe
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI button action '{buttonName}.{eventType}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiDynamization"), Description("[L2][HMI-Unified]Ensure a Unified HMI item property dynamization exists using a concrete dynamization type and return its API shape.")]
        public static ResponseMessage EnsureUnifiedHmiDynamization(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: HMI screen item name")] string itemName,
            [Description("propertyName: target property name, e.g. BackColor or Visible")] string propertyName,
            [Description("dynamizationType: type short name/full name; empty tries common candidates and returns errors if unsupported")] string dynamizationType = "")
        {
            try
            {
                return Portal.EnsureUnifiedHmiDynamization(hmiSoftwarePath, screenName, itemName, propertyName, dynamizationType);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring dynamization '{itemName}.{propertyName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BindUnifiedHmiTagDynamization"), Description("[L2][HMI-Unified]Ensure a Unified HMI TagDynamization exists for an item property and bind it to an HMI tag.")]
        public static ResponseMessage BindUnifiedHmiTagDynamization(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: HMI screen item name")] string itemName,
            [Description("propertyName: target property name, e.g. BackColor or Visible")] string propertyName,
            [Description("tagName: HMI tag name used as the dynamic source")] string tagName,
            [Description("dataType: tag data type, e.g. Bool, Int, Real")] string dataType = "Bool",
            [Description("plcTag: optional PLC tag/path")] string plcTag = "",
            [Description("address: optional absolute address")] string address = "")
        {
            try
            {
                return Portal.BindUnifiedHmiTagDynamization(hmiSoftwarePath, screenName, itemName, propertyName, tagName, dataType, plcTag, address);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error binding tag dynamization '{itemName}.{propertyName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiScreens"), Description("[L2][HMI] List all screen names in an HMI (Classic or Unified). Requires: Connect + OpenProject. softwarePath from GetProjectTree. Use before EnsureUnifiedHmiScreen/ExportHmiScreen to confirm which screens exist.")]
        public static ResponseStringList GetHmiScreens(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetHmiScreens(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI screens listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI screens for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiTagTables"), Description("[L2][HMI]List HMI tag table names (Classic/Unified, best-effort)")]
        public static ResponseStringList GetHmiTagTables(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetHmiTagTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tag tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tag tables for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiTags"), Description("[L2][HMI]List HMI tag names (best-effort). If tagTableName empty, returns tags found at root collection if available.")]
        public static ResponseStringList GetHmiTags(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: optional tag table name to list tags from")] string tagTableName = "")
        {
            try
            {
                var items = Portal.GetHmiTags(softwarePath, tagTableName);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tags listed for '{softwarePath}' (table='{tagTableName}')",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tags for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiConnections"), Description("[L2][HMI]List HMI connection names (Classic/Unified, best-effort)")]
        public static ResponseStringList GetHmiConnections(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetHmiConnections(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI connections listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI connections for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiScreen"), Description("[L2][HMI]Export one HMI screen to a file (best-effort; requires Openness export support)")]
        public static ResponseExportFile ExportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("screenName: the screen name to export")] string screenName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)")] string exportPath)
        {
            try
            {
                Portal.ExportHmiScreen(softwarePath, screenName, exportPath);
                return new ResponseExportFile
                {
                    Message = $"HMI screen '{screenName}' exported",
                    ExportPath = exportPath,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI screen '{screenName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI screen '{screenName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiTagTable"), Description("[L2][HMI]Export one HMI tag table to a file (best-effort; requires Openness export support)")]
        public static ResponseExportFile ExportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: the tag table name to export")] string tagTableName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\tagtable.xml)")] string exportPath)
        {
            try
            {
                Portal.ExportHmiTagTable(softwarePath, tagTableName, exportPath);
                return new ResponseExportFile
                {
                    Message = $"HMI tag table '{tagTableName}' exported",
                    ExportPath = exportPath,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI tag table '{tagTableName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI tag table '{tagTableName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiConnection"), Description("[L2][HMI]Export one HMI connection to a file (best-effort; Classic/Unified via reflection)")]
        public static ResponseExportFile ExportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("connectionName: the HMI connection name to export")] string connectionName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\connection.xml)")] string exportPath)
        {
            try
            {
                Portal.ExportHmiConnection(softwarePath, connectionName, exportPath);
                return new ResponseExportFile
                {
                    Message = $"HMI connection '{connectionName}' exported",
                    ExportPath = exportPath,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI connection '{connectionName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI connection '{connectionName}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiProgram"), Description("[L2][HMI]Batch export HMI screens/tagtables into a directory (best-effort)")]
        public static ResponseBatchExport ExportHmiProgram(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("exportDir: directory to write exported files into")] string exportDir,
            [Description("exportScreens: default true")] bool exportScreens = true,
            [Description("exportTagTables: default true")] bool exportTagTables = true)
        {
            try
            {
                var res = Portal.ExportHmiProgram(softwarePath, exportDir, exportScreens, exportTagTables);
                if (res != null)
                {
                    return new ResponseBatchExport
                    {
                        Message = $"HMI program exported to '{exportDir}'",
                        Exported = res.Value.Exported,
                        Failed = res.Value.Failed,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }
                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI program: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreen"), Description("[L2][HMI]Import one HMI screen XML file into an HMI program (best-effort; Classic/Unified via reflection)")]
        public static ResponseMessage ImportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported screen XML")] string importPath)
        {
            try
            {
                Portal.ImportHmiScreen(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI screen imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI screen from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screen: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTable"), Description("[L2][HMI]Import one HMI tag table XML file into an HMI program (best-effort; Classic/Unified via reflection)")]
        public static ResponseMessage ImportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported tag table XML")] string importPath)
        {
            try
            {
                Portal.ImportHmiTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI tag table imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag table: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiConnection"), Description("[L2][HMI]Import one HMI connection XML file into an HMI program (best-effort; Classic/Unified via reflection)")]
        public static ResponseMessage ImportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("importPath: full file path of exported HMI connection XML")] string importPath)
        {
            try
            {
                Portal.ImportHmiConnection(softwarePath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI connection imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI connection from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI connection: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreensFromDirectory"), Description("[L2][HMI]Batch import HMI screen .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportHmiScreensFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported screen XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI screens from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screens from '{dir}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTablesFromDirectory"), Description("[L2][HMI]Batch import HMI tag table .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportHmiTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag tables from '{dir}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetCrossReferences"), Description("[L2][PLC-Software]Get cross references for a Step7 block/type (best-effort). Requires applicable object and Openness support.")]
        public static ResponseCrossReferences GetCrossReferences(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("objectPath: blockPath or typePath inside the PLC software")] string objectPath,
            [Description("objectKind: Block or Type")] string objectKind = "Block",
            [Description("filter: CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects)")] string filter = "AllObjects")
        {
            try
            {
                var items = Portal.GetCrossReferences(softwarePath, objectPath, objectKind, filter);
                if (items != null)
                {
                    return new ResponseCrossReferences
                    {
                        Message = $"Cross references retrieved for {objectKind} '{objectPath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Cross reference service not available for {objectKind} '{objectPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving cross references: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcExternalSources"), Description("[L2][PLC-Software]List PLC external source names (best-effort)")]
        public static ResponseStringList GetPlcExternalSources(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcExternalSources(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC external sources listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC external sources: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcTagTables"), Description("[L2][PLC-Software] List all PLC tag table names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). Use before ExportPlcTagTable to get exact table names, or before ImportPlcTagTable to check for conflicts.")]
        public static ResponseStringList GetPlcTagTables(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcTagTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC tag tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC tag tables: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcTagTable"), Description("[L2][PLC-Software]Export one PLC tag table (PlcTagTable) to XML file")]
        public static ResponseExportFile ExportPlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("tagTableName: PLC tag table name")] string tagTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
        {
            try
            {
                var ok = Portal.ExportPlcTagTable(softwarePath, tagTableName, exportPath);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC tag table '{tagTableName}' exported",
                        ExportPath = exportPath,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Failed exporting PLC tag table '{tagTableName}' from '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC tag table: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTable"), Description("[L1][PLC-Software]Import one PLC tag table XML file into PLC software (best-effort)")]
        public static ResponseMessage ImportPlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of PLC tag table XML")] string importPath)
        {
            try
            {
                Portal.ImportPlcTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"PLC tag table imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag table: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTablesFromDirectory"), Description("[L2][PLC-Software]Batch import PLC tag table .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportPlcTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing PLC tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportPlcTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} PLC tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag tables from '{dir}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcWatchTables"), Description("[L2][PLC-Software]List PLC watch/monitor table names (PlcWatchTable). Read-only.")]
        public static ResponseStringList GetPlcWatchTables(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcWatchTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC watch tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC watch tables: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcForceTables"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " List all force table names in the PLC software." +
            " Force tables configure which variables are continuously forced to specific values while the CPU is online." +
            " Use SetForceTableEntry to configure entries, then go online for the forces to take effect.")]
        public static ResponseStringList GetPlcForceTables(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                var names = Portal.GetPlcForceTables(softwarePath);
                return new ResponseStringList
                {
                    Items = names ?? new List<string>(),
                    Message = names == null ? $"PLC software '{softwarePath}' not found." : $"{names.Count} force table(s) found.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing force tables for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetWatchTableModifyValue"), Description(
            "[L2][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+GoOnline]" +
            " Configure a watch table entry to write a value to a PLC variable once (or on a trigger)." +
            " This is an OFFLINE CONFIGURATION step — the value is written to the PLC only when TIA Portal is online and the trigger fires." +
            " Trigger options: Permanent (every cycle), PermanentAtStart (every cycle, at scan start), OnceOnlyAtStart (single write at scan start), PermanentAtEnd, OnceOnlyAtEnd, OnceOnlyAtStop." +
            " Use GoOnline before calling this for the write to reach the PLC." +
            " Does NOT use Force — variable reverts to PLC logic after the modify. To hold a value persistently, use SetForceTableEntry instead." +
            " Example: SetWatchTableModifyValue('PLC_1', 'Debug_WT', 'DB1.DBX0.0', 'TRUE', 'OnceOnlyAtStart')")]
        public static ResponseMessage SetWatchTableModifyValue(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: name of the watch table to configure (created if not existing)")] string tableName,
            [Description("address: variable address, e.g. 'DB1.DBX0.0', '%M0.0', 'MyTag'")] string address,
            [Description("modifyValue: value to write, e.g. 'TRUE', '42', '3.14'")] string modifyValue,
            [Description("trigger: when to apply the write — Permanent | PermanentAtStart | OnceOnlyAtStart | PermanentAtEnd | OnceOnlyAtEnd | OnceOnlyAtStop (default: Permanent)")] string trigger = "Permanent")
        {
            try
            {
                return Portal.EnsureWatchTableEntry(softwarePath, tableName, address, modifyValue, trigger);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting watch table entry: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        // Force-write capability retained in the Portal layer but intentionally NOT exposed as an MCP tool:
        // forcing overrides live PLC logic and must not be AI-invocable. Online monitoring stays read-only
        // (see RunOnlineMonitoringSafetySelfTest / Test_OnlineMonitoringNoUnsafeToolNames). Use TIA Portal
        // directly for commissioning forces. Removed from the tool surface in 0.0.38.
        public static ResponseMessage SetForceTableEntry(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: name of the force table to configure (created if not existing)")] string tableName,
            [Description("address: variable address to force, e.g. 'DB1.DBX0.0', '%M0.0'")] string address,
            [Description("forceValue: value to force, e.g. 'TRUE', '42'")] string forceValue)
        {
            try
            {
                return Portal.EnsureForceTableEntry(softwarePath, tableName, address, forceValue);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting force table entry: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTable"), Description("[L2][PLC-Software]Export one PLC watch/monitor table (PlcWatchTable) to XML file. Read-only against the TIA project.")]
        public static ResponseExportFile ExportPlcWatchTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("watchTableName: PLC watch table name")] string watchTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
        {
            try
            {
                var ok = Portal.ExportPlcWatchTable(softwarePath, watchTableName, exportPath);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC watch table '{watchTableName}' exported",
                        ExportPath = exportPath,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Failed exporting PLC watch table '{watchTableName}' from '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch table: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTablesToDirectory"), Description("[L2][PLC-Software]Export all PLC watch/monitor tables to XML files. Read-only against the TIA project.")]
        public static ResponseImportBatch ExportPlcWatchTablesToDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("dir: output directory")] string dir,
            [Description("regexName: optional regex filter applied to table name")] string regexName = "")
        {
            try
            {
                var result = Portal.ExportPlcWatchTablesToDirectory(softwarePath, dir, regexName);
                return new ResponseImportBatch
                {
                    Message = $"Exported {result.Imported?.Count() ?? 0} PLC watch tables to '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch tables: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbePlcMonitorOnlineCapabilities"), Description("[L2][Online-Monitoring]Read-only probe for PLC online/offline/watch/monitor API surfaces. It does not go online/offline, change watch tables, write values, or touch restricted safety APIs.")]
        public static ResponseJsonReport ProbePlcMonitorOnlineCapabilities(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var result = Portal.ProbePlcMonitorOnlineCapabilities(softwarePath);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing PLC monitor/online capabilities: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ReadPlcWatchTableCurrentValuesReadOnly"), Description("[L2][Online-Monitoring] Read current/monitor value properties from an existing PLC watch table only. It does not create/modify watch tables, write PLC values, go offline, or use force operations.")]
        public static ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(
            [Description("softwarePath: PLC software path resolved from GetProjectTree/ValidateAutomationContext.")] string softwarePath,
            [Description("watchTableName: existing PLC watch table path/name returned by GetPlcWatchTables.")] string watchTableName,
            [Description("maxEntries: maximum entries to inspect.")] int maxEntries = 50)
        {
            try
            {
                var result = Portal.ReadPlcWatchTableCurrentValuesReadOnly(softwarePath, watchTableName, maxEntries);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading PLC watch table values read-only: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanOnlineReadOnlyMonitoring"), Description("[L2][Online-Monitoring] Validate an online-monitoring request shape without connecting to TIA Portal. Read-only preflight only: no go-online/offline, no watch-table modification, no value write, and no force operation.")]
        public static ResponseJsonReport PlanOnlineReadOnlyMonitoring(
            [Description("softwarePath: PLC software path resolved from GetProjectTree/ValidateAutomationContext.")] string softwarePath,
            [Description("tagPathsJson: JSON array of symbolic PLC tag/member paths, for example [\"DB_HMI.MotorRun\",\"DB_HMI.SpeedSet\"]. Do not pass guessed M bits.")] string tagPathsJson,
            [Description("mode: current-values or watch-table-export-plan. Both are read-only planning modes.")] string mode = "current-values")
        {
            try
            {
                var warnings = new JsonArray();
                var acceptedTags = new JsonArray();
                var rejectedTags = new JsonArray();
                var policy = new JsonArray();
                foreach (var policyLine in GetOnlineMonitoringSafetyPolicy())
                {
                    policy.Add(policyLine);
                }
                var normalizedMode = (mode ?? string.Empty).Trim();
                var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "current-values",
                    "watch-table-export-plan"
                };

                if (!allowedModes.Contains(normalizedMode))
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, $"Unsupported mode '{mode}'. Supported values: current-values, watch-table-export-plan.");
                }

                if (string.IsNullOrWhiteSpace(softwarePath))
                {
                    warnings.Add("softwarePath is empty. Resolve the PLC software path from GetProjectTree before real online monitoring.");
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(tagPathsJson);
                }
                catch (Exception ex)
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, "tagPathsJson must be a JSON array of symbolic PLC paths. Parse error: " + ex.Message);
                }

                if (parsed is not JsonArray tagArray)
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, "tagPathsJson must be a JSON array.");
                }

                foreach (var item in tagArray)
                {
                    var tag = item?.GetValue<string>()?.Trim() ?? string.Empty;
                    var rejectReason = GetOnlineMonitoringTagRejectReason(tag);
                    if (rejectReason == null)
                    {
                        acceptedTags.Add(tag);
                    }
                    else
                    {
                        rejectedTags.Add(new JsonObject
                        {
                            ["tagPath"] = tag,
                            ["reason"] = rejectReason
                        });
                    }
                }

                if (acceptedTags.Count == 0)
                {
                    warnings.Add("No accepted tag paths. Real online monitoring requires at least one declared PLC symbol or DB member.");
                }

                var ok = rejectedTags.Count == 0 && acceptedTags.Count > 0;
                var message = ok
                    ? "Online read-only monitoring plan validated. This preflight did not connect to TIA Portal."
                    : "Online read-only monitoring plan rejected. Fix rejected tag paths before any real online workflow.";

                return BuildOnlineMonitoringPlanResponse(ok, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, message);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning online read-only monitoring: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildOnlineMonitoringPlanResponse(bool ok, string softwarePath, string mode, JsonArray acceptedTags, JsonArray rejectedTags, JsonArray warnings, JsonArray policy, string message)
        {
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = new JsonObject
                {
                    ["softwarePath"] = softwarePath,
                    ["mode"] = mode,
                    ["readOnly"] = true,
                    ["connectsToTia"] = false,
                    ["goesOnlineOrOffline"] = false,
                    ["modifiesWatchTables"] = false,
                    ["writesPlcValues"] = false,
                    ["usesForce"] = false,
                    ["acceptedTags"] = acceptedTags,
                    ["rejectedTags"] = rejectedTags,
                    ["warnings"] = warnings,
                    ["policy"] = policy
                },
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok
                }
            };
        }

        private static string? GetOnlineMonitoringTagRejectReason(string tagPath)
        {
            if (string.IsNullOrWhiteSpace(tagPath))
            {
                return "Tag path is empty.";
            }

            var forbiddenIntent = new[]
            {
                "force", "write", "modify", "update", "create",
                "delete", "remove", "import", "insert", "download", "activate", "start", "stop",
                "goonline", "gooffline", "watchtable", "forcetable"
            };
            var compact = Regex.Replace(tagPath, @"[\s_\-\.]+", string.Empty);
            var segments = Regex.Split(tagPath, @"[\.\s_\-]+").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var forbidden = forbiddenIntent.FirstOrDefault(x =>
                compact.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                segments.Any(segment => segment.StartsWith(x, StringComparison.OrdinalIgnoreCase)));
            if (forbidden != null)
            {
                return $"Tag path contains unsafe online/write/force/watch-table intent keyword '{forbidden}'.";
            }

            if (Regex.IsMatch(tagPath, @"^%?[MIQ][BWD]?\d+(\.\d+)?$", RegexOptions.IgnoreCase))
            {
                return "Absolute I/Q/M address is not accepted for HMI/online planning. Use a declared PLC symbol or DB member read back from the project.";
            }

            if (!Regex.IsMatch(tagPath, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
            {
                return "Use a symbolic PLC path with at least one member separator, for example DB_HMI.MotorRun.";
            }

            return null;
        }

        [McpServerTool(Name = "PlanOnlineReadOnlyDataProvider"), Description("[L2][Online-Monitoring] Plan the commercial current-value path through an external read-only data provider such as opcua or s7-readonly. This is a preflight only: it does not connect, write PLC values, modify watch tables, go online/offline through TIA, or use force operations.")]
        public static ResponseJsonReport PlanOnlineReadOnlyDataProvider(
            [Description("provider: opcua or s7-readonly. opcua is preferred for commercial symbolic readback.")] string provider,
            [Description("endpoint: OPC UA endpoint URL or PLC endpoint/IP. It is validated only for shape and is not opened.")] string endpoint,
            [Description("tagPathsJson: JSON array of declared symbolic PLC tags/DB members. Guessed M bits and unsafe intent names are rejected.")] string tagPathsJson,
            [Description("optionsJson: optional JSON object such as {\"pollMs\":1000,\"source\":\"watch-table-export\"}.")] string optionsJson = "{}")
        {
            try
            {
                var normalizedProvider = (provider ?? "").Trim().ToLowerInvariant();
                var allowedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "opcua",
                    "s7-readonly"
                };

                var policy = new JsonArray(GetOnlineMonitoringSafetyPolicy().Select(x => JsonValue.Create(x)).ToArray());
                var warnings = new JsonArray();
                var acceptedTags = new JsonArray();
                var rejectedTags = new JsonArray();
                var options = ParseJsonObjectOrEmpty(optionsJson, "optionsJson");

                if (!allowedProviders.Contains(normalizedProvider))
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, $"Unsupported provider '{provider}'. Supported providers: opcua, s7-readonly.");
                }

                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    warnings.Add("endpoint is empty. Real read-only providers require an OPC UA endpoint URL or PLC endpoint/IP before execution.");
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(tagPathsJson);
                }
                catch (Exception ex)
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, "tagPathsJson must be a JSON array. Parse error: " + ex.Message);
                }

                if (parsed is not JsonArray tagArray)
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, "tagPathsJson must be a JSON array.");
                }

                foreach (var item in tagArray)
                {
                    var tag = item?.GetValue<string>()?.Trim() ?? "";
                    var rejectReason = GetOnlineMonitoringTagRejectReason(tag);
                    if (rejectReason == null)
                    {
                        acceptedTags.Add(tag);
                    }
                    else
                    {
                        rejectedTags.Add(new JsonObject
                        {
                            ["tagPath"] = tag,
                            ["reason"] = rejectReason
                        });
                    }
                }

                if (normalizedProvider == "s7-readonly")
                {
                    warnings.Add("s7-readonly must be implemented as a read-only adapter with no Write/Force API surface exposed by MCP.");
                }

                var ok = acceptedTags.Count > 0 && rejectedTags.Count == 0;
                return BuildReadOnlyProviderPlan(
                    ok,
                    normalizedProvider,
                    endpoint,
                    acceptedTags,
                    rejectedTags,
                    warnings,
                    policy,
                    options,
                    ok
                        ? "Read-only data provider plan validated. This preflight did not open a network connection."
                        : "Read-only data provider plan rejected. Fix rejected tags/provider settings before any real read workflow.");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning read-only data provider: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildReadOnlyProviderPlan(bool ok, string provider, string endpoint, JsonArray acceptedTags, JsonArray rejectedTags, JsonArray warnings, JsonArray policy, JsonObject options, string message)
        {
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = new JsonObject
                {
                    ["provider"] = provider,
                    ["endpoint"] = endpoint ?? "",
                    ["implementationPath"] = provider.Equals("opcua", StringComparison.OrdinalIgnoreCase)
                        ? "Use OPC UA read/subscribe as the preferred commercial current-value channel."
                        : "Use a strictly read-only S7 adapter for address/symbol reads when OPC UA is unavailable.",
                    ["status"] = "planned-read-only-provider",
                    ["usesTiaOpennessForCurrentValues"] = false,
                    ["usesTiaOpennessForTagDiscovery"] = true,
                    ["readOnly"] = true,
                    ["connectsNow"] = false,
                    ["writesPlcValues"] = false,
                    ["modifiesWatchTables"] = false,
                    ["usesForce"] = false,
                    ["acceptedTags"] = acceptedTags,
                    ["rejectedTags"] = rejectedTags,
                    ["warnings"] = warnings,
                    ["policy"] = policy,
                    ["options"] = options
                },
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok
                }
            };
        }

        private static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                throw new McpException(parameterName + " must be a JSON object. Parse error: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ProbeGlobalLibrary"), Description("[L2][HMI-Library]Open a TIA global library (.al21) read-only/best-effort and list accessible master copies/types/folders through public/reflection APIs. It does not import library content.")]
        public static ResponseGlobalLibraryProbe ProbeGlobalLibrary(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("maxItems: maximum items per list")] int maxItems = 500)
        {
            try
            {
                var result = Portal.ProbeGlobalLibrary(libraryPath, maxItems);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing global library: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportMasterCopyFromGlobalLibrary"), Description("[L2][HMI-Library] Import one MasterCopy from a TIA global library into a real Unified HMI screen and return ScreenItems readback evidence. This modifies the project, must be tried in a temporary project first, and reports failure unless the imported item is visible after readback.")]
        public static ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("masterCopyName: exact or suffix path/name from ProbeGlobalLibrary MasterCopies readback")] string masterCopyName,
            [Description("hmiSoftwarePath: real Unified HMI software path resolved from GetProjectTree, e.g. HMI_RT_1")] string hmiSoftwarePath,
            [Description("screenName: existing target Unified screen name; create it first with EnsureUnifiedHmiScreen if needed")] string screenName,
            [Description("importedItemName: optional expected item name after import; empty means use masterCopyName leaf")] string importedItemName = "",
            [Description("left: optional Left coordinate applied after import when supported")] int left = 0,
            [Description("top: optional Top coordinate applied after import when supported")] int top = 0)
        {
            try
            {
                var result = Portal.ImportMasterCopyFromGlobalLibrary(libraryPath, masterCopyName, hmiSoftwarePath, screenName, importedItemName, left, top);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing global-library master copy: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeGlobalLibraryPackage"), Description("[L2][HMI-Library]Analyze a TIA global library folder offline by file-system structure. It does not connect to TIA Portal, open the library, import content, or modify files.")]
        public static ResponseJsonReport AnalyzeGlobalLibraryPackage(
            [Description("libraryPath: global library folder path or .al* file path")] string libraryPath)
        {
            try
            {
                var data = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                data["timestamp"] = DateTime.Now.ToString("O");
                data["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Offline file-system analysis only.",
                    ["tia"] = "TIA Portal is not connected or opened by this analysis.",
                    ["write"] = "No global library content is imported, modified, or written."
                };

                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Global library package offline analysis completed" : "Global library package offline analysis completed with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing global library package: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanGlobalLibraryTemplateReuse"), Description("[L2][HMI-Library] Plan the commercial fallback when direct MasterCopy import is not publicly verifiable: learn reference/global-library template evidence and rebuild screens with native Unified HMI MCP theme/layout/action tools. Offline planning only; it does not import library content or modify projects.")]
        public static ResponseJsonReport PlanGlobalLibraryTemplateReuse(
            [Description("libraryPath: reference global library folder path or .al* file path.")] string libraryPath,
            [Description("templateIntentJson: optional JSON {\"screenType\":\"overview\",\"targetRuntime\":\"Unified\",\"preferredComponents\":[...]}.")] string templateIntentJson = "{}")
        {
            try
            {
                var analysis = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                var intent = ParseJsonObjectOrEmpty(templateIntentJson, "templateIntentJson");
                var exists = analysis["exists"]?.GetValue<bool>() == true;
                var hasCoreFiles = analysis["ok"]?.GetValue<bool>() == true;
                var stringHints = analysis["stringHints"] as JsonObject;
                var patternCounts = stringHints?["patternCounts"] as JsonObject;
                var screenHintCount = patternCounts?["Screen"]?.GetValue<int>() ?? 0;
                var templateHintCount = patternCounts?["Template"]?.GetValue<int>() ?? 0;
                var masterCopyHintCount = patternCounts?["MasterCopy"]?.GetValue<int>() ?? 0;

                var data = new JsonObject
                {
                    ["libraryPath"] = libraryPath,
                    ["intent"] = intent,
                    ["offlineAnalysisOk"] = exists,
                    ["hasCoreGlobalLibraryFiles"] = hasCoreFiles,
                    ["strategy"] = "template-learn-and-native-rebuild",
                    ["directMasterCopyImportRequired"] = false,
                    ["directMasterCopyImportStatus"] = "optional-unverified-path",
                    ["commercialFallbackReady"] = exists,
                    ["safety"] = new JsonObject
                    {
                        ["offlineOnly"] = true,
                        ["importsLibraryContent"] = false,
                        ["modifiesProject"] = false,
                        ["requiresReadbackBeforeClaimingDirectImport"] = true
                    },
                    ["templateEvidence"] = new JsonObject
                    {
                        ["screenHintCount"] = screenHintCount,
                        ["templateHintCount"] = templateHintCount,
                        ["masterCopyHintCount"] = masterCopyHintCount
                    },
                    ["recommendedMcpTools"] = new JsonArray(
                        "AnalyzeGlobalLibraryPackage",
                        "ProbeGlobalLibrary",
                        "BuildUnifiedHmiThemeDesignJson",
                        "BuildUnifiedHmiLayoutDesignJson",
                        "BuildUnifiedHmiTemplateApplyDesignJson",
                        "ApplyUnifiedHmiScreenDesignJson",
                        "EnsureUnifiedHmiButtonAction"),
                    ["validationGates"] = new JsonArray(
                        "Template plan has offline package evidence.",
                        "Generated Unified design JSON passes layout QA.",
                        "Applied HMI screen items are read back by DescribeHmiScreenItem.",
                        "Button actions pass SyntaxCheck with zero errors.",
                        "HMI tags bind only to declared PLC symbols/DB members."),
                    ["reconstructionPlan"] = new JsonArray(
                        "Analyze global library/package structure and string hints without importing content.",
                        "Use ProbeGlobalLibrary only as read-only evidence when TIA is available; do not claim direct MasterCopy import unless readback succeeds.",
                        "Map reusable UI intent to Unified HMI native tools: theme, layout, template apply design, and button action recipes.",
                        "Apply generated design with ApplyUnifiedHmiScreenDesignJson and verify with item readback plus action SyntaxCheck.",
                        "Bind controls only to declared PLC symbols or DB members discovered from project exports/readback."),
                    ["analysis"] = analysis
                };

                return new ResponseJsonReport
                {
                    Ok = exists,
                    Message = exists
                        ? "Global library template reuse plan built. Direct MasterCopy import remains optional until real readback is verified."
                        : "Global library template reuse plan blocked because the library path was not found.",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = exists }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning global library template reuse: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeHmiTemplateReference"), Description("[L2][HMI-Library]Analyze local Unified HMI JSON templates against reference-project/runtime/global-library hints offline. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport AnalyzeHmiTemplateReference(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("referenceProjectPath: reference TIA project folder containing HMI runtime export/currentConfiguration")] string referenceProjectPath,
            [Description("referenceGlobalLibraryPath: reference global library folder or .al* file")] string referenceGlobalLibraryPath)
        {
            try
            {
                var data = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, referenceProjectPath, referenceGlobalLibraryPath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template/reference offline analysis completed" : "HMI template/reference offline analysis completed with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing HMI template/reference assets: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeUnifiedHmiTemplateLayout"), Description("[L2][HMI-Library]Offline-only QA for Unified HMI JSON templates. Checks theme metadata, screen bounds, duplicate item names, size issues, layout overlap warnings, density, and execution JSON shape. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport AnalyzeUnifiedHmiTemplateLayout(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory)
        {
            try
            {
                var data = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(templateDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template layout offline QA completed" : "Unified HMI template layout offline QA found blocking issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing Unified HMI template layout: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetTechnologyObjects"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " List all Technology Objects (TOs) in the PLC software: axes, cams, measuring inputs, etc." +
            " Returns each TO's Name, type (OfSystemLibElement), and firmware version (OfSystemLibVersion)." +
            " Use this to discover TO names before ExportTechnologyObject or GetAxisParameters." +
            " TOs are stored as TechnologicalInstanceDB instances in the TechnologicalObjectGroup.")]
        public static ResponseTechnologyObjectList GetTechnologyObjects(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                var items = Portal.GetTechnologyObjects(softwarePath);
                var typed = items.Select(jo => new TechnologyObjectInfo
                {
                    Name = jo["Name"]?.GetValue<string>(),
                    OfSystemLibElement = jo["OfSystemLibElement"]?.GetValue<string>(),
                    OfSystemLibVersion = jo["OfSystemLibVersion"]?.GetValue<string>(),
                    TypeHint = jo["TypeHint"]?.GetValue<string>(),
                }).ToArray();

                return new ResponseTechnologyObjectList
                {
                    Ok = true,
                    SoftwarePath = softwarePath,
                    Count = typed.Length,
                    Items = typed,
                    Message = $"{typed.Length} technology object(s) found in '{softwarePath}'."
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing technology objects: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTechnologyObject"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " Export a single Technology Object (axis, cam, measuring input, etc.) to an XML file." +
            " The XML can be inspected, modified offline, and re-imported with ImportTechnologyObject." +
            " Use GetTechnologyObjects first to confirm the exact TO name.")]
        public static ResponseMessage ExportTechnologyObject(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("toName: exact name of the technology object, e.g. 'Axis_1'")] string toName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\Axis_1.xml'")] string exportPath)
        {
            try { return Portal.ExportTechnologyObject(softwarePath, toName, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting technology object: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportTechnologyObjectsToDirectory"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " Batch-export all (or regex-filtered) Technology Objects to XML files in a directory." +
            " Each TO is saved as '<TOName>.xml'. Returns lists of exported names and any failures." +
            " Use regexName to filter by TO name, e.g. 'Axis_.*' for all axes.")]
        public static ResponseImportBatch ExportTechnologyObjectsToDirectory(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportDir: directory to write XML files to, e.g. 'C:\\Temp\\TOs'")] string exportDir,
            [Description("regexName: optional regex filter on TO name; empty = export all")] string regexName = "")
        {
            try { return Portal.ExportTechnologyObjectsToDirectory(softwarePath, exportDir, regexName); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error batch-exporting technology objects: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportTechnologyObject"), Description("[L2][PLC-Software]Import one PLC Technology Object XML file into PLC software (best-effort)")]
        public static ResponseMessage ImportTechnologyObject(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional technology object group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of Technology Object XML")] string importPath)
        {
            try
            {
                Portal.ImportTechnologyObject(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"Technology object imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing technology object from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing technology object: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportTechnologyObjectsFromDirectory"), Description("[L2][PLC-Software]Batch import PLC technology object .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportTechnologyObjectsFromDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional technology object group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing technology object XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportTechnologyObjectsFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} technology objects from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing technology objects from '{dir}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcExternalSource"), Description("[L2][PLC-Software]Import one PLC external source file into a group (best-effort)")]
        public static ResponseMessage ImportPlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: external source group path (use empty for root)")] string groupPath,
            [Description("filePath: path to external source file (.scl, etc.)")] string filePath)
        {
            try
            {
                Portal.ImportPlcExternalSource(softwarePath, groupPath, filePath);
                return new ResponseMessage
                {
                    Message = "PLC external source imported",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC external source [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC external source: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcExternalSource"), Description("[L2][PLC-Software]Delete a PLC external source by name so ImportPlcExternalSource can replace it (idempotent). Name may include or omit .scl.")]
        public static ResponseMessage DeletePlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl)")] string externalSourceName)
        {
            try
            {
                Portal.DeletePlcExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"PLC external source '{externalSourceName}' deleted or was not present",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed deleting PLC external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error deleting PLC external source: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GenerateBlocksFromExternalSource"), Description("[L2][PLC-Software]Generate blocks from a PLC external source by name (best-effort)")]
        public static ResponseMessage GenerateBlocksFromExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources")] string externalSourceName)
        {
            try
            {
                Portal.GenerateBlocksFromExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"Blocks generated from external source '{externalSourceName}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed generating blocks from external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating blocks from external source: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetOpcUaConfig"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Read the full OPC UA server configuration for a PLC: server interfaces, SIMATIC interfaces, and reference namespaces — each with their Name, Enabled state, and key properties." +
            " Use this to audit what OPC UA interfaces exist before enabling or exporting them." +
            " Enabled=true means the interface is active and will be downloaded to the CPU.")]
        public static ResponseJsonReport GetOpcUaConfig(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try { return Portal.GetOpcUaConfig(softwarePath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error reading OPC UA config: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "SetOpcUaInterfaceEnabled"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Enable or disable an OPC UA server interface, SIMATIC interface, or reference namespace." +
            " Setting Enabled=true activates the interface — download to PLC is required for the change to take effect on the CPU." +
            " interfaceType options: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'." +
            " Workflow: GetOpcUaConfig → SetOpcUaInterfaceEnabled → DownloadToPlc.")]
        public static ResponseMessage SetOpcUaInterfaceEnabled(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name of the interface as shown in GetOpcUaConfig")] string interfaceName,
            [Description("enabled: true to enable, false to disable")] bool enabled,
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
        {
            try { return Portal.SetOpcUaInterfaceEnabled(softwarePath, interfaceName, enabled, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error setting OPC UA interface enabled state: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Export an OPC UA server interface or reference namespace to an XML file." +
            " The exported XML can be inspected, modified, and re-imported." +
            " interfaceType: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'.")]
        public static ResponseMessage ExportOpcUaInterface(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name of the interface to export")] string interfaceName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\OpcUa_Interface.xml'")] string exportPath,
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
        {
            try { return Portal.ExportOpcUaInterface(softwarePath, interfaceName, exportPath, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting OPC UA interface: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Import an OPC UA server interface or reference namespace from an XML file." +
            " If an interface with the same name (derived from the file name) already exists, it is updated in place." +
            " Otherwise a new interface is created." +
            " Download to PLC after import to apply changes to the CPU." +
            " interfaceType: 'ServerInterface' (default), 'ReferenceNamespace'.")]
        public static ResponseMessage ImportOpcUaInterface(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XML file")] string importPath,
            [Description("interfaceType: 'ServerInterface' (default) or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
        {
            try { return Portal.ImportOpcUaInterface(softwarePath, importPath, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing OPC UA interface: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export PLC alarm classes to a file. Alarm classes define severity, acknowledgment behavior, and display colors for alarms." +
            " The exported file can be edited and re-imported to update alarm class configurations." +
            " Use before bulk alarm class updates to create a backup.")]
        public static ResponseMessage ExportAlarmClasses(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the export, e.g. 'C:\\Temp\\AlarmClasses.xml'")] string exportPath)
        {
            try { return Portal.ExportAlarmClasses(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm classes: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Import PLC alarm classes from a previously exported file." +
            " Overwrites existing alarm class definitions. Run CompileSoftware after import.")]
        public static ResponseMessage ImportAlarmClasses(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to import from")] string importPath)
        {
            try { return Portal.ImportAlarmClasses(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm classes: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export all PLC alarm text lists to an XLSX (Excel) file." +
            " Text lists contain the text strings shown for each alarm condition." +
            " Supports multi-language projects — all configured languages are exported." +
            " Typical use: export → translate in Excel → ImportAlarmTextLists.")]
        public static ResponseMessage ExportAlarmTextLists(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output, e.g. 'C:\\Temp\\AlarmTexts.xlsx'")] string exportPath)
        {
            try { return Portal.ExportAlarmTextLists(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm text lists: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Import PLC alarm text lists from an XLSX file." +
            " The file must match the format exported by ExportAlarmTextLists." +
            " Run CompileSoftware after import to validate alarm configuration.")]
        public static ResponseMessage ImportAlarmTextLists(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XLSX file")] string importPath)
        {
            try { return Portal.ImportAlarmTextLists(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm text lists: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmInstanceTexts"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export PLC alarm instance texts to an XLSX file." +
            " Instance texts are the alarm messages tied to specific FB/FC instances (e.g. Motor_01.AlarmText)." +
            " Options control what additional columns are included in the export." +
            " Typical use: export → fill in alarm descriptions → ImportInstanceTexts (not yet exposed — edit via TIA Portal UI).")]
        public static ResponseMessage ExportAlarmInstanceTexts(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output")] string exportPath,
            [Description("includeInfoText: include the Info Text column (default: true)")] bool includeInfoText = true,
            [Description("includeAdditionalTexts: include Additional Texts columns (default: true)")] bool includeAdditionalTexts = true,
            [Description("includeAlarmClass: include the Alarm Class column (default: true)")] bool includeAlarmClass = true)
        {
            try { return Portal.ExportAlarmInstanceTexts(softwarePath, exportPath, includeInfoText, includeAdditionalTexts, includeAlarmClass); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm instance texts: {ex.Message}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "CompileSoftware"), Description("[L1][PLC-Software] Compile all blocks in the PLC software. Requires: Connect + OpenProject. Returns basic success/failure. For structured error/warning details use CompileAndDiagnosePlc instead. Must compile before ExportBlock if any blocks are inconsistent. After adding new blocks via import, always compile to catch type/interface mismatches.")]
        public static ResponseCompile CompileSoftware(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("password: the password to access adminsitration, default: no password")] string password = "")
        {
            try
            {
                var result = Portal.CompileSoftware(softwarePath, password);
                var collected = CollectCompilerMessages(result.Messages);

                return new ResponseCompile
                {
                    Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                    State = result.State.ToString(),
                    ErrorCount = result.ErrorCount,
                    WarningCount = result.WarningCount,
                    Messages = collected.Raw,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase),
                        ["errorDetailCount"] = collected.Errors.Count,
                        ["warningDetailCount"] = collected.Warnings.Count
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetOnlineState"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Read the current online connection state of a PLC (Offline/Connecting/Online/Incompatible/NotReachable/Protected/Disconnecting)." +
            " Does NOT change state — purely a read operation." +
            " Use before GoOnline to check current state, or after DownloadToPlc to verify the CPU is reachable." +
            " State=Online means the PC is communicating with the physical CPU." +
            " State=Incompatible means online but firmware/config mismatch — download required." +
            " State=NotReachable means network or IP configuration issue." +
            " NOTE: This reports Openness connection state, NOT the CPU operating mode (RUN/STOP)." +
            " The TIA Portal public API does not expose CPU operating mode — check the CPU front panel LEDs or HMI for RUN/STOP status.")]
        public static ResponseOnlineState GetOnlineState(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.GetOnlineState(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading online state for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOnline"), Description(
            "[L1][Category:PLC-Online][ONLINE-CONNECT][PreCondition:Connect+OpenProject]" +
            " Establish an online connection from TIA Portal to the physical PLC." +
            " Required before DownloadToPlc to confirm reachability, or for future online monitoring tools." +
            " Returns State=Online on success." +
            " If ipAddress is omitted, uses the IP address configured in the project's hardware configuration." +
            " If ipAddress is provided, overrides the configured IP for this session (useful for commissioning with a different IP)." +
            " Common failures: NotReachable (wrong IP / no cable), Protected (CPU requires authentication — supply password), Incompatible (firmware mismatch).")]
        public static ResponseOnlineState GoOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("ipAddress: optional IP address override, e.g. '192.168.1.10'. Leave empty to use the project's configured IP.")] string ipAddress = "",
            [Description("password: optional CPU access password. Required when the CPU has read/write protection configured. Leave empty for unprotected CPUs.")] string password = "")
        {
            try
            {
                return Portal.GoOnline(
                    softwarePath,
                    string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress,
                    string.IsNullOrWhiteSpace(password) ? null : password);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going online for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOffline"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Disconnect the online session between TIA Portal and the physical PLC." +
            " Safe to call even if not currently online. Always go offline when monitoring or download is complete.")]
        public static ResponseMessage GoOffline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.GoOffline(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going offline for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CompareSoftwareToOnline"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject+GoOnline]" +
            " Compare the offline PLC software in the project against the program currently running on the physical CPU." +
            " Use after editing blocks to confirm what differs from the live CPU before downloading," +
            " or after a download to verify offline/online consistency." +
            " Returns a tree-walked list of differences (only entries where ComparisonResult is not 'Equal' are reported)." +
            " Requires GoOnline to be called first; will return IsOnline=false with guidance otherwise.")]
        public static ResponseCompare CompareSoftwareToOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("maxDepth: maximum tree depth to walk (default 4). Lower = faster but less detail.")] int maxDepth = 4,
            [Description("maxEntries: cap on differences returned (default 200). Truncated=true in response if reached.")] int maxEntries = 200)
        {
            try
            {
                return Portal.CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{softwarePath}' to online: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CheckDownloadReadiness"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject+CompileSoftware]" +
            " Check whether a PLC is ready to receive a program download WITHOUT actually downloading." +
            " Verifies: DownloadProvider service is available, a network/IP configuration exists in the hardware config." +
            " Returns Ready=true only when all checks pass." +
            " Use this before DownloadToPlc to surface problems early (missing IP, no hardware config, etc.)." +
            " Does NOT compile — run CompileSoftware first to ensure blocks are consistent.")]
        public static ResponseCheckDownload CheckDownloadReadiness(
            [Description("softwarePath: path to the PLC software in the project tree, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.CheckDownloadReadiness(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error checking download readiness for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DownloadToPlc"), Description(
            "[L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+CompileSoftware+CheckDownloadReadiness]" +
            " Download the compiled PLC program to the physical CPU over the network." +
            " The CPU will stop briefly during download and restart automatically (controlled by startAfterDownload)." +
            " SAFETY: Verify no personnel are near the machine before downloading. This changes live PLC behavior." +
            " Workflow: Connect → OpenProject → CompileSoftware → CheckDownloadReadiness → DownloadToPlc → GetCpuOnlineState." +
            " On success State=Success or Warning. On Error check Errors[] for details." +
            " Default options (keepActualValues=true, consistentBlocksOnly=true) are safe for most scenarios." +
            " Set keepActualValues=false only when DB initial values must be reset — this is irreversible.")]
        public static ResponseDownload DownloadToPlc(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("consistentBlocksOnly: true=download only consistent blocks (safe default), false=download all blocks even inconsistent ones")] bool consistentBlocksOnly = true,
            [Description("keepActualValues: true=preserve current DB actual values (safe default), false=reset all DB values to initial values (irreversible)")] bool keepActualValues = true,
            [Description("startAfterDownload: true=automatically set CPU to RUN after download (default), false=leave CPU in STOP")] bool startAfterDownload = true,
            [Description("stopBeforeDownload: true=automatically stop CPU before download (required for most downloads), false=attempt online download without stopping")] bool stopBeforeDownload = true,
            [Description("password: optional CPU access password. Required when the CPU has download protection configured. Leave empty for unprotected CPUs.")] string password = "")
        {
            try
            {
                var result = Portal.DownloadToPlc(
                    softwarePath,
                    consistentBlocksOnly,
                    keepActualValues,
                    startAfterDownload,
                    stopBeforeDownload,
                    string.IsNullOrWhiteSpace(password) ? null : password);

                if (result.Ok == false && result.Errors != null && result.Errors.Length > 0)
                    throw new McpException(
                        $"Download to '{softwarePath}' failed: {result.Message}",
                        McpErrorCode.InternalError);

                return result;
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpException($"Unexpected error downloading to '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private sealed class CompilerMessageCollectResult
        {
            public List<string> Raw { get; } = new List<string>();
            public List<string> Errors { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public List<string> Info { get; } = new List<string>();
        }

        private static CompilerMessageCollectResult CollectCompilerMessages(object? messagesRoot)
        {
            var collected = new CompilerMessageCollectResult();
            if (messagesRoot is System.Collections.IEnumerable enumerable && messagesRoot is not string)
            {
                foreach (var message in enumerable)
                    WalkCompilerMessageNode(message, collected);
            }
            return collected;
        }

        private static void WalkCompilerMessageNode(object? message, CompilerMessageCollectResult collected)
        {
            if (message == null) return;

            var formatted = FormatCompilerMessage(message);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                collected.Raw.Add(formatted!);
                ClassifyCompilerMessage(message, formatted!, collected);
            }

            if (!TryGetCompilerMessageChildren(message, out var children)) return;
            foreach (var child in children)
                WalkCompilerMessageNode(child, collected);
        }

        private static void ClassifyCompilerMessage(object message, string formatted, CompilerMessageCollectResult collected)
        {
            var state = ReadCompilerMessageState(message);
            var description = ReadCompilerMessageProperty(message, "Description") ?? string.Empty;
            var hasChildren = HasCompilerMessageChildren(message);

            if (IsCompilerSummaryDescription(description))
                return;

            if (IsCompilerErrorState(state))
            {
                if (!hasChildren || !string.IsNullOrWhiteSpace(description))
                    AddUniqueCompilerLine(collected.Errors, formatted);
                return;
            }

            if (IsCompilerWarningState(state))
            {
                if (!hasChildren || !string.IsNullOrWhiteSpace(description))
                    AddUniqueCompilerLine(collected.Warnings, formatted);
                return;
            }

            if (!string.IsNullOrWhiteSpace(description))
                AddUniqueCompilerLine(collected.Info, formatted);
        }

        private static bool HasCompilerMessageChildren(object message)
        {
            return TryGetCompilerMessageChildren(message, out var children) && children.Count > 0;
        }

        private static bool TryGetCompilerMessageChildren(object message, out List<object> children)
        {
            children = new List<object>();
            try
            {
                var messagesValue = message.GetType().GetProperty("Messages")?.GetValue(message);
                if (messagesValue is System.Collections.IEnumerable enumerable && messagesValue is not string)
                {
                    foreach (var child in enumerable)
                    {
                        if (child != null)
                            children.Add(child);
                    }
                }
            }
            catch
            {
                // best effort only
            }

            return children.Count > 0;
        }

        private static string? ReadCompilerMessageProperty(object message, string propertyName)
        {
            try
            {
                var value = message.GetType().GetProperty(propertyName)?.GetValue(message);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadCompilerMessageState(object message)
        {
            return ReadCompilerMessageProperty(message, "State") ?? string.Empty;
        }

        private static bool IsCompilerErrorState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            return state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || state.IndexOf("fehler", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompilerWarningState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            return state.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0
                || state.IndexOf("warnung", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompilerSummaryDescription(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return false;
            var text = description!.Trim();
            return text.StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Compilation finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Kompilierung beendet", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddUniqueCompilerLine(List<string> target, string line)
        {
            if (!target.Contains(line))
                target.Add(line);
        }

        private static void AppendCompilerEngineeringAttributes(object message, List<string> parts)
        {
            var getAttribute = message.GetType().GetMethod(
                "GetAttribute",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            if (getAttribute == null) return;

            foreach (var attrName in new[]
            {
                "Line", "Column", "BlockName", "Severity", "ErrorCode", "Message", "Text", "ObjectPath"
            })
            {
                if (parts.Any(p => p.StartsWith(attrName + "=", StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var value = getAttribute.Invoke(message, new object[] { attrName });
                    if (value == null) continue;
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add($"{attrName}={text}");
                }
                catch
                {
                    // attribute not supported on this message type
                }
            }
        }

        private static string? FormatCompilerMessage(object? message)
        {
            if (message == null) return null;

            try
            {
                var t = message.GetType();
                var parts = new List<string>();

                foreach (var name in new[]
                {
                    "State", "Severity", "ErrorCode", "Message", "Description", "Text",
                    "Path", "ObjectPath", "BlockName", "Line", "Column", "DateTime"
                })
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || p.GetIndexParameters().Length != 0) continue;

                    object? value = null;
                    try { value = p.GetValue(message); } catch { }
                    if (value == null) continue;

                    var s = value.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add($"{name}={s}");
                }

                AppendCompilerEngineeringAttributes(message, parts);

                if (parts.Count == 0)
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length != 0) continue;
                        if (string.Equals(p.Name, "Messages", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Name, "Parent", StringComparison.OrdinalIgnoreCase))
                            continue;

                        object? value = null;
                        try { value = p.GetValue(message); } catch { }
                        if (value == null) continue;
                        var s = value.ToString();
                        if (!string.IsNullOrWhiteSpace(s) && s != t.FullName)
                            parts.Add($"{p.Name}={s}");
                    }
                }

                return parts.Count > 0 ? string.Join("; ", parts) : message.ToString();
            }
            catch
            {
                return message.ToString();
            }
        }

        [McpServerTool(Name = "GetSoftwareTree"), Description("[L1][PLC-Software] Get the full PLC block/type/external-source hierarchy as ASCII tree. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). ALWAYS call before ExportBlock/ImportBlock to get exact group paths (e.g. 'Program blocks/FBs/FB_Motor'). Returns OB/FB/FC/GlobalDB/UDT/ExternalSource blocks with group hierarchy.")]
        public static ResponseSoftwareTree GetSoftwareTree(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var tree = Portal.GetSoftwareTree(softwarePath);

                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseSoftwareTree
                    {
                        Message = $"Software tree retrieved from '{softwarePath}'",
                        Tree = "```\n" + tree + "\n```",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving software tree from '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software tree from '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region blocks

        [McpServerTool(Name = "GetBlockInfo"), Description("[L2][PLC-Software] Get detailed info for one block (attributes, language, number, modification time). Requires: Connect + OpenProject. blockPath must be fully qualified: 'Group/Subgroup/BlockName' — get it from GetSoftwareTree or GetBlocksWithHierarchy. Returns: IsConsistent (false = must compile before export).")]
        public static ResponseBlockInfo GetBlockInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath)
        {
            try
            {
                var block = Portal.GetBlock(softwarePath, blockPath);
                if (block != null)
                {
                    var attributes = Helper.GetAttributeList(block);

                    return new ResponseBlockInfo
                    {
                        Message = $"Block info retrieved from '{blockPath}' in '{softwarePath}'",
                        Name = block.Name,
                        TypeName = block.GetType().Name,
                        Namespace = block.Namespace,
                        ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage),block.ProgrammingLanguage),
                        MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                        IsConsistent = block.IsConsistent,
                        HeaderName = block.HeaderName,
                        ModifiedDate = block.ModifiedDate,
                        IsKnowHowProtected = block.IsKnowHowProtected,
                        Attributes = attributes,
                        Description = block.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Block not found at '{blockPath}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving block info from '{blockPath}' in '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetBlocks"), Description("[L2][PLC-Software] Get a flat list of all blocks in PLC software. Requires: Connect + OpenProject. Use GetBlocksWithHierarchy instead when you need group/folder paths for ExportBlock. Returns: block name, number, type (OB/FC/FB/GlobalDB/InstanceDB), programming language.")]
        public static ResponseBlocks GetBlocks(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "")
        {
            try
            {
                var list = Portal.GetBlocks(softwarePath, regexName);

                var responseList = new List<ResponseBlockInfo>();
                foreach (var block in list)
                {
                    if (block != null)
                    {
                        var attributes = Helper.GetAttributeList(block);

                        responseList.Add(new ResponseBlockInfo
                        {
                            Name = block.Name,
                            TypeName = block.GetType().Name,
                            Namespace = block.Namespace,
                            ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                            MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                            IsConsistent = block.IsConsistent,
                            HeaderName = block.HeaderName,
                            ModifiedDate = block.ModifiedDate,
                            IsKnowHowProtected = block.IsKnowHowProtected,
                            Attributes = attributes,
                            Description = block.ToString()
                        });
                    }
                }

                if (list != null)
                {
                    return new ResponseBlocks
                    {
                        Message = $"Blocks with regex '{regexName}' retrieved from '{softwarePath}'",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving blocks with regex '{regexName}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving blocks with regex '{regexName}' in '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetBlocksWithHierarchy"), Description("[L2][PLC-Software]Get a list of all blocks with their group hierarchy from the plc software.")]
        public static ResponseBlocksWithHierarchy GetBlocksWithHierarchy(
        [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var rootGroup = Portal.GetBlockRootGroup(softwarePath);
                if (rootGroup != null)
                {
                    var hierarchy = Helper.BuildBlockHierarchy(rootGroup);
                    return new ResponseBlocksWithHierarchy
                    {
                        Message = $"Block hierarchy retrieved from '{softwarePath}'",
                        Root = hierarchy,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    // Specific failure: root group could not be resolved
                    throw new McpException($"Block root group not found for '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Generic unexpected failure wrapper
                throw new McpException($"Unexpected error retrieving block hierarchy for '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }



        [McpServerTool(Name = "ExportBlock"), Description("[L2][PLC-Software] Export one block to an XML file. Requires: Connect + OpenProject + block must be consistent (compile first if IsConsistent=false). blockPath must be fully qualified 'Group/Subgroup/Name' from GetSoftwareTree — bare names return InvalidParams with suggestions. For batch export use ExportBlocks.")]
        public static ResponseExportBlock ExportBlock(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: full path to the block in the project structure, e.g. 'Group/Subgroup/Name' (single names are ambiguous)")] string blockPath,
            [Description("exportPath: defines the path where to export the block")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                var block = Portal.ExportBlock(softwarePath, blockPath, exportPath, preservePath);
                if (block != null)
                {
                    return new ResponseExportBlock
                    {
                        Message = $"Block exported from '{blockPath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                // Should not be reachable because Portal.ExportBlock throws on failure
                throw new McpException($"Failed exporting block from '{blockPath}' to '{exportPath}'", McpErrorCode.InternalError);
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                // Map known portal errors to sharper MCP errors and messages.
                switch (pex.Code)
                {
                    case TiaMcpServer.Siemens.PortalErrorCode.NotFound:
                        {
                            var msg = ("Block not found." + BuildBlockDidYouMean(softwarePath, blockPath)).Trim();
                            throw new McpException(msg, McpErrorCode.InvalidParams);
                        }

                    case TiaMcpServer.Siemens.PortalErrorCode.ExportFailed:
                        {
                            // Relay underlying portal error with concise reason; log full details
                            var reason = pex.InnerException?.Message?.Trim();
                            var msg = "Failed to export block.";
                            if (!string.IsNullOrEmpty(reason)) msg += $" Reason: {reason}";

                            Logger?.LogError(pex, "MCP ExportBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}",
                                pex.Data?["softwarePath"], pex.Data?["blockPath"], pex.Data?["exportPath"]);

                            throw new McpException(msg, McpErrorCode.InternalError);
                        }

                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidParams:
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidState:
                        {
                            throw new McpException(pex.Message, McpErrorCode.InvalidParams);
                        }
                }

                // Fallback
                throw new McpException(pex.Message, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting block from '{blockPath}' to '{exportPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportBlockToTemp"), Description("[L2][PLC-Software]Export one block to a temporary directory and return written file paths")]
        public static ResponseTempExport ExportBlockToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("blockPath: full block path inside PLC software")] string blockPath,
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportBlockToTemp(softwarePath, blockPath, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Block exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException("Failed exporting block to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting block to temp: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static string BuildBlockPathSuggestion(string softwarePath, string blockPath)
        {
            if (string.IsNullOrEmpty(blockPath) || blockPath.Contains('/')) return string.Empty;
            try
            {
                var escaped = Regex.Escape(blockPath);
                var blocks = Portal.GetBlocks(softwarePath, $"^{escaped}$");
                if (blocks == null || blocks.Count == 0)
                {
                    blocks = Portal.GetBlocks(softwarePath, escaped);
                }

                var candidates = blocks
                    .Take(10)
                    .Select(b =>
                    {
                        var name = b.Name;
                        var parts = new List<string> { name };
                        var parent = b.Parent;
                        while (parent != null)
                        {
                            if (parent is PlcBlockSystemGroup) break;
                            if (parent is PlcBlockGroup grp)
                            {
                                parts.Insert(0, grp.Name);
                                parent = grp.Parent;
                            }
                            else break;
                        }
                        if (parts.Count > 1) parts.RemoveAt(0);
                        return string.Join("/", parts);
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return candidates.Count > 0 ? $" Did you mean: {string.Join(", ", candidates)}?" : string.Empty;
            }
            catch
            {
                return string.Empty; // best effort only
            }
        }

        private static string BestEffortSuggestGroupPath(string softwarePath, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return string.Empty;

            try
            {
                // Suggest existing group paths based on blocks' parent groups (best effort).
                var blocks = Portal.GetBlocks(softwarePath, "");
                if (blocks == null) return string.Empty;

                var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in blocks.Take(300))
                {
                    var parent = b?.Parent;
                    var parts = new List<string>();
                    while (parent != null)
                    {
                        if (parent is PlcBlockSystemGroup) break;
                        if (parent is PlcBlockGroup grp)
                        {
                            parts.Insert(0, grp.Name);
                            parent = grp.Parent;
                        }
                        else break;
                    }
                    if (parts.Count > 0)
                        groups.Add(string.Join("/", parts));
                }

                var key = groupPath.Trim().Trim('/').ToLowerInvariant();
                var candidates = groups
                    .Where(g => g.ToLowerInvariant().Contains(key) || key.Contains(g.ToLowerInvariant()))
                    .Take(10)
                    .ToList();

                return candidates.Count > 0 ? $" Did you mean groupPath: {string.Join(", ", candidates)}?" : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        [McpServerTool(Name = "ImportBlock"), Description("[L1][PLC-Software] Import a single XML block file into PLC software. Requires: Connect + OpenProject. importPath must be an absolute path to a .xml file. After import, call CompileAndDiagnosePlc to verify. For multiple files use ImportBlocksFromDirectory; for JSON-built blocks use PlcBuildAndImport.")]
        public static ResponseImportBlock ImportBlock(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the block")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the block")] string importPath)
        {
            try
            {
                Portal.ImportBlock(softwarePath, groupPath, importPath);
                return new ResponseImportBlock
                {
                    Message = $"Block imported from '{importPath}' to '{groupPath}'",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (PortalException pex) when (pex.Code == PortalErrorCode.NotFound)
            {
                var hint = BestEffortSuggestGroupPath(softwarePath, groupPath);
                throw new McpException($"{pex.Message}{hint}", McpErrorCode.InvalidParams);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Failed importing block from '{importPath}' to '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportBlocksFromDirectory"), Description("[L2][PLC-Software]Batch import PLC block .xml files from a directory into a block group (V21 recommended path)")]
        public static ResponseImportBatch ImportBlocksFromDirectory(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import blocks")] string groupPath,
            [Description("dir: directory that contains block .xml files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override, false=Rename")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportBlocksFromDirectory(softwarePath, groupPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} blocks from '{dir}' into '{groupPath}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing blocks from '{dir}' to '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcProgramFromDirectory"), Description("[L2][PLC-Software] HIGH-LEVEL batch import tool. Recursively scans a directory for PLC XML files, auto-classifies them as UDT/TagTable/Block, imports in correct dependency order (UDTs first, then tag tables, then blocks), and optionally compiles. Requires: Connect + OpenProject. Best for importing a full exported PLC program or a set of generated XML blocks.")]
        public static ResponsePlcProgramImport ImportPlcProgramFromDirectory(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("sourceDir: root directory containing exported PLC XML files")] string sourceDir,
            [Description("typeGroupPath: PLC data type group path; use empty for root")] string typeGroupPath = "",
            [Description("tagFolderPath: PLC tag table group path; use empty for root")] string tagFolderPath = "",
            [Description("technologyFolderPath: PLC technology object group path; use empty for root")] string technologyFolderPath = "",
            [Description("blockGroupPath: PLC block group path; use empty for root Program blocks")] string blockGroupPath = "",
            [Description("regexName: optional regex filter applied to file name without extension")] string regexName = "",
            [Description("compileAfter: compile PLC software after imports")] bool compileAfter = true,
            [Description("stopOnImportFailure: skip remaining imports after first import failure")] bool stopOnImportFailure = false,
            [Description("dryRun: only classify and return discovered objects; do not import or compile")] bool dryRun = false)
        {
            var importedTypes = new List<string>();
            var importedTagTables = new List<string>();
            var importedTechnologyObjects = new List<string>();
            var importedBlocks = new List<string>();
            var failed = new List<ImportFailure>();
            ResponseCompile? compile = null;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                {
                    failed.Add(new ImportFailure { Path = sourceDir, Error = "Directory not found" });
                    return BuildPlcProgramImportResponse(sourceDir, dryRun, new List<string>(), new List<string>(), new List<string>(), new List<string>(), importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }

                var files = Directory.GetFiles(sourceDir, "*.xml", SearchOption.AllDirectories)
                    .Where(f => regex == null || regex.IsMatch(Path.GetFileNameWithoutExtension(f)))
                    .Select(f =>
                    {
                        var kind = ClassifyPlcXml(f, out var subKind, out var objectName);
                        return new { File = f, Kind = kind, SubKind = subKind, ObjectName = objectName };
                    })
                    .Where(x => x.Kind != "unknown")
                    .GroupBy(x => $"{x.Kind}:{x.ObjectName}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(x => x.File.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                                  .ThenBy(x => x.File.Length)
                                  .First())
                    .ToList();

                var discoveredTypes = files.Where(x => x.Kind == "type").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredTagTables = files.Where(x => x.Kind == "tagtable").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredTechnologyObjects = files.Where(x => x.Kind == "technology").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var discoveredBlocks = files.Where(x => x.Kind == "block").Select(x => x.ObjectName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                if (dryRun)
                {
                    return BuildPlcProgramImportResponse(sourceDir, true, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
                }

                foreach (var item in files.Where(x => x.Kind == "type").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportType(softwarePath, typeGroupPath, item.File);
                        importedTypes.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "tagtable").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportPlcTagTable(softwarePath, tagFolderPath, item.File);
                        importedTagTables.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "technology").OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportTechnologyObject(softwarePath, technologyFolderPath, item.File);
                        importedTechnologyObjects.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                foreach (var item in files.Where(x => x.Kind == "block")
                                          .OrderBy(x => GetBlockImportOrder(x.SubKind))
                                          .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase))
                {
                    if (stopOnImportFailure && failed.Any()) break;
                    try
                    {
                        Portal.ImportBlock(softwarePath, blockGroupPath, item.File);
                        importedBlocks.Add(item.ObjectName);
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = item.File, Error = pex.Message });
                    }
                }

                if (compileAfter && !(stopOnImportFailure && failed.Any()))
                {
                    try
                    {
                        var result = Portal.CompileSoftware(softwarePath);
                        var collected = CollectCompilerMessages(result.Messages);
                        compile = new ResponseCompile
                        {
                            Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                            State = result.State.ToString(),
                            ErrorCount = result.ErrorCount,
                            WarningCount = result.WarningCount,
                            Messages = collected.Raw,
                            Meta = new JsonObject
                            {
                                ["timestamp"] = DateTime.Now,
                                ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase),
                                ["errorDetailCount"] = collected.Errors.Count,
                                ["warningDetailCount"] = collected.Warnings.Count
                            }
                        };
                    }
                    catch (PortalException pex)
                    {
                        failed.Add(new ImportFailure { Path = softwarePath, Error = $"[{pex.Code}] {pex.Message}" });
                    }
                }

                return BuildPlcProgramImportResponse(sourceDir, false, discoveredTypes, discoveredTagTables, discoveredTechnologyObjects, discoveredBlocks, importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                failed.Add(new ImportFailure { Path = sourceDir, Error = ex.ToString() });
                return BuildPlcProgramImportResponse(sourceDir, dryRun, new List<string>(), new List<string>(), new List<string>(), new List<string>(), importedTypes, importedTagTables, importedTechnologyObjects, importedBlocks, failed, compile);
            }
        }

        [McpServerTool(Name = "CompileAndDiagnosePlc"), Description("[L1][PLC-Software] PREFERRED compile tool. Compiles PLC and returns structured errors/warnings by recursively walking CompilerResult.Messages (V20/V21 PublicAPI). Leaf diagnostics include Path + Description; optional Line/Column via GetAttribute when exposed. Requires: Connect + OpenProject.")]
        public static ResponseCompileDiagnose CompileAndDiagnosePlc(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("password: optional safety password")] string password = "")
        {
            try
            {
                var result = Portal.CompileSoftware(softwarePath, password);

                var raw = new List<string>();
                var errs = new List<string>();
                var warns = new List<string>();
                var info = new List<string>();

                try
                {
                    var collected = CollectCompilerMessages(result.Messages);
                    raw = collected.Raw;
                    errs = collected.Errors;
                    warns = collected.Warnings;
                    info = collected.Info;
                }
                catch { }

                return new ResponseCompileDiagnose
                {
                    Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                    State = result.State.ToString(),
                    ErrorCount = result.ErrorCount,
                    WarningCount = result.WarningCount,
                    Errors = errs,
                    Warnings = warns,
                    Info = info,
                    RawMessages = raw,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase),
                        ["errorDetailCount"] = errs.Count,
                        ["warningDetailCount"] = warns.Count
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RepairAndReimportBlock"), Description("[L2][PLC-Software]Try import a block XML; if compile fails, return diagnostics and best-effort suggestions (no destructive actions).")]
        public static ResponseRepairAndCompile RepairAndReimportBlock(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: block XML path")] string importPath,
            [Description("groupPath: block group path; use empty for root Program blocks")] string groupPath = "",
            [Description("compileAfter: compile PLC after import")] bool compileAfter = true)
        {
            var suggestions = new List<string>();
            try
            {
                // ImportBlock 现以 PortalException 报失败；成功即已导入
                Portal.ImportBlock(softwarePath, groupPath, importPath);

                ResponseCompileDiagnose? compile = null;
                if (compileAfter)
                {
                    compile = CompileAndDiagnosePlc(softwarePath);
                    if (compile.Meta?["success"]?.GetValue<bool>() == false)
                    {
                        suggestions.Add("If errors mention missing symbols, ensure PLC tag table/UDTs are imported before blocks.");
                        suggestions.Add("If block/type is inconsistent, compile PLC software once to update consistency before exporting.");
                    }
                }

                return new ResponseRepairAndCompile
                {
                    Message = "Imported (best-effort) and compiled.",
                    Imported = true,
                    ImportError = null,
                    Compile = compile,
                    Suggestions = suggestions,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false) }
                };
            }
            catch (PortalException pex)
            {
                // 导入失败：返回诊断而非抛出（本工具契约是给出修复建议）
                suggestions.Add("If groupPath is wrong, retry with empty groupPath for root Program blocks.");
                return new ResponseRepairAndCompile
                {
                    Message = "Import failed.",
                    Imported = false,
                    ImportError = $"[{pex.Code}] {pex.Message}",
                    Compile = null,
                    Suggestions = suggestions,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error repairing/reimporting block '{importPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponsePlcProgramImport BuildPlcProgramImportResponse(
            string sourceDir,
            bool dryRun,
            List<string> discoveredTypes,
            List<string> discoveredTagTables,
            List<string> discoveredTechnologyObjects,
            List<string> discoveredBlocks,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedTechnologyObjects,
            List<string> importedBlocks,
            List<ImportFailure> failed,
            ResponseCompile? compile)
        {
            var compileOk = compile == null || (compile.Meta?["success"]?.GetValue<bool>() ?? false);
            var success = failed.Count == 0 && compileOk;
            return new ResponsePlcProgramImport
            {
                Message = dryRun
                    ? $"PLC program dry-run from '{sourceDir}': types={discoveredTypes.Count}, tagTables={discoveredTagTables.Count}, technologyObjects={discoveredTechnologyObjects.Count}, blocks={discoveredBlocks.Count}, failed={failed.Count}"
                    : $"PLC program import from '{sourceDir}': types={importedTypes.Count}, tagTables={importedTagTables.Count}, technologyObjects={importedTechnologyObjects.Count}, blocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}",
                DryRun = dryRun,
                DiscoveredTypes = discoveredTypes,
                DiscoveredTagTables = discoveredTagTables,
                DiscoveredTechnologyObjects = discoveredTechnologyObjects,
                DiscoveredBlocks = discoveredBlocks,
                ImportedTypes = importedTypes,
                ImportedTagTables = importedTagTables,
                ImportedTechnologyObjects = importedTechnologyObjects,
                ImportedBlocks = importedBlocks,
                Failed = failed,
                Compile = compile,
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = success }
            };
        }

        private static string ClassifyPlcXml(string file, out string subKind, out string objectName)
        {
            subKind = "";
            objectName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = XDocument.Load(file);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase));

                var local = obj?.Name.LocalName ?? "";
                subKind = local;
                var name = obj?.Element("AttributeList")?.Element("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    objectName = name!.Trim();
                }
                if (local.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase)) return "type";
                if (string.Equals(local, "SW.Tags.PlcTagTable", StringComparison.OrdinalIgnoreCase)) return "tagtable";
                if (local.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)) return "block";
                if (local.StartsWith("SW.TechnologicalObjects.", StringComparison.OrdinalIgnoreCase)) return "technology";
            }
            catch
            {
                subKind = "";
            }

            return "unknown";
        }

        private static int GetBlockImportOrder(string subKind)
        {
            return subKind switch
            {
                "SW.Blocks.GlobalDB" => 10,
                "SW.Blocks.FC" => 20,
                "SW.Blocks.FB" => 20,
                "SW.Blocks.InstanceDB" => 30,
                "SW.Blocks.OB" => 40,
                _ => 50
            };
        }

        [McpServerTool(Name = "ExportBlocks"), Description("[L2][PLC-Software]Export all blocks from the plc software to path")]
        public static async Task<ResponseExportBlocks> ExportBlocks(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the blocks")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;
            
            try
            {
                // First, get the list of blocks to determine total count
                Logger?.LogInformation($"Starting export of blocks from '{softwarePath}' to '{exportPath}'");
                
                var allBlocks = await Task.Run(() => Portal.GetBlocks(softwarePath, regexName));
                var totalBlocks = allBlocks?.Count ?? 0;

                if (totalBlocks == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No blocks found to export",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportBlocks
                    {
                        Message = $"No blocks found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseBlockInfo>(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = 0,
                            ["exportedBlocks"] = 0,
                            ["duration"] = (DateTime.Now - startTime).TotalSeconds
                        }
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalBlocks,
                        Message = $"Starting export of {totalBlocks} blocks...",
                        progressToken
                    });
                }

                // Export blocks asynchronously
                var exportedBlocks = await Task.Run(() => Portal.ExportBlocks(softwarePath, exportPath, regexName, preservePath));

                // Build list of inconsistent (skipped) blocks for reporting
                var inconsistentInfos = new List<ResponseBlockInfo>();
                if (allBlocks != null)
                {
                    foreach (var b in allBlocks)
                    {
                        if (b != null && b.IsConsistent == false)
                        {
                            var attrs = Helper.GetAttributeList(b);
                            inconsistentInfos.Add(new ResponseBlockInfo
                            {
                                Name = b.Name,
                                TypeName = b.GetType().Name,
                                Namespace = b.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), b.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), b.MemoryLayout),
                                IsConsistent = b.IsConsistent,
                                HeaderName = b.HeaderName,
                                ModifiedDate = b.ModifiedDate,
                                IsKnowHowProtected = b.IsKnowHowProtected,
                                Attributes = attrs,
                                Description = b.ToString()
                            });
                        }
                    }
                }
                
                // Send progress update after export completion
                if (exportedBlocks != null && progressToken != null)
                {
                    var exportedCount = exportedBlocks.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalBlocks,
                        Message = $"Exported {exportedCount} of {totalBlocks} blocks",
                        progressToken
                    });
                }

                if (exportedBlocks != null)
                {
                    var responseList = new List<ResponseBlockInfo>();
                    var processedCount = 0;
                    
                    foreach (var block in exportedBlocks)
                    {
                        if (block != null)
                        {
                            var attributes = Helper.GetAttributeList(block);

                            responseList.Add(new ResponseBlockInfo
                            {
                                Name = block.Name,
                                TypeName = block.GetType().Name,
                                Namespace = block.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                                IsConsistent = block.IsConsistent,
                                HeaderName = block.HeaderName,
                                ModifiedDate = block.ModifiedDate,
                                IsKnowHowProtected = block.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = block.ToString()
                            });
                        }
                        processedCount++;
                    }

                    // Send final progress notification
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processedCount,
                            Total = totalBlocks,
                            Message = $"Export completed: {processedCount} blocks exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Export completed: {processedCount} blocks exported in {duration:F2} seconds");

                    return new ResponseExportBlocks
                    {
                        Message = $"Export completed: {processedCount} blocks with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Inconsistent = inconsistentInfos,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = totalBlocks,
                            ["exportedBlocks"] = processedCount,
                            ["inconsistentBlocks"] = inconsistentInfos.Count,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Send error progress notification if we have a progress token
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
                        // Ignore notification errors during error handling
                    }
                }
                
                Logger?.LogError(ex, $"Failed exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}");
                throw new McpException($"Unexpected error exporting blocks with '{regexName}' from '{softwarePath}' to {exportPath}: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportBlocksToTemp"), Description("[L2][PLC-Software]Export blocks to a temporary directory and return written file paths")]
        public static ResponseTempExport ExportBlocksToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("regexName: optional regex filter")] string regexName = "",
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportBlocksToTemp(softwarePath, regexName, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Blocks exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }
                throw new McpException("Failed exporting blocks to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting blocks to temp: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region types

        [McpServerTool(Name = "GetTypeInfo"), Description("[L2][PLC-Software]Get a type info from the plc software")]
        public static ResponseTypeInfo GetTypeInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("typePath: defines the path in the project structure to the type")] string typePath)
        {
            try
            {
                var type = Portal.GetType(softwarePath, typePath);
                if (type != null)
                {
                    var attributes = Helper.GetAttributeList(type);

                    return new ResponseTypeInfo
                    {
                        Message = $"Type info retrieved from '{typePath}' in '{softwarePath}'",
                        Name = type.Name,
                        TypeName = type.GetType().Name,
                        Namespace = type.Namespace,
                        IsConsistent = type.IsConsistent,
                        ModifiedDate = type.ModifiedDate,
                        IsKnowHowProtected = type.IsKnowHowProtected,
                        Attributes = attributes,
                        Description = type.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Type not found at '{typePath}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving type info from '{typePath}' in '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetTypes"), Description("[L2][PLC-Software]Get a list of types from the plc software")]
        public static ResponseTypes GetTypes(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "")
        {
            try
            {
                var list = Portal.GetTypes(softwarePath, regexName);

                var responseList = new List<ResponseTypeInfo>();
                foreach (var type in list)
                {
                    if (type != null)
                    {
                        var attributes = Helper.GetAttributeList(type);

                        responseList.Add(new ResponseTypeInfo
                        {
                            Name = type.Name,
                            TypeName = type.GetType().Name,
                            Namespace = type.Namespace,
                            IsConsistent = type.IsConsistent,
                            ModifiedDate = type.ModifiedDate,
                            IsKnowHowProtected = type.IsKnowHowProtected,
                            Attributes = attributes,
                            Description = type.ToString()
                        });
                    }
                }

                if (list != null)
                {
                    return new ResponseTypes
                    {
                        Message = $"Types with regex '{regexName}' retrieved from '{softwarePath}'",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving user defined types with regex '{regexName}' in '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving user defined types with regex '{regexName}' in '{softwarePath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportType"), Description("[L2][PLC-Software]Export a type from the plc software")]
        public static ResponseExportType ExportType(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the directory where to export the type; output file will be '<type name>.xml'")] string exportPath,
            [Description("typePath: defines the path in the project structure to the type")] string typePath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                var type = Portal.ExportType(softwarePath, typePath, exportPath, preservePath);
                if (type != null)
                {
                    return new ResponseExportType
                    {
                        Message = $"Type exported from '{typePath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting type from '{typePath}' to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                switch (pex.Code)
                {
                    case TiaMcpServer.Siemens.PortalErrorCode.NotFound:
                        throw new McpException(("Type not found." + BuildTypeDidYouMean(softwarePath, typePath)).Trim(), McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidState:
                    case TiaMcpServer.Siemens.PortalErrorCode.InvalidParams:
                        throw new McpException(pex.Message, McpErrorCode.InvalidParams);
                    case TiaMcpServer.Siemens.PortalErrorCode.ExportFailed:
                        {
                            var reason = pex.InnerException?.Message?.Trim();
                            var msg = "Failed to export type.";
                            if (!string.IsNullOrEmpty(reason)) msg += $" Reason: {reason}";
                            Logger?.LogError(pex, "MCP ExportType failed for {SoftwarePath} {TypePath} -> {ExportPath}",
                                pex.Data?["softwarePath"], pex.Data?["typePath"], pex.Data?["exportPath"]);
                            throw new McpException(msg, McpErrorCode.InternalError);
                        }
                }
                throw new McpException(pex.Message, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting type from '{typePath}' to '{exportPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTypeToTemp"), Description("[L2][PLC-Software]Export one type to a temporary directory and return written file paths")]
        public static ResponseTempExport ExportTypeToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("typePath: full type path inside PLC software")] string typePath,
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportTypeToTemp(softwarePath, typePath, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Type exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException("Failed exporting type to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting type to temp: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportType"), Description("[L1][PLC-Software]Import a type from file into the plc software")]
        public static ResponseImportType ImportType(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: defines the path in the project structure to the group, where to import the type")] string groupPath,
            [Description("importPath: defines the path of the xml file from where to import the type")] string importPath)
        {
            try
            {
                Portal.ImportType(softwarePath, groupPath, importPath);
                return new ResponseImportType
                {
                    Message = $"Type imported from '{importPath}' to '{groupPath}'",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing type from '{importPath}' to '{groupPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing type from '{importPath}' to '{groupPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SeedProjectFromReference"), Description("[L2][PLC-Software]Seed PLC blocks/types and HMI screens/tagtables from a reference directory (manifest.json + {{PLACEHOLDER}} replace)")]
        public static ResponseSeed SeedProjectFromReference(
            [Description("plcSoftwarePath: path in the project structure to the PLC software")] string plcSoftwarePath,
            [Description("hmiSoftwarePath: path in the project structure to the HMI software")] string hmiSoftwarePath,
            [Description("referenceDir: directory containing manifest.json and subfolders (plc/blocks, plc/types, hmi/screens, hmi/tags)")] string referenceDir,
            [Description("placeholders: JsonObject key-values for replacement in XML, e.g. {\"PLC_NAME\":\"PLC_1\"}")] JsonObject? placeholders = null)
        {
            try
            {
                var res = Portal.SeedProjectFromReference(plcSoftwarePath, hmiSoftwarePath, referenceDir, placeholders);
                res.Meta ??= new JsonObject();
                res.Meta["timestamp"] = DateTime.Now;
                res.Meta["success"] = (res.Failed == null || !res.Failed.Any());
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error seeding project from reference: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTypes"), Description("[L2][PLC-Software]Export types from the plc software to path")]
        public static async Task<ResponseExportTypes> ExportTypes(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the types")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;
            
            try
            {
                // First, get the list of types to determine total count
                Logger?.LogInformation($"Starting export of types from '{softwarePath}' to '{exportPath}'");
                
                var allTypes = await Task.Run(() => Portal.GetTypes(softwarePath, regexName));
                var totalTypes = allTypes?.Count ?? 0;

                if (totalTypes == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No types found to export",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportTypes
                    {
                        Message = $"No types found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseTypeInfo>(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalTypes"] = 0,
                            ["exportedTypes"] = 0,
                            ["duration"] = (DateTime.Now - startTime).TotalSeconds
                        }
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalTypes,
                        Message = $"Starting export of {totalTypes} types...",
                        progressToken
                    });
                }

                // Export types asynchronously
                var exportedTypes = await Task.Run(() => Portal.ExportTypes(softwarePath, exportPath, regexName, preservePath));

                // Build list of inconsistent (skipped) types for reporting
                var inconsistentTypeInfos = new List<ResponseTypeInfo>();
                if (allTypes != null)
                {
                    foreach (var t in allTypes)
                    {
                        if (t != null && t.IsConsistent == false)
                        {
                            var attrs = Helper.GetAttributeList(t);
                            inconsistentTypeInfos.Add(new ResponseTypeInfo
                            {
                                Name = t.Name,
                                TypeName = t.GetType().Name,
                                Namespace = t.Namespace,
                                IsConsistent = t.IsConsistent,
                                ModifiedDate = t.ModifiedDate,
                                IsKnowHowProtected = t.IsKnowHowProtected,
                                Attributes = attrs,
                                Description = t.ToString()
                            });
                        }
                    }
                }
                
                // Send progress update after export completion
                if (exportedTypes != null && progressToken != null)
                {
                    var exportedCount = exportedTypes.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalTypes,
                        Message = $"Exported {exportedCount} of {totalTypes} types",
                        progressToken
                    });
                }

                if (exportedTypes != null)
                {
                    var responseList = new List<ResponseTypeInfo>();
                    var processedCount = 0;
                    
                    foreach (var type in exportedTypes)
                    {
                        if (type != null)
                        {
                            var attributes = Helper.GetAttributeList(type);

                            responseList.Add(new ResponseTypeInfo
                            {
                                Name = type.Name,
                                TypeName = type.GetType().Name,
                                Namespace = type.Namespace,
                                IsConsistent = type.IsConsistent,
                                ModifiedDate = type.ModifiedDate,
                                IsKnowHowProtected = type.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = type.ToString()
                            });
                        }
                        processedCount++;
                    }

                    // Send final progress notification
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processedCount,
                            Total = totalTypes,
                            Message = $"Export completed: {processedCount} types exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Type export completed: {processedCount} types exported in {duration:F2} seconds");

                    return new ResponseExportTypes
                    {
                        Message = $"Export completed: {processedCount} types with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Inconsistent = inconsistentTypeInfos,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalTypes"] = totalTypes,
                            ["exportedTypes"] = processedCount,
                            ["inconsistentTypes"] = inconsistentTypeInfos.Count,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting types '{regexName}' from '{softwarePath}' to {exportPath}", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Send error progress notification if we have a progress token
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Type export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
                        // Ignore notification errors during error handling
                    }
                }
                
                Logger?.LogError(ex, $"Failed exporting types '{regexName}' from '{softwarePath}' to {exportPath}");
                throw new McpException($"Unexpected error exporting types '{regexName}' from '{softwarePath}' to {exportPath}: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTypesToTemp"), Description("[L2][PLC-Software]Export types to a temporary directory and return written file paths")]
        public static ResponseTempExport ExportTypesToTemp(
            [Description("softwarePath: path to the PLC software")] string softwarePath,
            [Description("regexName: optional regex filter")] string regexName = "",
            [Description("preservePath: keep hierarchy in temp dir")] bool preservePath = false)
        {
            try
            {
                var res = Portal.ExportTypesToTemp(softwarePath, regexName, preservePath);
                if (res != null)
                {
                    return new ResponseTempExport
                    {
                        Message = "Types exported to temp directory",
                        TempDir = res.Value.TempDir,
                        Paths = res.Value.Paths,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }
                throw new McpException("Failed exporting types to temp", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting types to temp: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion

        #region documents

        [McpServerTool(Name = "ExportAsDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for exporting one block. Exports a single program block to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML (ExportBlock). Requires TIA Portal V20 or newer.")]
        public static ResponseExportAsDocuments ExportAsDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block")] string blockPath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ExportAsDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                if (Portal.ExportAsDocuments(softwarePath, blockPath, exportPath, preservePath))
                {
                    return new ResponseExportAsDocuments
                    {
                        Message = $"Documents exported from '{blockPath}' to '{exportPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting documents from '{blockPath}' to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting documents from '{blockPath}' to '{exportPath}': {ex}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportBlocksAsDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for batch export. Exports multiple program blocks to SIMATIC SD textual / SCL document format (.s7dcl + .s7res) — far more readable/diff-friendly than SimaticML XML. Requires TIA Portal V20 or newer.")]
        public static async Task<ResponseExportBlocksAsDocuments> ExportBlocksAsDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("exportPath: defines the path where to export the documents")] string exportPath,
            [Description("regexName: defines the name or regular expression to find the block. Use empty string (default) to find all")] string regexName = "",
            [Description("preservePath: preserves the path/structure of the plc software")] bool preservePath = false)
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;
            
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ExportBlocksAsDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }
                // First, get the list of blocks to determine total count
                Logger?.LogInformation($"Starting export of blocks as documents from '{softwarePath}' to '{exportPath}'");
                
                var allBlocks = await Task.Run(() => Portal.GetBlocks(softwarePath, regexName));
                var totalBlocks = allBlocks?.Count ?? 0;

                if (totalBlocks == 0)
                {
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = "No blocks found to export as documents",
                            progressToken
                        });
                    }
                    
                    return new ResponseExportBlocksAsDocuments
                    {
                        Message = $"No blocks found with regex '{regexName}' in '{softwarePath}'",
                        Items = new List<ResponseBlockInfo>(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = 0,
                            ["exportedBlocks"] = 0,
                            ["duration"] = (DateTime.Now - startTime).TotalSeconds
                        }
                    };
                }

                // Send initial progress notification
                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = totalBlocks,
                        Message = $"Starting export of {totalBlocks} blocks as documents...",
                        progressToken
                    });
                }

                // Export blocks as documents asynchronously
                var exportedBlocks = await Task.Run(() => Portal.ExportBlocksAsDocuments(softwarePath, exportPath, regexName, preservePath));
                
                // Send progress update after export completion
                if (exportedBlocks != null && progressToken != null)
                {
                    var exportedCount = exportedBlocks.Count();
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = exportedCount,
                        Total = totalBlocks,
                        Message = $"Exported {exportedCount} of {totalBlocks} blocks as documents",
                        progressToken
                    });
                }

                if (exportedBlocks != null)
                {
                    var responseList = new List<ResponseBlockInfo>();
                    var processedCount = 0;
                    
                    foreach (var block in exportedBlocks)
                    {
                        if (block != null)
                        {
                            var attributes = Helper.GetAttributeList(block);

                            responseList.Add(new ResponseBlockInfo
                            {
                                Name = block.Name,
                                TypeName = block.GetType().Name,
                                Namespace = block.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                                IsConsistent = block.IsConsistent,
                                HeaderName = block.HeaderName,
                                ModifiedDate = block.ModifiedDate,
                                IsKnowHowProtected = block.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = block.ToString()
                            });
                        }
                        processedCount++;
                    }

                    // Send final progress notification
                    if (progressToken != null)
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = processedCount,
                            Total = totalBlocks,
                            Message = $"Document export completed: {processedCount} blocks exported successfully",
                            progressToken
                        });
                    }

                    var duration = (DateTime.Now - startTime).TotalSeconds;
                    Logger?.LogInformation($"Document export completed: {processedCount} blocks exported in {duration:F2} seconds");

                    return new ResponseExportBlocksAsDocuments
                    {
                        Message = $"Document export completed: {processedCount} blocks with regex '{regexName}' exported from '{softwarePath}' to '{exportPath}'",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["totalBlocks"] = totalBlocks,
                            ["exportedBlocks"] = processedCount,
                            ["duration"] = duration
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed exporting documents to '{exportPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // Send error progress notification if we have a progress token
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Document export failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch
                    {
                        // Ignore notification errors during error handling
                    }
                }
                
                Logger?.LogError(ex, $"Failed exporting documents to '{exportPath}'");
                throw new McpException($"Unexpected error exporting documents to '{exportPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportFromDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for importing one block. Imports a single program block from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer.")]
        public static ResponseImportFromDocuments ImportFromDocuments(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the block should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("fileNameWithoutExtension: name of the block file without extension") ] string fileNameWithoutExtension,
            [Description("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ImportFromDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                var option = ParseImportDocumentOption(importOption);

                // Pre-check .s7res for missing en-US tags
                var warnings = new JsonArray();
                try
                {
                    var missingIds = GetResMissingEnUsIds(importPath, fileNameWithoutExtension);
                    if (missingIds != null && missingIds.Count > 0)
                    {
                        Logger?.LogWarning($".s7res for '{fileNameWithoutExtension}' missing en-US tags for {missingIds.Count} items: {string.Join(", ", missingIds)}");
                        warnings.Add(new JsonObject
                        {
                            ["name"] = fileNameWithoutExtension,
                            ["missingEnUsIds"] = new JsonArray(missingIds.Select(id => (JsonNode)id).ToArray())
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug(ex, "Failed to evaluate .s7res warnings");
                }

                var ok = Portal.ImportFromDocuments(softwarePath, groupPath, importPath, fileNameWithoutExtension, option);
                if (ok)
                {
                    return new ResponseImportFromDocuments
                    {
                        Message = $"Imported '{fileNameWithoutExtension}' from '{importPath}'",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true,
                            ["warnings"] = warnings
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed importing '{fileNameWithoutExtension}' from '{importPath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing from documents: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportBlocksFromDocuments"), Description("[L2][PLC-Software] PREFERRED on V21+ for batch import. Imports multiple program blocks from SIMATIC SD textual / SCL documents (.s7dcl + .s7res) into PLC software. Requires TIA Portal V20 or newer.")]
        public static async Task<ResponseImportBlocksFromDocuments> ImportBlocksFromDocuments(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("groupPath: optional path within the PLC program where the blocks should be placed (empty for root)")] string groupPath,
            [Description("importPath: directory containing the document files (.s7dcl/.s7res)")] string importPath,
            [Description("regexName: name or regular expression to select block files (empty for all)")] string regexName = "",
            [Description("importOption: ImportDocumentOptions value (None, Override, SkipInactiveCultures, ActivateInactiveCultures)")] string importOption = "Override")
        {
            var startTime = DateTime.Now;
            var progressToken = context.Params?.ProgressToken;

            try
            {
                if (Engineering.TiaMajorVersion < 20)
                {
                    throw new McpException("ImportBlocksFromDocuments requires TIA Portal V20 or newer", McpErrorCode.InvalidParams);
                }

                // Determine total by scanning .s7dcl files matching regex
                int total = 0;
                var scanWarnings = new JsonArray();
                try
                {
                    if (Directory.Exists(importPath))
                    {
                        var rx = string.IsNullOrWhiteSpace(regexName) ? null : new Regex(regexName, RegexOptions.Compiled);
                        var files = Directory.GetFiles(importPath, "*.s7dcl", SearchOption.TopDirectoryOnly);
                        foreach (var f in files)
                        {
                            var name = Path.GetFileNameWithoutExtension(f);
                            if (rx != null && !rx.IsMatch(name))
                                continue;
                            total++;

                            try
                            {
                                var missingIds = GetResMissingEnUsIds(importPath, name);
                                if (missingIds != null && missingIds.Count > 0)
                                {
                                    scanWarnings.Add(new JsonObject
                                    {
                                        ["name"] = name,
                                        ["missingEnUsIds"] = new JsonArray(missingIds.Select(id => (JsonNode)id).ToArray())
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { /* ignore pre-scan errors */ }

                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = 0,
                        Total = total,
                        Message = total > 0 ? $"Starting import of {total} blocks from documents..." : "Scanning import directory...",
                        progressToken
                    });
                }

                var option = ParseImportDocumentOption(importOption);
                var imported = await Task.Run(() => Portal.ImportBlocksFromDocuments(softwarePath, groupPath, importPath, regexName, option));

                var responseList = new List<ResponseBlockInfo>();
                int processed = 0;
                if (imported != null)
                {
                    foreach (var block in imported)
                    {
                        if (block != null)
                        {
                            var attributes = Helper.GetAttributeList(block);
                            responseList.Add(new ResponseBlockInfo
                            {
                                Name = block.Name,
                                TypeName = block.GetType().Name,
                                Namespace = block.Namespace,
                                ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                                MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                                IsConsistent = block.IsConsistent,
                                HeaderName = block.HeaderName,
                                ModifiedDate = block.ModifiedDate,
                                IsKnowHowProtected = block.IsKnowHowProtected,
                                Attributes = attributes,
                                Description = block.ToString()
                            });
                        }
                        processed++;
                    }
                }

                if (progressToken != null)
                {
                    await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = processed,
                        Total = total,
                        Message = $"Document import completed: {processed} blocks imported successfully",
                        progressToken
                    });
                }

                var duration = (DateTime.Now - startTime).TotalSeconds;
                Logger?.LogInformation($"Document import completed: {processed} blocks imported in {duration:F2} seconds");

                return new ResponseImportBlocksFromDocuments
                {
                    Message = $"Document import completed: {processed} blocks imported from '{importPath}'",
                    Items = responseList,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["totalBlocks"] = total,
                        ["importedBlocks"] = processed,
                        ["duration"] = duration,
                        ["warnings"] = scanWarnings
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                if (progressToken != null)
                {
                    try
                    {
                        await server.SendNotificationAsync("notifications/progress", new
                        {
                            Progress = 0,
                            Total = 0,
                            Message = $"Document import failed: {ex.Message}",
                            Error = true,
                            progressToken
                        });
                    }
                    catch { }
                }

                Logger?.LogError(ex, $"Failed importing documents from '{importPath}'");
                throw new McpException($"Unexpected error importing documents from '{importPath}': {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeObject"), Description("[L2][Reflection]Describe an Openness object via reflection. Use this first when a natural-language TIA operation has no direct MCP tool. objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")]
        public static ResponseObjectDescribe DescribeObject(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path. For Device/DeviceItem/Software: path in project tree. For Block/Type: blockPath/typePath.")] string objectPath,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
        {
            try
            {
                return Portal.DescribeObject(objectKind, objectPath, softwarePath, maxMembers);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing object: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetObjectProperty"), Description("[L2][Reflection]Get an Openness object property by dotted path. Use after DescribeObject/DescribeObjectProperty to safely inspect current state before writing.")]
        public static ResponseObjectValue GetObjectProperty(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path")] string objectPath,
            [Description("Property path, e.g. Name or BlockGroup.Groups")] string propertyPath,
            [Description("softwarePath required for Block/Type")] string softwarePath = "")
        {
            try
            {
                return Portal.GetObjectProperty(objectKind, objectPath, propertyPath, softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading property: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListObjectChildren"), Description("[L2][Reflection]List child items from an enumerable Openness property, e.g. Devices, DeviceItems, Connections, Screens, Blocks. Use to discover paths instead of guessing.")]
        public static ResponseObjectChildren ListObjectChildren(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path")] string objectPath,
            [Description("Enumerable property name/path, e.g. Devices, DeviceItems, BlockGroup.Blocks")] string collectionProperty,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Max child items to return")] int limit = 200)
        {
            try
            {
                return Portal.ListObjectChildren(objectKind, objectPath, collectionProperty, softwarePath, limit);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing children: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeObject"), Description("[L2][Reflection]Invoke an Openness method via reflection. Default is read-oriented; set allowWrite=true only after DescribeObject confirms the target method/signature. This is the generic bridge for public API operations not yet wrapped by MCP.")]
        public static ResponseObjectValue InvokeObject(
            [Description("Object kind: Project|Portal|Device|DeviceItem|Software|Block|Type|HmiScreen|HmiTag|HmiScreenItem")] string objectKind,
            [Description("Object path")] string objectPath,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, e.g. [\"AttrName\"]. Empty for no args.")] JsonArray? args = null,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
        {
            try
            {
                return Portal.InvokeObject(objectKind, objectPath, methodName, args, softwarePath, allowWrite);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error invoking method: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeService"), Description("[L2][Reflection]GetService bridge: describe a service object (by type name suffix) from a target object.")]
        public static ResponseObjectDescribe DescribeService(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("Target object path")] string objectPath,
            [Description("Service type suffix, e.g. CrossReferenceService or ICompilable")] string serviceTypeSuffix,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Max member count to return")] int maxMembers = 200)
        {
            try
            {
                return Portal.DescribeService(objectKind, objectPath, serviceTypeSuffix, softwarePath, maxMembers);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing service: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "InvokeService"), Description("[L2][Reflection]GetService bridge: invoke a method on a service object (by type name suffix) from a target object.")]
        public static ResponseObjectValue InvokeService(
            [Description("Target object kind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("Target object path")] string objectPath,
            [Description("Service type suffix, e.g. CrossReferenceService or ICompilable")] string serviceTypeSuffix,
            [Description("Method name (case-insensitive)")] string methodName,
            [Description("JSON array of args, empty for no args")] JsonArray? args = null,
            [Description("softwarePath required for Block/Type")] string softwarePath = "",
            [Description("Allow write/dangerous methods. Default false.")] bool allowWrite = false)
        {
            try
            {
                return Portal.InvokeService(objectKind, objectPath, serviceTypeSuffix, methodName, args, softwarePath, allowWrite);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error invoking service method: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }

        private static ImportDocumentOptions ParseImportDocumentOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option)) return ImportDocumentOptions.Override;

            var normalized = option.Trim();

            // Primary: accept exact enum names (case-insensitive)
            if (Enum.TryParse<ImportDocumentOptions>(normalized, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            // Aliases and common misspellings
            switch (normalized.ToLowerInvariant())
            {
                case "override": return ImportDocumentOptions.Override;
                case "none": return ImportDocumentOptions.None;
                case "skipinactiveculture":
                case "skipinactivecultures":
                case "skipinactive":
                case "skipinactivecult":
                    return ImportDocumentOptions.SkipInactiveCultures;
                case "activeinactiveculture":
                case "activateinactivecultures":
                case "activeinactivecultures":
                case "activateinactive":
                    return ImportDocumentOptions.ActivateInactiveCultures;
                default:
                    throw new McpException($"Invalid importOption '{option}'. Allowed: None, Override, SkipInactiveCultures, ActivateInactiveCultures", McpErrorCode.InvalidParams);
            }
        }

        private static List<string> GetResMissingEnUsIds(string directory, string baseName)
        {
            var resPath = Path.Combine(directory, baseName + ".s7res");
            var missing = new List<string>();
            if (!File.Exists(resPath))
            {
                return missing;
            }
            var xdoc = XDocument.Load(resPath);
            XNamespace ns = xdoc.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var comment in xdoc.Descendants(ns + "Comment"))
            {
                var hasEnUs = comment.Elements(ns + "MultiLanguageText")
                                     .Any(e => string.Equals((string?)e.Attribute("Lang"), "en-US", StringComparison.OrdinalIgnoreCase));
                if (!hasEnUs)
                {
                    var id = (string?)comment.Attribute("Id") ?? "";
                    missing.Add(id);
                }
            }
            return missing;
        }

        #endregion
    }
}
