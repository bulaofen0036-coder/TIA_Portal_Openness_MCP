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
                throw new McpException($"Unexpected error connecting to TIA-Portal: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error listing TIA Portal processes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error ensuring Openness user group: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error disconnecting from TIA-Portal: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error retrieving TIA-Portal MCP server state: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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

                var rules = new[]
                {
                    "ORDER: Connect → (OpenProject | AttachToOpenProject | CreateProject) → GetProjectTree → read/write → CompileSoftware → SaveProject. The server now auto-connects and auto-binds an already-open project, but always confirm with GetState/GetProjectTree before writing.",
                    "AFTER ANY WRITE: call CompileSoftware to validate, then SaveProject to persist. Changes are NOT saved automatically.",
                    "NAMES ARE EXACT: plc software path defaults to 'PLC_1', HMI to 'HMI_RT_1'. If a name/path is rejected, call GetProjectTree / GetSoftwareTree to read the real names instead of guessing.",
                    "ON ERROR: read the error message — it names the recovery tool (e.g. 'call OpenProject/AttachToOpenProject'). Do that instead of retrying the same call or switching tools at random.",
                    "BIG TASKS: to create or extend a whole project in one shot, prefer ScaffoldProject (one JSON spec) over many small calls; pass dryRun=true first to validate the spec offline.",
                    "WRITING CODE: call GetAuthoringGuide('scl' or 'lad') BEFORE authoring block code — it returns the verified syntax and encoding rules. NEVER hand-write FlgNet XML for ladder logic; use S7DCL text via ImportFromDocuments/ImportBlocksFromScl.",
                    "ENCODING: .scl external source = UTF-8 without BOM; .s7dcl/.s7res and all XML = UTF-8 WITH BOM. Wrong BOM is the #1 cause of mojibake/import failures with Chinese text.",
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
                    OperatingRules = rules,
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
                throw new McpException($"Bootstrap unexpected error: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error running capability self-test: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error running online monitoring safety self-test: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error generating acceptance report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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
                throw new McpException($"Unexpected error generating error report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
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

        // #region project/session — moved to McpServer.ProjectSession.cs

        // #region devices — moved to McpServer.Devices.cs

        // #region plc software — moved to McpServer.PlcSoftware.cs

        // #region blocks — moved to McpServer.Blocks.cs

        // #region types — moved to McpServer.Types.cs

        // #region documents — moved to McpServer.Documents.cs
    }
}
