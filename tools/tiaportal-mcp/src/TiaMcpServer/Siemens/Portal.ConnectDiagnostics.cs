using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private const int ConnectAttachTimeoutMs = 30000;
        private const int ConnectInspectTimeoutMs = 10000;
        private const int ConnectLaunchTimeoutMs = 120000;
        private const int ConnectProjectAttachDeadlineMs = 15000;

        private ConnectAttemptResult? _lastConnectAttempt;

        public ResponseConnect GetLastConnectResponse(bool success)
        {
            var attempt = _lastConnectAttempt;
            return new ResponseConnect
            {
                Message = success ? "Connected to TIA-Portal" : LastConnectError ?? "TIA Portal connect failed",
                Stage = attempt?.Stage,
                Strategy = attempt?.Strategy,
                AttemptedPids = attempt?.AttemptedPids,
                LaunchMode = attempt?.LaunchMode,
                NeedsUserConfirmation = attempt?.NeedsUserConfirmation,
                DetectedPromptTitles = attempt?.DetectedPromptTitles,
                DetectedPromptPids = attempt?.DetectedPromptPids,
                ElapsedMs = attempt?.ElapsedMs,
                CleanupPids = attempt?.CleanupPids,
                Recommendations = attempt?.Recommendations,
                ErrorCode = attempt?.ErrorCode,
                Meta = new System.Text.Json.Nodes.JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = success
                }
            };
        }

        public ResponsePortalConnectReadiness DiagnosePortalConnectReadiness(bool includeWindows = true)
        {
            var started = Stopwatch.StartNew();
            var processInfos = EnumeratePortalProcessInfos();
            var diagnostics = processInfos.Select(info => ProbePortalProcess(info, ConnectAttachTimeoutMs, ConnectInspectTimeoutMs)).ToList();
            var windows = includeWindows
                ? CaptureWindowsSnapshot(includeOnlyPortalRelated: true, baseline: null).ToList()
                : new List<PortalWindowObservation>();
            var needsConfirmation = diagnostics.Any(d => string.Equals(d.ProbeStatus, "confirmation-required", StringComparison.OrdinalIgnoreCase));

            return new ResponsePortalConnectReadiness
            {
                Message = needsConfirmation ? "Detected possible TIA/Openness confirmation window." : "TIA Portal connect readiness inspected.",
                Stage = "Diagnose",
                Strategy = BuildConnectStrategyLabel(),
                NeedsUserConfirmation = needsConfirmation,
                VisibleProcessCount = processInfos.Count,
                Processes = diagnostics,
                Windows = windows,
                Recommendations = BuildConnectRecommendations(needsConfirmation),
                ElapsedMs = started.ElapsedMilliseconds,
                Meta = new System.Text.Json.Nodes.JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = true
                }
            };
        }

        private bool ConnectPortalSafe()
        {
            _logger?.LogInformation("Connecting to TIA Portal...");
            var sw = Stopwatch.StartNew();
            var attempt = new ConnectAttemptResult
            {
                Stage = "GetProcesses",
                Strategy = BuildConnectStrategyLabel()
            };
            _lastConnectAttempt = attempt;

            try
            {
                LastConnectError = null;
                _project = null;
                _session = null;
                _portal = null;

                var processes = EnumeratePortalProcessInfos();
                _logger?.LogInformation($"TIA Portal process count: {processes.Count}");

                foreach (var processInfo in processes.Where(p => p.HasVisibleWindow))
                {
                    attempt.Stage = "Attach";
                    attempt.AttemptedPids.Add(processInfo.ProcessId);
                    var diag = ProbePortalProcess(processInfo, ConnectAttachTimeoutMs, ConnectInspectTimeoutMs);
                    attempt.ProcessDiagnostics.Add(diag);
                    if (diag.ProbeStatus != "ok")
                    {
                        if (diag.ProbeStatus == "confirmation-required")
                        {
                            attempt.NeedsUserConfirmation = true;
                        }
                        continue;
                    }

                    var portal = AttachExistingPortal(processInfo.ProcessId);
                    if (portal == null)
                    {
                        continue;
                    }

                    _portal = portal;
                    BindBestAvailableProject(_portal, processInfo.ProcessId);
                    attempt.ElapsedMs = sw.ElapsedMilliseconds;
                    return true;
                }

                attempt.Stage = "Launch";
                attempt.LaunchMode = GetLaunchModeForConnect().ToString();
                _logger?.LogInformation($"Starting a new TIA Portal instance ({attempt.LaunchMode}).");
                try
                {
                    _portal = CreatePortalForCurrentOperation(GetLaunchModeForConnect());
                    BindBestAvailableProject(_portal, -1);
                    attempt.ElapsedMs = sw.ElapsedMilliseconds;
                    return true;
                }
                catch (PortalException ex) when (ex.Code == PortalErrorCode.Timeout)
                {
                    attempt.Windows.AddRange(CaptureWindowsSnapshot(includeOnlyPortalRelated: true, baseline: null));
                    PopulatePromptCollections(attempt);
                    attempt.NeedsUserConfirmation = attempt.DetectedPromptTitles.Count > 0;
                    attempt.ErrorCode = attempt.NeedsUserConfirmation
                        ? PortalErrorCode.ConfirmationRequired.ToString()
                        : PortalErrorCode.Timeout.ToString();
                    attempt.Recommendations.AddRange(BuildConnectRecommendations(attempt.NeedsUserConfirmation));
                    throw new PortalException(
                        attempt.NeedsUserConfirmation ? PortalErrorCode.ConfirmationRequired : PortalErrorCode.Timeout,
                        attempt.NeedsUserConfirmation
                            ? "TIA Portal launch did not finish because a confirmation dialog is likely waiting in the GUI."
                            : ex.Message,
                        inner: ex);
                }
            }
            catch (PortalException ex)
            {
                attempt.ElapsedMs = sw.ElapsedMilliseconds;
                if (!attempt.Recommendations.Any())
                {
                    attempt.Recommendations.AddRange(BuildConnectRecommendations(attempt.NeedsUserConfirmation));
                }
                attempt.ErrorCode ??= ex.Code.ToString();
                LastConnectError = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                attempt.ElapsedMs = sw.ElapsedMilliseconds;
                attempt.ErrorCode ??= PortalErrorCode.OpennessError.ToString();
                LastConnectError = ex.Message;
                throw new PortalException(PortalErrorCode.OpennessError, $"ConnectPortal failed: {FormatExceptionDetail(ex)}", inner: ex);
            }
        }

        private List<string> ListPortalProcessProjectsSafe()
        {
            var diagnosis = DiagnosePortalConnectReadiness(includeWindows: true);
            var lines = new List<string>
            {
                "TIA Portal process count: " + (diagnosis.VisibleProcessCount ?? 0),
                "Strategy=" + (diagnosis.Strategy ?? "unknown"),
                "NeedsUserConfirmation=" + (diagnosis.NeedsUserConfirmation ?? false)
            };

            foreach (var proc in diagnosis.Processes ?? Enumerable.Empty<PortalProcessDiagnostic>())
            {
                lines.Add($"PID={proc.ProcessId} visible={proc.HasVisibleWindow} status={proc.ProbeStatus} hasSessions={proc.HasSessions} hasProjects={proc.HasProjects} elapsedMs={proc.ElapsedMs}");
                foreach (var sessionProject in proc.SessionProjectNames ?? Enumerable.Empty<string>())
                {
                    lines.Add($"PID={proc.ProcessId} sessionProject={sessionProject}");
                }
                foreach (var project in proc.ProjectNames ?? Enumerable.Empty<string>())
                {
                    lines.Add($"PID={proc.ProcessId} project={project}");
                }
                if (!string.IsNullOrWhiteSpace(proc.ErrorMessage))
                {
                    lines.Add($"PID={proc.ProcessId} error={proc.ErrorType}: {proc.ErrorMessage}");
                }
            }

            foreach (var window in diagnosis.Windows ?? Enumerable.Empty<PortalWindowObservation>())
            {
                lines.Add($"WINDOW PID={window.ProcessId} title={window.Title} class={window.ClassName} new={window.IsNewWindow}");
            }

            return lines;
        }

        private bool AttachToOpenProjectSafe(string projectName)
        {
            _logger?.LogInformation($"Attaching to open project: {projectName}");
            if (string.IsNullOrWhiteSpace(projectName)) return false;
            projectName = projectName.Trim();

            var deadline = DateTime.UtcNow.AddMilliseconds(ConnectProjectAttachDeadlineMs);
            PortalException? confirmationEx = null;
            while (DateTime.UtcNow < deadline)
            {
                if (_portal != null && TryAttachProjectInPortal(_portal, projectName))
                {
                    return true;
                }

                foreach (var proc in EnumeratePortalProcessInfos().Where(p => p.HasVisibleWindow))
                {
                    var diag = ProbePortalProcess(proc, ConnectAttachTimeoutMs, ConnectInspectTimeoutMs);
                    if (diag.ProbeStatus == "confirmation-required")
                    {
                        confirmationEx = new PortalException(PortalErrorCode.ConfirmationRequired, "Detected TIA/Openness confirmation window. Confirm it in TIA and retry AttachToOpenProject.");
                        continue;
                    }
                    if (diag.ProbeStatus != "ok") continue;

                    var candidate = AttachExistingPortal(proc.ProcessId);
                    if (candidate == null) continue;
                    if (TryAttachProjectInPortal(candidate, projectName))
                    {
                        if (_portal != null && !ReferenceEquals(_portal, candidate))
                        {
                            try { _portal.Dispose(); } catch { }
                        }
                        _portal = candidate;
                        return true;
                    }

                    if (!ReferenceEquals(_portal, candidate))
                    {
                        try { candidate.Dispose(); } catch { }
                    }
                }

                Thread.Sleep(500);
            }

            if (confirmationEx != null)
            {
                throw confirmationEx;
            }

            return false;
        }

        private sealed class ConnectAttemptResult
        {
            public string? Stage { get; set; }
            public string? Strategy { get; set; }
            public List<int> AttemptedPids { get; } = new();
            public string? LaunchMode { get; set; }
            public bool NeedsUserConfirmation { get; set; }
            public List<string> DetectedPromptTitles { get; } = new();
            public List<int> DetectedPromptPids { get; } = new();
            public long ElapsedMs { get; set; }
            public List<int> CleanupPids { get; } = new();
            public List<string> Recommendations { get; } = new();
            public string? ErrorCode { get; set; }
            public List<PortalProcessDiagnostic> ProcessDiagnostics { get; } = new();
            public List<PortalWindowObservation> Windows { get; } = new();
        }

        private sealed class PortalProcessInfo
        {
            public int ProcessId { get; set; }
            public bool HasVisibleWindow { get; set; }
            public DateTime StartTime { get; set; }
            public string SortKey => $"visible={(HasVisibleWindow ? 1 : 0)};start={StartTime:O}";
        }

        private sealed class PortalProbeResult
        {
            [JsonPropertyName("action")]
            public string? Action { get; set; }

            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("apartmentState")]
            public string? ApartmentState { get; set; }

            [JsonPropertyName("errorType")]
            public string? ErrorType { get; set; }

            [JsonPropertyName("errorMessage")]
            public string? ErrorMessage { get; set; }

            [JsonPropertyName("hasSessions")]
            public bool? HasSessions { get; set; }

            [JsonPropertyName("hasProjects")]
            public bool? HasProjects { get; set; }

            [JsonPropertyName("sessionProjectNames")]
            public List<string>? SessionProjectNames { get; set; }

            [JsonPropertyName("projectNames")]
            public List<string>? ProjectNames { get; set; }

            [JsonPropertyName("processId")]
            public int? ProcessId { get; set; }
        }

        private sealed class PortalProbeInvocationResult
        {
            public bool Completed { get; set; }
            public bool TimedOut { get; set; }
            public PortalProbeResult? Payload { get; set; }
            public string? ErrorMessage { get; set; }
            public long ElapsedMs { get; set; }
        }

        private List<PortalProcessInfo> EnumeratePortalProcessInfos()
        {
            return global::Siemens.Engineering.TiaPortal.GetProcesses()
                .Select(proc => new PortalProcessInfo
                {
                    ProcessId = proc.Id,
                    HasVisibleWindow = HasVisibleMainWindow(proc.Id),
                    StartTime = GetProcessStartTimeOrMin(proc.Id)
                })
                .OrderByDescending(p => p.HasVisibleWindow)
                .ThenByDescending(p => p.StartTime)
                .ToList();
        }

        private string BuildConnectStrategyLabel()
        {
            if (Engineering.TiaMajorVersion == 17 && !Engineering.ForceWithoutUserInterface)
            {
                return "gui-first-interactive-safe";
            }

            return Engineering.LaunchWithUserInterface ? "with-ui" : "headless-first";
        }

        private TiaPortalMode GetLaunchModeForConnect()
        {
            if (Engineering.ForceWithoutUserInterface)
            {
                return TiaPortalMode.WithoutUserInterface;
            }

            if (Engineering.TiaMajorVersion == 17)
            {
                return TiaPortalMode.WithUserInterface;
            }

            return Engineering.LaunchWithUserInterface
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface;
        }

        private PortalProcessDiagnostic ProbePortalProcess(PortalProcessInfo processInfo, int attachTimeoutMs, int inspectTimeoutMs)
        {
            var attachProbe = RunPortalProbe("attach_pid", processInfo.ProcessId, attachTimeoutMs);
            if (!attachProbe.Completed || attachProbe.Payload?.Success != true)
            {
                var windows = CaptureWindowsSnapshot(includeOnlyPortalRelated: true, baseline: null);
                var confirmation = windows.Any(IsConfirmationCandidateWindow);
                return new PortalProcessDiagnostic
                {
                    ProcessId = processInfo.ProcessId,
                    HasVisibleWindow = processInfo.HasVisibleWindow,
                    SortKey = processInfo.SortKey,
                    ProbeStatus = confirmation ? "confirmation-required" : (attachProbe.TimedOut ? "timeout" : "attach-failed"),
                    ElapsedMs = attachProbe.ElapsedMs,
                    ErrorType = confirmation ? PortalErrorCode.ConfirmationRequired.ToString() : (attachProbe.Payload?.ErrorType ?? PortalErrorCode.Timeout.ToString()),
                    ErrorMessage = confirmation ? "Attach probe timed out and a TIA-related confirmation window was detected." : attachProbe.ErrorMessage
                };
            }

            var inspectProbe = RunPortalProbe("inspect_attached_portal", processInfo.ProcessId, inspectTimeoutMs);
            if (!inspectProbe.Completed || inspectProbe.Payload?.Success != true)
            {
                var windows = CaptureWindowsSnapshot(includeOnlyPortalRelated: true, baseline: null);
                var confirmation = windows.Any(IsConfirmationCandidateWindow);
                return new PortalProcessDiagnostic
                {
                    ProcessId = processInfo.ProcessId,
                    HasVisibleWindow = processInfo.HasVisibleWindow,
                    SortKey = processInfo.SortKey,
                    ProbeStatus = confirmation ? "confirmation-required" : (inspectProbe.TimedOut ? "inspect-timeout" : "inspect-failed"),
                    ElapsedMs = attachProbe.ElapsedMs + inspectProbe.ElapsedMs,
                    ErrorType = confirmation ? PortalErrorCode.ConfirmationRequired.ToString() : (inspectProbe.Payload?.ErrorType ?? PortalErrorCode.Timeout.ToString()),
                    ErrorMessage = confirmation ? "Inspect probe timed out and a TIA-related confirmation window was detected." : inspectProbe.ErrorMessage
                };
            }

            return new PortalProcessDiagnostic
            {
                ProcessId = processInfo.ProcessId,
                HasVisibleWindow = processInfo.HasVisibleWindow,
                SortKey = processInfo.SortKey,
                ProbeStatus = "ok",
                ElapsedMs = attachProbe.ElapsedMs + inspectProbe.ElapsedMs,
                HasSessions = inspectProbe.Payload?.HasSessions,
                HasProjects = inspectProbe.Payload?.HasProjects,
                SessionProjectNames = inspectProbe.Payload?.SessionProjectNames,
                ProjectNames = inspectProbe.Payload?.ProjectNames
            };
        }

        private TiaPortal? AttachExistingPortal(int processId)
        {
            var proc = global::Siemens.Engineering.TiaPortal.GetProcesses().FirstOrDefault(p => p.Id == processId);
            if (proc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"TIA Portal process not found: PID={processId}");
            }

            if (Engineering.TiaMajorVersion == 17)
            {
                return proc.Attach();
            }

            return AttachWithTimeout(proc, 5000);
        }

        private TiaPortal CreatePortalForCurrentOperation(TiaPortalMode mode)
        {
            if (Engineering.TiaMajorVersion == 17)
            {
                return new TiaPortal(mode);
            }

            return LaunchPortalWithTimeout(mode, ConnectLaunchTimeoutMs);
        }

        private void BindBestAvailableProject(TiaPortal portal, int processId)
        {
            try
            {
                foreach (var s in portal.LocalSessions)
                {
                    try
                    {
                        var p = s.Project;
                        var _ = p?.Name;
                        _session = s;
                        _project = p;
                        return;
                    }
                    catch { }
                }

                foreach (var p in portal.Projects)
                {
                    try
                    {
                        var _ = p?.Name;
                        _session = null;
                        _project = p;
                        return;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, $"Failed to bind project from attached TIA Portal PID={processId}");
            }
        }

        private PortalProbeInvocationResult RunPortalProbe(string action, int? pid, int timeoutMs)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Cannot resolve current executable path.");
            var args = new List<string>
            {
                "--internal-portal-probe",
                "--internal-portal-probe-action", QuoteArgument(action),
                "--tia-major-version", Engineering.TiaMajorVersion.ToString()
            };

            if (!string.IsNullOrWhiteSpace(Engineering.TiaPortalLocationOverride))
            {
                args.Add("--tia-portal-location");
                args.Add(QuoteArgument(Engineering.TiaPortalLocationOverride!));
            }

            if (pid.HasValue)
            {
                args.Add("--internal-portal-probe-pid");
                args.Add(pid.Value.ToString());
            }

            if (Engineering.ForceWithoutUserInterface)
            {
                args.Add("--without-ui");
            }
            else if (Engineering.LaunchWithUserInterface || Engineering.TiaMajorVersion == 17)
            {
                args.Add("--with-ui");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            var sw = Stopwatch.StartNew();
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(3000); } catch { }
                return new PortalProbeInvocationResult
                {
                    Completed = false,
                    TimedOut = true,
                    ErrorMessage = $"Probe action '{action}' exceeded {timeoutMs}ms.",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            stdoutTask.Wait();
            stderrTask.Wait();
            try
            {
                var payload = JsonSerializer.Deserialize<PortalProbeResult>(stdoutTask.Result.Trim(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return new PortalProbeInvocationResult
                {
                    Completed = process.ExitCode == 0,
                    TimedOut = false,
                    Payload = payload,
                    ErrorMessage = process.ExitCode == 0 ? null : (payload?.ErrorMessage ?? stderrTask.Result?.Trim()),
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                return new PortalProbeInvocationResult
                {
                    Completed = false,
                    TimedOut = false,
                    ErrorMessage = "Failed to parse portal probe JSON: " + ex.Message,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
        }

        private static IEnumerable<string> BuildConnectRecommendations(bool needsConfirmation)
        {
            if (needsConfirmation)
            {
                return new[]
                {
                    "Switch to the TIA Portal GUI and confirm the Openness / security prompt.",
                    "Run Connect again after the dialog is confirmed.",
                    "If it still fails, close leftover Siemens.Automation.Portal.exe and reopen TIA manually before retrying."
                };
            }

            return new[]
            {
                "Confirm the current Windows user is still in the Siemens TIA Openness group.",
                "Close leftover Siemens.Automation.Portal.exe processes and retry.",
                "Open TIA Portal GUI manually first, then run DiagnosePortalConnectReadiness."
            };
        }

        private static void PopulatePromptCollections(ConnectAttemptResult attempt)
        {
            foreach (var window in attempt.Windows.Where(IsConfirmationCandidateWindow))
            {
                if (!string.IsNullOrWhiteSpace(window.Title) && !attempt.DetectedPromptTitles.Contains(window.Title))
                {
                    attempt.DetectedPromptTitles.Add(window.Title);
                }
                if (window.ProcessId.HasValue && !attempt.DetectedPromptPids.Contains(window.ProcessId.Value))
                {
                    attempt.DetectedPromptPids.Add(window.ProcessId.Value);
                }
            }
        }

        private static bool IsConfirmationCandidateWindow(PortalWindowObservation observation)
        {
            var title = (observation.Title ?? string.Empty).Trim();
            var className = (observation.ClassName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            var joined = (title + " " + className).ToLowerInvariant();
            return joined.Contains("openness")
                || joined.Contains("confirm")
                || joined.Contains("confirmation")
                || joined.Contains("security")
                || joined.Contains("allow")
                || joined.Contains("authorize")
                || joined.Contains("authorization")
                || joined.Contains("permission")
                || joined.Contains("access")
                || joined.Contains("grant")
                || joined.Contains("授权")
                || joined.Contains("确认")
                || joined.Contains("安全")
                || joined.Contains("允许")
                || joined.Contains("访问");
        }

        private static List<PortalWindowObservation> CaptureWindowsSnapshot(bool includeOnlyPortalRelated, IReadOnlyCollection<PortalWindowObservation>? baseline)
        {
            var list = new List<PortalWindowObservation>();
            var baselineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (baseline != null)
            {
                foreach (var item in baseline)
                {
                    baselineKeys.Add(BuildWindowKey(item.ProcessId, item.Title, item.ClassName));
                }
            }

            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var title = GetWindowTextSafe(hwnd);
                var className = GetClassNameSafe(hwnd);
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(className)) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                var processName = string.Empty;
                try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { }

                var candidate = new PortalWindowObservation
                {
                    ProcessId = (int)pid,
                    ProcessName = processName,
                    Title = title,
                    ClassName = className
                };

                if (includeOnlyPortalRelated && !IsPortalRelatedWindow(candidate))
                {
                    return true;
                }

                candidate.IsNewWindow = !baselineKeys.Contains(BuildWindowKey(candidate.ProcessId, candidate.Title, candidate.ClassName));
                list.Add(candidate);
                return true;
            }, IntPtr.Zero);

            return list;
        }

        private static bool IsPortalRelatedWindow(PortalWindowObservation observation)
        {
            var joined = ((observation.Title ?? string.Empty) + " " + (observation.ClassName ?? string.Empty) + " " + (observation.ProcessName ?? string.Empty)).ToLowerInvariant();
            return joined.Contains("portal")
                || joined.Contains("siemens")
                || joined.Contains("tia")
                || joined.Contains("openness")
                || joined.Contains("automation")
                || joined.Contains("博途");
        }

        private static string BuildWindowKey(int? pid, string? title, string? className)
        {
            return $"{pid}|{title}|{className}";
        }

        private static string QuoteArgument(string value)
        {
            return value.Contains(" ") ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
        }

        private static string GetWindowTextSafe(IntPtr hwnd)
        {
            var buffer = new char[512];
            var len = GetWindowText(hwnd, buffer, buffer.Length);
            return len > 0 ? new string(buffer, 0, len) : string.Empty;
        }

        private static string GetClassNameSafe(IntPtr hwnd)
        {
            var buffer = new char[256];
            var len = GetClassName(hwnd, buffer, buffer.Length);
            return len > 0 ? new string(buffer, 0, len) : string.Empty;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
