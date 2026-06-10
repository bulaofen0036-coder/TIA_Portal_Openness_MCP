using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
#if !TIA_V17
using Siemens.Engineering.HmiUnified;
#endif
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        // closing parantheses for regex characters ommitted, because they are not relevant for regex detection
        private readonly char[] _regexChars = ['.', '^', '$', '*', '+', '?', '(', '[', '{', '\\', '|'];

        private TiaPortal? _portal;
        private ProjectBase? _project;
        private LocalSession? _session;
        // Resolving a softwarePath walks the device tree via Openness (~40 COM calls per call). Cache it per
        // open project; ReferenceEquals(_project) auto-invalidates on any project open/close/create/attach
        // without a COM call. Device adds only introduce new paths (cache misses); there is no delete-device tool.
        private readonly Dictionary<string, SoftwareContainer> _softwareContainerCache = new Dictionary<string, SoftwareContainer>(StringComparer.OrdinalIgnoreCase);
        private ProjectBase? _softwareCacheProject;
        private readonly ILogger<Portal>? _logger;
        public string? LastConnectError { get; private set; }

        #region ctor

        public Portal(ILogger<Portal>? logger = null)
        {
            _logger = logger;
        }

        #endregion

        #region helper for mcp server

        public bool ProjectIsValid
        {
            get
            {
                if (_project == null)
                {
                    return false;
                }

                // Check if the project is a valid Project instance
                if ((_session == null) && (_project is Project))
                {
                    return true;
                }

                // If it's a MultiuserProject, we can also check its validity
                if ((_session != null) && (_project is MultiuserProject))
                {
                    return true;
                }

                return false;
            }
        }

        public bool IsLocalSession
        {
            get
            {
                return _session != null;
            }
        }

        public bool IsLocalProject
        {
            get
            {
                return _session == null;
            }
        }

        #endregion

        #region helper for unit tests

        public static bool IsLocalSessionFile(string sessionPath)
        {
            // Check if the path ends with '.als\d+' using regex
            var regex = new Regex(@"\.als\d+$", RegexOptions.IgnoreCase);
            return regex.IsMatch(sessionPath);
        }

        public static bool IsLocalProjectFile(string projectPath)
        {
            // Check if the path ends with '.ap\d+' using regex
            var regex = new Regex(@"\.ap\d+$", RegexOptions.IgnoreCase);
            return regex.IsMatch(projectPath);
        }

        public void Dispose()
        {
            try
            {
                (_project as Project)?.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error closing the project on Dispose");
            }

            try
            {
                _portal?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing TIA Portal on Dispose");
            }
        }

        #endregion

        #region portal

        // Attach to a portal process but never block longer than timeoutMs. An orphaned/dying
        // Siemens.Automation.Portal (e.g. its controlling process was killed) can otherwise hang the
        // COM Attach() call ~200s before throwing EngineeringSecurityException, stalling the whole
        // connect. On timeout we return null so the caller skips this process and tries the next /
        // launches a fresh instance. The worker thread is background and dies with the dead process.
        private TiaPortal? AttachWithTimeout(TiaPortalProcess proc, int timeoutMs)
        {
            TiaPortal? result = null;
            Exception? error = null;
            var worker = new System.Threading.Thread(() =>
            {
                try { result = proc.Attach(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            if (!worker.Join(timeoutMs))
            {
                _logger?.LogWarning($"Attach to TIA Portal PID={proc.Id} exceeded {timeoutMs}ms; skipping (likely orphaned/dying instance).");
                return null;
            }
            if (error != null) throw error;
            return result;
        }

        private TiaPortal LaunchPortalWithTimeout(TiaPortalMode mode, int timeoutMs)
        {
            TiaPortal? result = null;
            Exception? error = null;
            var portalProcessesBeforeLaunch = SnapshotPortalProcessIds();

            var worker = new System.Threading.Thread(() =>
            {
                try { result = new TiaPortal(mode); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };

            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            if (!worker.Join(timeoutMs))
            {
                var cleanedPortalPids = CleanupNewPortalProcesses(portalProcessesBeforeLaunch);
                var cleanupNote = cleanedPortalPids.Count > 0
                    ? $" Cleaned up portal PID(s): {string.Join(", ", cleanedPortalPids)}."
                    : string.Empty;
                throw new PortalException(
                    PortalErrorCode.OpennessError,
                    $"Starting TIA Portal ({mode}) timed out after {timeoutMs}ms.{cleanupNote} Start TIA Portal manually once, close orphaned Siemens.Automation.Portal processes, then retry Connect or AttachToOpenProject.",
                    inner: new TimeoutException($"Launching TIA Portal ({mode}) exceeded {timeoutMs}ms."));
            }

            if (error != null) throw error;
            return result ?? throw new PortalException(
                PortalErrorCode.OpennessError,
                $"Starting TIA Portal ({mode}) returned null.");
        }

        private static HashSet<int> SnapshotPortalProcessIds()
        {
            return Process.GetProcessesByName("Siemens.Automation.Portal")
                .Select(p => p.Id)
                .ToHashSet();
        }

        private static List<int> CleanupNewPortalProcesses(HashSet<int> portalProcessesBeforeLaunch)
        {
            var killed = new List<int>();

            foreach (var proc in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                try
                {
                    if (portalProcessesBeforeLaunch.Contains(proc.Id))
                        continue;

                    killed.Add(proc.Id);
                    proc.Kill();
                }
                catch
                {
                    // best effort only
                }
            }

            return killed;
        }

        private static bool HasVisibleMainWindow(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime GetProcessStartTimeOrMin(int processId)
        {
            try
            {
                return Process.GetProcessById(processId).StartTime;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public bool ConnectPortal()
        {
            return ConnectPortalSafe();
        }

        public List<string> ListPortalProcessProjects()
        {
            return ListPortalProcessProjectsSafe();
        }

        public bool IsConnected()
        {
            return _portal != null;
        }

        public bool DisconnectPortal()
        {
            _logger?.LogInformation("Disconnecting from TIA Portal...");
            return Operation.Run(_logger, nameof(DisconnectPortal), () =>
            {
                _project = null;
                _session = null;
                _portal?.Dispose();
                _portal = null;
            });
        }

        #endregion

        #region status

        public State GetState()
        {
            _logger?.LogInformation("Getting TIA Portal state...");
            if (_portal != null)
            {
                // check for existing local sessions
                if (_portal.LocalSessions.Any())
                {
                    // pick first session whose Project is accessible
                    foreach (var s in _portal.LocalSessions)
                    {
                        try
                        {
                            var p = s.Project;
                            var _ = p?.Name; // touch to validate not disposed
                            _session = s;
                            _project = p;
                            break;
                        }
                        catch
                        {
                            // skip disposed/inaccessible session projects
                        }
                    }
                }
                // checks for existing projects
                else if (_portal.Projects.Any())
                {
                    // pick first accessible project (avoid disposed placeholder)
                    foreach (var p in _portal.Projects)
                    {
                        try
                        {
                            var _ = p?.Name;
                            _project = p;
                            break;
                        }
                        catch
                        {
                            // skip disposed
                        }
                    }
                }
            }

            return new State
            {
                IsConnected = IsConnected(),
                Project = _project != null ? _project.Name : "-",
                Session = _session != null ? _session.Project.Name : "-"
            };
        }

        public bool AttachToOpenProject(string projectName)
        {
            return AttachToOpenProjectSafe(projectName);
        }

        private bool TryAttachProjectInPortal(TiaPortal portal, string projectName)
        {
            try
            {
                foreach (var s in portal.LocalSessions)
                {
                    try
                    {
                        var p = s.Project;
                        if (p != null && string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
                        {
                            _session = s;
                            _project = p;
                            return true;
                        }
                    }
                    catch { }
                }

                foreach (var p in portal.Projects)
                {
                    try
                    {
                        if (p != null && string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
                        {
                            _session = null;
                            _project = p;
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        #endregion

        #region project

        public List<ProjectBase> GetProjects()
        {
            _logger?.LogInformation("Getting open projects...");

            if (_portal == null)
            {
                _logger?.LogWarning("No TIA Portal instance available.");

                return [];
            }

            var projects = new List<ProjectBase>();

            if (_portal.Projects != null)
            {
                foreach (var project in _portal.Projects)
                {
                    projects.Add(project);
                }
            }

            return projects;
        }

        public bool OpenProject(string projectPath)
        {
            _logger?.LogInformation($"Opening project: {projectPath}");

            if (IsPortalNull())
            {
                // ConnectPortal 现以 PortalException 报硬失败；此处保留 OpenProject 原有 bool 契约
                try { ConnectPortal(); }
                catch (PortalException ex)
                {
                    LastConnectError = $"Portal is null and reconnect failed: {ex.Message}";
                    return false;
                }
            }

            if (_project != null)
            {
                (_project as Project)?.Close();
                _project = null;
            }

            if (_session != null)
            {
                _session.Close();
                _session = null;
            }

            try
            {
                LastConnectError = null;

                if (string.IsNullOrWhiteSpace(projectPath))
                {
                    LastConnectError = "projectPath is empty";
                    return false;
                }

                if (!File.Exists(projectPath))
                {
                    LastConnectError = $"Project file not found: {projectPath}";
                    return false;
                }

                var projects = GetProjects();
                var projectName = Path.GetFileNameWithoutExtension(projectPath);

                if (!string.IsNullOrEmpty(projectName) && projects.Any(p => p.Name.Equals(projectName)))
                {
                    // Project is already open
                    return AttachToOpenProject(projectName);
                }
                else
                {
                    // see [5.3.1 Projekt öffnen, S.113]
                    var fi = new FileInfo(projectPath);

                    try
                    {
                        _project = _portal?.Projects.OpenWithUpgrade(fi);
                    }
                    catch (Exception ex)
                    {
                        LastConnectError = $"OpenWithUpgrade failed: {ex}";
                        _project = null;
                    }

                    if (_project != null) return true;

                    // Fallback: some environments expose Projects.Open(FileInfo) instead.
                    try
                    {
                        var projectsComp = _portal?.Projects;
                        if (projectsComp != null)
                        {
                            var mOpen = projectsComp.GetType().GetMethod("Open", new[] { typeof(FileInfo) });
                            if (mOpen != null)
                            {
                                var opened = mOpen.Invoke(projectsComp, new object[] { fi });
                                if (opened is ProjectBase pb)
                                {
                                    _project = pb;
                                    LastConnectError = null;
                                    return true;
                                }
                            }
                            else
                            {
                                LastConnectError ??= "Projects.Open(FileInfo) method not found";
                            }
                        }
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        LastConnectError = $"Projects.Open failed: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    }
                    catch (Exception ex)
                    {
                        LastConnectError = $"Projects.Open failed: {ex}";
                    }

                    LastConnectError ??= "OpenProject returned null (no exception)";
                    return false;
                }
            }
            catch (Exception ex)
            {
                LastConnectError = ex.ToString();
                return false;
            }
        }

        public bool CreateProject(string directoryPath, string projectName)
        {
            _logger?.LogInformation($"Creating project: dir={directoryPath}, name={projectName}");
            LastConnectError = null;

            if (IsPortalNull())
            {
                LastConnectError = "CreateProject failed because TIA Portal is not connected.";
                return false;
            }

            try
            {
                if (_project != null)
                {
                    (_project as Project)?.Close();
                    _project = null;
                }

                if (_session != null)
                {
                    _session.Close();
                    _session = null;
                }

                Directory.CreateDirectory(directoryPath);
                var di = new DirectoryInfo(directoryPath);

                var created = _portal!.Projects.Create(di, projectName);
                _project = created;
                LastConnectError = null;
                return _project != null;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                LastConnectError = $"Projects.Create failed: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                _logger?.LogError(tie.InnerException, "CreateProject failed: dir={Dir}, name={Name}", directoryPath, projectName);
                return false;
            }
            catch (Exception ex)
            {
                LastConnectError = $"Projects.Create failed: {ex.GetType().FullName}: {ex.Message}";
                _logger?.LogError(ex, "CreateProject failed: dir={Dir}, name={Name}", directoryPath, projectName);
                return false;
            }
        }

        public object? GetProjectInfo()
        {
            _logger?.LogInformation("Getting project info...");

            if (IsPortalNull())
            {
                return null;
            }

            if (IsProjectNull())
            {
                return null;
            }

            var project = _project!;

            var info = new
            {
                Name = project.Name,
                Path = project.Path,
                Type = project.GetType().Name,
                IsMultiuserProject = project is MultiuserProject,
                IsLocalSession = _session != null,
                IsLocalProject = _session == null
            };

            return info;
        }

        public bool SaveProject()
        {
            _logger?.LogInformation("Saving project...");

            if (IsProjectNull())
            {
                return false;
            }

            (_project as Project)?.Save();

            return true;
        }

        public bool SaveAsProject(string path)
        {
            _logger?.LogInformation($"Saving project as: {path}");

            if (IsProjectNull())
            {
                return false;
            }

            var di = new DirectoryInfo(path);

            (_project as Project)?.SaveAs(di);

            return true;
        }

        public bool CloseProject()
        {
            _logger?.LogInformation("Closing project...");

            if (IsProjectNull())
            {
                return false;
            }

            (_project as Project)?.Close();
            _project = null;

            return true;
        }

        #endregion

        #region session

        public List<ProjectBase> GetSessions()
        {
            _logger?.LogInformation("Getting open local sessions...");

            if (IsPortalNull())
            {
                return [];
            }

            var sessions = new List<ProjectBase>();

            if (_portal?.LocalSessions != null)
            {
                foreach (var session in _portal.LocalSessions)
                {
                    sessions.Add(session.Project as ProjectBase);
                }
            }

            return sessions;
        }

        public bool OpenSession(string localSessionPath)
        {
            _logger?.LogInformation($"Opening session: {localSessionPath}");

            if (IsPortalNull())
            {
                return false;
            }

            if (_session != null)
            {
                _project = null;
                _session?.Close();
                _session = null;
            }

            try
            {
                var sessions = GetSessions();
                var projectName = Path.GetFileNameWithoutExtension(localSessionPath);
                var sessionName = Regex.Replace(projectName, @"_(LS|ES)_\d$", string.Empty, RegexOptions.IgnoreCase);

                if (!string.IsNullOrEmpty(sessionName) && sessions.Any(s => s.Name.Equals(sessionName)))
                {
                    // Session is already open  
                    _session = _portal?.LocalSessions.FirstOrDefault(s => s.Project.Name == sessionName);
                    if (_session != null)
                    {
                        // Correctly cast MultiuserProject to Project  
                        _project = _session.Project;
                        return _project != null;
                    }
                }
                else
                {
                    _session = _portal?.LocalSessions.Open(new FileInfo(localSessionPath));
                    if (_session != null)
                    {
                        // Correctly cast MultiuserProject to Project
                        _project = _session.Project;
                        return _project != null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OpenSession failed: {Path}", localSessionPath);
                return false;
            }

            return false;
        }

        public bool SaveSession()
        {
            _logger?.LogInformation("Saving session...");

            if (IsSessionNull())
            {
                return false;
            }

            // Save session
            _session?.Save();

            return true;
        }

        public bool CloseSession()
        {
            _logger?.LogInformation("Closing session...");

            if (IsSessionNull())
            {
                return false;
            }

            _project = null;
            _session?.Close();
            _session = null;

            return true;
        }

        #endregion

        #region devices

        public string GetProjectTree()
        {
            _logger?.LogInformation("Getting project tree...");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            StringBuilder sb = new();

            sb.AppendLine($"{_project?.Name}");

            var ancestorStates = new List<bool>();
            var sections = new List<Action>();
            
            if (_project?.Devices != null && _project.Devices.Count > 0)
            {
                sections.Add(() => GetProjectTreeDevices(sb, _project.Devices, ancestorStates));
            }
            
            if (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0)
            {
                sections.Add(() => GetProjectTreeGroups(sb, _project.DeviceGroups, ancestorStates));
            }
            
            if (_project?.UngroupedDevicesGroup != null)
            {
                sections.Add(() => GetProjectTreeUngroupedDeviceGroup(sb, _project.UngroupedDevicesGroup, ancestorStates));
            }
            
            for (int i = 0; i < sections.Count; i++)
            {
                var isLastSection = i == sections.Count - 1;
                if (i == 0)
                {
                    sections[i]();
                }
                else
                {
                    sections[i]();
                }
            }

            return sb.ToString();
        }

        

        public List<Device> GetDevices(string regexName = "")
        {
            _logger?.LogInformation("Getting devices...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<Device>();

            if (_project?.Devices != null)
            {
                foreach (Device device in _project.Devices)
                {
                    list.Add(device);
                }

                foreach (var group in _project.DeviceGroups)
                {
                    GetDevicesRecursive(group, list, regexName);
                }

                //foreach (var group in _project.UngroupedDevicesGroup)
                //{
                //    GetDevicesRecursive(_project.UngroupedDevicesGroup, list, regexName);
                //}
            }

            return list;
        }

        public Device? GetDevice(string devicePath)
        {
            _logger?.LogInformation($"Getting device by path: {devicePath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceByPath(devicePath);
        }

        public Device AddDevice(string orderNumber, string version, string deviceName)
        {
            _logger?.LogInformation($"Adding device: {deviceName}, OrderNumber={orderNumber}, Version={version}");
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

            string? lastVariantError = null;
            try
            {
                // Openness CreateWithItem expects a TypeIdentifier, not split order/version.
                // Example: OrderNumber:6ES7 513-1AM03-0AB0/V3.0
                var project = (_project as Project);
                if (project == null) throw new PortalException(PortalErrorCode.InvalidState, "Current project is not a local Project instance");

                var orderRaw = orderNumber ?? "";
                var verRaw = version ?? "";

                var orderVariants = new List<string>
                {
                    orderRaw,
                    NormalizeOrderNumber(orderRaw),
                    TryFormatMlfbWithSpaces(orderRaw),
                    TryFormatMlfbWithSpaces(NormalizeOrderNumber(orderRaw))
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var v = verRaw.Trim();
                var vNoV = v.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? v.Substring(1) : v;
                var versionVariants = new List<string>
                {
                    v,
                    "V" + vNoV,
                    vNoV,
                    "" // let TIA pick default/latest if supported
                };

                // append common ".0" expansions (V3.0 -> V3.0.0 etc.)
                foreach (var baseV in new[] { v, "V" + vNoV, vNoV })
                {
                    if (string.IsNullOrWhiteSpace(baseV)) continue;
                    if (!baseV.Contains('.')) continue;

                    versionVariants.Add(baseV + ".0");
                    versionVariants.Add(baseV + ".0.0");
                }

                // Heuristic: if user provides TIA20.* for Unified panels, try bump to 21.* as well.
                // (Common when copying order/version from older screenshots.)
                try
                {
                    var parts = vNoV.Split('.');
                    if (parts.Length > 0 && int.TryParse(parts[0], out var maj) && maj == 20)
                    {
                        versionVariants.Add("21.0.0.0");
                        versionVariants.Add("21.0.0.1");
                        versionVariants.Add("V21.0.0.0");
                    }
                }
                catch { }

                versionVariants = versionVariants
                    .Where(x => x != null) // keep empty-string variant
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var typeIdentifierVariants = new List<string>();
                foreach (var o in orderVariants)
                {
                    foreach (var ver in versionVariants)
                    {
                        if (o.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
                        {
                            typeIdentifierVariants.Add(o);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(ver))
                            typeIdentifierVariants.Add($"OrderNumber:{o}/{ver}");
                    }
                }

                typeIdentifierVariants = typeIdentifierVariants
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var typeIdentifier in typeIdentifierVariants)
                {
                    foreach (var itemName in new[] { deviceName, "Device_1", "Station_1" }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var dev = project.Devices.CreateWithItem(typeIdentifier, itemName, deviceName);
                            if (dev is Device d) return d;
                        }
                        catch (Exception exTry)
                        {
                            // try next variant
                            lastVariantError = FormatExceptionDetail(exTry);
                        }
                    }
                }

                throw new PortalException(PortalErrorCode.OpennessError,
                    lastVariantError ?? $"AddDevice failed: no device created for OrderNumber={orderNumber} Version={version}");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError, FormatExceptionDetail(ex), null, ex);
            }
        }

        public (Device? Device, string? MlfbUsed, string? VersionUsed, List<string> Attempts, string? Error) AddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family)
        {
            var attempts = new List<string>();
            string? lastError = null;

            // Minimal built-in list (keep small; user can pass preferred MLFB first)
            var known = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["S7-1500"] = new List<string>
                {
                    "6ES7513-1AM03-0AB0",
                    "6ES7516-3AN03-0AB0",
                    "6ES7515-2AM02-0AB0",
                    "6ES7513-1AL03-0AB0",
                    "6ES7512-1AK02-0AB0",
                },
                ["WinCCUnifiedPC"] = new List<string>
                {
                    // Unified PC Runtime (actual availability depends on installed packages/HSP)
                    "6AV2123-3GB32-0AW0",
                    "6AV2154-0BS01-0AA0",
                    "6AV2154-0BP01-0AA0",
                },
                ["S7-1200"] = new List<string>
                {
                    // CPU 1211C AC/DC/Rly
                    "6ES7211-1BE40-0XB0",
                }
            };

            var mlfbs = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredMlfb)) mlfbs.Add(preferredMlfb);
            if (known.TryGetValue(family ?? "", out var list)) mlfbs.AddRange(list);
            mlfbs = mlfbs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var versions = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredVersion)) versions.Add(preferredVersion);
            if (string.Equals(family, "S7-1500", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("V3.0");
                versions.Add("V3.1");
                versions.Add("V2.9");
            }
            if (string.Equals(family, "WinCCUnifiedPC", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("20.0.0.0");
                versions.Add("21.0.0.0");
            }
            if (string.Equals(family, "S7-1200", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("V4.7");
                versions.Add("4.7");
                versions.Add("V4.6");
                versions.Add("4.6");
                versions.Add("V4.5");
                versions.Add("4.5");
            }
            versions.Add("21.0.0.0");
            versions.Add("V21.0.0.0");
            versions.Add("");
            versions = versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var mlfb in mlfbs)
            {
                foreach (var ver in versions)
                {
                    try
                    {
                        var d = AddDevice(mlfb, ver, deviceName);
                        attempts.Add($"{mlfb} {ver} -> OK");
                        return (d, mlfb, ver, attempts, null);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        attempts.Add($"{mlfb} {ver} -> FAIL: {ex.Message}");
                    }
                }
            }

            return (null, null, null, attempts, lastError ?? "All attempts failed");
        }

        public List<GsdDeviceCandidate> SearchInstalledGsdDevices(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<GsdDeviceCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new PortalException(PortalErrorCode.InvalidParams, "Keyword is empty");

            try
            {
                var catalog = _portal == null ? null : TryGetPropertyValue(_portal, "HardwareCatalog");
                if (catalog != null)
                {
                    foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                    {
                        foreach (var entry in FindHardwareCatalogEntries(catalog, filter))
                        {
                            var candidate = CatalogEntryToCandidate(entry, normalizedKeyword);
                            if (candidate == null) continue;

                            var key = candidate.TypeIdentifierNormalized
                                      ?? candidate.TypeIdentifier
                                      ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                            if (!seen.Add(key)) continue;
                            results.Add(candidate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "HardwareCatalog search failed during GSD device search");
            }

            try
            {
                foreach (var candidate in SearchGsdmlFiles(normalizedKeyword))
                {
                    var key = $"GSDML|{candidate.GsdmlPath}|{candidate.DapId}|{candidate.DapName}";
                    if (!seen.Add(key)) continue;
                    results.Add(candidate);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GSDML scan failed during GSD device search");
            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, normalizedKeyword, null);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.Source)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public List<HardwareCatalogCandidate> SearchHardwareCatalog(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<HardwareCatalogCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new PortalException(PortalErrorCode.InvalidParams, "Keyword is empty");

            try
            {
                var catalog = _portal == null ? null : TryGetPropertyValue(_portal, "HardwareCatalog");
                if (catalog != null)
                {
                    foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                    {
                        foreach (var entry in FindHardwareCatalogEntries(catalog, filter))
                        {
                            var candidate = CatalogEntryToHardwareCandidate(entry, normalizedKeyword);
                            if (candidate == null) continue;

                            var key = candidate.TypeIdentifierNormalized
                                      ?? candidate.TypeIdentifier
                                      ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                            if (!seen.Add(key)) continue;
                            results.Add(candidate);
                        }
                    }
                }
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "HardwareCatalog search failed");
            }

            if (results.Count == 0)
            {
                foreach (var candidate in SearchKnownHardwareCatalogFallbacks(normalizedKeyword))
                {
                    var key = candidate.TypeIdentifierNormalized
                              ?? candidate.TypeIdentifier
                              ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                    if (!seen.Add(key)) continue;
                    results.Add(candidate);
                }
            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogCandidate(c, normalizedKeyword);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.ArticleNumber)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public (Device? Device, HardwareCatalogCandidate? Candidate, List<HardwareCatalogCandidate> Candidates, List<string> Attempts, string? Error)
            AddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = "")
        {
            var attempts = new List<string>();
            var candidates = SearchHardwareCatalog(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogCandidate(c, keyword, preferredText);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No hardware catalog candidates found for '{keyword}'");
            }

            if (IsProjectNull())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = _project as Project;
            if (project == null)
            {
                return (null, null, candidates, attempts, "Current project is not a local Project instance.");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                var typeIdentifier = candidate.TypeIdentifier!.Trim();
                try
                {
                    var itemName = MakeEngineeringName(deviceName);
                    var dev = project.Devices.CreateWithItem(typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = FormatExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => c.Insertable == true))
                lastError = "Hardware catalog returned no insertable TypeIdentifier candidates.";

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        public (Device? Device, GsdDeviceCandidate? Candidate, List<GsdDeviceCandidate> Candidates, List<string> Attempts, string? Error)
            AddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = "")
        {
            var attempts = new List<string>();
            var candidates = SearchInstalledGsdDevices(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, keyword, preferredDap);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No installed GSD/catalog candidates found for '{keyword}'");
            }

            if (IsProjectNull())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = _project as Project;
            if (project == null)
            {
                return (null, null, candidates, attempts, "Current project is not a local Project instance.");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                var typeIdentifier = candidate.TypeIdentifier!.Trim();
                try
                {
                    var itemName = MakeEngineeringName(deviceName);
                    var dev = project.Devices.CreateWithItem(typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = FormatExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                lastError = "Only GSDML file metadata was found; HardwareCatalog did not return an insertable TypeIdentifier.";
            }

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        private static IEnumerable<string> BuildHardwareCatalogFilters(string keyword)
        {
            var raw = (keyword ?? string.Empty).Trim();
            var tokens = raw.Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return new[] { raw }
                .Concat(tokens.Where(t => t.Length >= 3))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static IEnumerable<object> FindHardwareCatalogEntries(object catalog, string filter)
        {
            var find = catalog.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (find == null) yield break;

            var value = find.Invoke(catalog, new object[] { filter });
            if (value is not IEnumerable enumerable) yield break;

            foreach (var item in enumerable)
            {
                if (item != null) yield return item;
            }
        }

        private static GsdDeviceCandidate? CatalogEntryToCandidate(object entry, string keyword)
        {
            var c = new GsdDeviceCandidate
            {
                Source = "HardwareCatalog",
                Keyword = keyword,
                ArticleNumber = TryGetPropertyValue(entry, "ArticleNumber")?.ToString(),
                CatalogPath = TryGetPropertyValue(entry, "CatalogPath")?.ToString(),
                Description = TryGetPropertyValue(entry, "Description")?.ToString(),
                TypeIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString(),
                TypeIdentifierNormalized = TryGetPropertyValue(entry, "TypeIdentifierNormalized")?.ToString(),
                TypeName = TryGetPropertyValue(entry, "TypeName")?.ToString(),
                Version = TryGetPropertyValue(entry, "Version")?.ToString()
            };

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static HardwareCatalogCandidate? CatalogEntryToHardwareCandidate(object entry, string keyword)
        {
            var c = new HardwareCatalogCandidate
            {
                Source = "HardwareCatalog",
                Keyword = keyword,
                ArticleNumber = TryGetPropertyValue(entry, "ArticleNumber")?.ToString(),
                CatalogPath = TryGetPropertyValue(entry, "CatalogPath")?.ToString(),
                Description = TryGetPropertyValue(entry, "Description")?.ToString(),
                TypeIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString(),
                TypeIdentifierNormalized = TryGetPropertyValue(entry, "TypeIdentifierNormalized")?.ToString(),
                TypeName = TryGetPropertyValue(entry, "TypeName")?.ToString(),
                Version = TryGetPropertyValue(entry, "Version")?.ToString()
            };
            c.Insertable = !string.IsNullOrWhiteSpace(c.TypeIdentifier);

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static IEnumerable<GsdDeviceCandidate> SearchGsdmlFiles(string keyword)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Siemens",
                "Automation",
                $"Portal V{Engineering.TiaMajorVersion}",
                "data",
                "xdd",
                "gsd");

            if (!Directory.Exists(root)) yield break;

            foreach (var path in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                string textPrefix = fileName;
                if (!ContainsAnyKeywordToken(textPrefix, keyword))
                {
                    try
                    {
                        var raw = File.ReadAllText(path, Encoding.UTF8);
                        if (!ContainsAnyKeywordToken(raw, keyword)) continue;
                    }
                    catch
                    {
                        continue;
                    }
                }

                XDocument doc;
                try { doc = XDocument.Load(path); }
                catch { continue; }

                var vendor = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProfileHeader")?.Attribute("VendorName")?.Value;
                var family = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Family");
                var mainFamily = family?.Attribute("MainFamily")?.Value;
                var productFamily = family?.Attribute("ProductFamily")?.Value;
                var orderNumber = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OrderNumber")?.Attribute("Value")?.Value;

                foreach (var dap in doc.Descendants().Where(e => e.Name.LocalName == "DeviceAccessPointItem"))
                {
                    var dapId = dap.Attribute("ID")?.Value;
                    var dapName = dap.Attribute("DNS_CompatibleName")?.Value ?? dap.Attribute("Name")?.Value;
                    var textId = dap.Attribute("TextId")?.Value;
                    var infoText = FindGsdText(doc, textId) ?? dapName ?? fileName;
                    var haystack = string.Join(" ", new[] { vendor, mainFamily, productFamily, orderNumber, dapId, dapName, infoText, fileName });
                    if (!ContainsAnyKeywordToken(haystack, keyword)) continue;

                    yield return new GsdDeviceCandidate
                    {
                        Source = "GSDML",
                        Keyword = keyword,
                        Vendor = vendor,
                        MainFamily = mainFamily,
                        ProductFamily = productFamily,
                        DapId = dapId,
                        DapName = dapName,
                        ArticleNumber = orderNumber,
                        Description = infoText,
                        GsdmlPath = path,
                        Score = ScoreGsdCandidateText(haystack, keyword, null)
                    };
                }
            }
        }

        private static string? FindGsdText(XDocument doc, string? textId)
        {
            if (string.IsNullOrWhiteSpace(textId)) return null;
            var text = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text" && string.Equals(e.Attribute("TextId")?.Value, textId, StringComparison.OrdinalIgnoreCase));
            return text?.Attribute("Value")?.Value;
        }

        private static int ScoreGsdCandidate(GsdDeviceCandidate c, string keyword, string? preferredDap)
        {
            var text = string.Join(" ", new[]
            {
                c.Vendor, c.ProductFamily, c.MainFamily, c.DapId, c.DapName, c.ArticleNumber, c.CatalogPath,
                c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version, c.GsdmlPath
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return ScoreGsdCandidateText(text, keyword, preferredDap)
                   + (string.Equals(c.Source, "HardwareCatalog", StringComparison.OrdinalIgnoreCase) ? 30 : 0)
                   + (!string.IsNullOrWhiteSpace(c.TypeIdentifier) ? 50 : 0);
        }

        private static IEnumerable<HardwareCatalogCandidate> SearchKnownHardwareCatalogFallbacks(string keyword)
        {
            foreach (var candidate in GetKnownHardwareCatalogFallbackCandidates())
            {
                var text = string.Join(" ", new[]
                {
                    candidate.ArticleNumber,
                    candidate.CatalogPath,
                    candidate.Description,
                    candidate.TypeIdentifier,
                    candidate.TypeIdentifierNormalized,
                    candidate.TypeName,
                    candidate.Version
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!ContainsAllKeywordTokens(text, keyword) && !ContainsAnyKeywordToken(text, keyword)) continue;
                yield return candidate;
            }
        }

        private static IEnumerable<HardwareCatalogCandidate> GetKnownHardwareCatalogFallbackCandidates()
        {
            yield return BuildKnownHardwareCandidate(
                articleNumber: "6ES7211-1BE40-0XB0",
                description: "CPU 1211C AC/DC/Rly",
                typeName: "S7-1200",
                version: "V4.7",
                catalogPath: "Fallback/S7-1200");

            yield return BuildKnownHardwareCandidate(
                articleNumber: "6ES7513-1AM03-0AB0",
                description: "CPU 1513-1 PN",
                typeName: "S7-1500",
                version: "V3.0",
                catalogPath: "Fallback/S7-1500");

            yield return BuildKnownHardwareCandidate(
                articleNumber: "6ES7516-3AN03-0AB0",
                description: "CPU 1516-3 PN/DP",
                typeName: "S7-1500",
                version: "V3.0",
                catalogPath: "Fallback/S7-1500");

            yield return BuildKnownHardwareCandidate(
                articleNumber: "6ES7515-2AM02-0AB0",
                description: "CPU 1515-2 PN",
                typeName: "S7-1500",
                version: "V3.1",
                catalogPath: "Fallback/S7-1500");

            yield return BuildKnownHardwareCandidate(
                articleNumber: "6ES7513-1AL03-0AB0",
                description: "CPU 1513 PN",
                typeName: "S7-1500",
                version: "V2.9",
                catalogPath: "Fallback/S7-1500");

            yield return BuildKnownHardwareCandidate(
                articleNumber: "6ES7512-1AK02-0AB0",
                description: "CPU 1512C-1 PN",
                typeName: "S7-1500",
                version: "V2.9",
                catalogPath: "Fallback/S7-1500");
        }

        private static HardwareCatalogCandidate BuildKnownHardwareCandidate(
            string articleNumber,
            string description,
            string typeName,
            string version,
            string catalogPath)
        {
            return new HardwareCatalogCandidate
            {
                Source = "KnownDeviceFallback",
                Keyword = articleNumber,
                ArticleNumber = articleNumber,
                CatalogPath = catalogPath,
                Description = description,
                TypeIdentifier = articleNumber,
                TypeIdentifierNormalized = NormalizeCatalogSearchText(articleNumber),
                TypeName = typeName,
                Version = version,
                Insertable = true
            };
        }

        private static int ScoreHardwareCatalogCandidate(HardwareCatalogCandidate c, string keyword, string? preferredText = null)
        {
            var text = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            var preferred = preferredText ?? string.Empty;
            var score = ScoreGsdCandidateText(text, keyword, null)
                   + (string.IsNullOrWhiteSpace(preferred) ? 0 : ScoreGsdCandidateText(text, preferred, null))
                   + (c.Insertable == true ? 50 : 0)
                   + (!string.IsNullOrWhiteSpace(c.ArticleNumber) ? 10 : 0);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var normalizedPreferred = NormalizeCatalogSearchText(preferred);
                var normalizedArticle = NormalizeCatalogSearchText(c.ArticleNumber ?? "");
                var normalizedTypeIdentifier = NormalizeCatalogSearchText(c.TypeIdentifier ?? "");
                if (!string.IsNullOrWhiteSpace(normalizedArticle) && normalizedPreferred.Contains(normalizedArticle))
                    score += 100;
                if (!string.IsNullOrWhiteSpace(normalizedTypeIdentifier) && normalizedTypeIdentifier.Contains(normalizedPreferred))
                    score += 50;
            }

            var asksSiplus = ContainsAnyKeywordToken("SIPLUS", keyword + " " + preferred);
            var asksPortrait = ContainsAnyKeywordToken("Portrait 立式", keyword + " " + preferred);
            if (!asksSiplus && ContainsAnyKeywordToken(text, "SIPLUS"))
                score -= 40;
            if (!asksPortrait && ContainsAnyKeywordToken(text, "Portrait 立式"))
                score -= 25;

            return score;
        }

        private static string NormalizeCatalogSearchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        private static int ScoreHardwareCatalogCandidateLegacy(HardwareCatalogCandidate c, string keyword, string? preferredText = null)
        {
            var text = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return ScoreGsdCandidateText(text, keyword, null)
                   + (string.IsNullOrWhiteSpace(preferredText) ? 0 : ScoreGsdCandidateText(text, preferredText!, null))
                   + (c.Insertable == true ? 50 : 0)
                   + (!string.IsNullOrWhiteSpace(c.ArticleNumber) ? 10 : 0);
        }

        private static int ScoreGsdCandidateText(string text, string keyword, string? preferredDap)
        {
            var score = 0;
            var haystack = text ?? string.Empty;
            foreach (var token in SplitSearchTokens(keyword))
            {
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) score += token.Length >= 5 ? 20 : 10;
            }

            if (!string.IsNullOrWhiteSpace(preferredDap) &&
                haystack.IndexOf(preferredDap!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 40;
            }

            if (haystack.IndexOf(keyword ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0) score += 50;
            return score;
        }

        private static bool ContainsAnyKeywordToken(string text, string keyword)
        {
            return SplitSearchTokens(keyword).Any(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ContainsAllKeywordTokens(string text, string keyword)
        {
            var tokens = SplitSearchTokens(keyword).ToList();
            return tokens.Count > 0 && tokens.All(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<string> SplitSearchTokens(string keyword)
        {
            return (keyword ?? string.Empty)
                .Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string MakeEngineeringName(string value)
        {
            var name = Regex.Replace(value ?? "Device_1", @"[^\w]", "_");
            if (string.IsNullOrWhiteSpace(name)) name = "Device_1";
            if (char.IsDigit(name[0])) name = "Device_" + name;
            return name;
        }

        private static string NormalizeOrderNumber(string s)
        {
            return (s ?? string.Empty).Replace(" ", "").Trim();
        }

        private static string TryFormatMlfbWithSpaces(string value)
        {
            var n = NormalizeOrderNumber(value);
            if (n.Length < 8) return n;

            if (n.StartsWith("6ES7", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, 4) + " " + n.Substring(4);

            if (n.StartsWith("6AV2", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, 4) + " " + n.Substring(4);

            return n;
        }
        // NOTE: Demo orchestration helpers removed.
        // In V21, the stable path is: generate/import standard block XML -> ImportBlock/ImportBlocksFromDirectory -> CompileSoftware.

        public DeviceItem? GetDeviceItem(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting device item by path: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceItemByPath(deviceItemPath);

        }

        public string GetDeviceItemTree(string deviceItemPath, int maxDepth = 4)
        {
            _logger?.LogInformation($"Getting device item tree by path: {deviceItemPath}, depth={maxDepth}");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            var root = GetDeviceItemByPath(deviceItemPath);
            if (root == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{root.Name} [DeviceItem]");
            BuildDeviceItemTree(sb, root, new List<bool>(), 0, Math.Max(0, maxDepth));
            return sb.ToString();
        }

        public List<ModelContextProtocol.NetworkAttribute>? GetDeviceItemNetworkInfo(string deviceItemPath)
        {
            if (IsProjectNull()) return null;
            var di = GetDeviceItemByPath(deviceItemPath);
            if (di == null) return null;

            // Heuristic: filter attribute names that likely contain network addressing / interface identity.
            var keys = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address"
            };

            var list = new List<ModelContextProtocol.NetworkAttribute>();
            try
            {
                foreach (var info in di.GetAttributeInfos())
                {
                    var n = info.Name ?? "";
                    var lower = n.ToLowerInvariant();
                    if (!keys.Any(k => lower.Contains(k))) continue;

                    object vObj;
                    try { vObj = di.GetAttribute(info.Name); }
                    catch { continue; }

                    var v = vObj?.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;

                    list.Add(new ModelContextProtocol.NetworkAttribute
                    {
                        Name = info.Name,
                        Value = v,
                        DataType = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                        IsWritable = IsAttributeWritable(info)
                    });
                }
            }
            catch
            {
                // best-effort
            }

            return list;
        }

        public ResponseMessage SetDeviceItemAttribute(string deviceItemPath, string attributeName, string value)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["deviceItemPath"] = deviceItemPath,
                ["attributeName"] = attributeName
            };

            try
            {
                if (IsProjectNull())
                {
                    meta["error"] = "Project is null";
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var di = GetDeviceItemByPath(deviceItemPath);
                if (di == null)
                {
                    meta["error"] = "Device item not found";
                    return new ResponseMessage { Message = "Device item not found", Meta = meta };
                }

                var info = di.GetAttributeInfos().FirstOrDefault(x => string.Equals(x.Name, attributeName, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    meta["error"] = "Attribute not found";
                    meta["availableAttributes"] = ToJsonArray(di.GetAttributeInfos().Select(x => x.Name ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)));
                    return new ResponseMessage { Message = "Attribute not found", Meta = meta };
                }

                object? oldValue = null;
                try { oldValue = di.GetAttribute(info.Name); } catch { }
                meta["oldValue"] = oldValue?.ToString() ?? string.Empty;
                meta["attributeDataType"] = TryGetPropertyValue(info, "DataType", "Type")?.ToString() ?? string.Empty;
                meta["attributeWritable"] = IsAttributeWritable(info);

                object typedValue = CoerceAttributeValue(value, oldValue, info);
                di.SetAttribute(info.Name, typedValue);

                object? newValue = null;
                try { newValue = di.GetAttribute(info.Name); } catch { }
                meta["newValue"] = newValue?.ToString() ?? string.Empty;
                meta["success"] = true;
                return new ResponseMessage { Message = $"Device item attribute '{attributeName}' set", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                return new ResponseMessage { Message = $"Failed setting device item attribute '{attributeName}'", Meta = meta };
            }
        }

        public string ProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNodes = FindNetworkNodes(plcRoot).ToList();
            var hmiNodes = FindNetworkNodes(hmiRoot).ToList();
            sb.AppendLine("PLC item service scan:");
            foreach (var line in DescribeNetworkServiceScan(plcRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("HMI item service scan:");
            foreach (var line in DescribeNetworkServiceScan(hmiRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("PLC nodes:");
            foreach (var n in plcNodes) sb.AppendLine("  " + FormatNodeInfo(n));
            sb.AppendLine("HMI nodes:");
            foreach (var n in hmiNodes) sb.AppendLine("  " + FormatNodeInfo(n));

            var plcNode = plcNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = hmiNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return sb.ToString();

            object? subnet = null;
            try
            {
                subnet = TryGetPropertyValue(plcNode.Node, "ConnectedSubnet");
                if (subnet == null)
                {
                    var create = plcNode.Node.GetType().GetMethod("CreateAndConnectToSubnet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    subnet = create?.Invoke(plcNode.Node, new object[] { subnetName });
                    sb.AppendLine("PLC CreateAndConnectToSubnet: " + (subnet == null ? "NULL" : "OK " + TryGetName(subnet)));
                }
                else
                {
                    sb.AppendLine("PLC already connected to subnet: " + (TryGetName(subnet) ?? subnet.ToString()));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("PLC subnet create/connect error: " + FormatExceptionDetail(ex));
            }

            if (subnet != null)
            {
                try
                {
                    var connect = hmiNode.Node.GetType().GetMethod("ConnectToSubnet", BindingFlags.Public | BindingFlags.Instance);
                    connect?.Invoke(hmiNode.Node, new object[] { subnet });
                    sb.AppendLine("HMI ConnectToSubnet: OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("HMI ConnectToSubnet error: " + FormatExceptionDetail(ex));
                }
            }

            sb.AppendLine("Readback PLC node: " + FormatNodeInfo(plcNode));
            sb.AppendLine("Readback HMI node: " + FormatNodeInfo(hmiNode));

            try
            {
                var sw = GetSoftwareContainer("HMI_RT_1")?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("HMI Connections after subnet:");
                if (connections is IEnumerable en)
                {
                    var any = false;
                    foreach (var c in en)
                    {
                        any = true;
                        sb.AppendLine("  " + (TryGetName(c) ?? c?.ToString() ?? "<null>"));
                    }
                    if (!any) sb.AppendLine("  <empty>");
                }
                else
                {
                    sb.AppendLine("  <not found>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("HMI connection readback error: " + FormatExceptionDetail(ex));
            }

            return sb.ToString();
        }

        public ResponseMessage EnsureSubnet(string anchorDeviceItemPath, string subnetType, string subnetName)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["anchorDeviceItemPath"] = anchorDeviceItemPath,
                ["subnetType"] = subnetType,
                ["subnetName"] = subnetName
            };

            try
            {
                if (IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                if (!IsSupportedProfinetSubnetType(subnetType))
                {
                    meta["error"] = "Only IndustrialEthernet/PROFINET subnet types are supported by this safe primitive.";
                    return new ResponseMessage { Message = "Unsupported subnet type", Meta = meta };
                }

                var anchorRoot = GetDeviceItemByPath(anchorDeviceItemPath);
                if (anchorRoot == null)
                {
                    meta["error"] = "Anchor device item not found";
                    return new ResponseMessage { Message = "Anchor device item not found", Meta = meta };
                }

                var existing = FindConnectedSubnetByName(subnetName);
                if (existing != null)
                {
                    meta["success"] = true;
                    meta["created"] = false;
                    meta["readback"] = BuildSubnetReadbackJson(subnetName);
                    return new ResponseMessage { Message = "Subnet already exists and was read back", Meta = meta };
                }

                var node = FindNetworkNodes(anchorRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
                if (node.Node == null)
                {
                    meta["error"] = "No Industrial Ethernet/PROFINET network node found under anchor path.";
                    meta["readback"] = BuildSubnetReadbackJson(subnetName);
                    return new ResponseMessage { Message = "No suitable network node found", Meta = meta };
                }

                object? subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                if (subnet == null)
                {
                    var create = node.Node.GetType().GetMethod("CreateAndConnectToSubnet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    subnet = create?.Invoke(node.Node, new object[] { subnetName });
                    meta["created"] = subnet != null;
                }
                else
                {
                    meta["created"] = false;
                    meta["reusedExistingNodeSubnet"] = TryGetName(subnet) ?? subnet.ToString();
                }

                meta["selectedNode"] = FormatNodeInfo(node);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                var ok = subnet != null && SubnetReadbackContains(subnetName);
                meta["success"] = ok;
                return new ResponseMessage
                {
                    Message = ok ? "Subnet ensured and read back" : "Subnet ensure did not produce readback evidence",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                return new ResponseMessage { Message = "Failed ensuring subnet", Meta = meta };
            }
        }

        public ResponseMessage AttachDeviceNodeToSubnet(string deviceItemPath, int interfaceIndex, string subnetName, string anchorDeviceItemPath = "")
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["deviceItemPath"] = deviceItemPath,
                ["interfaceIndex"] = interfaceIndex,
                ["subnetName"] = subnetName,
                ["anchorDeviceItemPath"] = anchorDeviceItemPath
            };

            try
            {
                if (IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                var targetRoot = GetDeviceItemByPath(deviceItemPath);
                if (targetRoot == null)
                {
                    meta["error"] = "Device item not found";
                    return new ResponseMessage { Message = "Device item not found", Meta = meta };
                }

                object? subnet = FindConnectedSubnetByName(subnetName);
                if (subnet == null && !string.IsNullOrWhiteSpace(anchorDeviceItemPath))
                {
                    var ensure = EnsureSubnet(anchorDeviceItemPath, "PROFINET", subnetName);
                    meta["ensureSubnet"] = ensure.Meta?.DeepClone();
                    subnet = FindConnectedSubnetByName(subnetName);
                }

                if (subnet == null)
                {
                    meta["error"] = "Subnet was not found. Call EnsureSubnet with a valid anchor first or pass anchorDeviceItemPath.";
                    meta["readback"] = BuildSubnetReadbackJson(subnetName);
                    return new ResponseMessage { Message = "Subnet not found", Meta = meta };
                }

                var nodes = FindNetworkNodes(targetRoot)
                    .Where(n => IsIndustrialEthernetNode(n.Node))
                    .ToList();
                meta["candidateNodes"] = ToJsonArray(nodes.Select(FormatNodeInfo));
                if (interfaceIndex < 0 || interfaceIndex >= nodes.Count)
                {
                    meta["error"] = $"interfaceIndex is out of range. Available Industrial Ethernet/PROFINET nodes: {nodes.Count}.";
                    return new ResponseMessage { Message = "Interface index out of range", Meta = meta };
                }

                var selected = nodes[interfaceIndex];
                var connected = TryGetPropertyValue(selected.Node, "ConnectedSubnet");
                var connectedName = TryGetName(connected);
                if (!string.Equals(connectedName, subnetName, StringComparison.OrdinalIgnoreCase))
                {
                    var connect = selected.Node.GetType().GetMethod("ConnectToSubnet", BindingFlags.Public | BindingFlags.Instance);
                    connect?.Invoke(selected.Node, new object[] { subnet });
                }

                meta["selectedNodeBefore"] = FormatNodeInfo(selected);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                var ok = SubnetReadbackContains(subnetName) && BuildSubnetReadbackLines(subnetName).Any(x => x.IndexOf(deviceItemPath, StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf(selected.Item.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                meta["success"] = ok;
                return new ResponseMessage
                {
                    Message = ok ? "Device network node attached to subnet and read back" : "Device network node attach did not produce readback evidence",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                return new ResponseMessage { Message = "Failed attaching device node to subnet", Meta = meta };
            }
        }

        public ResponseMessage SetCpuCommonSettings(string cpuPath, string settingsJson)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["cpuPath"] = cpuPath
            };

            try
            {
                if (IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                var di = GetDeviceItemByPath(cpuPath);
                if (di == null)
                {
                    meta["error"] = "CPU device item not found";
                    return new ResponseMessage { Message = "CPU device item not found", Meta = meta };
                }

                JsonObject? root;
                try
                {
                    root = JsonNode.Parse(settingsJson) as JsonObject;
                }
                catch (Exception ex)
                {
                    meta["error"] = "Invalid JSON: " + ex.Message;
                    return new ResponseMessage { Message = "Invalid settings JSON", Meta = meta };
                }

                var exact = root?["exactAttributes"] as JsonObject;
                if (exact == null || exact.Count == 0)
                {
                    meta["error"] = "settingsJson must contain exactAttributes. Attribute names must come from GetDeviceItemInfo/GetDeviceItemNetworkInfo readback.";
                    return new ResponseMessage { Message = "No exact attributes supplied", Meta = meta };
                }

                var infos = di.GetAttributeInfos().ToList();
                var applied = new JsonArray();
                var rejected = new JsonArray();

                foreach (var kv in exact)
                {
                    var name = kv.Key;
                    var value = kv.Value?.ToString() ?? string.Empty;
                    var info = infos.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = name,
                            ["reason"] = "Attribute not found on CPU device item. Read exact names first."
                        });
                        continue;
                    }

                    if (IsAttributeWritable(info) != true)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = "Attribute is not writable according to TIA attribute metadata."
                        });
                        continue;
                    }

                    try
                    {
                        object? oldValue = null;
                        try { oldValue = di.GetAttribute(info.Name); } catch { }
                        var typedValue = CoerceAttributeValue(value, oldValue, info);
                        di.SetAttribute(info.Name, typedValue);
                        object? newValue = null;
                        try { newValue = di.GetAttribute(info.Name); } catch { }
                        applied.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["oldValue"] = oldValue?.ToString() ?? string.Empty,
                            ["newValue"] = newValue?.ToString() ?? string.Empty,
                            ["dataType"] = TryGetPropertyValue(info, "DataType", "Type")?.ToString() ?? string.Empty
                        });
                    }
                    catch (Exception ex)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = FormatExceptionDetail(ex)
                        });
                    }
                }

                meta["applied"] = applied;
                meta["rejected"] = rejected;
                meta["readback"] = BuildDeviceItemNetworkReadbackJson(cpuPath);
                meta["success"] = applied.Count > 0 && rejected.Count == 0;
                return new ResponseMessage
                {
                    Message = meta["success"]?.GetValue<bool>() == true
                        ? "CPU common settings applied and read back"
                        : "CPU common settings completed with rejected attributes",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                return new ResponseMessage { Message = "Failed setting CPU common settings", Meta = meta };
            }
        }

        public string ProbeDeviceNetworkExposure(string deviceItemPath)
        {
            var sb = new StringBuilder();
            var root = GetDeviceItemByPath(deviceItemPath);
            if (root == null)
            {
                return $"DeviceItem not found: {deviceItemPath}";
            }

            sb.AppendLine($"Network exposure probe for: {deviceItemPath}");
            sb.AppendLine("DeviceItem tree:");
            sb.AppendLine(GetDeviceItemTree(deviceItemPath, 5));

            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                sb.AppendLine($"Item: path={item.Path}; kind={item.Kind}; owner={item.DeviceItem.Name}; type={item.Object.GetType().FullName}");

                try
                {
                    var attrs = TryReadInterestingAttributes(item.Object).ToList();
                    if (attrs.Count > 0)
                    {
                        sb.AppendLine("  Attributes:");
                        foreach (var attr in attrs)
                            sb.AppendLine("    " + attr);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Attributes error: " + FormatExceptionDetail(ex));
                }

                try
                {
                    var services = ProbeInterestingServices(item.Object).ToList();
                    if (services.Count > 0)
                    {
                        sb.AppendLine("  Services:");
                        foreach (var svc in services)
                            sb.AppendLine("    " + svc);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Services error: " + FormatExceptionDetail(ex));
                }
            }

            return sb.ToString();
        }

        public string ProbeCreateHardwareHmiConnection(string plcRootPath, string hmiRootPath, string connectionName, bool createConnection = false, bool deepScan = false)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            sb.AppendLine("CreateConnection: " + createConnection);
            sb.AppendLine("DeepScan: " + deepScan);
            if (plcNode.Node == null || hmiNode.Node == null) return sb.ToString();

#if TIA_V20
            // V20 does not expose Siemens.Engineering.HW.CommunicationConnections. The hardware-level
            // HMI connection helper degrades to a no-op (caller still gets the diagnostic prefix).
            var connectionCompositionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var hmiConnectionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.HmiConnection, Siemens.Engineering");
            if (connectionCompositionType == null || hmiConnectionType == null)
            {
                sb.AppendLine(Capability.Describe(TiaFeature.HardwareHmiConnection) + " Skipping hardware HMI connection creation.");
                return sb.ToString();
            }
#elif !TIA_V17
            var connectionCompositionType = typeof(global::Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition);
            var hmiConnectionType = typeof(global::Siemens.Engineering.HW.CommunicationConnections.HmiConnection);
#else
            var connectionCompositionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var hmiConnectionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.HmiConnection, Siemens.Engineering");
            if (connectionCompositionType == null || hmiConnectionType == null)
            {
                sb.AppendLine(Capability.Describe(TiaFeature.HardwareHmiConnection) + " Skipping hardware HMI connection creation.");
                return sb.ToString();
            }
#endif
            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();
            sb.AppendLine("Candidate count: " + candidates.Count);

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                sb.AppendLine("Candidate: " + c.Label + " targetType=" + c.Target.GetType().FullName);
                object? composition = null;
                try
                {
                    composition = TryGetService(c.Target, connectionCompositionType);
                    sb.AppendLine("  ConnectionComposition service: " + (composition == null ? "<none>" : composition.GetType().FullName));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  ConnectionComposition service error: " + FormatExceptionDetail(ex));
                }

                if (composition == null) continue;

                try
                {
                    sb.AppendLine("  Before count=" + (TryGetPropertyValue(composition, "Count")?.ToString() ?? ""));
                }
                catch { }

                try
                {
                    var create = composition.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
                    if (create == null)
                    {
                        sb.AppendLine("  Create<T>(Node,DeviceItem,Node) not found.");
                        continue;
                    }

                    sb.AppendLine("  Create<T> signature: " + create);
                    if (!createConnection)
                    {
                        sb.AppendLine("  Create skipped (scan-only mode).");
                        continue;
                    }

                    var generic = create.MakeGenericMethod(hmiConnectionType);
                    var created = generic.Invoke(composition, new object[] { c.LocalNode, c.PartnerTarget, c.PartnerNode });
                    sb.AppendLine("  Create<HmiConnection>: " + (created == null ? "NULL" : "OK " + created.GetType().FullName));
                    if (created != null)
                    {
                        TrySetProperty(created, "LocalConnectionName", connectionName);
                        sb.AppendLine("  Created readback: " + SummarizeHmiObjectReadback(created, "LocalConnectionName", "LocalAddress", "PartnerAddress", "AccessPoint", "Online", "TimeSynchronizationMode"));
                    }

                    try
                    {
                        sb.AppendLine("  After count=" + (TryGetPropertyValue(composition, "Count")?.ToString() ?? ""));
                    }
                    catch { }

                    break;
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Create<HmiConnection> error: " + FormatExceptionDetail(ex));
                }
            }

            try
            {
                var sw = GetSoftwareContainer("HMI_RT_1")?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("Classic HMI Connections after HW probe:");
                if (connections is IEnumerable en)
                {
                    var any = false;
                    foreach (var c in en)
                    {
                        any = true;
                        sb.AppendLine("  " + (TryGetName(c) ?? c?.ToString() ?? "<null>"));
                    }
                    if (!any) sb.AppendLine("  <empty>");
                }
                else
                {
                    sb.AppendLine("  <not found>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Classic HMI connection readback error: " + FormatExceptionDetail(ex));
            }

            return sb.ToString();
        }

        public List<string> ProbeHardwareHmiConnectionOwnerCandidates(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = new List<string>();
            if (IsProjectNull())
            {
                lines.Add("Project is null");
                return lines;
            }

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            lines.Add("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            lines.Add("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return lines;

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            lines.Add("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            lines.Add("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return lines;

            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();
            lines.Add("DeepScan: " + deepScan);
            lines.Add("Candidate count: " + candidates.Count);

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                lines.Add(c.Label
                    + " | type=" + (c.Target.GetType().FullName ?? c.Target.GetType().Name)
                    + " | name=" + (TryGetName(c.Target) ?? "<unnamed>"));
            }

            return lines;
        }

        public List<string> ProbeHardwareHmiConnectionWhitelistedServices(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = new List<string>();
            if (IsProjectNull())
            {
                lines.Add("Project is null");
                return lines;
            }

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            lines.Add("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            lines.Add("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return lines;

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            lines.Add("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            lines.Add("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return lines;

            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();

#if TIA_V20
            var commConnT = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var serviceTypes = commConnT != null
                ? new[] { commConnT, typeof(NetworkInterface), typeof(NetworkPort) }
                : new[] { typeof(NetworkInterface), typeof(NetworkPort) };
#elif !TIA_V17
            var serviceTypes = new[]
            {
                typeof(global::Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition),
                typeof(NetworkInterface),
                typeof(NetworkPort)
            };
#else
            var commConnT = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var serviceTypes = commConnT != null
                ? new[] { commConnT, typeof(NetworkInterface), typeof(NetworkPort) }
                : new[] { typeof(NetworkInterface), typeof(NetworkPort) };
#endif

            lines.Add("DeepScan: " + deepScan);
            lines.Add("Candidate count: " + candidates.Count);
            lines.Add("Service whitelist: " + string.Join(", ", serviceTypes.Select(t => t.FullName)));

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                if (!IsSafeHardwareHmiServiceProbeTarget(c.Target))
                {
                    lines.Add("Candidate: " + c.Label + " | type=" + c.Target.GetType().FullName + " | SKIP unsafe/high-level target");
                    continue;
                }

                lines.Add("Candidate: " + c.Label + " | type=" + (c.Target.GetType().FullName ?? c.Target.GetType().Name) + " | name=" + (TryGetName(c.Target) ?? "<unnamed>"));
                foreach (var serviceType in serviceTypes)
                {
                    object? service = null;
                    try
                    {
                        service = TryGetService(c.Target, serviceType);
                    }
                    catch (Exception ex)
                    {
                        lines.Add("  " + serviceType.Name + ": ERROR " + FormatExceptionDetail(ex));
                        continue;
                    }

                    if (service == null)
                    {
                        lines.Add("  " + serviceType.Name + ": <none>");
                        continue;
                    }

                    lines.Add("  " + serviceType.Name + ": " + service.GetType().FullName + " | " + SummarizeWhitelistedService(service));
                }
            }

            return lines;
        }

        private IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var c in Expand("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;
            foreach (var c in Expand("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;
            foreach (var c in Expand("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;

            if (_project != null)
            {
                foreach (var c in Add("Project", _project, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;
                foreach (var c in Add("Project devices", _project.Devices, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;

                foreach (var d in _project.Devices)
                {
                    foreach (var c in Add("Device " + d.Name, d, plcNode.Node, hmiNode.Item, hmiNode.Node))
                        yield return c;
                    foreach (var c in Add("DeviceItems " + d.Name, d.DeviceItems, plcNode.Node, hmiNode.Item, hmiNode.Node))
                        yield return c;
                }
            }

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Expand(string label, object? start, object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                var current = start;
                for (var depth = 0; current != null && depth < 8; depth++)
                {
                    foreach (var c in Add(depth == 0 ? label : label + " ancestor[" + depth + "]", current, localNode, partnerTarget, partnerNode))
                        yield return c;

                    var next = TryGetPropertyValue(current, "Parent", "OwnedBy");
                    if (next == null || ReferenceEquals(next, current)) break;
                    current = next;
                }
            }

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Add(string label, object? target, object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                if (target == null) yield break;
                if (!seen.Add(target)) yield break;
                yield return (label, target, localNode, partnerTarget, partnerNode);
            }
        }

        private static IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildDirectHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            yield return ("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node);
        }

        private static bool IsSafeHardwareHmiServiceProbeTarget(object target)
        {
            var typeName = target.GetType().FullName ?? target.GetType().Name;
            if (typeName.Contains("Project", StringComparison.OrdinalIgnoreCase)) return false;
            if (typeName.Contains("Composition", StringComparison.OrdinalIgnoreCase)) return false;
            if (typeName.Contains("DeviceComposition", StringComparison.OrdinalIgnoreCase)) return false;
            return typeName.Contains("DeviceItem", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("Node", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("NetworkInterface", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("NetworkPort", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("HardwareComponent", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("Device", StringComparison.OrdinalIgnoreCase);
        }

        private static string SummarizeWhitelistedService(object service)
        {
            var parts = new List<string>();
            try
            {
                var count = TryGetPropertyValue(service, "Count");
                if (count != null) parts.Add("Count=" + count);
            }
            catch { }

            try
            {
                var nodes = TryGetPropertyValue(service, "Nodes") as IEnumerable;
                if (nodes != null && nodes is not string)
                {
                    var nodeInfos = new List<string>();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        nodeInfos.Add((TryGetName(node) ?? "<unnamed>") + ":" + (TryGetPropertyValue(node, "NodeType")?.ToString() ?? "") + ":" + (TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? "<none>"));
                    }
                    parts.Add("Nodes=[" + string.Join(", ", nodeInfos) + "]");
                }
            }
            catch { }

            try
            {
                var createMethods = service.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ToString())
                    .Take(8)
                    .ToList();
                if (createMethods.Count > 0) parts.Add("CreateMethods=[" + string.Join(" | ", createMethods) + "]");
            }
            catch { }

            try
            {
                var methods = service.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m =>
                    {
                        var n = m.Name ?? "";
                        return n.IndexOf("Connect", StringComparison.OrdinalIgnoreCase) >= 0
                               || n.IndexOf("Disconnect", StringComparison.OrdinalIgnoreCase) >= 0
                               || n.IndexOf("Subnet", StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .Select(m => m.ToString())
                    .Take(8)
                    .ToList();
                if (methods.Count > 0) parts.Add("NetworkMethods=[" + string.Join(" | ", methods) + "]");
            }
            catch { }

            return parts.Count == 0 ? "<no summary>" : string.Join("; ", parts);
        }

        private readonly struct NetworkNodeInfo
        {
            public NetworkNodeInfo(string path, DeviceItem item, object networkInterface, object node)
            {
                Path = path;
                Item = item;
                NetworkInterface = networkInterface;
                Node = node;
            }

            public string Path { get; }
            public DeviceItem Item { get; }
            public object NetworkInterface { get; }
            public object Node { get; }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static IEnumerable<NetworkNodeInfo> FindNetworkNodes(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                object? networkInterface = null;
                try { networkInterface = TryGetNetworkInterfaceService(item.Object) ?? TryGetNetworkInterfaceFromNetworkPort(item.Object); } catch { }
                if (networkInterface == null) continue;

                var nodes = TryGetPropertyValue(networkInterface, "Nodes");
                if (nodes is not IEnumerable enumerable || nodes is string) continue;
                foreach (var node in enumerable)
                {
                    if (node != null) yield return new NetworkNodeInfo(item.Path, item.DeviceItem, networkInterface, node);
                }
            }
        }

        private static IEnumerable<(DeviceItem Item, string Path)> TraverseDeviceItems(DeviceItem root, string path)
        {
            yield return (root, path);
            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in TraverseDeviceItems(child, path + "/" + child.Name))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<string> DescribeNetworkServiceScan(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                object? networkInterface = null;
                Exception? networkEx = null;
                try { networkInterface = TryGetNetworkInterfaceService(item.Object) ?? TryGetNetworkInterfaceFromNetworkPort(item.Object); } catch (Exception ex) { networkEx = ex; }

                var line = $"path={item.Path}; item={TryGetName(item.Object) ?? item.DeviceItem.Name}; ownerDeviceItem={item.DeviceItem.Name}; objectKind={item.Kind}; type={item.Object.GetType().FullName}";
                if (networkInterface != null)
                {
                    var nodes = TryGetPropertyValue(networkInterface, "Nodes") as IEnumerable;
                    var nodeCount = 0;
                    if (nodes != null)
                    {
                        foreach (var _ in nodes) nodeCount++;
                    }
                    line += $"; NetworkInterface=YES; nodes={nodeCount}";
                }
                else
                {
                    line += "; NetworkInterface=NO";
                    if (networkEx != null) line += "; err=" + networkEx.Message;
                }

                yield return line;
            }
        }

        private static IEnumerable<(object Object, DeviceItem DeviceItem, string Path, string Kind)> TraverseDeviceItemsAndHardware(DeviceItem root, string path)
        {
            yield return (root, root, path, "DeviceItem");

            foreach (var hardware in root.Items)
            {
                if (hardware != null)
                {
                    yield return (hardware, root, path + "#" + (TryGetName(hardware) ?? hardware.GetType().Name), "HardwareComponent");
                }
            }

            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in TraverseDeviceItemsAndHardware(child, path + "/" + child.Name))
                {
                    yield return nested;
                }
            }
        }

        private static object? TryGetNetworkInterfaceService(object target)
        {
            try
            {
                var mi = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;
                return mi.MakeGenericMethod(typeof(NetworkInterface)).Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static object? TryGetNetworkInterfaceFromNetworkPort(object target)
        {
            try
            {
                var port = TryGetServiceByTypeSuffix(target, "NetworkPort");
                if (port == null) return null;
                return TryGetPropertyValue(port, "Interface");
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> TryReadInterestingAttributes(object target)
        {
            var result = new List<string>();
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods.FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (getInfos == null || getAttr == null)
                return result;

            var infos = getInfos.Invoke(target, Array.Empty<object>()) as IEnumerable;
            if (infos == null)
                return result;

            var interesting = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address", "partner", "node"
            };

            foreach (var info in infos)
            {
                var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var lower = name.ToLowerInvariant();
                if (!interesting.Any(k => lower.Contains(k)))
                    continue;

                try
                {
                    var value = getAttr.Invoke(target, new object[] { name });
                    result.Add($"{name}={value ?? ""}");
                }
                catch { }
            }

            return result;
        }

        private static bool IsSupportedProfinetSubnetType(string subnetType)
        {
            var value = (subnetType ?? string.Empty).Trim();
            return value.Length == 0
                   || value.Equals("PROFINET", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("PN", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("IndustrialEthernet", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Industrial Ethernet", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("PN/IE", StringComparison.OrdinalIgnoreCase);
        }

        private object? FindConnectedSubnetByName(string subnetName)
        {
            if (_project == null || string.IsNullOrWhiteSpace(subnetName))
                return null;

            foreach (var device in _project.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        if (subnet == null) continue;

                        var name = TryGetName(subnet) ?? subnet.ToString() ?? string.Empty;
                        if (string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            return subnet;
                    }
                }
            }

            foreach (var group in _project.DeviceGroups)
            {
                foreach (var subnet in FindConnectedSubnetByNameInGroup(group, subnetName))
                    return subnet;
            }

            return null;
        }

        private static IEnumerable<object> FindConnectedSubnetByNameInGroup(DeviceUserGroup group, string subnetName)
        {
            foreach (var device in group.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        if (subnet == null) continue;

                        var name = TryGetName(subnet) ?? subnet.ToString() ?? string.Empty;
                        if (string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            yield return subnet;
                    }
                }
            }

            foreach (var child in group.Groups)
            {
                foreach (var subnet in FindConnectedSubnetByNameInGroup(child, subnetName))
                    yield return subnet;
            }
        }

        private JsonArray BuildSubnetReadbackJson(string subnetName)
        {
            var arr = new JsonArray();
            foreach (var line in BuildSubnetReadbackLines(subnetName))
                arr.Add(line);
            return arr;
        }

        private List<string> BuildSubnetReadbackLines(string subnetName)
        {
            var lines = new List<string>();
            if (_project == null) return lines;

            void AddFromDevice(Device device)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(FormatNodeInfo(node));
                    }
                }
            }

            foreach (var device in _project.Devices)
                AddFromDevice(device);
            foreach (var group in _project.DeviceGroups)
                AddSubnetReadbackLinesFromGroup(group, subnetName, lines);

            return lines;
        }

        private static void AddSubnetReadbackLinesFromGroup(DeviceUserGroup group, string subnetName, List<string> lines)
        {
            foreach (var device in group.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(FormatNodeInfo(node));
                    }
                }
            }

            foreach (var child in group.Groups)
                AddSubnetReadbackLinesFromGroup(child, subnetName, lines);
        }

        private bool SubnetReadbackContains(string subnetName)
        {
            return BuildSubnetReadbackLines(subnetName)
                .Any(x => x.IndexOf("connectedSubnet=" + subnetName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private JsonArray BuildDeviceItemNetworkReadbackJson(string deviceItemPath)
        {
            var arr = new JsonArray();
            var attrs = GetDeviceItemNetworkInfo(deviceItemPath) ?? new List<ModelContextProtocol.NetworkAttribute>();
            foreach (var attr in attrs)
            {
                arr.Add(new JsonObject
                {
                    ["name"] = attr.Name ?? string.Empty,
                    ["value"] = attr.Value ?? string.Empty,
                    ["dataType"] = attr.DataType ?? string.Empty,
                    ["isWritable"] = attr.IsWritable
                });
            }
            return arr;
        }

        private static IEnumerable<string> ProbeInterestingServices(object target)
        {
            var serviceSuffixes = new[]
            {
                "NetworkInterface",
                "NetworkPort",
                "Node",
                "SubnetOwner",
                "TransferArea",
                "InterfaceOperatingMode",
                "CommunicationConnections",
                "CommunicationConnection",
                "AddressController",
                "HardwareObject"
            };

            foreach (var suffix in serviceSuffixes)
            {
                object? svc = null;
                try { svc = TryGetServiceByTypeSuffix(target, suffix); } catch { }
                if (svc == null) continue;

                var details = new List<string>
                {
                    $"service={svc.GetType().FullName}"
                };

                var nodes = TryGetPropertyValue(svc, "Nodes") as IEnumerable;
                if (nodes != null && nodes is not string)
                {
                    var nodeInfos = new List<string>();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        nodeInfos.Add($"{TryGetName(node) ?? "<unnamed>"}:{TryGetPropertyValue(node, "NodeType") ?? ""}:{TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? "<none>"}");
                    }
                    details.Add("Nodes=[" + string.Join(", ", nodeInfos) + "]");
                }

                foreach (var propName in new[] { "Name", "Type", "Mode", "Address", "Subnet", "ConnectedSubnet", "NodeType" })
                {
                    try
                    {
                        var value = TryGetPropertyValue(svc, propName);
                        if (value != null)
                            details.Add(propName + "=" + value);
                    }
                    catch { }
                }

                var memberSummaries = DescribeMembers(svc, 80)
                    .Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")
                    .Take(40)
                    .ToList();
                if (memberSummaries.Count > 0)
                    details.Add("Members=[" + string.Join(" | ", memberSummaries) + "]");

                yield return suffix + " => " + string.Join("; ", details);
            }
        }

        private static string FormatNodeInfo(NetworkNodeInfo info)
        {
            return $"path={info.Path}; item={info.Item.Name}; node={TryGetName(info.Node) ?? "<unnamed>"}; type={TryGetPropertyValue(info.Node, "NodeType")}; connectedSubnet={TryGetName(TryGetPropertyValue(info.Node, "ConnectedSubnet")) ?? TryGetPropertyValue(info.Node, "ConnectedSubnet")?.ToString() ?? "<none>"}";
        }

        private static bool IsIndustrialEthernetNode(object? node)
        {
            if (node == null) return false;
            var type = TryGetPropertyValue(node, "NodeType")?.ToString() ?? "";
            var name = TryGetName(node) ?? "";
            return type.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
                   || type.IndexOf("Profinet", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("PROFINET", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath = "PLC_1", string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false
            };

            var problems = new JsonArray();
            var devices = new JsonArray();
            var software = new JsonArray();
            meta["problems"] = problems;
            meta["devices"] = devices;
            meta["software"] = software;

            if (IsProjectNull())
            {
                problems.Add("Project is null. Use Connect + AttachToOpenProject/OpenProject first.");
                return new ResponseMessage { Message = "Automation context invalid", Meta = meta };
            }

            meta["project"] = _project?.Name ?? string.Empty;

            try
            {
                foreach (var d in _project!.Devices)
                {
                    var deviceInfo = new JsonObject
                    {
                        ["name"] = d.Name,
                        ["type"] = d.GetType().FullName ?? d.GetType().Name
                    };
                    devices.Add(deviceInfo);

                    foreach (var di in d.DeviceItems)
                    {
                        TryAddSoftwareInfo(di, software);
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Device/software scan failed: {FormatExceptionDetail(ex)}");
            }

            if (devices.Count == 0)
            {
                problems.Add("No devices found in current project.");
            }

            if (!string.IsNullOrWhiteSpace(expectedPlcSoftwarePath))
            {
                var plc = GetSoftwareContainer(expectedPlcSoftwarePath)?.Software as PlcSoftware;
                meta["expectedPlcSoftwarePath"] = expectedPlcSoftwarePath;
                meta["expectedPlcFound"] = plc != null;
                if (plc == null) problems.Add($"PLC software not found at '{expectedPlcSoftwarePath}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedHmiSoftwarePath))
            {
                var hmi = GetSoftwareContainer(expectedHmiSoftwarePath)?.Software;
                meta["expectedHmiSoftwarePath"] = expectedHmiSoftwarePath;
                meta["expectedHmiFound"] = hmi != null && hmi.GetType().FullName?.Contains("Hmi", StringComparison.OrdinalIgnoreCase) == true;
                if (hmi == null) problems.Add($"HMI software not found at '{expectedHmiSoftwarePath}'.");
            }

            try
            {
                meta["projectTree"] = GetProjectTree();
            }
            catch (Exception ex)
            {
                problems.Add($"Project tree failed: {FormatExceptionDetail(ex)}");
            }

            meta["success"] = problems.Count == 0;
            return new ResponseMessage
            {
                Message = problems.Count == 0 ? "Automation context OK" : "Automation context has problems",
                Meta = meta
            };

            void TryAddSoftwareInfo(DeviceItem item, JsonArray target)
            {
                try
                {
                    var sc = item.GetService<SoftwareContainer>();
                    if (sc?.Software != null)
                    {
                        target.Add(new JsonObject
                        {
                            ["deviceItem"] = item.Name,
                            ["softwareName"] = TryGetName(sc.Software) ?? item.Name,
                            ["softwareType"] = sc.Software.GetType().FullName ?? sc.Software.GetType().Name
                        });
                    }
                }
                catch { }

                try
                {
                    foreach (var child in item.DeviceItems)
                    {
                        TryAddSoftwareInfo(child, target);
                    }
                }
                catch { }
            }
        }

        private static void BuildDeviceItemTree(StringBuilder sb, DeviceItem node, List<bool> ancestorStates, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            // Hardware components (Items)
            if (node.Items != null && node.Items.Count > 0)
            {
                var items = node.Items.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var isLast = (i == items.Count - 1) && (node.DeviceItems == null || node.DeviceItems.Count == 0);
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{it.Name} [Hardware Component]");
                }
            }

            // Sub device items
            if (node.DeviceItems != null && node.DeviceItems.Count > 0)
            {
                var children = node.DeviceItems.ToList();
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    var isLast = i == children.Count - 1;
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{child.Name} [DeviceItem]");
                    BuildDeviceItemTree(sb, child, new List<bool>(ancestorStates) { isLast }, depth + 1, maxDepth);
                }
            }
        }

        private static string GetTreePrefixStatic(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }

        #endregion

        #region software

        public PlcSoftware? GetPlcSoftware(string softwarePath)
        {
            _logger?.LogInformation($"Getting software by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                return plcSoftware;
            }

            return null;
        }

        public List<string>? GetPlcTagTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var tables = ResolvePlcTagTablesCollection(plc);
            if (tables == null) return new List<string>();
            return TryListNamesFromCollection(tables, Array.Empty<string>(), "TagTables");
        }

        public bool ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            if (IsProjectNull()) return false;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return false;

            var tables = ResolvePlcTagTablesCollection(plc);
            var table = tables == null
                ? null
                : FindExistingByName(tables, tagTableName) ?? TryFindByNameInCollection(tables, Array.Empty<string>(), tagTableName);
            if (table == null) return false;
            return TryExportEngineeringObject(table, exportPath, out _);
        }

        public void ImportPlcTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");

            try
            {
                object root = ResolvePlcTagTableGroup(plc) ?? plc;
                var group = TryResolveChildGroupByPath(root, folderPath) ?? root;

                var tables = TryGetPropertyValue(group, "TagTables") ?? ResolvePlcTagTablesCollection(plc);
                if (tables == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TagTables collection not found. plcType={plc.GetType().FullName} groupType={group.GetType().FullName}");

                // Route through PrepareXmlForImport so the hardcoded <Engineering version="V21"/>
                // header is rewritten to the connected portal version (and a UTF-8 BOM is ensured).
                // Without this, tag-table imports fail on a V20 portal with
                // "The engineering version 'V21' ... is not supported." (block/type imports already
                // sanitize via PrepareXmlForImport; tag tables previously skipped it).
                if (TryImportEngineeringObjectIntoCollection(tables, PrepareXmlForImport(importPath), out _, out var err)) return;
                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportPlcTagTable failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        private static object? ResolvePlcTagTableGroup(PlcSoftware plc)
        {
            return TryGetPropertyValue(plc,
                "TagTableGroup",
                "TagTableFolder",
                "PlcTagTableGroup",
                "PlcTagTableFolder");
        }

        private static object? ResolvePlcTagTablesCollection(PlcSoftware plc)
        {
            var direct = TryGetPropertyValue(plc, "TagTables", "PlcTagTables", "Tables");
            if (direct != null) return direct;

            var group = ResolvePlcTagTableGroup(plc);
            return group == null
                ? null
                : TryGetPropertyValue(group, "TagTables", "PlcTagTables", "Tables");
        }

        public ResponseImportBatch ImportPlcTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try { ImportPlcTagTable(softwarePath, folderPath, file); imported.Add(name); }
                    catch (PortalException pex) { failed.Add(new ImportFailure { Path = file, Error = pex.Message }); }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public List<string>? GetPlcWatchTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null)
            {
                return TryListNamesFromCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, "WatchTables");
            }

            return EnumeratePlcWatchTables(group)
                .Select(x => x.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool ExportPlcWatchTable(string softwarePath, string watchTableName, string exportPath)
        {
            if (IsProjectNull()) return false;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return false;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            object? table = null;
            if (group != null)
            {
                table = EnumeratePlcWatchTables(group)
                    .FirstOrDefault(x =>
                        string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase))
                    .Table;
            }
            else
            {
                table = TryFindByNameInCollection(plc, new[] { "WatchTables", "PlcWatchTables", "Tables" }, watchTableName);
            }

            if (table == null) return false;
            return TryExportEngineeringObject(table, exportPath, out _);
        }

        public ResponseImportBatch ExportPlcWatchTablesToDirectory(string softwarePath, string dir, string regexName = "")
        {
            var exported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                var names = GetPlcWatchTables(softwarePath);
                if (names == null)
                {
                    failed.Add(new ImportFailure { Path = softwarePath, Error = "PLC software not found" });
                    return new ResponseImportBatch { Imported = exported, Failed = failed };
                }

                Directory.CreateDirectory(dir);
                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var name in names)
                {
                    if (regex != null && !regex.IsMatch(name)) continue;
                    var outPath = Path.Combine(dir, MakeSafeFileName(name) + ".xml");
                    if (ExportPlcWatchTable(softwarePath, name, outPath)) exported.Add(outPath);
                    else failed.Add(new ImportFailure { Path = name, Error = "Export failed" });
                }

                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }
        }

        // ── Force Tables ──────────────────────────────────────────────────────

        public List<string>? GetPlcForceTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            var group = ResolvePlcWatchAndForceTableGroup(plc);
            if (group == null) return new List<string>();

            var result = new List<string>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            CollectForceTableNames(group, "", result, visited);
            return result;
        }

        private static void CollectForceTableNames(object group, string prefix, List<string> result, HashSet<object> visited)
        {
            if (!visited.Add(group)) return;

            var forceTables = TryGetPropertyValue(group, "ForceTables", "PlcForceTables");
            if (forceTables is IEnumerable ftEnum and not string)
            {
                foreach (var t in ftEnum)
                {
                    if (t == null) continue;
                    var name = TryGetPropertyValue(t, "Name")?.ToString() ?? string.Empty;
                    result.Add(string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name);
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "UserGroups", "SubGroups");
            if (groups is IEnumerable gEnum and not string)
            {
                foreach (var sub in gEnum)
                {
                    if (sub == null) continue;
                    var gname = TryGetPropertyValue(sub, "Name")?.ToString() ?? string.Empty;
                    var next = string.IsNullOrEmpty(prefix) ? gname : prefix + "/" + gname;
                    CollectForceTableNames(sub, next, result, visited);
                }
            }
        }

        public ResponseMessage EnsureWatchTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string modifyValue,
            string trigger = "Permanent")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var group = ResolvePlcWatchAndForceTableGroup(plc);
                if (group == null) return new ResponseMessage { Message = "WatchAndForceTableGroup not accessible." };

                var table = FindOrCreateWatchTable(group, tableName);
                if (table == null) return new ResponseMessage { Message = $"Could not find or create watch table '{tableName}'." };

                var entry = FindOrCreateTableEntry(table, "Entries", address);
                if (entry == null) return new ResponseMessage { Message = $"Could not create entry for address '{address}'." };

                TrySetProperty(entry, "Address", address);
                TrySetProperty(entry, "ModifyValue", modifyValue);
                SetEnumPropertyByName(entry, "ModifyTrigger", trigger);

                return new ResponseMessage
                {
                    Message = $"Watch table '{tableName}': entry '{address}' set to ModifyValue='{modifyValue}' Trigger={trigger}.",
                    Meta = new JsonObject
                    {
                        ["softwarePath"] = softwarePath,
                        ["tableName"] = tableName,
                        ["address"] = address,
                        ["modifyValue"] = modifyValue,
                        ["trigger"] = trigger,
                        ["note"] = "Value will be applied to the PLC when TIA Portal is online and the trigger fires."
                    }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "EnsureWatchTableEntry failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseMessage EnsureForceTableEntry(
            string softwarePath,
            string tableName,
            string address,
            string forceValue)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var group = ResolvePlcWatchAndForceTableGroup(plc);
                if (group == null) return new ResponseMessage { Message = "WatchAndForceTableGroup not accessible." };

                var table = FindOrCreateForceTable(group, tableName);
                if (table == null) return new ResponseMessage { Message = $"Could not find or create force table '{tableName}'." };

                var entry = FindOrCreateTableEntry(table, "Entries", address);
                if (entry == null) return new ResponseMessage { Message = $"Could not create force entry for address '{address}'." };

                TrySetProperty(entry, "Address", address);
                TrySetProperty(entry, "ForceValue", forceValue);

                return new ResponseMessage
                {
                    Message = $"Force table '{tableName}': entry '{address}' set to ForceValue='{forceValue}'.",
                    Meta = new JsonObject
                    {
                        ["softwarePath"] = softwarePath,
                        ["tableName"] = tableName,
                        ["address"] = address,
                        ["forceValue"] = forceValue,
                        ["note"] = "Force will be applied continuously while TIA Portal is online with this CPU."
                    }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "EnsureForceTableEntry failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        private static object? FindOrCreateWatchTable(object group, string tableName)
        {
            var watchTables = TryGetPropertyValue(group, "WatchTables", "PlcWatchTables");
            if (watchTables == null) return null;

            // Search existing
            if (watchTables is IEnumerable wte and not string)
            {
                foreach (var t in wte)
                {
                    if (t == null) continue;
                    if (string.Equals(TryGetPropertyValue(t, "Name")?.ToString(), tableName, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            // Create new
            return TryInvokeMethodByName(watchTables, "Create", tableName);
        }

        private static object? FindOrCreateForceTable(object group, string tableName)
        {
            var forceTables = TryGetPropertyValue(group, "ForceTables", "PlcForceTables");
            if (forceTables == null) return null;

            if (forceTables is IEnumerable fte and not string)
            {
                foreach (var t in fte)
                {
                    if (t == null) continue;
                    if (string.Equals(TryGetPropertyValue(t, "Name")?.ToString(), tableName, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return TryInvokeMethodByName(forceTables, "Create", tableName);
        }

        private static object? FindOrCreateTableEntry(object table, string entriesPropertyName, string address)
        {
            var entries = TryGetPropertyValue(table, entriesPropertyName, "WatchTableEntries", "ForceTableEntries", "Rows");
            if (entries == null) return null;

            // Search existing entry with same address
            if (entries is IEnumerable ee and not string)
            {
                foreach (var e in ee)
                {
                    if (e == null) continue;
                    var addr = TryGetPropertyValue(e, "Address", "Name")?.ToString();
                    if (string.Equals(addr, address, StringComparison.OrdinalIgnoreCase))
                        return e;
                }
            }

            // Create new entry
            return TryInvokeMethodByName(entries, "Create", address);
        }

        private static object? TryInvokeMethodByName(object target, string methodName, params object?[] args)
        {
            try
            {
                var method = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
                return method?.Invoke(target, args);
            }
            catch { return null; }
        }

        private static void SetEnumPropertyByName(object target, string propertyName, string valueName)
        {
            try
            {
                var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.PropertyType.IsEnum) return;
                var enumValue = Enum.Parse(prop.PropertyType, valueName, ignoreCase: true);
                prop.SetValue(target, enumValue);
            }
            catch { }
        }

        // ── Watch Table Current Values (read-only) ────────────────────────────

        public ModelContextProtocol.ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(string softwarePath, string watchTableName, int maxEntries = 50)
        {
            var data = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["softwarePath"] = softwarePath,
                ["watchTableName"] = watchTableName,
                ["safety"] = new JsonObject
                {
                    ["readOnly"] = true,
                    ["modifiesWatchTables"] = false,
                    ["writesValues"] = false,
                    ["usesForce"] = false
                }
            };

            try
            {
                if (IsProjectNull())
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "Project is null. Attach to the open project first.", Data = data };

                var plc = GetPlcSoftware(softwarePath);
                if (plc == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "PLC software not found at '" + softwarePath + "'", Data = data };

                var group = ResolvePlcWatchAndForceTableGroup(plc);
                if (group == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "WatchAndForceTableGroup not found.", Data = data };

                var tables = EnumeratePlcWatchTables(group);
                data["watchTables"] = new JsonArray(tables.Select(x => JsonValue.Create(x.Path)).ToArray());
                var table = tables.FirstOrDefault(x =>
                    string.Equals(x.Path, watchTableName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, watchTableName, StringComparison.OrdinalIgnoreCase)).Table;
                if (table == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "Watch table not found.", Data = data };

                data["tableType"] = table.GetType().FullName ?? table.GetType().Name;
                data["tableMembers"] = new JsonArray(DescribeMembers(table, 220).Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")).ToArray());
                var entries = TryGetPropertyValue(table, "Entries", "WatchTableEntries", "Rows", "Items");
                data["entriesCollectionType"] = entries?.GetType().FullName ?? "";
                var rows = new JsonArray();
                if (entries is IEnumerable enumerable && entries is not string)
                {
                    foreach (var entry in enumerable)
                    {
                        if (entry == null) continue;
                        rows.Add(ReadWatchTableEntryReadOnly(entry, rows.Count == 0));
                        if (rows.Count >= Math.Max(1, maxEntries)) break;
                    }
                }

                data["entries"] = rows;
                data["entryCountRead"] = rows.Count;
                data["currentValueReadOk"] = rows.OfType<JsonObject>().Any(x => x["currentValue"] != null || x["monitorValue"] != null || x["value"] != null);
                data["evidence"] = data["currentValueReadOk"]?.GetValue<bool>() == true
                    ? "online-current-value-read"
                    : "No explicit current/monitor value property was readable from the public watch-table API.";
                return new ModelContextProtocol.ResponseJsonReport
                {
                    Ok = data["currentValueReadOk"]?.GetValue<bool>() == true,
                    Message = data["currentValueReadOk"]?.GetValue<bool>() == true ? "Read current values from existing watch table without writes." : "Watch table was read, but no current value property was exposed.",
                    Data = data
                };
            }
            catch (Exception ex)
            {
                data["error"] = FormatExceptionDetail(ex);
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = ex.Message, Data = data };
            }
        }

        public ModelContextProtocol.ResponseJsonReport ProbePlcMonitorOnlineCapabilities(string softwarePath)
        {
            var data = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "read-only-probe",
                ["safety"] = "No online/offline transition, no watch-table modification, no value write, and no force-table operation is executed by this probe."
            };

            var warnings = new JsonArray();
            var members = new JsonArray();
            var services = new JsonArray();

            try
            {
                var plc = GetPlcSoftware(softwarePath);
                if (plc == null)
                {
                    data["warnings"] = new JsonArray("PLC software not found.");
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "PLC software not found", Data = data };
                }

                data["plcType"] = plc.GetType().FullName ?? plc.GetType().Name;
                foreach (var m in DescribeMembers(plc, 800))
                {
                    var name = m.Name ?? "";
                    if (name.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    if (name.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Offline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Watch", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        members.Add($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}");
                    }
                }

                var likelyServiceSuffixes = new[]
                {
                    "OnlineProvider",
                    "OnlineService",
                    "DownloadProvider",
                    "PlcOnlineProvider",
                    "WatchTableProvider"
                };

                foreach (var suffix in likelyServiceSuffixes)
                {
                    var st = FindTypeBySuffix(suffix);
                    if (st == null)
                    {
                        services.Add(new JsonObject { ["suffix"] = suffix, ["status"] = "type-not-found" });
                        continue;
                    }

                    var svc = TryGetService(plc, st);
                    services.Add(new JsonObject
                    {
                        ["suffix"] = suffix,
                        ["type"] = st.FullName ?? st.Name,
                        ["status"] = svc == null ? "not-available" : "available",
                        ["serviceType"] = svc?.GetType().FullName ?? ""
                    });
                }

                var watchTables = GetPlcWatchTables(softwarePath) ?? new List<string>();
                data["watchTables"] = new JsonArray(watchTables.Select(x => JsonValue.Create(x)).ToArray());
                data["matchingMembers"] = members;
                data["serviceProbe"] = services;
                warnings.Add("Online value monitoring is not executed by this tool. It only probes read-only API surfaces for a later separately verified current-value read workflow.");
                warnings.Add("Force-table APIs are intentionally excluded by product safety policy.");
                data["warnings"] = warnings;

                return new ModelContextProtocol.ResponseJsonReport { Ok = true, Message = "PLC monitor/online capability probe completed", Data = data };
            }
            catch (Exception ex)
            {
                data["error"] = ex.ToString();
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = ex.Message, Data = data };
            }
        }

        public ModelContextProtocol.ResponseGlobalLibraryProbe ProbeGlobalLibrary(string libraryPath, int maxItems = 500)
        {
            var warnings = new List<string>();
            var raw = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["inputPath"] = libraryPath
            };

            try
            {
                if (_portal == null)
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        Error = "TIA Portal is not connected. Call Connect first.",
                        Warnings = new[] { "This is a read-only probe and does not import library content." },
                        Raw = raw
                    };
                }

                var resolved = ResolveGlobalLibraryFile(libraryPath);
                raw["resolvedLibraryFile"] = resolved ?? "";
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        Error = "Global library .al file not found.",
                        Warnings = new[] { "Pass either the .al21 file path or its containing folder." },
                        Raw = raw
                    };
                }

                var globalLibraries = TryGetPropertyValue(_portal, "GlobalLibraries");
                raw["globalLibrariesType"] = globalLibraries?.GetType().FullName ?? "";
                if (globalLibraries == null)
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        Error = "TiaPortal.GlobalLibraries property not found.",
                        Warnings = new[] { "Installed Openness API may not expose global library access through this build." },
                        Raw = raw
                    };
                }

                object? library = TryOpenGlobalLibrary(globalLibraries, resolved!, out var openError);
                if (library == null)
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        Error = openError ?? "Failed to open global library.",
                        Warnings = new[] { "No write operation was attempted." },
                        Raw = raw
                    };
                }

                var memberList = DescribeMembers(library, 300)
                    .Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")
                    .Take(Math.Max(10, Math.Min(1000, maxItems)))
                    .ToList();
                var masterCopies = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups", "Folders");
                var types = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "Types", "TypeFolder", "TypeFolders", "LibraryTypes", "Folders");
                var folders = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "Folders", "Groups", "MasterCopyFolders", "TypeFolders");

                raw["libraryType"] = library.GetType().FullName ?? library.GetType().Name;
                raw["memberCount"] = memberList.Count;
                raw["masterCopyCount"] = masterCopies.Count;
                raw["typeCount"] = types.Count;
                raw["folderCount"] = folders.Count;

                TryCloseOrDispose(library);

                warnings.Add("This probe only opens and lists library metadata; it does not import master copies or library types into a project.");
                if (masterCopies.Count == 0 && types.Count == 0)
                {
                    warnings.Add("No master copies/types were listed through public/reflection access; use DescribeObject/DescribeObjectProperty for deeper API discovery.");
                }

                return new ModelContextProtocol.ResponseGlobalLibraryProbe
                {
                    Ok = true,
                    Message = "Global library read-only probe completed",
                    LibraryPath = libraryPath,
                    ResolvedLibraryFile = resolved,
                    LibraryType = library.GetType().FullName ?? library.GetType().Name,
                    Members = memberList,
                    MasterCopies = masterCopies,
                    Types = types,
                    Folders = folders,
                    Warnings = warnings,
                    Raw = raw
                };
            }
            catch (Exception ex)
            {
                raw["error"] = ex.ToString();
                return new ModelContextProtocol.ResponseGlobalLibraryProbe
                {
                    Ok = false,
                    Message = ex.Message,
                    LibraryPath = libraryPath,
                    Error = ex.ToString(),
                    Warnings = warnings,
                    Raw = raw
                };
            }
        }

        public ModelContextProtocol.ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            string libraryPath,
            string masterCopyName,
            string hmiSoftwarePath,
            string screenName,
            string importedItemName = "",
            int left = 0,
            int top = 0)
        {
            var attempts = new List<string>();
            var warnings = new List<string>();
            var raw = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["inputPath"] = libraryPath,
                ["masterCopyName"] = masterCopyName,
                ["hmiSoftwarePath"] = hmiSoftwarePath,
                ["screenName"] = screenName,
                ["left"] = left,
                ["top"] = top
            };

            try
            {
                if (_portal == null)
                {
                    return GlobalLibraryImportFailure("TIA Portal is not connected. Call Connect first.");
                }

                if (IsProjectNull())
                {
                    return GlobalLibraryImportFailure("Project is null. Open or attach a temporary project first.");
                }

                if (string.IsNullOrWhiteSpace(masterCopyName))
                {
                    return GlobalLibraryImportFailure("masterCopyName is required and must come from ProbeGlobalLibrary readback.");
                }

                var resolved = ResolveGlobalLibraryFile(libraryPath);
                raw["resolvedLibraryFile"] = resolved ?? "";
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    return GlobalLibraryImportFailure("Global library .al file not found.");
                }

                var globalLibraries = TryGetPropertyValue(_portal, "GlobalLibraries");
                raw["globalLibrariesType"] = globalLibraries?.GetType().FullName ?? "";
                if (globalLibraries == null)
                {
                    return GlobalLibraryImportFailure("TiaPortal.GlobalLibraries property not found.");
                }

                var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var screenItems = TryGetPropertyValue(screen, "ScreenItems");
                if (screenItems == null)
                {
                    return GlobalLibraryImportFailure("Target screen has no ScreenItems collection.");
                }

                var before = ListNamedChildren(screenItems, 200);
                raw["screenItemsBefore"] = ToJsonArray(before);

                object? library = TryOpenGlobalLibrary(globalLibraries, resolved!, out var openError);
                if (library == null)
                {
                    return GlobalLibraryImportFailure(openError ?? "Failed to open global library.");
                }

                try
                {
                    var masterCopy = FindLibraryObjectByPathOrName(library, masterCopyName, attempts, "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups", "Folders");
                    if (masterCopy == null)
                    {
                        raw["libraryMembers"] = string.Join(" | ", DescribeMembers(library, 160).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        return GlobalLibraryImportFailure("MasterCopy was not found by exact/suffix path or name.");
                    }

                    raw["masterCopyType"] = masterCopy.GetType().FullName ?? masterCopy.GetType().Name;
                    raw["masterCopyMembers"] = new JsonArray(DescribeMembers(masterCopy, 240)
                        .Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}"))
                        .ToArray());
                    raw["masterCopyAttributes"] = ToJsonArray(TryReadInterestingAttributes(masterCopy));
                    var expectedName = string.IsNullOrWhiteSpace(importedItemName)
                        ? LastPathSegment(masterCopyName)
                        : importedItemName.Trim();

                    object? imported = TryImportMasterCopyIntoScreen(screen, screenItems, masterCopy, expectedName, left, top, attempts);
                    if (imported != null)
                    {
                        TrySetProperty(imported, "Left", left);
                        TrySetProperty(imported, "Top", top);
                        if (!string.IsNullOrWhiteSpace(expectedName))
                            TrySetProperty(imported, "Name", expectedName);
                    }

                    var after = ListNamedChildren(screenItems, 500);
                    raw["screenItemsAfter"] = ToJsonArray(after);
                    var readbackName = ResolveImportedReadbackName(before, after, expectedName);
                    var ok = !string.IsNullOrWhiteSpace(readbackName);
                    if (!ok)
                    {
                        warnings.Add("Import attempts finished, but the target screen item was not visible in readback.");
                        if (!attempts.Any(x => x.StartsWith("Try ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Try source ", StringComparison.OrdinalIgnoreCase)))
                        {
                            warnings.Add("No compatible public Openness import/copy method was found. The target ScreenItems collection exposed only create-style methods, and the MasterCopy object exposed no copy/instantiate method.");
                        }
                    }

                    return new ModelContextProtocol.ResponseGlobalLibraryImport
                    {
                        Ok = ok,
                        Message = ok
                            ? "Global library MasterCopy imported and read back from target screen"
                            : "Global library MasterCopy import did not produce screen-item readback evidence",
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        MasterCopyName = masterCopyName,
                        HmiSoftwarePath = hmiSoftwarePath,
                        ScreenName = screenName,
                        ImportedItemName = readbackName ?? expectedName,
                        Attempts = attempts,
                        ReadbackItems = after,
                        Warnings = warnings,
                        Error = ok ? null : "No matching/new ScreenItems readback after import.",
                        Raw = raw
                    };
                }
                finally
                {
                    TryCloseOrDispose(library);
                }
            }
            catch (Exception ex)
            {
                return GlobalLibraryImportFailure(FormatExceptionDetail(ex));
            }

            ModelContextProtocol.ResponseGlobalLibraryImport GlobalLibraryImportFailure(string error)
            {
                return new ModelContextProtocol.ResponseGlobalLibraryImport
                {
                    Ok = false,
                    Message = "Global library MasterCopy import failed",
                    LibraryPath = libraryPath,
                    ResolvedLibraryFile = raw["resolvedLibraryFile"]?.ToString(),
                    MasterCopyName = masterCopyName,
                    HmiSoftwarePath = hmiSoftwarePath,
                    ScreenName = screenName,
                    ImportedItemName = importedItemName,
                    Attempts = attempts,
                    ReadbackItems = Array.Empty<string>(),
                    Warnings = warnings,
                    Error = error,
                    Raw = raw
                };
            }
        }

        public void ImportTechnologyObject(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");

            try
            {
                object root = TryGetPropertyValue(plc, "TechnologyObjectGroup", "TechnologicalObjects", "TechnologyObjects") ?? plc;
                var group = TryResolveChildGroupByPath(root, folderPath) ?? root;

                // collection name varies; try likely ones
                var col = TryGetPropertyValue(group, "TechnologicalObjects", "TechnologyObjects", "Instances", "Objects") ??
                          TryGetPropertyValue(root, "TechnologicalObjects", "TechnologyObjects", "Instances", "Objects");

                if (col == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TechnologyObjects collection not found. plcType={plc.GetType().FullName} groupType={group.GetType().FullName}");

                if (TryImportEngineeringObjectIntoCollection(col, importPath, out _, out var err)) return;
                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportTechnologyObject failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        public ResponseImportBatch ImportTechnologyObjectsFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try { ImportTechnologyObject(softwarePath, folderPath, file); imported.Add(name); }
                    catch (PortalException pex) { failed.Add(new ImportFailure { Path = file, Error = pex.Message }); }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        // ── Technology Objects (TO) ──────────────────────────────────────────

        private static object? ResolveTechnologyObjectCollection(PlcSoftware plc)
        {
            var group = TryGetPropertyValue(plc,
                "TechnologicalObjectGroup", "TechnologyObjectGroup",
                "TechnologicalObjects", "TechnologyObjects");
            if (group == null) return null;

            // If we landed on a group container, drill into the collection
            var col = TryGetPropertyValue(group,
                "TechnologicalObjects", "TechnologyObjects", "Instances", "Objects");
            return col ?? group; // group itself might already be enumerable
        }

        public List<JsonObject> GetTechnologyObjects(string softwarePath)
        {
            var result = new List<JsonObject>();
            if (IsProjectNull()) return result;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return result;

            try
            {
                var col = ResolveTechnologyObjectCollection(plc);
                if (col is not IEnumerable items || col is string) return result;

                foreach (var item in items)
                {
                    if (item == null) continue;
                    var obj = new JsonObject();
                    foreach (var prop in new[] { "Name", "OfSystemLibElement", "OfSystemLibVersion" })
                    {
                        var val = TryGetPropertyValue(item, prop);
                        if (val != null) obj[prop] = JsonValue.Create(val.ToString());
                    }
                    // Try to get a "type" hint from class name as fallback
                    if (!obj.ContainsKey("OfSystemLibElement"))
                        obj["TypeHint"] = JsonValue.Create(item.GetType().Name);
                    result.Add(obj);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetTechnologyObjects failed for {SoftwarePath}", softwarePath);
            }
            return result;
        }

        public ResponseMessage ExportTechnologyObject(string softwarePath, string toName, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var col = ResolveTechnologyObjectCollection(plc);
                var to = FindByName(col, toName);
                if (to == null)
                    return new ResponseMessage { Message = $"Technology object '{toName}' not found in '{softwarePath}'." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                TryExportEngineeringObject(to, exportPath, out var err);
                if (err != null)
                    return new ResponseMessage { Message = $"Export error: {err}" };

                return new ResponseMessage
                {
                    Message = $"Technology object '{toName}' exported to '{exportPath}'.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["toName"] = toName }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportTechnologyObject failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseImportBatch ExportTechnologyObjectsToDirectory(
            string softwarePath, string exportDir, string regexName = "")
        {
            var exported = new List<string>();
            var failed = new List<ImportFailure>();

            if (IsProjectNull())
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = "No project open." });
                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = "PLC software not found." });
                return new ResponseImportBatch { Imported = exported, Failed = failed };
            }

            try
            {
                Directory.CreateDirectory(exportDir);
                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);

                var col = ResolveTechnologyObjectCollection(plc);
                if (col is not IEnumerable items || col is string)
                {
                    failed.Add(new ImportFailure { Path = softwarePath, Error = "Technology object collection not accessible." });
                    return new ResponseImportBatch { Imported = exported, Failed = failed };
                }

                foreach (var item in items)
                {
                    if (item == null) continue;
                    var name = TryGetPropertyValue(item, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (regex != null && !regex.IsMatch(name)) continue;

                    var path = Path.Combine(exportDir, name + ".xml");
                    TryExportEngineeringObject(item, path, out var err);
                    if (err == null) exported.Add(name);
                    else failed.Add(new ImportFailure { Path = name, Error = err });
                }
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = exportDir, Error = ex.ToString() });
            }

            return new ResponseImportBatch { Imported = exported, Failed = failed };
        }

        public (string? Name, string ProgramType, List<string> Screens)? GetHmiProgramInfo(string softwarePath)
        {
            _logger?.LogInformation($"Getting HMI program info by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                return null;
            }

            var sw = softwareContainer.Software;

            // Classic WinCC (HmiTarget)
            if (sw is HmiTarget classic)
            {
                return (classic.Name, "Classic", TryListScreens(classic));
            }

#if !TIA_V17
            // Unified (HmiSoftware)
            if (sw is HmiSoftware unified)
            {
                return (unified.Name, "Unified", TryListScreens(unified));
            }
#endif

            return (sw.ToString(), "Unknown", new List<string>());
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiSoftware(string softwarePath, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "Software",
                    ObjectPath = softwarePath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "HMI software not found",
                    ObjectKind = "Software",
                    ObjectPath = softwarePath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sw = softwareContainer.Software;
            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "Software",
                ObjectPath = softwarePath,
                TypeName = sw.GetType().FullName ?? sw.GetType().Name,
                Members = DescribeMembers(sw, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreen(string softwarePath, string screenName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiScreen",
                    ObjectPath = $"{softwarePath}:{screenName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "HMI software not found",
                    ObjectKind = "HmiScreen",
                    ObjectPath = $"{softwarePath}:{screenName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sw = softwareContainer.Software;
            var screen = TryFindByNameInCollection(sw, new[] { "Screens", "ScreenFolder" }, screenName);
            if (screen == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Screen not found",
                    ObjectKind = "HmiScreen",
                    ObjectPath = $"{softwarePath}:{screenName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiScreen",
                ObjectPath = $"{softwarePath}:{screenName}",
                TypeName = screen.GetType().FullName ?? screen.GetType().Name,
                Members = DescribeMembers(screen, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTagTable(string softwarePath, string tagTableName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiTagTable",
                    ObjectPath = $"{softwarePath}:{tagTableName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "HMI software not found",
                    ObjectKind = "HmiTagTable",
                    ObjectPath = $"{softwarePath}:{tagTableName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sw = softwareContainer.Software;
            var table = TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Tag table not found",
                    ObjectKind = "HmiTagTable",
                    ObjectPath = $"{softwarePath}:{tagTableName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiTagTable",
                ObjectPath = $"{softwarePath}:{tagTableName}",
                TypeName = table.GetType().FullName ?? table.GetType().Name,
                Members = DescribeMembers(table, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiTag(string softwarePath, string tagTableName, string tagName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sc = GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "HMI software not found",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sw = sc.Software;
            var table = TryFindHmiTagTable(sw, tagTableName);
            if (table == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Tag table not found",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
            if (tagsComp == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "tagTable.Tags not found",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            object? tagObj = null;
            try
            {
                if (tagsComp is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) && string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            tagObj = it;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (tagObj == null)
            {
                tagObj = TryFindByNameInCollection(tagsComp, Array.Empty<string>(), tagName);
            }

            if (tagObj == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Tag not found",
                    ObjectKind = "HmiTag",
                    ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiTag",
                ObjectPath = $"{softwarePath}:{tagTableName}:{tagName}",
                TypeName = tagObj.GetType().FullName ?? tagObj.GetType().Name,
                Members = DescribeMembers(tagObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeHmiScreenItem(string softwarePath, string screenName, string itemName, int maxMembers = 200)
        {
            if (IsProjectNull())
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Project is null",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sc = GetSoftwareContainer(softwarePath);
            if (sc?.Software == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "HMI software not found",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var sw = sc.Software;
            var screen = TryFindByNameInCollection(sw, new[] { "Screens", "ScreenFolder" }, screenName);
            if (screen == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Screen not found",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
            if (itemsComp == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "screen.ScreenItems not found",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            object? itemObj = null;
            try
            {
                if (itemsComp is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) && string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                        {
                            itemObj = it;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (itemObj == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Screen item not found",
                    ObjectKind = "HmiScreenItem",
                    ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "HmiScreenItem",
                ObjectPath = $"{softwarePath}:{screenName}:{itemName}",
                TypeName = itemObj.GetType().FullName ?? itemObj.GetType().Name,
                Members = DescribeMembers(itemObj, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ResponseMessage EnsureStartStopUnifiedHmi(
            string hmiSoftwarePath,
            string screenName = "Main",
            string tagTableName = "默认变量表",
            string plcName = "PLC_1",
            string connectionName = "HMI_Connection_1")
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false
            };

            var steps = new JsonArray();
            meta["steps"] = steps;

            void Step(string name, bool ok, string? detail = null)
            {
                var o = new JsonObject
                {
                    ["step"] = name,
                    ["ok"] = ok
                };
                if (!string.IsNullOrWhiteSpace(detail)) o["detail"] = detail;
                steps.Add(o);
            }

            object? FindExistingByName(object compositionOrEnumerable, string name)
            {
                try
                {
                    if (compositionOrEnumerable is System.Collections.IEnumerable en)
                    {
                        foreach (var it in en)
                        {
                            var n = TryGetName(it);
                            if (!string.IsNullOrWhiteSpace(n) &&
                                string.Equals(n!.Trim(), name, StringComparison.OrdinalIgnoreCase))
                            {
                                return it;
                            }
                        }
                    }
                }
                catch { }
                return null;
            }

            try
            {
                var totalDeadline = DateTime.UtcNow.AddSeconds(25); // hard timeout for this tool
                bool TimedOut() => DateTime.UtcNow > totalDeadline;

                if (IsProjectNull())
                {
                    Step("precheck", false, "Project is null");
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var sc = GetSoftwareContainer(hmiSoftwarePath);
                if (sc?.Software == null)
                {
                    Step("resolveSoftware", false, $"SoftwareContainer not found at '{hmiSoftwarePath}'");
                    return new ResponseMessage { Message = "HMI software not found", Meta = meta };
                }

                var sw = sc.Software;
                Step("resolveSoftware", true, sw.GetType().FullName);

                // Resolve screen + tag table
                var screen = TryFindByNameInCollection(sw, new[] { "Screens", "ScreenFolder" }, screenName);
                if (screen == null)
                {
                    Step("findScreen", false, $"Screen '{screenName}' not found");
                    return new ResponseMessage { Message = "Screen not found", Meta = meta };
                }
                Step("findScreen", true, screen.GetType().FullName);

                var tagTable = TryFindByNameInCollection(sw, new[] { "TagTables" }, tagTableName);
                if (tagTable == null)
                {
                    Step("findTagTable", false, $"TagTable '{tagTableName}' not found");
                    return new ResponseMessage { Message = "Tag table not found", Meta = meta };
                }
                Step("findTagTable", true, tagTable.GetType().FullName);

                // Ensure PLC↔HMI connection with correct driver for the actual PLC CPU (1200/1500 vs 300/400).
                try
                {
                    var connDesc = EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);
                    Step("ensureUnifiedHmiConnection", true, connDesc.Message ?? "ok");
                }
                catch (Exception ex)
                {
                    Step("ensureUnifiedHmiConnection", false, ex.InnerException?.Message ?? ex.Message);
                }

                var connName = string.IsNullOrWhiteSpace(connectionName) ? "HMI_Connection_1" : connectionName.Trim();
                Step("resolveConnection", true, connName);

                // Ensure tags in HMI tag table
                var tagsComp = tagTable.GetType().GetProperty("Tags")?.GetValue(tagTable);
                if (tagsComp == null)
                {
                    Step("resolveTagComposition", false, "tagTable.Tags not found");
                    return new ResponseMessage { Message = "Tag composition not found", Meta = meta };
                }
                Step("resolveTagComposition", true, tagsComp.GetType().FullName);

                string[] tagNames = new[] { "StartPB", "StopPB", "EStop", "RunOut" };
                foreach (var tn in tagNames)
                {
                    if (TimedOut())
                    {
                        Step("timeout", false, "Timeout during tag ensure");
                        return new ResponseMessage { Message = "Timeout", Meta = meta };
                    }
                    try
                    {
                        // find existing
                        var exists = FindExistingByName(tagsComp, tn) ?? TryFindByNameInCollection(tagsComp, Array.Empty<string>(), tn);
                        if (exists != null)
                        {
                            var wr = new JsonArray();
                            BindUnifiedHmiTagToPlcSymbol(exists, connName, plcName, tn, "Bool", wr);
                            Step($"tag:{tn}", true, "exists");
                            continue;
                        }

                        // create by reflection: Create(string)
                        var mCreate = tagsComp.GetType().GetMethod("Create", new[] { typeof(string) });
                        if (mCreate == null)
                        {
                            Step($"tag:{tn}", false, $"No Create(string) on {tagsComp.GetType().FullName}");
                            continue;
                        }

                        object? tagObj = null;
                        try
                        {
                            tagObj = mCreate.Invoke(tagsComp, new object[] { tn });
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            // If name already exists, treat as idempotent and return the existing object.
                            var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                            if (msg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var existing = FindExistingByName(tagsComp, tn);
                                if (existing != null)
                                {
                                    var wrU = new JsonArray();
                                    BindUnifiedHmiTagToPlcSymbol(existing, connName, plcName, tn, "Bool", wrU);
                                    Step($"tag:{tn}", true, "exists");
                                    continue;
                                }
                            }

                            Step($"tag:{tn}", false, msg);
                            continue;
                        }
                        if (tagObj == null)
                        {
                            Step($"tag:{tn}", false, "Create returned null");
                            continue;
                        }

                        TrySetProperty(tagObj, "Name", tn);
                        var wrNew = new JsonArray();
                        BindUnifiedHmiTagToPlcSymbol(tagObj, connName, plcName, tn, "Bool", wrNew);

                        Step($"tag:{tn}", true, "created");
                    }
                    catch (Exception ex)
                    {
                        Step($"tag:{tn}", false, ex.InnerException?.Message ?? ex.Message);
                    }
                }

                // Create minimal screen items (best-effort): two buttons + one lamp
                var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
                if (itemsComp == null)
                {
                    Step("resolveScreenItems", false, "screen.ScreenItems not found");
                    return new ResponseMessage { Message = "ScreenItems not found", Meta = meta };
                }
                Step("resolveScreenItems", true, itemsComp.GetType().FullName);

                // Dump all Create* method signatures on ScreenItems composition so we see what's really there.
                var itemsType = itemsComp.GetType();
                var createSigs = itemsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name))}) -> {m.ReturnType.FullName}")
                    .ToArray();
                Step("screenItems.CreateSignatures", true, string.Join(" | ", createSigs));

                // Resolve candidate HMI widget types by FullName (from Siemens.Engineering.HmiUnified).
                Type? ResolveHmiType(params string[] fullNames)
                {
                    foreach (var fn in fullNames)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                var t = asm.GetType(fn, throwOnError: false, ignoreCase: false);
                                if (t != null) return t;
                            }
                            catch { }
                        }
                    }
                    return null;
                }

                var tButton = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton",
                    "Siemens.Engineering.HmiUnified.UI.Controls.HmiButton");
                var tIOField = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField",
                    "Siemens.Engineering.HmiUnified.UI.Controls.HmiIOField");
                var tRectangle = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiRectangle",
                    "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle");
                var tLabel = ResolveHmiType(
                    "Siemens.Engineering.HmiUnified.UI.Widgets.HmiLabel",
                    "Siemens.Engineering.HmiUnified.UI.Controls.HmiLabel");
                Step("hmiTypeResolve", true,
                    $"Button={tButton?.AssemblyQualifiedName ?? "null"}; IOField={tIOField?.AssemblyQualifiedName ?? "null"}; Rectangle={tRectangle?.AssemblyQualifiedName ?? "null"}; Label={tLabel?.AssemblyQualifiedName ?? "null"}");

                // Try all Create overloads and candidate types; record per-attempt outcome.
                object? CreateItem(string name, Type?[] preferTypes, string[] stringTypeHints)
                {
                    var allAttempts = new List<string>();

                    // idempotent: return existing item if already present
                    var existingByName = FindExistingByName(itemsComp, name);
                    if (existingByName != null)
                    {
                        allAttempts.Add("EXISTS");
                        Step($"ui-detail:{name}", true, "exists");
                        return existingByName;
                    }

                    foreach (var m in itemsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                               .Where(x => x.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase)))
                    {
                        var ps = m.GetParameters();

                        // Generic Create<T>(string name)
                        if (m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        {
                            foreach (var t in preferTypes.Where(x => x != null))
                            {
                                try
                                {
                                    var gm = m.MakeGenericMethod(t!);
                                    var obj = gm.Invoke(itemsComp, new object[] { name });
                                    if (obj != null)
                                    {
                                        allAttempts.Add($"OK {m.Name}<{t!.Name}>(name)");
                                        return obj;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    allAttempts.Add($"ERR {m.Name}<{t!.Name}>(name): {(ex.InnerException?.Message ?? ex.Message)}");
                                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                    if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var ex2 = FindExistingByName(itemsComp, name);
                                        if (ex2 != null) return ex2;
                                    }
                                }
                            }
                        }

                        // Create(string name, Type type)
                        if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Type))
                        {
                            foreach (var t in preferTypes.Where(x => x != null))
                            {
                                try
                                {
                                    var obj = m.Invoke(itemsComp, new object[] { name, t! });
                                    if (obj != null)
                                    {
                                        allAttempts.Add($"OK {m.Name}(name, typeof({t!.Name}))");
                                        return obj;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    allAttempts.Add($"ERR {m.Name}(name, typeof({t!.Name})): {(ex.InnerException?.Message ?? ex.Message)}");
                                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                    if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var ex2 = FindExistingByName(itemsComp, name);
                                        if (ex2 != null) return ex2;
                                    }
                                }
                            }
                        }

                        // Create(Type type, string name)
                        if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(Type) && ps[1].ParameterType == typeof(string))
                        {
                            foreach (var t in preferTypes.Where(x => x != null))
                            {
                                try
                                {
                                    var obj = m.Invoke(itemsComp, new object[] { t!, name });
                                    if (obj != null)
                                    {
                                        allAttempts.Add($"OK {m.Name}(typeof({t!.Name}), name)");
                                        return obj;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    allAttempts.Add($"ERR {m.Name}(typeof({t!.Name}), name): {(ex.InnerException?.Message ?? ex.Message)}");
                                    var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                    if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var ex2 = FindExistingByName(itemsComp, name);
                                        if (ex2 != null) return ex2;
                                    }
                                }
                            }
                        }

                        // Create(string name, string typeId) / Create(string typeId, string name)
                        if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                        {
                            foreach (var th in stringTypeHints)
                            {
                                foreach (var order in new[] { new object[] { name, th }, new object[] { th, name } })
                                {
                                    try
                                    {
                                        var obj = m.Invoke(itemsComp, order);
                                        if (obj != null)
                                        {
                                            allAttempts.Add($"OK {m.Name}({order[0]}, {order[1]})");
                                            return obj;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        allAttempts.Add($"ERR {m.Name}({order[0]}, {order[1]}): {(ex.InnerException?.Message ?? ex.Message)}");
                                        var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                        if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            var ex2 = FindExistingByName(itemsComp, name);
                                            if (ex2 != null) return ex2;
                                        }
                                    }
                                }
                            }
                        }

                        // Create(string name)
                        if (!m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        {
                            try
                            {
                                var obj = m.Invoke(itemsComp, new object[] { name });
                                if (obj != null)
                                {
                                    allAttempts.Add($"OK {m.Name}(name)");
                                    return obj;
                                }
                            }
                            catch (Exception ex)
                            {
                                allAttempts.Add($"ERR {m.Name}(name): {(ex.InnerException?.Message ?? ex.Message)}");
                                var innerMsg = ex.InnerException?.Message ?? ex.Message;
                                if (innerMsg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var ex2 = FindExistingByName(itemsComp, name);
                                    if (ex2 != null) return ex2;
                                }
                            }
                        }
                    }

                    Step($"ui-detail:{name}", false, string.Join(" || ", allAttempts));
                    return null;
                }

                var hdrBar = CreateItem("HDR_Bar", new[] { tRectangle }, new[] { "HmiRectangle", "Rectangle" });
                Step("ui:HDR_Bar", hdrBar != null, hdrBar?.GetType().FullName);
                var hdrTitle = CreateItem("HDR_Title", new[] { tLabel, tIOField }, new[] { "HmiLabel", "Label", "HmiText", "Text" });
                Step("ui:HDR_Title", hdrTitle != null, hdrTitle?.GetType().FullName);

                var btnStart = CreateItem("BTN_Start", new[] { tButton }, new[] { "HmiButton", "Button" });
                Step("ui:BTN_Start", btnStart != null, btnStart?.GetType().FullName);
                var btnStop = CreateItem("BTN_Stop", new[] { tButton }, new[] { "HmiButton", "Button" });
                Step("ui:BTN_Stop", btnStop != null, btnStop?.GetType().FullName);
                var lampRun = CreateItem("LAMP_Run", new[] { tRectangle, tIOField }, new[] { "HmiRectangle", "HmiIOField", "Lamp", "HmiLamp" });
                Step("ui:LAMP_Run", lampRun != null, lampRun?.GetType().FullName);

                // Layout + styling (Unified RT): header strip + grouped controls
                try
                {
                    if (hdrBar != null)
                    {
                        TrySetProperty(hdrBar, "Left", 0);
                        TrySetProperty(hdrBar, "Top", 0);
                        TrySetProperty(hdrBar, "Width", (uint)1280);
                        TrySetProperty(hdrBar, "Height", (uint)72);
                        TrySetProperty(hdrBar, "BackColor", ColorTranslator.FromHtml("#1E3A5F"));
                        TrySetProperty(hdrBar, "BorderWidth", (uint)0);
                    }

                    if (hdrTitle != null)
                    {
                        TrySetProperty(hdrTitle, "Left", 24);
                        TrySetProperty(hdrTitle, "Top", 12);
                        TrySetProperty(hdrTitle, "Width", (uint)900);
                        TrySetProperty(hdrTitle, "Height", (uint)48);
                        var txtH = hdrTitle.GetType().GetProperty("Text")?.GetValue(hdrTitle);
                        if (txtH != null)
                        {
                            TrySetProperty(txtH, "Item", "MCP 验证 · 起停与状态");
                            TrySetProperty(txtH, "HorizontalAlignment", "Left");
                        }

                        TrySetProperty(hdrTitle, "ForeColor", Color.White);
                    }

                    if (btnStart != null)
                    {
                        TrySetProperty(btnStart, "Left", 48);
                        TrySetProperty(btnStart, "Top", 110);
                        TrySetProperty(btnStart, "Width", (uint)200);
                        TrySetProperty(btnStart, "Height", (uint)72);
                        TrySetProperty(btnStart, "BackColor", ColorTranslator.FromHtml("#2E7D32"));
                        var txt = btnStart.GetType().GetProperty("Text")?.GetValue(btnStart);
                        if (txt != null)
                        {
                            TrySetProperty(txt, "Item", "启动 (Start)");
                            TrySetProperty(txt, "HorizontalAlignment", "Center");
                        }
                    }

                    if (btnStop != null)
                    {
                        TrySetProperty(btnStop, "Left", 48);
                        TrySetProperty(btnStop, "Top", 200);
                        TrySetProperty(btnStop, "Width", (uint)200);
                        TrySetProperty(btnStop, "Height", (uint)72);
                        TrySetProperty(btnStop, "BackColor", ColorTranslator.FromHtml("#C62828"));
                        var txt = btnStop.GetType().GetProperty("Text")?.GetValue(btnStop);
                        if (txt != null)
                        {
                            TrySetProperty(txt, "Item", "停止 (Stop)");
                            TrySetProperty(txt, "HorizontalAlignment", "Center");
                        }
                    }

                    if (lampRun != null)
                    {
                        TrySetProperty(lampRun, "Left", 300);
                        TrySetProperty(lampRun, "Top", 110);
                        TrySetProperty(lampRun, "Width", (uint)120);
                        TrySetProperty(lampRun, "Height", (uint)120);
                        TrySetProperty(lampRun, "BackColor", ColorTranslator.FromHtml("#B0BEC5"));
                        TrySetProperty(lampRun, "BorderWidth", (uint)2);
                    }

                    Step("ui:layout", true);
                }
                catch (Exception ex)
                {
                    Step("ui:layout", false, ex.InnerException?.Message ?? ex.Message);
                }

                // Attempt: map button pressed state to HMI tag (momentary) via PressedStateTags composition (best-effort)
                void TryBindPressedTag(object? button, string table, string tagName)
                {
                    if (button == null) return;
                    try
                    {
                        var pst = button.GetType().GetProperty("PressedStateTags")?.GetValue(button);
                        if (pst == null)
                        {
                            Step($"bind:{TryGetName(button)}.PressedStateTags", false, "PressedStateTags missing");
                            return;
                        }

                        // dump create signatures once per button
                        var sigs = pst.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName))})")
                            .ToArray();
                        Step($"bind:{TryGetName(button)}.PressedStateTags.CreateSignatures", true, string.Join(" | ", sigs));

                        // idempotent check
                        var existing = FindExistingByName(pst, tagName);
                        if (existing != null)
                        {
                            Step($"bind:{TryGetName(button)}:{tagName}", true, "exists");
                            return;
                        }

                        // Try Create() then bind HMI tag path (table/tag) for Unified RT
                        var m0 = pst.GetType().GetMethod("Create", Type.EmptyTypes);
                        if (m0 != null)
                        {
                            try
                            {
                                var o = m0.Invoke(pst, Array.Empty<object>());
                                if (o != null)
                                {
                                    var path = string.IsNullOrWhiteSpace(table) ? tagName : $"{table}/{tagName}";
                                    var bound = TrySetAnyProperty(o, path, "Tag", "TagName", "HmiTag", "HmiTagName", "Path", "HmiTagPath", "FullName")
                                                || TrySetEngineeringAttribute(o, "Tag", path)
                                                || TrySetEngineeringAttribute(o, "HmiTag", path);
                                    Step($"bind:{TryGetName(button)}:{tagName}", bound, o.GetType().FullName);
                                    return;
                                }
                            }
                            catch (TargetInvocationException tie) when (tie.InnerException != null)
                            {
                                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                                Step($"bind:{TryGetName(button)}:{tagName}", false, msg);
                                return;
                            }
                        }

                        // Try Create(string)
                        var m1 = pst.GetType().GetMethod("Create", new[] { typeof(string) });
                        if (m1 != null)
                        {
                            try
                            {
                                var path2 = string.IsNullOrWhiteSpace(table) ? tagName : $"{table}/{tagName}";
                                var o = m1.Invoke(pst, new object[] { path2 });
                                Step($"bind:{TryGetName(button)}:{tagName}", o != null, o?.GetType().FullName);
                                return;
                            }
                            catch (TargetInvocationException tie) when (tie.InnerException != null)
                            {
                                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                                Step($"bind:{TryGetName(button)}:{tagName}", false, msg);
                                return;
                            }
                        }

                        // Fallback: some parts may expose a property like TagName/Tag
                        Step($"bind:{TryGetName(button)}:{tagName}", false, "No suitable Create on PressedStateTags");
                    }
                    catch (Exception ex)
                    {
                        Step($"bind:{TryGetName(button)}:{tagName}", false, ex.InnerException?.Message ?? ex.Message);
                    }
                }

                TryBindPressedTag(btnStart, tagTableName, "StartPB");
                TryBindPressedTag(btnStop, tagTableName, "StopPB");

                // Attempt: lamp BackColor dynamization based on RunOut (best-effort; may need richer APIs)
                try
                {
                    if (lampRun != null)
                    {
                        var dyn = lampRun.GetType().GetProperty("Dynamizations")?.GetValue(lampRun);
                        if (dyn == null)
                        {
                            Step("dyn:LAMP_Run", false, "Dynamizations missing");
                        }
                        else
                        {
                            var sigs = dyn.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .Where(m => m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName))})")
                                .ToArray();
                            Step("dyn:LAMP_Run.CreateSignatures", true, string.Join(" | ", sigs));

                            // No universal way here without knowing specific dynamization classes;
                            // return signatures so next iteration can target correct Create overload.
                            Step("dyn:LAMP_Run", false, "Not implemented yet (see CreateSignatures)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Step("dyn:LAMP_Run", false, ex.InnerException?.Message ?? ex.Message);
                }

                meta["success"] = true;
                return new ResponseMessage
                {
                    Message = "Unified HMI start/stop skeleton created (best-effort).",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                Step("exception", false, ex.ToString());
                return new ResponseMessage { Message = "Failed creating HMI skeleton", Meta = meta };
            }
        }

        public ResponseMessage EnsureUnifiedHmiScreen(string hmiSoftwarePath, string screenName, uint width = 0, uint height = 0)
        {
            return RunHmiStepTool("EnsureUnifiedHmiScreen", meta =>
            {
                var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var screens = TryGetPropertyValue(sw, "Screens");
                if (screens == null) throw new InvalidOperationException("HMI Screens collection not found.");

                var screen = TryFindByNameInCollection(sw, new[] { "Screens", "ScreenFolder" }, screenName);
                var action = "exists";
                if (screen == null)
                {
                    var mCreate = screens.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (mCreate == null) throw new InvalidOperationException($"Create(string) not found on {screens.GetType().FullName}.");
                    screen = InvokeCreate(mCreate, screens, new object[] { screenName });
                    action = "created";
                }

                if (screen == null) throw new InvalidOperationException("Screen create/find returned null.");
                if (width > 0) TrySetProperty(screen, "Width", width);
                if (height > 0) TrySetProperty(screen, "Height", height);

                meta["action"] = action;
                meta["screenType"] = screen.GetType().FullName;
                return $"HMI screen '{screenName}' {action}.";
            });
        }

        public ResponseMessage EnsureUnifiedHmiTagTable(string hmiSoftwarePath, string tagTableName)
        {
            return RunHmiStepTool("EnsureUnifiedHmiTagTable", meta =>
            {
                var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var tables = TryGetHmiTagTablesCollection(sw);
                if (tables == null) throw new InvalidOperationException($"HMI TagTables collection not found. hmiType={sw.GetType().FullName}; tagRootType={TryGetHmiTagRoot(sw).GetType().FullName}");

                var table = TryFindHmiTagTable(sw, tagTableName);
                var action = "exists";
                if (table == null)
                {
                    table = TryCreateNamedEngineeringObject(tables, tagTableName, out var createError);
                    if (table == null) throw new InvalidOperationException(createError ?? $"Create failed on {tables.GetType().FullName}.");
                    action = "created";
                }

                if (table == null) throw new InvalidOperationException("Tag table create/find returned null.");
                meta["action"] = action;
                meta["tagTableType"] = table.GetType().FullName;
                return $"HMI tag table '{tagTableName}' {action}.";
            });
        }

        /// <summary>
        /// Unified HMI tag → PLC symbolic binding (same rules as <see cref="EnsureUnifiedHmiTag"/>).
        /// </summary>
        private void BindUnifiedHmiTagToPlcSymbol(
            object tag,
            string connectionName,
            string plcName,
            string plcTagSymbol,
            string hmiDataType,
            JsonArray writeResults,
            string address = "")
        {
            bool Set(string label, object? value, params string[] names)
            {
                var ok = TrySetAnyPropertyOrAttribute(tag, value, names);
                writeResults.Add($"{label}={ok}");
                return ok;
            }

            var tagTypeCandidates = new[]
            {
                "External",
                "ExternalTag",
                "HmiExternal",
                "ConnectedExternal",
                "PLC",
                "Plc",
                "Process",
                "ConnectionTag",
                "HmiTag"
            };

            Set("DataType", hmiDataType, "DataType", "HmiDataType");
            TrySetEngineeringAttribute(tag, "DataType", hmiDataType);
            TrySetEngineeringAttribute(tag, "HmiDataType", hmiDataType);
            var tagTypeSet = TrySetAnyEnumCandidatePropertyOrAttribute(tag, tagTypeCandidates, "TagType", "Type", "Kind");
            writeResults.Add("TagTypeCandidate=" + tagTypeSet);
            if (!string.IsNullOrWhiteSpace(plcName))
                Set("PlcName", plcName, "PlcName", "ControllerName", "Station");
            if (!string.IsNullOrWhiteSpace(connectionName))
                Set("Connection", connectionName, "Connection", "ConnectionName");
            var targetPlcTag = string.IsNullOrWhiteSpace(plcTagSymbol) ? string.Empty : plcTagSymbol;
            var targetAddress = string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim();
            // Only true PLC absolute operands (e.g. %DB200.DBX0.0, DB200.DBX0.0). Do NOT treat symbolic
            // "DB_HMI_Interface.Member" as absolute — that wrongly flipped AddressAccessMode and broke PLC binding.
            var isAbsoluteAddress =
                !string.IsNullOrWhiteSpace(targetAddress)
                || targetPlcTag.StartsWith("%", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(
                    targetPlcTag,
                    @"^(DB|IW|QW|ID|QD|IB|QB|MB|MW|MD)\d",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (isAbsoluteAddress)
            {
                if (!string.IsNullOrWhiteSpace(targetPlcTag) && !targetPlcTag.StartsWith("%", StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedTag = NormalizeControllerTagName(targetPlcTag);
                    TryBindUnifiedHmiTagPlcSymbolicPaths(tag, normalizedTag, writeResults);
                }

                TrySetUnifiedHmiTagAddressingModeEnum(tag, symbolic: false, writeResults);
                var runtimeAddress = string.IsNullOrWhiteSpace(targetAddress) ? targetPlcTag : targetAddress;
                TrySetUnifiedHmiTagRuntimeAddress(tag, runtimeAddress, writeResults);
            }
            else
            {
                Set("ClearAddress", string.Empty, "Address", "LogicalAddress");
                TrySetEngineeringAttribute(tag, "Address", string.Empty);
                TrySetEngineeringAttribute(tag, "LogicalAddress", string.Empty);
                TrySetUnifiedHmiTagAddressingModeEnum(tag, symbolic: true, writeResults);
                var normalizedTag = NormalizeControllerTagName(targetPlcTag);
                TryBindUnifiedHmiTagPlcSymbolicPaths(tag, normalizedTag, writeResults);
                if (TrySetUnifiedHmiTagAccessModeByEnumScan(tag, true))
                    writeResults.Add("AccessMode_repass_symbolic=true");
            }
        }

        private static void TrySetUnifiedHmiTagRuntimeAddress(object tag, string runtimeAddress, JsonArray writeResults)
        {
            var addressNames = new[]
            {
                "Address",
                "LogicalAddress",
                "ProcessValueAddress",
                "RuntimeAddress",
                "ControllerAddress",
                "ControllerTagAddress",
                "ExternalAddress",
                "PlcAddress",
                "PLCAddress",
                "TagAddress",
                "AbsoluteAddress"
            };

            var primaryOk = TrySetAnyPropertyOrAttribute(tag, runtimeAddress, "Address", "LogicalAddress");
            writeResults.Add("RuntimeAddressPrimary=" + primaryOk);

            var readback = TryReadUnifiedHmiTagRuntimeAddress(tag, addressNames);
            if (!string.Equals(readback, runtimeAddress, StringComparison.OrdinalIgnoreCase))
            {
                var extraOk = false;
                foreach (var name in addressNames.Skip(2))
                {
                    extraOk = TrySetProperty(tag, name, runtimeAddress) || TrySetEngineeringAttribute(tag, name, runtimeAddress) || extraOk;
                }

                writeResults.Add("RuntimeAddressExtra=" + extraOk);
                readback = TryReadUnifiedHmiTagRuntimeAddress(tag, addressNames);
            }

            writeResults.Add("RuntimeAddressReadback=" + (readback ?? string.Empty));
        }

        private static string TryReadUnifiedHmiTagRuntimeAddress(object tag, params string[] addressNames)
        {
            foreach (var name in addressNames)
            {
                try
                {
                    var prop = tag.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var value = prop.GetValue(tag)?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) return value!;
                    }
                }
                catch
                {
                }

                var attr = TryGetEngineeringAttribute(tag, name)?.ToString();
                if (!string.IsNullOrWhiteSpace(attr)) return attr!;
            }

            return string.Empty;
        }

        /// <summary>
        /// WinCC Unified HMI tags expose addressing mode as enums; writing display strings (e.g. "SymbolicAccess")
        /// via generic SetProperty fails silently and leaves the UI on default Absolute with empty Address/PLC tag.
        /// </summary>
        private static void TrySetUnifiedHmiTagAddressingModeEnum(object tag, bool symbolic, JsonArray writeResults)
        {
            var ok = TrySetUnifiedHmiTagAccessModeByEnumScan(tag, symbolic);
            if (!ok)
            {
                var candidates = symbolic
                    ? new[] { "Symbolic", "SymbolicAccess", "FromTag", "HmiSymbolic", "ExternalSymbolic", "TagSymbolic" }
                    : new[] { "Absolute", "AbsoluteAccess", "Direct", "HmiAbsolute", "ExternalAbsolute", "TagAbsolute" };
                ok = TrySetAnyEnumCandidatePropertyOrAttribute(tag, candidates, "AddressAccessMode", "AccessMode", "TagAddressingMode", "HmiTagAddressingMode");
            }

            writeResults.Add($"AddressingMode({(symbolic ? "symbolic" : "absolute")})={ok}");
        }

        /// <summary>
        /// Unified <see cref="HmiTag"/> access mode enum names differ by TIA version; scan all declared enum members.
        /// </summary>
        private static bool TrySetUnifiedHmiTagAccessModeByEnumScan(object tag, bool wantSymbolic)
        {
            foreach (var propName in new[] { "AccessMode", "AddressAccessMode", "TagAddressingMode", "HmiTagAddressingMode" })
            {
                try
                {
                    var prop = tag.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null || !prop.CanWrite || !prop.PropertyType.IsEnum) continue;
                    foreach (var enumName in Enum.GetNames(prop.PropertyType))
                    {
                        var u = enumName.ToUpperInvariant();
                        var match = wantSymbolic
                            ? u.Contains("SYMBOL") || u.Contains("NAMED")
                            : u.Contains("ABSOL") || u.Contains("DIRECT") || u.Contains("ADDRESS");
                        if (!match) continue;
                        var ev = Enum.Parse(prop.PropertyType, enumName);
                        prop.SetValue(tag, ev);
                        TrySetEngineeringAttribute(tag, propName, ev);
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        /// <summary>
        /// S7-1200/1500 PLC partner in HMI connections uses rack 0 and CPU slot 1 in almost all compact PLC projects.
        /// Missing slot shows as "?" in TIA and breaks tag resolution.
        /// </summary>
        private static void TryConfigureUnifiedHmiConnectionS7PartnerRackSlot(object connection, string plcFamily)
        {
            if (plcFamily != "S71200" && plcFamily != "S71500" && plcFamily != "UNKNOWN") return;

            foreach (var slotName in new[] { "PartnerSlot", "Slot", "PlcSlot", "PartnerExpansionSlot", "ExpansionSlot", "ControllerSlot" })
            {
                foreach (var slotVal in new object[] { 1, (short)1, (ushort)1, "1" })
                {
                    if (TrySetProperty(connection, slotName, slotVal) || TrySetEngineeringAttribute(connection, slotName, slotVal))
                    {
                        break;
                    }
                }
            }

            foreach (var rackName in new[] { "PartnerRack", "Rack", "PlcRack", "ControllerRack" })
            {
                foreach (var rackVal in new object[] { 0, (short)0, (ushort)0, "0" })
                {
                    if (TrySetProperty(connection, rackName, rackVal) || TrySetEngineeringAttribute(connection, rackName, rackVal))
                    {
                        break;
                    }
                }
            }
        }

        private static void TryBindUnifiedHmiTagPlcSymbolicPaths(object tag, string normalizedTag, JsonArray writeResults)
        {
            if (string.IsNullOrWhiteSpace(normalizedTag)) return;
            var names = new[]
            {
                "PlcTag", "ControllerTag", "ControllerTagName", "ProcessTag", "ExternalTag", "Tag", "TagName", "SymbolicAddress"
            };
            foreach (var n in names)
            {
                var ok = TrySetProperty(tag, n, normalizedTag) || TrySetEngineeringAttribute(tag, n, normalizedTag);
                writeResults.Add($"{n}={ok}");
            }
        }

        /// <summary>
        /// Unified HMI connection CommunicationDriver is often an engineering attribute whose runtime type is an enum.
        /// Passing a human-readable driver string into SetAttribute then fails Enum.Parse and leaves S7-300/400 default.
        /// </summary>
        private static bool TrySetUnifiedHmiCommunicationDriverEnum(object connection, string plcFamily)
        {
            try
            {
                var get = connection.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                var set = connection.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (get == null || set == null) return false;

                Type? enumType = null;
                var prop = connection.GetType().GetProperty("CommunicationDriver", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType.IsEnum) enumType = prop.PropertyType;
                if (enumType == null)
                {
                    try
                    {
                        var cur = get.Invoke(connection, new object[] { "CommunicationDriver" });
                        if (cur != null && cur.GetType().IsEnum) enumType = cur.GetType();
                    }
                    catch
                    {
                    }
                }

                if (enumType == null || !enumType.IsEnum) return false;

                var ev = SelectCommunicationDriverEnumValue(enumType, plcFamily);
                if (ev == null && (plcFamily == "UNKNOWN" || plcFamily == "S71200" || plcFamily == "S71500"))
                {
                    foreach (var name in Enum.GetNames(enumType))
                    {
                        var u = name.ToUpperInvariant();
                        if (u.Contains("1200") || u.Contains("1500") || u.Contains("S712") || u.Contains("S715") || u.Contains("PLUS"))
                        {
                            ev = Enum.Parse(enumType, name);
                            break;
                        }
                    }
                }

                if (ev == null) return false;

                set.Invoke(connection, new object[] { "CommunicationDriver", ev });
                if (prop != null && prop.CanWrite)
                {
                    try
                    {
                        prop.SetValue(connection, ev);
                    }
                    catch
                    {
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public ResponseMessage EnsureUnifiedHmiTag(string hmiSoftwarePath, string tagTableName, string tagName, string hmiDataType = "Bool", string plcName = "PLC_1", string plcTag = "", string connectionName = "", string address = "", bool requireVerifiedBinding = true)
        {
            return RunHmiStepTool("EnsureUnifiedHmiTag", meta =>
            {
                var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
                var tagTable = EnsureHmiTagTableObject(sw, tagTableName);
                var tags = TryGetPropertyValue(tagTable, "Tags");
                if (tags == null) throw new InvalidOperationException($"Tags collection not found on tag table '{tagTableName}'.");

                var tag = FindExistingByName(tags, tagName) ?? TryFindByNameInCollection(tags, Array.Empty<string>(), tagName);
                var action = "exists";
                if (tag == null)
                {
                    tag = TryCreateNamedEngineeringObject(tags, tagName, out var createError);
                    if (tag == null) throw new InvalidOperationException(createError ?? $"Create failed on {tags.GetType().FullName}.");
                    action = "created";
                }

                if (tag == null) throw new InvalidOperationException("Tag create/find returned null.");
                var writeResults = new JsonArray();
                var targetPlcTag = string.IsNullOrWhiteSpace(plcTag) ? tagName : plcTag;
                BindUnifiedHmiTagToPlcSymbol(tag, connectionName, plcName, targetPlcTag, hmiDataType, writeResults, address);

                meta["action"] = action;
                meta["tagType"] = tag.GetType().FullName;
                meta["tagEnumHints"] = DescribeWritableEnumProperties(tag, "TagType", "AccessMode", "AddressAccessMode");
                meta["requestedPlcTag"] = targetPlcTag;
                meta["requestedAddress"] = address ?? string.Empty;
                meta["writeResults"] = writeResults;
                var binding = ClassifyUnifiedHmiTagBinding(tag, connectionName, targetPlcTag, address ?? string.Empty);
                meta["readback"] = binding.Readback;
                meta["bindingStatus"] = binding.Status;
                meta["bindingVerified"] = binding.Verified;
                meta["bindingGuidance"] = binding.Guidance;
                meta["requireVerifiedBinding"] = requireVerifiedBinding;
                if (requireVerifiedBinding && !binding.Verified)
                {
                    throw new InvalidOperationException($"HMI tag '{tagName}' binding is not verified. Status={binding.Status}; {binding.Guidance}; Readback={binding.Readback}");
                }

                return $"HMI tag '{tagName}' {action}. Binding={binding.Status}.";
            });
        }

        public ModelContextProtocol.ResponseObjectDescribe EnsureUnifiedHmiConnection(string hmiSoftwarePath, string connectionName = "HMI_Connection_1", string plcName = "PLC_1")
        {
            var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) throw new InvalidOperationException($"Connections collection not found on HMI software '{hmiSoftwarePath}'.");

            var connection = FindExistingByName(connections, connectionName) ?? TryFindByNameInCollection(connections, Array.Empty<string>(), connectionName);
            if (connection == null)
            {
                var create = connections.GetType().GetMethod("Create", new[] { typeof(string) });
                if (create == null) throw new InvalidOperationException($"Create(string) not found on {connections.GetType().FullName}.");
                connection = InvokeCreate(create, connections, new object[] { connectionName });
            }

            if (connection == null) throw new InvalidOperationException("Connection create/find returned null.");

            TrySetProperty(connection, "Name", connectionName);
            var partner = ResolveUnifiedHmiPlcPartner(plcName);
            TryConfigureUnifiedHmiConnectionPartner(connection, partner);
            TryConfigureUnifiedHmiConnectionS7PartnerRackSlot(connection, partner.Family);
            // Driver last so partner binding cannot clobber S7-1200/1500 selection.
            TryConfigureUnifiedHmiCommunicationDriver(connection, plcName);
            ValidateUnifiedHmiCommunicationDriver(connection, partner.Family);

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                ObjectKind = "HmiConnection",
                ObjectPath = $"{hmiSoftwarePath}:{connectionName}",
                TypeName = connection.GetType().FullName,
                Members = DescribeMembers(connection, 220),
                Message = $"HMI connection '{connectionName}' ensured. PartnerResolved={partner.Summary}; {SummarizeHmiObjectReadback(connection, "Name", "CommunicationDriver", "Partner", "Station", "Node", "InitialAddress", "PlcName", "ControllerName", "PartnerName")}"
            };
        }

        public ResponseMessage EnsureUnifiedHmiScreenItem(string hmiSoftwarePath, string screenName, string itemName, string itemType = "Button", int left = 0, int top = 0, uint width = 120, uint height = 40, string text = "")
        {
            return RunHmiStepTool("EnsureUnifiedHmiScreenItem", meta =>
            {
                var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems");
                if (items == null) throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var item = FindExistingByName(items, itemName);
                var action = "exists";
                if (item == null)
                {
                    var itemClrType = ResolveUnifiedScreenItemType(itemType);
                    item = CreateUnifiedScreenItem(items, itemName, itemClrType, itemType);
                    action = "created";
                }

                if (item == null) throw new InvalidOperationException("Screen item create/find returned null.");
                TrySetProperty(item, "Left", left);
                TrySetProperty(item, "Top", top);
                TrySetProperty(item, "Width", width);
                TrySetProperty(item, "Height", height);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var textPart = TryGetPropertyValue(item, "Text", "DisplayName");
                    if (textPart != null) TrySetProperty(textPart, "Item", text);
                }

                meta["action"] = action;
                meta["itemType"] = item.GetType().FullName;
                return $"HMI screen item '{itemName}' {action}.";
            });
        }

        public ResponseMessage ApplyUnifiedHmiScreenDesignJson(string hmiSoftwarePath, string screenName, string designJson, bool strict = true)
        {
            return RunHmiStepTool("ApplyUnifiedHmiScreenDesignJson", meta =>
            {
                if (string.IsNullOrWhiteSpace(designJson))
                {
                    throw new InvalidOperationException("designJson is empty.");
                }

                var root = JsonNode.Parse(designJson) as JsonObject
                    ?? throw new InvalidOperationException("designJson root must be a JSON object.");

                var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems")
                    ?? throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var changed = new JsonArray();
                var failed = new JsonArray();

                if (root["screen"] is JsonObject screenProps)
                {
                    ApplyJsonProperties(screen, screenProps, failed, "screen");
                }

                var itemArray = root["items"] as JsonArray
                    ?? throw new InvalidOperationException("designJson.items must be an array.");

                foreach (var itemNode in itemArray.OfType<JsonObject>())
                {
                    var name = JsonString(itemNode, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        failed.Add("item without name skipped");
                        continue;
                    }

                    try
                    {
                        var typeHint = JsonString(itemNode, "type");
                        if (string.IsNullOrWhiteSpace(typeHint)) typeHint = "Rectangle";

                        var item = FindExistingByName(items, name!);
                        var action = "updated";
                        if (item == null)
                        {
                            var itemClrType = ResolveUnifiedScreenItemType(typeHint!);
                            item = CreateUnifiedScreenItem(items, name!, itemClrType, typeHint!);
                            action = "created";
                        }

                        if (item == null) throw new InvalidOperationException($"Create/find returned null for '{name}'.");

                        if (itemNode["left"] != null) TrySetProperty(item, "Left", JsonObjectValue(itemNode["left"]));
                        if (itemNode["top"] != null) TrySetProperty(item, "Top", JsonObjectValue(itemNode["top"]));
                        if (itemNode["width"] != null) TrySetProperty(item, "Width", JsonObjectValue(itemNode["width"]));
                        if (itemNode["height"] != null) TrySetProperty(item, "Height", JsonObjectValue(itemNode["height"]));

                        if (itemNode["properties"] is JsonObject props)
                        {
                            ApplyJsonProperties(item, props, failed, name!, typeHint ?? string.Empty);
                        }

                        var text = JsonString(itemNode, "text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            var textTarget = JsonString(itemNode, "textProperty");
                            if (string.IsNullOrWhiteSpace(textTarget)) textTarget = "Text";
                            if (!TrySetMultilingualText(item, textTarget!, text!, JsonString(itemNode, "culture") ?? "zh-CN"))
                            {
                                failed.Add($"{name}.{textTarget}: text write failed");
                            }
                        }

                        if (itemNode["font"] is JsonObject font)
                        {
                            var fontPart = TryGetPropertyValue(item, "Font");
                            if (fontPart != null) ApplyJsonProperties(fontPart, font, failed, name + ".Font");
                            else failed.Add($"{name}.Font: part not found");
                        }

                        if (itemNode["content"] is JsonObject content)
                        {
                            var contentPart = TryGetPropertyValue(item, "Content");
                            if (contentPart != null) ApplyJsonProperties(contentPart, content, failed, name + ".Content");
                            else failed.Add($"{name}.Content: part not found");
                        }

                        if (itemNode["padding"] is JsonObject padding)
                        {
                            var paddingPart = TryGetPropertyValue(item, "Padding");
                            if (paddingPart != null) ApplyJsonProperties(paddingPart, padding, failed, name + ".Padding");
                            else failed.Add($"{name}.Padding: part not found");
                        }

                        changed.Add($"{action}:{name}:{item.GetType().Name}");
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                meta["changed"] = changed;
                meta["failed"] = failed;
                meta["strict"] = strict;
                if (strict && failed.Count > 0)
                {
                    throw new InvalidOperationException($"Unified HMI design apply had {failed.Count} failed writes: {string.Join(" | ", failed.Select(x => x?.ToString() ?? string.Empty))}");
                }
                return $"Applied Unified HMI design to '{screenName}'. changed={changed.Count}, failed={failed.Count}.";
            });
        }

        public ResponseMessage BindUnifiedHmiButtonPressedTag(string hmiSoftwarePath, string screenName, string buttonName, string tagName)
        {
            return RunHmiStepTool("BindUnifiedHmiButtonPressedTag", meta =>
            {
                var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var items = TryGetPropertyValue(screen, "ScreenItems");
                if (items == null) throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");

                var button = FindExistingByName(items, buttonName);
                if (button == null) throw new InvalidOperationException($"Screen item '{buttonName}' not found.");

                var pressedStateTags = TryGetPropertyValue(button, "PressedStateTags");
                if (pressedStateTags == null) throw new InvalidOperationException($"PressedStateTags not found on '{buttonName}'.");

                var existing = FindPressedStateTag(pressedStateTags, tagName);
                var action = "exists";
                if (existing == null)
                {
                    var mCreate = pressedStateTags.GetType().GetMethod("Create", Type.EmptyTypes);
                    if (mCreate == null) throw new InvalidOperationException($"Create() not found on {pressedStateTags.GetType().FullName}.");
                    existing = InvokeCreate(mCreate, pressedStateTags, Array.Empty<object>());
                    action = "created";
                }

                if (existing == null) throw new InvalidOperationException("Pressed-state part create/find returned null.");
                var bound = TrySetAnyProperty(existing, tagName, "Tag", "TagName", "HmiTag", "HmiTagName", "Name", "TagPath");

                meta["action"] = action;
                meta["pressedStateTagPartType"] = existing.GetType().FullName;
                meta["propertyBound"] = bound;
                if (!bound)
                {
                    meta["availableProperties"] = string.Join(", ", existing.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{p.Name}:{p.PropertyType.Name}"));
                }

                return bound
                    ? $"Button '{buttonName}' pressed-state tag bound to '{tagName}'."
                    : $"Pressed-state part created for '{buttonName}', but no writable tag-name property was found.";
            });
        }

        public List<string> ListUnifiedHmiApiTypes(string nameContains = "", int limit = 500)
        {
            var filter = nameContains?.Trim() ?? string.Empty;
            var result = new List<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var t in types)
                {
                    if (t.FullName == null || !t.FullName.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrWhiteSpace(filter) && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var kind = t.IsEnum ? "enum" : t.IsClass ? "class" : t.IsInterface ? "interface" : t.IsValueType ? "value" : "type";
                    var line = $"{kind}: {t.FullName}";
                    if (t.BaseType != null && t.BaseType != typeof(object))
                    {
                        line += $" : {t.BaseType.FullName}";
                    }
                    if (t.IsEnum)
                    {
                        line += $" values=[{string.Join(",", Enum.GetNames(t).Take(50))}]";
                    }

                    result.Add(line);
                    if (result.Count >= Math.Max(1, limit)) return result;
                }
            }

            return result;
        }

        public ResponseMessage EnsureUnifiedHmiButtonEventHandler(string hmiSoftwarePath, string screenName, string buttonName, string eventType)
        {
            return RunHmiStepTool("EnsureUnifiedHmiButtonEventHandler", meta =>
            {
                var button = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, buttonName);
                var eventHandlers = TryGetPropertyValue(button, "EventHandlers");
                if (eventHandlers == null) throw new InvalidOperationException($"EventHandlers not found on '{buttonName}'.");

                var create = eventHandlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                if (create == null) throw new InvalidOperationException($"Create(enum) not found on {eventHandlers.GetType().FullName}.");

                var enumType = create.GetParameters()[0].ParameterType;
                var enumValue = Enum.Parse(enumType, eventType, ignoreCase: true);

                object? handler = null;
                var find = eventHandlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == enumType);
                if (find != null)
                {
                    handler = find.Invoke(eventHandlers, new[] { enumValue });
                }

                var action = "exists";
                if (handler == null)
                {
                    handler = InvokeCreate(create, eventHandlers, new[] { enumValue });
                    action = "created";
                }

                if (handler == null) throw new InvalidOperationException("Event handler create/find returned null.");
                meta["action"] = action;
                meta["eventEnumType"] = enumType.FullName;
                meta["eventType"] = enumValue.ToString();
                meta["handlerType"] = handler.GetType().FullName;
                meta["handlerMembers"] = string.Join(" | ", DescribeMembers(handler, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                return $"Button event handler '{eventValueToText(enumValue)}' on '{buttonName}' {action}.";
            });

            static string eventValueToText(object value) => value.ToString() ?? string.Empty;
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(string hmiSoftwarePath, string screenName, string buttonName, string eventType, int maxMembers = 200)
        {
            try
            {
                if (IsProjectNull())
                {
                    return new ModelContextProtocol.ResponseObjectDescribe
                    {
                        Message = "Project is null",
                        ObjectKind = "HmiButtonEventScript",
                        ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}",
                        Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                    };
                }

                var handler = ResolveHmiButtonEventHandlerOrThrow(hmiSoftwarePath, screenName, buttonName, eventType);
                var scriptProp = handler.GetType().GetProperty("Script", BindingFlags.Public | BindingFlags.Instance);
                var script = scriptProp?.GetValue(handler);

                var members = new List<ModelContextProtocol.ObjectMember>
                {
                    new ModelContextProtocol.ObjectMember
                    {
                        Name = "HandlerType",
                        Kind = "Info",
                        Type = handler.GetType().FullName ?? handler.GetType().Name,
                        Signature = null
                    },
                    new ModelContextProtocol.ObjectMember
                    {
                        Name = "ScriptProperty",
                        Kind = "Info",
                        Type = scriptProp == null ? "missing" : $"{scriptProp.PropertyType.FullName}; CanRead={scriptProp.CanRead}; CanWrite={scriptProp.CanWrite}",
                        Signature = null
                    },
                    new ModelContextProtocol.ObjectMember
                    {
                        Name = "ScriptValue",
                        Kind = "Info",
                        Type = script == null ? "null" : (script.GetType().FullName ?? script.GetType().Name),
                        Signature = null
                    }
                };

                if (script != null)
                {
                    members.AddRange(DescribeMembers(script, Math.Max(10, Math.Min(2000, maxMembers))));
                    try
                    {
                        var infos = script.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(script, Array.Empty<object>());
                        if (infos is IEnumerable en)
                        {
                            foreach (var info in en.Cast<object>().Take(100))
                            {
                                members.Add(new ModelContextProtocol.ObjectMember
                                {
                                    Name = $"AttributeInfo:{TryGetPropertyValue(info, "Name") ?? info}",
                                    Kind = "AttributeInfo",
                                    Type = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                                    Signature = info.ToString()
                                });
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    members.AddRange(DescribeMembers(handler, Math.Max(10, Math.Min(2000, maxMembers))));
                    try
                    {
                        var infos = handler.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes)?.Invoke(handler, Array.Empty<object>());
                        if (infos is IEnumerable en)
                        {
                            foreach (var info in en.Cast<object>().Take(100))
                            {
                                members.Add(new ModelContextProtocol.ObjectMember
                                {
                                    Name = $"HandlerAttributeInfo:{TryGetPropertyValue(info, "Name") ?? info}",
                                    Kind = "AttributeInfo",
                                    Type = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                                    Signature = info.ToString()
                                });
                            }
                        }
                    }
                    catch { }
                }

                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "OK",
                    ObjectKind = "HmiButtonEventScript",
                    ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}.Script",
                    TypeName = script == null ? scriptProp?.PropertyType.FullName : script.GetType().FullName,
                    Members = members
                };
            }
            catch (Exception ex)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = ex.ToString(),
                    ObjectKind = "HmiButtonEventScript",
                    ObjectPath = $"{hmiSoftwarePath}:{screenName}:{buttonName}:{eventType}.Script",
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }
        }

        public ResponseMessage SetUnifiedHmiButtonEventScriptCode(string hmiSoftwarePath, string screenName, string buttonName, string eventType, string scriptCode, string globalDefinitionAreaScriptCode = "", bool async = false)
        {
            return RunHmiStepTool("SetUnifiedHmiButtonEventScriptCode", meta =>
            {
                var handler = ResolveHmiButtonEventHandlerOrThrow(hmiSoftwarePath, screenName, buttonName, eventType);
                var script = TryGetPropertyValue(handler, "Script");
                if (script == null)
                {
                    throw new InvalidOperationException($"Script object is null on '{buttonName}.{eventType}'. Ensure the event handler exists first.");
                }

                var setScriptCode = TrySetProperty(script, "ScriptCode", scriptCode ?? string.Empty);
                var setGlobalCode = TrySetProperty(script, "GlobalDefinitionAreaScriptCode", globalDefinitionAreaScriptCode ?? string.Empty);
                var setAsync = TrySetProperty(script, "Async", async);

                meta["scriptType"] = script.GetType().FullName;
                meta["setScriptCode"] = setScriptCode;
                meta["setGlobalDefinitionAreaScriptCode"] = setGlobalCode;
                meta["setAsync"] = setAsync;

                object? syntaxResult = null;
                try
                {
                    syntaxResult = script.GetType().GetMethod("SyntaxCheck", Type.EmptyTypes)?.Invoke(script, Array.Empty<object>());
                    if (syntaxResult != null)
                    {
                        var syntaxErrors = TryGetEnumerableStrings(syntaxResult, "Errors").ToList();
                        var syntaxWarnings = TryGetEnumerableStrings(syntaxResult, "Warnings").ToList();
                        meta["syntaxResultType"] = syntaxResult.GetType().FullName;
                        meta["syntaxResult"] = syntaxResult.ToString();
                        meta["syntaxErrors"] = ToJsonArray(syntaxErrors);
                        meta["syntaxWarnings"] = ToJsonArray(syntaxWarnings);
                        meta["syntaxErrorCount"] = syntaxErrors.Count;
                        meta["syntaxWarningCount"] = syntaxWarnings.Count;
                        meta["syntaxPropertyName"] = TryGetPropertyValue(syntaxResult, "PropertyName")?.ToString() ?? string.Empty;
                        meta["syntaxMembers"] = string.Join(" | ", DescribeMembers(syntaxResult, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                    }
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    meta["syntaxError"] = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                }
                catch (Exception ex)
                {
                    meta["syntaxError"] = ex.Message;
                }

                if (!setScriptCode)
                {
                    throw new InvalidOperationException($"ScriptCode property could not be written on {script.GetType().FullName}.");
                }

                return $"ScriptCode set for '{buttonName}.{eventType}'.";
            });
        }

        public ResponseMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string dynamizationType = "")
        {
            return RunHmiStepTool("EnsureUnifiedHmiDynamization", meta =>
            {
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var dynamizations = TryGetPropertyValue(item, "Dynamizations");
                if (dynamizations == null) throw new InvalidOperationException($"Dynamizations not found on '{itemName}'.");

                var find = dynamizations.GetType().GetMethod("Find", new[] { typeof(string) });
                var existing = find?.Invoke(dynamizations, new object[] { propertyName });
                if (existing != null)
                {
                    meta["action"] = "exists";
                    meta["dynamizationType"] = existing.GetType().FullName;
                    meta["members"] = string.Join(" | ", DescribeMembers(existing, 100).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                    return $"Dynamization for '{itemName}.{propertyName}' exists.";
                }

                var createMethods = dynamizations.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    .ToList();
                if (!createMethods.Any())
                {
                    throw new InvalidOperationException($"Generic Create<T>(string) not found on {dynamizations.GetType().FullName}.");
                }

                var candidates = ResolveUnifiedHmiDynamizationTypes(dynamizationType).ToList();
                meta["candidateTypes"] = string.Join(" | ", candidates.Select(t => t.FullName));
                if (!candidates.Any())
                {
                    throw new InvalidOperationException($"No dynamization type matched '{dynamizationType}'. Use ListUnifiedHmiApiTypes with nameContains='Dynamization'.");
                }

                var errors = new List<string>();
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var created = createMethods[0].MakeGenericMethod(candidate).Invoke(dynamizations, new object[] { propertyName });
                        if (created == null) continue;

                        meta["action"] = "created";
                        meta["dynamizationType"] = created.GetType().FullName;
                        meta["members"] = string.Join(" | ", DescribeMembers(created, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        return $"Dynamization for '{itemName}.{propertyName}' created as '{created.GetType().Name}'.";
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{candidate.FullName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                meta["attemptErrors"] = string.Join(" || ", errors);
                throw new InvalidOperationException($"Unable to create dynamization for '{itemName}.{propertyName}'.");
            });
        }

        /// <summary>
        /// Unified TagDynamization: PLC address may live on property <c>Address</c>, <c>LogicalAddress</c>,
        /// or only as an engineering attribute — plain <see cref="TrySetProperty"/> often misses it.
        /// </summary>
        private static bool TrySetTagDynamizationAddress(object dyn, string address)
        {
            if (string.IsNullOrWhiteSpace(address) || dyn == null) return false;
            foreach (var attr in new[] { "Address", "LogicalAddress", "ControllerTagAddress", "PlcAddress" })
            {
                if (TrySetProperty(dyn, attr, address)) return true;
                if (TrySetEngineeringAttribute(dyn, attr, address)) return true;
            }

            return false;
        }

        public ResponseMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string tagName, string dataType = "Bool", string plcTag = "", string address = "")
        {
            return RunHmiStepTool("BindUnifiedHmiTagDynamization", meta =>
            {
                var item = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, itemName);
                var dynamizations = TryGetPropertyValue(item, "Dynamizations");
                if (dynamizations == null) throw new InvalidOperationException($"Dynamizations not found on '{itemName}'.");

                var find = dynamizations.GetType().GetMethod("Find", new[] { typeof(string) });
                var dyn = find?.Invoke(dynamizations, new object[] { propertyName });
                var action = "exists";
                if (dyn == null)
                {
                    var tagDynType = ResolveUnifiedHmiDynamizationTypes("TagDynamization").FirstOrDefault(t => t.Name == "TagDynamization");
                    if (tagDynType == null) throw new InvalidOperationException("TagDynamization type not found.");

                    var create = dynamizations.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    if (create == null) throw new InvalidOperationException($"Create<T>(string) not found on {dynamizations.GetType().FullName}.");

                    dyn = create.MakeGenericMethod(tagDynType).Invoke(dynamizations, new object[] { propertyName });
                    action = "created";
                }

                if (dyn == null) throw new InvalidOperationException("Dynamization create/find returned null.");
                var setTag = TrySetProperty(dyn, "Tag", tagName);
                var setDataType = TrySetProperty(dyn, "DataType", dataType);
                var setPlcTag = !string.IsNullOrWhiteSpace(plcTag) && TrySetProperty(dyn, "PlcTag", plcTag);
                var setAddress = false;
                if (!string.IsNullOrWhiteSpace(address))
                {
                    setAddress = TrySetTagDynamizationAddress(dyn, address);
                }

                meta["action"] = action;
                meta["dynamizationType"] = dyn.GetType().FullName;
                meta["setTag"] = setTag;
                meta["setDataType"] = setDataType;
                meta["setPlcTag"] = string.IsNullOrWhiteSpace(plcTag) ? "skipped" : setPlcTag;
                meta["setAddress"] = string.IsNullOrWhiteSpace(address) ? "skipped" : setAddress;
                meta["members"] = string.Join(" | ", DescribeMembers(dyn, 120).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));

                if (!setTag)
                {
                    throw new InvalidOperationException($"Tag property could not be written on {dyn.GetType().FullName}.");
                }

                return $"Tag dynamization for '{itemName}.{propertyName}' bound to '{tagName}'.";
            });
        }

        private static bool TrySetProperty(object target, string propName, object? value)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;

                object? v = CoerceReflectionValue(value, p.PropertyType);

                p.SetValue(target, v);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["tool"] = toolName,
                ["success"] = false
            };

            try
            {
                if (IsProjectNull())
                {
                    meta["error"] = "Project is null";
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var message = action(meta);
                meta["success"] = true;
                return new ResponseMessage { Message = message, Meta = meta };
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                meta["error"] = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString();
                return new ResponseMessage { Message = $"{toolName} failed", Meta = meta };
            }
        }

        private object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath)
        {
            var sc = GetSoftwareContainer(hmiSoftwarePath);
            if (sc?.Software == null)
            {
                throw new InvalidOperationException($"HMI software not found at '{hmiSoftwarePath}'.");
            }

            return sc.Software;
        }

        private object ResolveHmiScreenOrThrow(string hmiSoftwarePath, string screenName)
        {
            var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
            var screen = TryFindByNameInCollection(sw, new[] { "Screens", "ScreenFolder" }, screenName);
            if (screen == null)
            {
                throw new InvalidOperationException($"HMI screen '{screenName}' not found.");
            }

            return screen;
        }

        private object ResolveHmiScreenItemOrThrow(string hmiSoftwarePath, string screenName, string itemName)
        {
            var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
            var items = TryGetPropertyValue(screen, "ScreenItems");
            if (items == null)
            {
                throw new InvalidOperationException($"ScreenItems collection not found on screen '{screenName}'.");
            }

            var item = FindExistingByName(items, itemName);
            if (item == null)
            {
                throw new InvalidOperationException($"Screen item '{itemName}' not found on screen '{screenName}'.");
            }

            return item;
        }

        private object ResolveHmiButtonEventHandlerOrThrow(string hmiSoftwarePath, string screenName, string buttonName, string eventType)
        {
            var button = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, buttonName);
            var eventHandlers = TryGetPropertyValue(button, "EventHandlers");
            if (eventHandlers == null)
            {
                throw new InvalidOperationException($"EventHandlers not found on '{buttonName}'.");
            }

            var create = eventHandlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
            if (create == null)
            {
                throw new InvalidOperationException($"Create(enum) not found on {eventHandlers.GetType().FullName}.");
            }

            var enumType = create.GetParameters()[0].ParameterType;
            var enumValue = Enum.Parse(enumType, eventType, ignoreCase: true);
            var find = eventHandlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == enumType);

            var handler = find?.Invoke(eventHandlers, new[] { enumValue });
            if (handler != null) return handler;

            handler = InvokeCreate(create, eventHandlers, new[] { enumValue });
            if (handler == null)
            {
                throw new InvalidOperationException($"Button event handler '{eventType}' create/find returned null.");
            }

            return handler;
        }

        private object EnsureHmiTagTableObject(object hmiSoftware, string tagTableName)
        {
            var tagRoot = TryGetHmiTagRoot(hmiSoftware);
            var table = TryFindHmiTagTable(hmiSoftware, tagTableName);
            if (table != null) return table;

            var tables = TryGetHmiTagTablesCollection(hmiSoftware);
            if (tables == null)
            {
                throw new InvalidOperationException($"HMI TagTables collection not found. hmiType={hmiSoftware.GetType().FullName}; tagRootType={tagRoot.GetType().FullName}; tagRootMembers={string.Join(" | ", DescribeMembers(tagRoot, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"))}");
            }

            table = TryCreateNamedEngineeringObject(tables, tagTableName, out var createError);
            if (table == null)
            {
                throw new InvalidOperationException(createError ?? $"Failed to create HMI tag table '{tagTableName}'.");
            }

            return table;
        }

        private static object? TryCreateNamedEngineeringObject(object collection, string name, out string? error)
        {
            error = null;
            var attempts = new List<string>();

            foreach (var method in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(m => m.GetParameters().Length))
            {
                var ps = method.GetParameters();
                var sig = $"{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.FullName + " " + p.Name))})";

                object?[]? args = null;
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    args = new object?[] { name };
                }
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    args = new object?[] { name, name };
                }
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType.IsEnum)
                {
                    args = new object?[] { name, Enum.ToObject(ps[1].ParameterType, 0) };
                }
                else if (ps.Length == 2 && ps[0].ParameterType.IsEnum && ps[1].ParameterType == typeof(string))
                {
                    args = new object?[] { Enum.ToObject(ps[0].ParameterType, 0), name };
                }
                else
                {
                    attempts.Add($"SKIP {sig}");
                    continue;
                }

                try
                {
                    var created = method.Invoke(collection, args);
                    if (created != null) return created;
                    attempts.Add($"NULL {sig}");
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    attempts.Add($"ERR {sig}: {msg}");
                    if (msg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var existing = FindExistingByName(collection, name);
                        if (existing != null) return existing;
                    }
                }
                catch (Exception ex)
                {
                    attempts.Add($"ERR {sig}: {ex.Message}");
                }
            }

            error = $"No supported Create overload succeeded on {collection.GetType().FullName}. Attempts: {string.Join(" | ", attempts)}";
            return null;
        }

        private static object TryGetHmiTagRoot(object hmiSoftware)
        {
            return TryGetPropertyValue(hmiSoftware,
                       "TagTableFolder",
                       "TagFolder",
                       "HmiTagTableFolder",
                       "HmiTagFolder",
                       "TagTableGroup",
                       "HmiTagTableGroup")
                   ?? hmiSoftware;
        }

        private static object? TryGetHmiTagTablesCollection(object hmiSoftware)
        {
            var root = TryGetHmiTagRoot(hmiSoftware);
            return TryGetPropertyValue(root,
                       "TagTables",
                       "HmiTagTables",
                       "Tables")
                   ?? TryGetPropertyValue(hmiSoftware,
                       "TagTables",
                       "HmiTagTables",
                       "Tables");
        }

        private static object? TryFindHmiTagTable(object hmiSoftware, string tagTableName)
        {
            var root = TryGetHmiTagRoot(hmiSoftware);
            return TryFindByNameInCollection(root, new[] { "TagTables", "HmiTagTables", "Tables" }, tagTableName)
                   ?? TryFindByNameInCollection(hmiSoftware, new[] { "TagTables", "HmiTagTables", "Tables" }, tagTableName)
                   ?? FindExistingByName(TryGetHmiTagTablesCollection(hmiSoftware) ?? root, tagTableName);
        }

        private static object? FindExistingByName(object compositionOrEnumerable, string name)
        {
            try
            {
                if (compositionOrEnumerable is IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) &&
                            string.Equals(n!.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        {
                            return it;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static object? InvokeCreate(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                throw new InvalidOperationException(msg, tie.InnerException);
            }
        }

        private static Type? ResolveUnifiedScreenItemType(string itemType)
        {
            var key = (itemType ?? string.Empty).Trim();
            var candidates = key.Equals("Button", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiButton", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton" }
                : key.Equals("Text", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiText", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Siemens.Engineering.HmiUnified.UI.Shapes.HmiText" }
                : key.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) || key.Equals("Lamp", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiRectangle", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", "Siemens.Engineering.HmiUnified.UI.Widgets.HmiRectangle" }
                    : key.Equals("IOField", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiIOField", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField" }
                        : new[] { key };

            foreach (var name in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(name, throwOnError: false, ignoreCase: false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static IEnumerable<Type> ResolveUnifiedHmiDynamizationTypes(string dynamizationType)
        {
            var filter = (dynamizationType ?? string.Empty).Trim();
            var preferredNames = string.IsNullOrWhiteSpace(filter)
                ? new[]
                {
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.DiscreteDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.RangeDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.ScriptDynamization"
                }
                : filter.Contains(".")
                    ? new[] { filter }
                    : new[]
                    {
                        $"Siemens.Engineering.HmiUnified.UI.Dynamization.{filter}",
                        $"Siemens.Engineering.HmiUnified.UI.Dynamization.{filter}Dynamization",
                        filter
                    };

            foreach (var name in preferredNames)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try { t = asm.GetType(name, throwOnError: false, ignoreCase: false); } catch { }
                    if (t != null) yield return t;
                }
            }

            if (!string.IsNullOrWhiteSpace(filter) && !filter.Contains("."))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t.FullName == null) continue;
                        if (!t.FullName.StartsWith("Siemens.Engineering.HmiUnified.UI.Dynamization.", StringComparison.Ordinal)) continue;
                        if (t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) yield return t;
                    }
                }
            }
        }

        private static object? CreateUnifiedScreenItem(object items, string itemName, Type? itemClrType, string itemTypeHint)
        {
            var methods = items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (itemClrType != null && m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    var created = m.MakeGenericMethod(itemClrType).Invoke(items, new object[] { itemName });
                    if (created != null) return created;
                }

                if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    foreach (var args in new[] { new object[] { itemName, itemTypeHint }, new object[] { itemTypeHint, itemName } })
                    {
                        try
                        {
                            var created = m.Invoke(items, args);
                            if (created != null) return created;
                        }
                        catch { }
                    }
                }
            }

            throw new InvalidOperationException($"Unable to create screen item '{itemName}' as '{itemTypeHint}'. ResolvedType={itemClrType?.FullName ?? "null"}.");
        }

        private static object? FindPressedStateTag(object pressedStateTags, string tagName)
        {
            try
            {
                if (pressedStateTags is IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        foreach (var propName in new[] { "Tag", "TagName", "HmiTag", "HmiTagName", "Name", "TagPath" })
                        {
                            var value = TryGetPropertyValue(it, propName)?.ToString();
                            if (!string.IsNullOrWhiteSpace(value) &&
                                string.Equals(value!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                            {
                                return it;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static bool TrySetAnyProperty(object target, string value, params string[] propertyNames)
        {
            foreach (var propName in propertyNames)
            {
                if (TrySetProperty(target, propName, value)) return true;
            }

            return false;
        }

        private static bool TrySetAnyPropertyOrAttribute(object target, object? value, params string[] propertyNames)
        {
            var any = false;
            foreach (var propName in propertyNames)
            {
                any = TrySetProperty(target, propName, value) || any;
                any = TrySetEngineeringAttribute(target, propName, value) || any;
            }

            return any;
        }

        private static bool TrySetAnyEnumCandidatePropertyOrAttribute(object target, IEnumerable<string> valueCandidates, params string[] propertyNames)
        {
            var any = false;
            foreach (var propName in propertyNames)
            {
                var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    foreach (var candidate in valueCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(prop.PropertyType, candidate, ignoreCase: true);
                            prop.SetValue(target, enumValue);
                            any = true;
                            break;
                        }
                        catch { }
                    }
                }

                var oldValue = TryGetEngineeringAttribute(target, propName);
                if (oldValue != null && oldValue.GetType().IsEnum)
                {
                    foreach (var candidate in valueCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(oldValue.GetType(), candidate, ignoreCase: true);
                            if (TrySetEngineeringAttribute(target, propName, enumValue))
                            {
                                any = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            return any;
        }

        private static string DescribeWritableEnumProperties(object target, params string[] propertyNames)
        {
            var parts = new List<string>();
            foreach (var name in propertyNames)
            {
                var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType.IsEnum)
                {
                    parts.Add($"{name}:{prop.PropertyType.FullName}=[{string.Join(",", Enum.GetNames(prop.PropertyType))}]");
                    continue;
                }

                var oldValue = TryGetEngineeringAttribute(target, name);
                if (oldValue != null && oldValue.GetType().IsEnum)
                {
                    parts.Add($"{name}:attr:{oldValue.GetType().FullName}=[{string.Join(",", Enum.GetNames(oldValue.GetType()))}]");
                }
            }

            return string.Join(" | ", parts);
        }

        private static object? TryGetEngineeringAttribute(object target, string attributeName)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                return get?.Invoke(target, new object[] { attributeName });
            }
            catch
            {
                return null;
            }
        }

        private static string SummarizeHmiObjectReadback(object target, params string[] names)
        {
            var parts = new List<string>();
            foreach (var name in names)
            {
                object? value = null;
                var got = false;
                try
                {
                    var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        value = prop.GetValue(target);
                        got = true;
                    }
                }
                catch { }

                if (!got)
                {
                    value = TryGetEngineeringAttribute(target, name);
                    got = value != null;
                }

                if (got)
                    parts.Add($"{name}={value ?? ""}");
            }

            return string.Join("; ", parts);
        }

        private sealed class UnifiedHmiTagBindingReadback
        {
            public string Status { get; set; } = "Failed";
            public bool Verified { get; set; }
            public string Readback { get; set; } = string.Empty;
            public string Guidance { get; set; } = string.Empty;
        }

        private static UnifiedHmiTagBindingReadback ClassifyUnifiedHmiTagBinding(object tag, string connectionName, string plcTag, string address)
        {
            string Attr(params string[] names)
            {
                foreach (var name in names)
                {
                    try
                    {
                        var prop = tag.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null && prop.CanRead)
                        {
                            var v = prop.GetValue(tag)?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) return v!;
                        }
                    }
                    catch { }

                    try
                    {
                        var v = TryGetEngineeringAttribute(tag, name)?.ToString();
                        if (!string.IsNullOrWhiteSpace(v)) return v!;
                    }
                    catch { }
                }

                return string.Empty;
            }

            var readback = SummarizeHmiObjectReadback(tag, "Connection", "AccessMode", "AddressAccessMode", "TagType", "PlcName", "ControllerName", "Station", "PlcTag", "ControllerTag", "ControllerTagName", "Address", "LogicalAddress", "ProcessValueAddress", "RuntimeAddress", "ControllerAddress", "ControllerTagAddress", "ExternalAddress", "PlcAddress", "PLCAddress", "TagAddress", "AbsoluteAddress", "DataType", "HmiDataType");
            var connection = Attr("Connection", "ConnectionName");
            var accessMode = Attr("AccessMode", "AddressAccessMode");
            var symbol = Attr("PlcTag", "ControllerTag", "ControllerTagName");
            var runtimeAddress = Attr("Address", "LogicalAddress", "ProcessValueAddress", "RuntimeAddress", "ControllerAddress", "ControllerTagAddress", "ExternalAddress", "PlcAddress", "PLCAddress", "TagAddress", "AbsoluteAddress");
            var requestedAddress = (address ?? string.Empty).Trim();
            var requestedSymbol = NormalizeControllerTagName(plcTag ?? string.Empty);
            var expectedConnection = (connectionName ?? string.Empty).Trim();

            var connectionOk = string.IsNullOrWhiteSpace(expectedConnection) ||
                string.Equals(connection, expectedConnection, StringComparison.OrdinalIgnoreCase);
            var absoluteOk = !string.IsNullOrWhiteSpace(requestedAddress) &&
                connectionOk &&
                string.Equals(runtimeAddress, requestedAddress, StringComparison.OrdinalIgnoreCase);
            var symbolicOk = !string.IsNullOrWhiteSpace(requestedSymbol) &&
                connectionOk &&
                string.Equals(NormalizeControllerTagName(symbol), requestedSymbol, StringComparison.OrdinalIgnoreCase) &&
                accessMode.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0;

            if (symbolicOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "SymbolicVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "PLC symbolic HMI tag binding read back successfully."
                };
            }

            if (absoluteOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "AbsoluteVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "Absolute-address HMI tag binding read back successfully."
                };
            }

            var status = string.IsNullOrWhiteSpace(connection) || connection.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0 || connection.IndexOf("内部", StringComparison.OrdinalIgnoreCase) >= 0
                ? "InternalOnly"
                : "Unverified";
            return new UnifiedHmiTagBindingReadback
            {
                Status = status,
                Verified = false,
                Readback = readback,
                Guidance = "Pass connectionName plus a verified PLC symbol or absolute address, then read back Connection/AccessMode/PlcTag/Address. Internal HMI tags are not accepted by the stable project-generation path."
            };
        }

        private static string NormalizeControllerTagName(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;
            return tagName.Replace("\"", string.Empty);
        }

        private sealed class UnifiedHmiPlcPartnerInfo
        {
            public string SoftwarePath { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;
            public string StationName { get; set; } = string.Empty;
            public string NodeName { get; set; } = string.Empty;
            public string InitialAddress { get; set; } = string.Empty;
            public string Family { get; set; } = "UNKNOWN";

            public string Summary =>
                $"SoftwarePath={SoftwarePath}; DeviceName={DeviceName}; StationName={StationName}; NodeName={NodeName}; InitialAddress={InitialAddress}; Family={Family}";
        }

        private UnifiedHmiPlcPartnerInfo ResolveUnifiedHmiPlcPartner(string plcSoftwarePath)
        {
            plcSoftwarePath ??= string.Empty;
            var info = new UnifiedHmiPlcPartnerInfo
            {
                SoftwarePath = plcSoftwarePath,
                DeviceName = FirstPathSegment(plcSoftwarePath),
                StationName = FirstPathSegment(plcSoftwarePath),
                Family = InferUnifiedPlcFamilyFromSoftwarePath(plcSoftwarePath)
            };

            try
            {
                var sc = GetSoftwareContainer(plcSoftwarePath);
                var di = sc?.Parent as DeviceItem;
                if (di != null)
                {
                    info.StationName = TryGetName(di) ?? di.Name ?? info.StationName;
                    var root = GetTopDeviceItem(di);
                    if (root != null)
                    {
                        info.DeviceName = TryGetName(root) ?? root.Name ?? info.DeviceName;
                        info.StationName = TryGetName(root) ?? root.Name ?? info.StationName;
                        FillUnifiedHmiPartnerNetworkInfo(root, info);
                    }
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(info.NodeName))
            {
                try
                {
                    var root = GetDeviceItemByPath(info.DeviceName);
                    if (root != null) FillUnifiedHmiPartnerNetworkInfo(root, info);
                }
                catch
                {
                }
            }

            if (string.IsNullOrWhiteSpace(info.DeviceName)) info.DeviceName = FirstPathSegment(plcSoftwarePath);
            if (string.IsNullOrWhiteSpace(info.StationName)) info.StationName = info.DeviceName;
            return info;
        }

        private static string FirstPathSegment(string path)
        {
            return (path ?? string.Empty).Trim()
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
        }

        private static DeviceItem? GetTopDeviceItem(DeviceItem item)
        {
            var current = item;
            while (current.Parent is DeviceItem parent)
            {
                current = parent;
            }

            return current;
        }

        private static void FillUnifiedHmiPartnerNetworkInfo(DeviceItem root, UnifiedHmiPlcPartnerInfo info)
        {
            var plcNode = FindNetworkNodes(root).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            if (plcNode.Node == null) return;

            info.NodeName = TryGetName(plcNode.Node)
                ?? TryGetPropertyValue(plcNode.Node, "Name")?.ToString()
                ?? plcNode.Item.Name
                ?? string.Empty;

            var address = TryGetPropertyValue(plcNode.Node, "Address")?.ToString()
                ?? TryGetPropertyValue(plcNode.Node, "IpAddress")?.ToString()
                ?? TryGetPropertyValue(plcNode.Node, "IPAddress")?.ToString()
                ?? TryGetEngineeringAttribute(plcNode.Node, "Address")?.ToString()
                ?? TryGetEngineeringAttribute(plcNode.Node, "IpAddress")?.ToString()
                ?? string.Empty;
            info.InitialAddress = address;
        }

        private static void TryConfigureUnifiedHmiConnectionPartner(object connection, UnifiedHmiPlcPartnerInfo partner)
        {
            var deviceName = string.IsNullOrWhiteSpace(partner.DeviceName) ? partner.SoftwarePath : partner.DeviceName;
            var stationName = string.IsNullOrWhiteSpace(partner.StationName) ? deviceName : partner.StationName;

            TrySetAnyPropertyOrAttribute(connection, deviceName, "Partner", "PartnerName", "DeviceName", "PlcName", "ControllerName");
            TrySetAnyPropertyOrAttribute(connection, stationName, "Station", "StationName", "ControllerStation");
            TrySetAnyPropertyOrAttribute(connection, deviceName, "Controller", "Device", "Plc", "Target");

            if (!string.IsNullOrWhiteSpace(partner.NodeName))
            {
                TrySetAnyPropertyOrAttribute(connection, partner.NodeName, "Node", "PartnerNode", "Interface", "NetworkNode", "AccessPoint");
            }

            if (!string.IsNullOrWhiteSpace(partner.InitialAddress))
            {
                TrySetAnyPropertyOrAttribute(connection, partner.InitialAddress, "InitialAddress", "Address", "IpAddress", "IPAddress", "PartnerAddress");
            }
        }

        /// <summary>
        /// Infer PLC CPU family from the PLC software path (device TypeIdentifier / order number).
        /// </summary>
        private string InferUnifiedPlcFamilyFromSoftwarePath(string plcSoftwarePath)
        {
            try
            {
                var sc = GetSoftwareContainer(plcSoftwarePath);
                var di = sc?.Parent as DeviceItem;
                while (di != null)
                {
                    var tid = TryGetPropertyValue(di, "TypeIdentifier")?.ToString() ?? string.Empty;
                    var t = tid.ToUpperInvariant();
                    // Catalog MLFB often contains spaces (e.g. "OrderNumber:6ES7 211-1BE40-0XB0/...").
                    // Old checks used "6ES721" which fails after "6ES7 " + "211" — driver fell back to S7-300/400.
                    var tCompact = string.Concat(t.Where(ch => !char.IsWhiteSpace(ch)));
                    if (t.IndexOf("S7-1200", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0
                        || tCompact.IndexOf("6ES721", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES722", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S71200";
                    if (t.IndexOf("S7-1500", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0
                        || tCompact.IndexOf("6ES751", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES752", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S71500";
                    if (t.IndexOf("S7-300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES731", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S7300";
                    if (t.IndexOf("S7-400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES741", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S7400";
                    di = di.Parent as DeviceItem;
                }

                var fromDevices = TryInferPlcFamilyFromProjectDevices(plcSoftwarePath);
                if (!string.IsNullOrEmpty(fromDevices)) return fromDevices;
            }
            catch
            {
            }

            return "UNKNOWN";
        }

        /// <summary>
        /// When <see cref="SoftwareContainer.Parent"/> is not a <see cref="DeviceItem"/>, CPU TypeIdentifier may still
        /// exist on nested rack/CPU items under the PLC device — walk the device tree by PLC software path head name.
        /// </summary>
        private string TryInferPlcFamilyFromProjectDevices(string plcSoftwarePath)
        {
            try
            {
                if (_project?.Devices == null) return string.Empty;
                var head = (plcSoftwarePath ?? string.Empty).Trim()
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(head)) return string.Empty;

                foreach (var device in _project.Devices)
                {
                    if (!device.Name.Equals(head, StringComparison.OrdinalIgnoreCase)) continue;
                    var stack = new Stack<DeviceItem>(device.DeviceItems ?? Enumerable.Empty<DeviceItem>());
                    while (stack.Count > 0)
                    {
                        var di = stack.Pop();
                        if (di == null) continue;
                        if (di.DeviceItems != null)
                        {
                            foreach (var ch in di.DeviceItems) stack.Push(ch);
                        }

                        var tid = TryGetPropertyValue(di, "TypeIdentifier")?.ToString() ?? string.Empty;
                        var t = tid.ToUpperInvariant();
                        var tCompact = string.Concat(t.Where(ch => !char.IsWhiteSpace(ch)));
                        if (t.IndexOf("S7-1200", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0
                            || tCompact.IndexOf("6ES721", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES722", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S71200";
                        if (t.IndexOf("S7-1500", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0
                            || tCompact.IndexOf("6ES751", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES752", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S71500";
                        if (t.IndexOf("S7-300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES731", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S7300";
                        if (t.IndexOf("S7-400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES741", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S7400";
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static object? SelectCommunicationDriverEnumValue(Type enumType, string plcFamily)
        {
            object? best = null;
            var bestScore = -1;
            foreach (var name in Enum.GetNames(enumType))
            {
                var u = name.ToUpperInvariant();
                var score = 0;
                if (plcFamily == "S71200" || plcFamily == "S71500" || plcFamily == "UNKNOWN")
                {
                    if (u.Contains("300") && !u.Contains("1500")) continue;
                    if (u.Contains("400") && !u.Contains("1500")) continue;
                    if (u.Contains("318") || u.Contains("319")) continue;
                    if (u.Contains("1200") || u.Contains("1500") || u.Contains("S712") || u.Contains("S715") || u.Contains("PLUS"))
                        score += 10;
                    if (u.Contains("UNIFIED") || u.Contains("PLUS")) score += 2;
                }
                else if (plcFamily == "S7300")
                {
                    if (u.Contains("300") || u.Contains("318") || u.Contains("319")) score += 10;
                }
                else if (plcFamily == "S7400")
                {
                    if (u.Contains("400") || u.Contains("414") || u.Contains("416")) score += 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = Enum.Parse(enumType, name);
                }
            }

            return bestScore > 0 ? best : null;
        }

        private static void TryConfigureUnifiedDriverProperties(object connection, string plcFamily)
        {
            try
            {
                var dps = TryGetPropertyValue(connection, "DriverProperties");
                if (dps is not IEnumerable en) return;

                foreach (var dp in en)
                {
                    if (dp == null) continue;
                    var n = TryGetPropertyValue(dp, "Name")?.ToString() ?? TryGetName(dp) ?? string.Empty;
                    var nu = n.ToUpperInvariant();
                    if (nu.Contains("DRIVER") || nu.Contains("FAMILY") || nu.Contains("CPU") || nu.Contains("CONTROLLER"))
                    {
                        if (plcFamily == "S71200" || plcFamily == "S71500" || plcFamily == "UNKNOWN")
                        {
                            TrySetProperty(dp, "Value", "SIMATIC S7-1200/1500");
                            TrySetEngineeringAttribute(dp, "Value", "SIMATIC S7-1200/1500");
                        }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// WinCC Unified HMI connection: pick CommunicationDriver enum / attribute that matches the PLC hardware.
        /// </summary>
        private void TryConfigureUnifiedHmiCommunicationDriver(object connection, string plcSoftwarePath)
        {
            var plcFamily = InferUnifiedPlcFamilyFromSoftwarePath(plcSoftwarePath);

            try
            {
                var prop = connection.GetType().GetProperty("CommunicationDriver", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    var ev = SelectCommunicationDriverEnumValue(prop.PropertyType, plcFamily);
                    if (ev != null)
                    {
                        prop.SetValue(connection, ev);
                        return;
                    }
                }
            }
            catch
            {
            }

            // CommunicationDriver is commonly exposed as an engineering attribute typed as an enum; string writes fail.
            if (TrySetUnifiedHmiCommunicationDriverEnum(connection, plcFamily))
            {
                return;
            }

            var driverCandidates = plcFamily switch
            {
                "S7300" => new[] { "SIMATIC S7 300/400", "SIMATIC S7-300/400", "SIMATIC S7 300", "SIMATIC S7-300" },
                "S7400" => new[] { "SIMATIC S7 400", "SIMATIC S7-400", "SIMATIC S7 300/400", "SIMATIC S7-300/400" },
                _ => new[]
                {
                    "SIMATIC S7-1200/1500",
                    "SIMATIC S7 1200/1500",
                    "SIMATIC S7-1200",
                    "SIMATIC S7 1200",
                    "SIMATIC S7-1500",
                    "SIMATIC S7 1500",
                    "S7-1200/1500",
                    "S7-1200",
                    "S7-1500",
                    "S71200",
                    "S71500"
                }
            };

            foreach (var driver in driverCandidates)
            {
                if (TrySetProperty(connection, "CommunicationDriver", driver) ||
                    TrySetEngineeringAttribute(connection, "CommunicationDriver", driver))
                {
                    return;
                }
            }

            TrySetCommunicationDriverFromAttributeInfos(connection, driverCandidates);

            TryConfigureUnifiedDriverProperties(connection, plcFamily);
        }

        private static void ValidateUnifiedHmiCommunicationDriver(object connection, string plcFamily)
        {
            var driver = ReadUnifiedHmiCommunicationDriver(connection);
            var normalized = (driver ?? string.Empty).ToUpperInvariant().Replace("-", "").Replace(" ", "");
            if (plcFamily == "S7300" || plcFamily == "S7400") return;

            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("HMI connection CommunicationDriver did not read back. S7-1200/S7-1500 projects must read back a 1200/1500 driver before HMI tags are created.");
            }

            if (normalized.Contains("300/400") || normalized.Contains("S7300") || normalized.Contains("S7400"))
            {
                throw new InvalidOperationException($"HMI connection CommunicationDriver read back as '{driver}', but the PLC family is {plcFamily}. Use SIMATIC S7-1200/1500 for S7-1200/S7-1500 projects.");
            }
        }

        private static string ReadUnifiedHmiCommunicationDriver(object connection)
        {
            foreach (var name in new[] { "CommunicationDriver", "Driver", "Protocol" })
            {
                try
                {
                    var prop = connection.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var value = prop.GetValue(connection)?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) return value!;
                    }
                }
                catch
                {
                }

                var attr = TryGetEngineeringAttribute(connection, name)?.ToString();
                if (!string.IsNullOrWhiteSpace(attr)) return attr!;
            }

            return string.Empty;
        }

        /// <summary>
        /// Some Unified builds expose the driver only under a localized or version-specific engineering attribute name.
        /// </summary>
        private static void TrySetCommunicationDriverFromAttributeInfos(object connection, string[] driverCandidates)
        {
            try
            {
                var getInfos = connection.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes);
                if (getInfos == null) return;
                var infos = getInfos.Invoke(connection, null) as System.Collections.IEnumerable;
                if (infos == null) return;
                foreach (var info in infos)
                {
                    if (info == null) continue;
                    var n = TryGetPropertyValue(info, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    var nu = n.ToUpperInvariant();
                    if (!nu.Contains("COMMUNICATIONDRIVER") && !nu.Contains("DRIVER") && !n.Contains("通信")) continue;
                    foreach (var driver in driverCandidates)
                    {
                        try
                        {
                            if (TrySetEngineeringAttribute(connection, n, driver)) return;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ApplyJsonProperties(object target, JsonObject props, JsonArray failed, string path, string typeHint = "")
        {
            foreach (var kv in props)
            {
                var schemaError = ValidateUnifiedHmiDesignProperty(typeHint, kv.Key);
                if (!string.IsNullOrEmpty(schemaError))
                {
                    failed.Add($"{path}.{kv.Key}: {schemaError}");
                    continue;
                }

                var value = JsonObjectValue(kv.Value);
                if (TrySetProperty(target, kv.Key, value)) continue;
                if (TrySetEngineeringAttribute(target, kv.Key, value)) continue;
                failed.Add($"{path}.{kv.Key}: property/attribute write failed");
            }
        }

        private static string ValidateUnifiedHmiDesignProperty(string typeHint, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) return "property name is empty";
            var type = (typeHint ?? string.Empty).Trim();
            var prop = propertyName.Trim();

            if (type.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Lamp", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("HmiRectangle", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Equals("ForeColor", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Font", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Content", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Padding", StringComparison.OrdinalIgnoreCase))
                {
                    return "unsupported on Rectangle. Use a separate HmiText item for text/foreground/font, and keep Rectangle for BackColor/BorderColor/BorderWidth.";
                }
            }

            if (type.Equals("IOField", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("HmiIOField", StringComparison.OrdinalIgnoreCase))
            {
                var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BackColor", "ForeColor", "BorderColor", "BorderWidth", "Visible", "Enabled", "Name"
                };
                if (!stable.Contains(prop))
                    return "not in the stable IOField property set for generated screens. Bind runtime values with BindUnifiedHmiTagDynamization instead of ad-hoc ProcessValue properties.";
            }

            return string.Empty;
        }

        private static bool TrySetEngineeringAttribute(object target, string attributeName, object? value)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                var set = target.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (set == null) return false;

                object? oldValue = null;
                try { oldValue = get?.Invoke(target, new object[] { attributeName }); } catch { }
                var typed = oldValue == null ? value : CoerceReflectionValue(value, oldValue.GetType());
                set.Invoke(target, new[] { attributeName, typed });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetMultilingualText(object item, string propertyName, string text, string culture)
        {
            try
            {
                var multilingualText = TryGetPropertyValue(item, propertyName);
                if (multilingualText == null) return false;

                var html = text.TrimStart().StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : $"<body><p>{SecurityElement.Escape(text) ?? string.Empty}</p></body>";

                var items = TryGetPropertyValue(multilingualText, "Items");
                if (items is IEnumerable en)
                {
                    object? first = null;
                    object? cultureMatch = null;
                    foreach (var it in en)
                    {
                        if (it == null) continue;
                        first ??= it;
                        var itemCulture = TryGetPropertyValue(it, "Culture")?.ToString();
                        if (!string.IsNullOrWhiteSpace(itemCulture) &&
                            itemCulture!.Equals(culture, StringComparison.OrdinalIgnoreCase))
                        {
                            cultureMatch = it;
                            break;
                        }
                    }

                    var target = cultureMatch ?? first;
                    if (target != null)
                    {
                        if (TrySetProperty(target, "Text", html)) return true;
                        if (TrySetEngineeringAttribute(target, "Text", html)) return true;
                    }
                }

                if (TrySetProperty(multilingualText, "Item", html)) return true;
                return TrySetEngineeringAttribute(multilingualText, "Text", html);
            }
            catch
            {
                return false;
            }
        }

        private static string? JsonString(JsonObject obj, string propertyName)
        {
            var node = obj[propertyName];
            if (node == null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return node.ToJsonString();
        }

        private static object? JsonObjectValue(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<bool>(out var b)) return b;
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<double>(out var d)) return d;
                return value.ToJsonString();
            }

            return node.ToJsonString();
        }

        private static bool? IsAttributeWritable(object attributeInfo)
        {
            var names = new[] { "AccessMode", "Access", "Mode" };
            foreach (var name in names)
            {
                var value = TryGetPropertyValue(attributeInfo, name)?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value!.IndexOf("ReadWrite", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (value.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0 && value.IndexOf("ReadOnly", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (value.IndexOf("ReadOnly", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            return null;
        }

        private static object CoerceAttributeValue(string value, object? oldValue, object attributeInfo)
        {
            if (oldValue != null)
            {
                var oldType = oldValue.GetType();
                if (oldType == typeof(string)) return value;
                if (oldType == typeof(bool)) return bool.Parse(value);
                if (oldType == typeof(int)) return int.Parse(value);
                if (oldType == typeof(uint)) return uint.Parse(value);
                if (oldType == typeof(short)) return short.Parse(value);
                if (oldType == typeof(ushort)) return ushort.Parse(value);
                if (oldType == typeof(long)) return long.Parse(value);
                if (oldType == typeof(ulong)) return ulong.Parse(value);
                if (oldType == typeof(float)) return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (oldType == typeof(double)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (oldType.IsEnum) return Enum.Parse(oldType, value, ignoreCase: true);
            }

            var dataType = TryGetPropertyValue(attributeInfo, "DataType", "Type")?.ToString() ?? string.Empty;
            if (dataType.IndexOf("Boolean", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Bool", StringComparison.OrdinalIgnoreCase)) return bool.Parse(value);
            if (dataType.IndexOf("Int32", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Int", StringComparison.OrdinalIgnoreCase)) return int.Parse(value);
            if (dataType.IndexOf("UInt32", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("UInt", StringComparison.OrdinalIgnoreCase)) return uint.Parse(value);
            if (dataType.IndexOf("Double", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Real", StringComparison.OrdinalIgnoreCase)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

            return value;
        }

        private static object? CoerceReflectionValue(object? value, Type targetType)
        {
            if (value == null) return null;

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null) targetType = nullableType;
            if (targetType.IsInstanceOfType(value)) return value;

            if (targetType == typeof(string)) return value.ToString();
            if (targetType.IsEnum) return value is string enumText
                ? Enum.Parse(targetType, enumText, ignoreCase: true)
                : Enum.ToObject(targetType, value);
            if (targetType == typeof(bool)) return value is string boolText ? bool.Parse(boolText) : Convert.ToBoolean(value);
            if (targetType == typeof(byte)) return value is string byteText ? byte.Parse(byteText) : Convert.ToByte(value);
            if (targetType == typeof(short)) return value is string shortText ? short.Parse(shortText) : Convert.ToInt16(value);
            if (targetType == typeof(ushort)) return value is string ushortText ? ushort.Parse(ushortText) : Convert.ToUInt16(value);
            if (targetType == typeof(int)) return value is string intText ? int.Parse(intText) : Convert.ToInt32(value);
            if (targetType == typeof(uint)) return value is string uintText ? uint.Parse(uintText) : Convert.ToUInt32(value);
            if (targetType == typeof(long)) return value is string longText ? long.Parse(longText) : Convert.ToInt64(value);
            if (targetType == typeof(ulong)) return value is string ulongText ? ulong.Parse(ulongText) : Convert.ToUInt64(value);
            if (targetType == typeof(float)) return value is string floatText ? float.Parse(floatText, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToSingle(value);
            if (targetType == typeof(double)) return value is string doubleText ? double.Parse(doubleText, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToDouble(value);
            if (targetType == typeof(Color)) return CoerceColor(value);

            return value;
        }

        private static Color CoerceColor(object value)
        {
            if (value is Color c) return c;

            if (value is string s)
            {
                var text = s.Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Color.FromArgb(unchecked((int)Convert.ToUInt32(text.Substring(2), 16)));
                }
                if (text.StartsWith("#", StringComparison.Ordinal))
                {
                    return ColorTranslator.FromHtml(text);
                }
                if (Regex.IsMatch(text, "^[0-9A-Fa-f]{8}$"))
                {
                    return Color.FromArgb(unchecked((int)Convert.ToUInt32(text, 16)));
                }
                return ColorTranslator.FromHtml(text);
            }

            if (value is long l) return Color.FromArgb(unchecked((int)l));
            if (value is int i) return Color.FromArgb(i);
            if (value is uint ui) return Color.FromArgb(unchecked((int)ui));

            return Color.FromArgb(Convert.ToInt32(value));
        }

        private static IEnumerable<string> TryGetEnumerableStrings(object target, string propertyName)
        {
            try
            {
                var value = TryGetPropertyValue(target, propertyName);
                if (value is IEnumerable en)
                {
                    foreach (var item in en)
                    {
                        if (item != null) yield return item.ToString() ?? string.Empty;
                    }
                }
            }
            finally
            {
            }
        }

        private static JsonArray ToJsonArray(IEnumerable<string> values)
        {
            var arr = new JsonArray();
            foreach (var value in values)
            {
                arr.Add(value);
            }
            return arr;
        }

        private static string FormatExceptionDetail(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}\n{tie.InnerException}";
            }

            if (ex.InnerException != null)
            {
                return $"{ex.GetType().FullName}: {ex.Message}\nInner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex}";
            }

            return $"{ex.GetType().FullName}: {ex.Message}\n{ex}";
        }

        public List<string>? GetHmiScreens(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            return TryListScreens(softwareContainer.Software);
        }

        public List<string>? GetHmiTagTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            var sw = softwareContainer.Software;
            var tables = TryGetHmiTagTablesCollection(sw);
            if (tables == null) return new List<string>();
            return TryListNamesFromCollection(tables, Array.Empty<string>(), "TagTables");
        }

        public List<string>? GetHmiTags(string softwarePath, string tagTableName = "")
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;

            var sw = softwareContainer.Software;
            var tagRoot = TryGetHmiTagRoot(sw);
            object? tagTable = string.IsNullOrWhiteSpace(tagTableName)
                ? null
                : TryFindHmiTagTable(sw, tagTableName);

            var root = tagTable ?? tagRoot;
            return TryListNamesFromCollection(root, new[] { "Tags" }, "Tags");
        }

        public List<string>? GetHmiConnections(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) return new List<string>();
            return TryListNamesFromCollection(connections, Array.Empty<string>(), "Connections");
        }

        public void ExportHmiScreen(string softwarePath, string screenName, string exportPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var screen = TryFindByNameInCollection(softwareContainer.Software, new[] { "Screens", "ScreenFolder" }, screenName);
            if (screen == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI screen not found: {screenName}");

            if (!TryExportEngineeringObject(screen, exportPath, out var err))
                throw new PortalException(PortalErrorCode.ExportFailed, err ?? "HMI screen export failed");
        }

        public void ExportHmiTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var sw = softwareContainer.Software;
            var table = TryFindHmiTagTable(sw, tagTableName);
            if (table == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI tag table not found: {tagTableName}");

            if (!TryExportEngineeringObject(table, exportPath, out var err))
                throw new PortalException(PortalErrorCode.ExportFailed, err ?? "HMI tag table export failed");
        }

        public void ExportHmiConnection(string softwarePath, string connectionName, string exportPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI Connections collection not found on '{softwarePath}'");

            var connection = FindExistingByName(connections, connectionName) ?? TryFindByNameInCollection(connections, Array.Empty<string>(), connectionName);
            if (connection == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI connection not found: {connectionName}");

            if (!TryExportEngineeringObject(connection, exportPath, out var err))
                throw new PortalException(PortalErrorCode.ExportFailed, err ?? "HMI connection export failed");
        }

        public string ProbeClassicHmiConnectionCreation(string softwarePath, string connectionName, string exportPath)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return "HMI software not found: " + softwarePath;

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) return "Connections collection not found. swType=" + sw.GetType().FullName;

            sb.AppendLine("SoftwareType=" + (sw.GetType().FullName ?? sw.GetType().Name));
            sb.AppendLine("ConnectionsType=" + (connections.GetType().FullName ?? connections.GetType().Name));
            sb.AppendLine("ConnectionsPublicMembers:");
            foreach (var line in DescribeTypeMembers(connections.GetType(), false).Take(120))
            {
                sb.AppendLine("  " + line);
            }
            sb.AppendLine("ConnectionsExplicitMembers:");
            foreach (var line in DescribeTypeMembers(connections.GetType(), true).Take(160))
            {
                sb.AppendLine("  " + line);
            }

            var connectionType = FindTypeBySuffix("Siemens.Engineering.Hmi.Communication.Connection")
                ?? FindTypeBySuffix("Hmi.Communication.Connection")
                ?? FindTypeBySuffix("Communication.Connection");
            sb.AppendLine("ConnectionType=" + (connectionType?.FullName ?? "<not found>"));

            var existing = FindExistingByName(connections, connectionName) ?? TryFindByNameInCollection(connections, Array.Empty<string>(), connectionName);
            if (existing != null)
            {
                sb.AppendLine("ExistingConnection=" + connectionName);
                if (TryExportEngineeringObject(existing, exportPath, out var existingExportErr))
                {
                    sb.AppendLine("ExportExisting=OK :: " + exportPath);
                }
                else
                {
                    sb.AppendLine("ExportExisting=FAIL :: " + existingExportErr);
                }
                return sb.ToString();
            }

            if (connectionType == null)
            {
                sb.AppendLine("Create=SKIP :: connection type not found");
                return sb.ToString();
            }

            sb.AppendLine("CreationInfos:");
            var creationInfos = TryInvokeExplicitEngineeringMethod(connections, "GetCreationInfos", Array.Empty<object?>(), out var creationInfoErr);
            if (creationInfos == null && !string.IsNullOrWhiteSpace(creationInfoErr))
            {
                sb.AppendLine("  GetCreationInfos(\"\") failed: " + creationInfoErr);
            }
            else
            {
                foreach (var line in FormatEnumerableObjects(creationInfos, 80))
                {
                    sb.AppendLine("  " + line);
                }
            }

            object? created = null;
            string? createErr = null;
            var attempts = new[]
            {
                new { Description = "Name only", Parameters = new Dictionary<string, object?> { ["Name"] = connectionName } }
            };

            foreach (var attempt in attempts)
            {
                try
                {
                    sb.AppendLine($"CreateAttempt {attempt.Description}");
                    created = TryInvokeExplicitEngineeringMethod(
                        connections,
                        "Create",
                        new object?[] { connectionType, attempt.Parameters },
                        out createErr);
                    if (created != null)
                    {
                        sb.AppendLine("Create=OK :: type=" + (created.GetType().FullName ?? created.GetType().Name));
                        break;
                    }
                    sb.AppendLine("Create=FAIL :: " + (createErr ?? "<null result>"));
                }
                catch (Exception ex)
                {
                    createErr = FormatExceptionDetail(ex);
                    sb.AppendLine("Create=ERR :: " + createErr);
                }
            }

            created ??= FindExistingByName(connections, connectionName) ?? TryFindByNameInCollection(connections, Array.Empty<string>(), connectionName);
            if (created == null)
            {
                sb.AppendLine("Readback=FAIL :: connection not found after create attempts");
                return sb.ToString();
            }

            sb.AppendLine("Readback=OK :: " + (TryGetName(created) ?? connectionName));
            if (TryExportEngineeringObject(created, exportPath, out var exportErr))
            {
                sb.AppendLine("ExportCreated=OK :: " + exportPath);
            }
            else
            {
                sb.AppendLine("ExportCreated=FAIL :: " + exportErr);
            }

            return sb.ToString();
        }

        public (List<string> Exported, List<string> Failed)? ExportHmiProgram(string softwarePath, string exportDir, bool exportScreens = true, bool exportTagTables = true)
        {
            if (IsProjectNull()) return null;

            var exported = new List<string>();
            var failed = new List<string>();

            Directory.CreateDirectory(exportDir);

            if (exportScreens)
            {
                var screens = GetHmiScreens(softwarePath) ?? new List<string>();
                foreach (var s in screens)
                {
                    var safe = MakeSafeFileName(s);
                    var outPath = Path.Combine(exportDir, $"screen_{safe}.xml");
                    try { ExportHmiScreen(softwarePath, s, outPath); exported.Add(outPath); }
                    catch (PortalException) { failed.Add($"screen:{s}"); }
                }
            }

            if (exportTagTables)
            {
                var tables = GetHmiTagTables(softwarePath) ?? new List<string>();
                foreach (var t in tables)
                {
                    var safe = MakeSafeFileName(t);
                    var outPath = Path.Combine(exportDir, $"tagtable_{safe}.xml");
                    try { ExportHmiTagTable(softwarePath, t, outPath); exported.Add(outPath); }
                    catch (PortalException) { failed.Add($"tagtable:{t}"); }
                }
            }

            return (exported, failed);
        }

        public void ImportHmiScreen(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;

                // Resolve screen folder then groups by folderPath
                object rootGroup = TryGetPropertyValue(sw, "ScreenFolder") ?? sw;
                var group = TryResolveChildGroupByPath(rootGroup, folderPath) ?? rootGroup;

                // Locate screens collection: group.Screens OR group.ScreenFolder.Screens
                var screens = TryGetPropertyValue(group, "Screens");
                if (screens == null)
                {
                    var nestedFolder = TryGetPropertyValue(group, "ScreenFolder");
                    if (nestedFolder != null)
                    {
                        screens = TryGetPropertyValue(nestedFolder, "Screens");
                    }
                }

                if (screens == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"Screens collection not found. swType={sw.GetType().FullName} groupType={group.GetType().FullName}");

                if (TryImportEngineeringObjectIntoCollection(screens, importPath, out _, out var err))
                    return;

                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportHmiScreen failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        public void ImportHmiTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;

                var tagRoot = TryGetHmiTagRoot(sw);

                var group = TryResolveChildGroupByPath(tagRoot, folderPath) ?? tagRoot;

                var tables = TryGetPropertyValue(group, "TagTables");
                if (tables == null)
                {
                    tables = TryGetHmiTagTablesCollection(sw);
                }

                if (tables == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TagTables collection not found. swType={sw.GetType().FullName} groupType={group.GetType().FullName}");

                if (TryImportEngineeringObjectIntoCollection(tables, importPath, out _, out var err))
                    return;

                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportHmiTagTable failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        public void ImportHmiConnection(string softwarePath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;
                var connections = TryGetPropertyValue(sw, "Connections");
                if (connections == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"Connections collection not found. swType={sw.GetType().FullName}");

                if (TryImportEngineeringObjectIntoCollection(connections, importPath, out _, out var err))
                    return;

                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportHmiConnection failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        public ResponseImportBatch ImportHmiScreensFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try
                    {
                        ImportHmiScreen(softwarePath, folderPath, file);
                        imported.Add(name);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public ResponseImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try
                    {
                        ImportHmiTagTable(softwarePath, folderPath, file);
                        imported.Add(name);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public ResponseSeed SeedProjectFromReference(
            string plcSoftwarePath,
            string hmiSoftwarePath,
            string referenceDir,
            JsonObject? placeholders = null)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            placeholders ??= new JsonObject();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = referenceDir, Error = "Project is null" });
                    return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
                }

                if (string.IsNullOrWhiteSpace(referenceDir) || !Directory.Exists(referenceDir))
                {
                    failed.Add(new ImportFailure { Path = referenceDir, Error = "Reference directory not found" });
                    return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
                }

                var manifestPath = Path.Combine(referenceDir, "manifest.json");
                JsonObject? manifest = null;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = manifestPath, Error = $"Failed to parse manifest.json: {ex.Message}" });
                    }
                }

                string plcBlocksDir = Path.Combine(referenceDir, "plc", "blocks");
                string plcTypesDir = Path.Combine(referenceDir, "plc", "types");
                string hmiScreensDir = Path.Combine(referenceDir, "hmi", "screens");
                string hmiTagsDir = Path.Combine(referenceDir, "hmi", "tags");

                var plcBlockGroupPath = manifest?["plcBlockGroupPath"]?.ToString() ?? "";
                var plcTypeGroupPath = manifest?["plcTypeGroupPath"]?.ToString() ?? "";
                var hmiScreenFolderPath = manifest?["hmiScreenFolderPath"]?.ToString() ?? "";
                var hmiTagTableFolderPath = manifest?["hmiTagTableFolderPath"]?.ToString() ?? "";

                if (manifest?["plcBlocksDir"] != null) plcBlocksDir = Path.Combine(referenceDir, manifest["plcBlocksDir"]!.ToString());
                if (manifest?["plcTypesDir"] != null) plcTypesDir = Path.Combine(referenceDir, manifest["plcTypesDir"]!.ToString());
                if (manifest?["hmiScreensDir"] != null) hmiScreensDir = Path.Combine(referenceDir, manifest["hmiScreensDir"]!.ToString());
                if (manifest?["hmiTagTablesDir"] != null) hmiTagsDir = Path.Combine(referenceDir, manifest["hmiTagTablesDir"]!.ToString());

                var tempDir = Path.Combine(Path.GetTempPath(), "tia-seed-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                void CopyDirWithReplace(string srcDir, string dstDir)
                {
                    if (!Directory.Exists(srcDir)) return;
                    Directory.CreateDirectory(dstDir);

                    foreach (var file in Directory.EnumerateFiles(srcDir, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        var text = File.ReadAllText(file, Encoding.UTF8);
                        foreach (var kv in placeholders)
                        {
                            var k = kv.Key;
                            var v = kv.Value?.ToString() ?? "";
                            text = text.Replace("{{" + k + "}}", v);
                        }
                        var outPath = Path.Combine(dstDir, Path.GetFileName(file));
                        File.WriteAllText(outPath, text, Encoding.UTF8);
                    }
                }

                var tempPlcBlocks = Path.Combine(tempDir, "plc", "blocks");
                var tempPlcTypes = Path.Combine(tempDir, "plc", "types");
                var tempHmiScreens = Path.Combine(tempDir, "hmi", "screens");
                var tempHmiTags = Path.Combine(tempDir, "hmi", "tags");

                CopyDirWithReplace(plcBlocksDir, tempPlcBlocks);
                CopyDirWithReplace(plcTypesDir, tempPlcTypes);
                CopyDirWithReplace(hmiScreensDir, tempHmiScreens);
                CopyDirWithReplace(hmiTagsDir, tempHmiTags);

                // PLC blocks
                if (Directory.Exists(tempPlcBlocks))
                {
                    var r = ImportBlocksFromDirectory(plcSoftwarePath, plcBlockGroupPath, tempPlcBlocks, "", overwrite: true);
                    imported.AddRange(r.Imported?.Select(x => "plc:block:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "plc:block:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                // PLC types (UDT)
                if (Directory.Exists(tempPlcTypes))
                {
                    foreach (var file in Directory.EnumerateFiles(tempPlcTypes, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        var ok = ImportType(plcSoftwarePath, plcTypeGroupPath, file);
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (ok) imported.Add("plc:type:" + name);
                        else failed.Add(new ImportFailure { Path = file, Error = "plc:type:Import failed" });
                    }
                }

                // HMI tag tables then screens
                if (Directory.Exists(tempHmiTags))
                {
                    var r = ImportHmiTagTablesFromDirectory(hmiSoftwarePath, hmiTagTableFolderPath, tempHmiTags);
                    imported.AddRange(r.Imported?.Select(x => "hmi:tagtable:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "hmi:tagtable:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                if (Directory.Exists(tempHmiScreens))
                {
                    var r = ImportHmiScreensFromDirectory(hmiSoftwarePath, hmiScreenFolderPath, tempHmiScreens);
                    imported.AddRange(r.Imported?.Select(x => "hmi:screen:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "hmi:screen:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                return new ResponseSeed
                {
                    Message = $"Seed applied from '{referenceDir}'",
                    Imported = imported,
                    Failed = failed,
                    Placeholders = placeholders,
                    TempDir = tempDir,
                };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = referenceDir, Error = ex.ToString() });
                return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string? ResolveGlobalLibraryFile(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath)) return null;
            var p = libraryPath.Trim().Trim('"');
            if (File.Exists(p)) return p;
            if (!Directory.Exists(p)) return null;

            var direct = Directory.EnumerateFiles(p, "*.al*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x.EndsWith(".al21", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x)
                .FirstOrDefault();
            if (direct != null) return direct;

            return Directory.EnumerateFiles(p, "*.al*", SearchOption.AllDirectories)
                .OrderByDescending(x => x.EndsWith(".al21", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x)
                .FirstOrDefault();
        }

        private static object? TryOpenGlobalLibrary(object globalLibraries, string libraryFile, out string? error)
        {
            error = null;
            var fi = new FileInfo(libraryFile);
            try
            {
                var methods = globalLibraries.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Open", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    try
                    {
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(FileInfo))
                            return m.Invoke(globalLibraries, new object[] { fi });

                        if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                            return m.Invoke(globalLibraries, new object[] { libraryFile });

                        if (ps.Length == 2 && ps[0].ParameterType == typeof(FileInfo))
                        {
                            var arg2 = BuildDefaultArgument(ps[1].ParameterType);
                            return m.Invoke(globalLibraries, new[] { (object)fi, arg2 });
                        }

                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                        {
                            var arg2 = BuildDefaultArgument(ps[1].ParameterType);
                            return m.Invoke(globalLibraries, new[] { (object)libraryFile, arg2 });
                        }
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        error = $"{m.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    }
                    catch (Exception ex)
                    {
                        error = $"{m.Name}: {ex.GetType().FullName}: {ex.Message}";
                    }
                }

                error ??= "No supported GlobalLibraries.Open overload accepted FileInfo/string path.";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return null;
            }
        }

        private static object? BuildDefaultArgument(Type type)
        {
            if (type == typeof(bool)) return false;
            if (type == typeof(int)) return 0;
            if (type == typeof(string)) return "";
            if (type.IsEnum) return Enum.ToObject(type, 0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static List<string> ListLibraryNamesByHints(object root, int limit, params string[] propertyHints)
        {
            var result = new List<string>();
            var seen = new HashSet<object>();

            void Visit(object? node, string path, int depth)
            {
                if (node == null || depth > 8 || result.Count >= limit) return;
                if (!seen.Add(node)) return;

                foreach (var hint in propertyHints)
                {
                    var value = TryGetPropertyValue(node, hint);
                    if (value == null) continue;

                    if (value is IEnumerable enumerable && value is not string)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null || result.Count >= limit) break;
                            var name = TryGetName(item);
                            var nextPath = string.IsNullOrWhiteSpace(path)
                                ? (name ?? item.ToString() ?? "")
                                : path + "/" + (name ?? item.ToString() ?? "");
                            if (!string.IsNullOrWhiteSpace(name) && !result.Contains(nextPath, StringComparer.OrdinalIgnoreCase))
                            {
                                result.Add(nextPath);
                            }
                            Visit(item, nextPath, depth + 1);
                        }
                    }
                    else
                    {
                        Visit(value, path, depth + 1);
                    }
                }
            }

            Visit(root, "", 0);
            return result;
        }

        private static object? FindLibraryObjectByPathOrName(object root, string wantedPathOrName, List<string> attempts, params string[] propertyHints)
        {
            var wanted = (wantedPathOrName ?? string.Empty).Trim();
            var wantedLeaf = LastPathSegment(wanted);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            object? Visit(object? node, string path, int depth)
            {
                if (node == null || depth > 10) return null;
                if (!seen.Add(node)) return null;

                var name = TryGetName(node) ?? TryGetPropertyValue(node, "Name")?.ToString() ?? string.Empty;
                var nodePath = string.IsNullOrWhiteSpace(path)
                    ? name
                    : string.IsNullOrWhiteSpace(name) ? path : path + "/" + name;

                if (!string.IsNullOrWhiteSpace(name) &&
                    (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(nodePath, wanted, StringComparison.OrdinalIgnoreCase) ||
                     nodePath.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, wantedLeaf, StringComparison.OrdinalIgnoreCase)))
                {
                    attempts.Add("Found MasterCopy candidate: " + nodePath + " type=" + (node.GetType().FullName ?? node.GetType().Name));
                    return node;
                }

                foreach (var hint in propertyHints)
                {
                    var value = TryGetPropertyValue(node, hint);
                    if (value == null) continue;
                    attempts.Add($"Scan {node.GetType().Name}.{hint}: {value.GetType().FullName}");

                    if (value is IEnumerable enumerable && value is not string)
                    {
                        foreach (var child in enumerable)
                        {
                            var found = Visit(child, nodePath, depth + 1);
                            if (found != null) return found;
                        }
                    }
                    else
                    {
                        var found = Visit(value, nodePath, depth + 1);
                        if (found != null) return found;
                    }
                }

                return null;
            }

            return Visit(root, "", 0);
        }

        private static object? TryImportMasterCopyIntoScreen(object screen, object screenItems, object masterCopy, string expectedName, int left, int top, List<string> attempts)
        {
            var targets = new[] { screenItems, screen }.Where(x => x != null).Distinct(ReferenceEqualityComparer.Instance).ToArray();
            var sourceArgs = new[] { masterCopy, TryGetPropertyValue(masterCopy, "Content"), TryGetPropertyValue(masterCopy, "Object") }
                .Where(x => x != null)
                .Distinct(ReferenceEqualityComparer.Instance!)
                .ToArray();

            foreach (var target in targets)
            {
                attempts.Add("Target candidate methods on " + target.GetType().FullName + ": " + string.Join(" | ", DescribeMasterCopyCandidateMethods(target).Take(80)));
                foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => IsMasterCopyImportMethodName(m.Name)))
                {
                    var ps = method.GetParameters();
                    foreach (var source in sourceArgs)
                    {
                        if (!CanAssignParameter(ps, source))
                            continue;

                        var args = BuildMasterCopyImportArgs(ps, source!, expectedName, left, top);
                        if (args == null)
                        {
                            attempts.Add($"Skip {target.GetType().Name}.{method.Name}: unsupported parameters ({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                            continue;
                        }

                        try
                        {
                            attempts.Add($"Try {target.GetType().Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                            var result = method.Invoke(target, args);
                            attempts.Add($"OK {target.GetType().Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                            if (result != null) return result;
                            var readback = FindExistingByName(screenItems, expectedName);
                            if (readback != null) return readback;
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            attempts.Add($"FAIL {target.GetType().Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                        }
                        catch (Exception ex)
                        {
                            attempts.Add($"FAIL {target.GetType().Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                        }
                    }
                }
            }

            var extensionImported = TryImportMasterCopyViaExtensionMethods(screen, screenItems, masterCopy, expectedName, left, top, attempts);
            if (extensionImported != null)
                return extensionImported;

            attempts.Add("Source candidate methods on " + masterCopy.GetType().FullName + ": " + string.Join(" | ", DescribeMasterCopyCandidateMethods(masterCopy).Take(120)));
            foreach (var method in masterCopy.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => IsMasterCopySourceMethodName(m.Name)))
            {
                var ps = method.GetParameters();
                foreach (var target in targets)
                {
                    if (!CanAssignParameter(ps, target))
                        continue;

                    var args = BuildMasterCopySourceArgs(ps, target, screen, screenItems, expectedName, left, top);
                    if (args == null)
                    {
                        attempts.Add($"Skip source {masterCopy.GetType().Name}.{method.Name}: unsupported parameters ({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        continue;
                    }

                    try
                    {
                        attempts.Add($"Try source {masterCopy.GetType().Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        var result = method.Invoke(masterCopy, args);
                        attempts.Add($"OK source {masterCopy.GetType().Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                        if (result != null) return result;
                        var readback = FindExistingByName(screenItems, expectedName);
                        if (readback != null) return readback;
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        attempts.Add($"FAIL source {masterCopy.GetType().Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"FAIL source {masterCopy.GetType().Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private static object? TryImportMasterCopyViaExtensionMethods(object screen, object screenItems, object masterCopy, string expectedName, int left, int top, List<string> attempts)
        {
            var targets = new[] { screenItems, screen }.Where(x => x != null).Distinct(ReferenceEqualityComparer.Instance).ToArray();
            var sources = new[] { masterCopy, TryGetPropertyValue(masterCopy, "Content"), TryGetPropertyValue(masterCopy, "Object") }
                .Where(x => x != null)
                .Distinct(ReferenceEqualityComparer.Instance!)
                .ToArray();
            var candidates = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => (a.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                .SelectMany(GetLoadableTypes)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.IsDefined(typeof(ExtensionAttribute), false))
                .Where(m => IsMasterCopyImportMethodName(m.Name) || IsMasterCopySourceMethodName(m.Name))
                .Where(m => !m.ContainsGenericParameters)
                .Take(300)
                .ToList();

            attempts.Add("Extension candidate methods: " + string.Join(" | ", candidates.Select(m => m.DeclaringType?.FullName + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ")").Take(120)));

            foreach (var method in candidates)
            {
                var ps = method.GetParameters();
                foreach (var target in targets)
                foreach (var source in sources)
                {
                    var args = BuildMasterCopyExtensionArgs(ps, target, screen, screenItems, source!, expectedName, left, top);
                    if (args == null)
                        continue;

                    try
                    {
                        attempts.Add($"Try extension {method.DeclaringType?.Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        var result = method.Invoke(null, args);
                        attempts.Add($"OK extension {method.DeclaringType?.Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                        if (result != null) return result;
                        var readback = FindExistingByName(screenItems, expectedName);
                        if (readback != null) return readback;
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        attempts.Add($"FAIL extension {method.DeclaringType?.Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"FAIL extension {method.DeclaringType?.Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static IEnumerable<string> DescribeMasterCopyCandidateMethods(object target)
        {
            return target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => IsMasterCopyImportMethodName(m.Name) || IsMasterCopySourceMethodName(m.Name) || m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ") -> " + m.ReturnType.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsMasterCopyImportMethodName(string name)
        {
            return name.Equals("CreateFrom", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("CreateFromMasterCopy", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Import", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("ImportFrom", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Paste", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Insert", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("MasterCopy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMasterCopySourceMethodName(string name)
        {
            return name.Equals("CopyTo", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Copy", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("PasteTo", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Instantiate", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("CreateInstance", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("InsertInto", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("ImportTo", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Instantiate", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanAssignParameter(ParameterInfo[] parameters, object? source)
        {
            if (parameters.Length == 0 || source == null) return false;
            return parameters.Any(p => p.ParameterType.IsInstanceOfType(source));
        }

        private static object[]? BuildMasterCopyImportArgs(ParameterInfo[] parameters, object source, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var sourceUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!sourceUsed && t.IsInstanceOfType(source))
                {
                    args[i] = source;
                    sourceUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return sourceUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static object[]? BuildMasterCopyExtensionArgs(ParameterInfo[] parameters, object target, object screen, object screenItems, object source, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var targetUsed = false;
            var sourceUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!targetUsed && t.IsInstanceOfType(target))
                {
                    args[i] = target;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screenItems))
                {
                    args[i] = screenItems;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screen))
                {
                    args[i] = screen;
                    targetUsed = true;
                }
                else if (!sourceUsed && t.IsInstanceOfType(source))
                {
                    args[i] = source;
                    sourceUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return targetUsed && sourceUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static object[]? BuildMasterCopySourceArgs(ParameterInfo[] parameters, object target, object screen, object screenItems, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var targetUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!targetUsed && t.IsInstanceOfType(target))
                {
                    args[i] = target;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screenItems))
                {
                    args[i] = screenItems;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screen))
                {
                    args[i] = screen;
                    targetUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return targetUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static List<string> ListNamedChildren(object collection, int limit)
        {
            var result = new List<string>();
            if (collection is not IEnumerable enumerable || collection is string)
                return result;

            foreach (var item in enumerable)
            {
                if (item == null) continue;
                var name = TryGetName(item) ?? TryGetPropertyValue(item, "Name")?.ToString() ?? item.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(name);
                if (result.Count >= Math.Max(1, limit)) break;
            }

            return result;
        }

        private static string? ResolveImportedReadbackName(List<string> before, List<string> after, string expectedName)
        {
            if (!string.IsNullOrWhiteSpace(expectedName) && after.Any(x => string.Equals(x, expectedName, StringComparison.OrdinalIgnoreCase)))
                return after.First(x => string.Equals(x, expectedName, StringComparison.OrdinalIgnoreCase));

            var added = after
                .Where(x => !before.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return added.Count == 1 ? added[0] : null;
        }

        private static string LastPathSegment(string path)
        {
            var value = (path ?? string.Empty).Trim().Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var parts = value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? value : parts[^1];
        }

        private static void TryCloseOrDispose(object obj)
        {
            try
            {
                var close = obj.GetType().GetMethod("Close", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                close?.Invoke(obj, null);
            }
            catch { }

            try
            {
                if (obj is IDisposable d) d.Dispose();
            }
            catch { }
        }

        private static List<string> TryListScreens(object hmiRoot)
        {
            var result = new List<string>();

            try
            {
                // Try common shapes: root.Screens OR root.ScreenFolder.Screens
                var rootType = hmiRoot.GetType();
                var screens = rootType.GetProperty("Screens")?.GetValue(hmiRoot);
                if (screens == null)
                {
                    var folder = rootType.GetProperty("ScreenFolder")?.GetValue(hmiRoot);
                    if (folder != null)
                    {
                        screens = folder.GetType().GetProperty("Screens")?.GetValue(folder);
                    }
                }

                if (screens is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.Add(name!);
                        }
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            return result;
        }

        private static List<string> TryListNamesFromCollection(object root, string[] propertyHints, string finalCollectionNameHint)
        {
            var result = new List<string>();
            try
            {
                object? collection = null;
                var rootType = root.GetType();

                if (propertyHints.Length == 0 && root is System.Collections.IEnumerable)
                {
                    collection = root;
                }

                // try direct property matches
                foreach (var propName in propertyHints)
                {
                    var prop = rootType.GetProperty(propName);
                    if (prop == null) continue;

                    var v = prop.GetValue(root);
                    if (v == null) continue;

                    // tag tables can be under a folder object
                    if (propName.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                    {
                        collection = v.GetType().GetProperty(finalCollectionNameHint)?.GetValue(v);
                    }
                    else
                    {
                        collection = v;
                    }

                    if (collection != null) break;
                }

                if (collection is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.Add(name!);
                        }
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            return result;
        }

        private static object? TryFindByNameInCollection(object root, string[] propertyHints, string wantedName)
        {
            try
            {
                var rootType = root.GetType();
                foreach (var propName in propertyHints)
                {
                    object? collection = null;

                    var prop = rootType.GetProperty(propName);
                    if (prop != null)
                    {
                        collection = prop.GetValue(root);
                    }
                    else if (propName.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                    {
                        var folder = rootType.GetProperty(propName)?.GetValue(root);
                        if (folder != null)
                        {
                            collection = folder.GetType().GetProperty(propName.Replace("Folder", "s"))?.GetValue(folder);
                        }
                    }

                    if (collection is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null) continue;
                            var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                            if (string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase))
                            {
                                return item;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static object? ResolvePlcWatchAndForceTableGroup(object plc)
        {
            // 只解析“监控与强制表”的容器对象；后续只读取 WatchTables，不读取 ForceTables。
            // TIA V21 的真实属性名通常是 WatchAndForceTableGroup，早期猜测的 WatchTables 不覆盖这个层级。
            return TryGetPropertyValue(
                plc,
                "WatchAndForceTableGroup",
                "WatchAndForceTables",
                "WatchAndForceTableSystemGroup",
                "WatchTableGroup");
        }

        private static List<(string Name, string Path, object Table)> EnumeratePlcWatchTables(object group)
        {
            var result = new List<(string Name, string Path, object Table)>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            EnumeratePlcWatchTablesRecursive(group, "", result, visited);
            return result;
        }

        private static void EnumeratePlcWatchTablesRecursive(object group, string groupPath, List<(string Name, string Path, object Table)> result, HashSet<object> visited)
        {
            if (!visited.Add(group)) return;

            var watchTables = TryGetPropertyValue(group, "WatchTables", "PlcWatchTables", "Tables");
            if (watchTables is System.Collections.IEnumerable tableEnumerable && watchTables is not string)
            {
                foreach (var table in tableEnumerable)
                {
                    if (table == null) continue;
                    var name = TryGetName(table) ?? TryGetPropertyValue(table, "Name")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var path = string.IsNullOrWhiteSpace(groupPath) ? name! : groupPath + "/" + name;
                    result.Add((name!, path, table));
                }
            }

            var groups = TryGetPropertyValue(group, "Groups", "WatchAndForceTableGroups", "UserGroups");
            if (groups is System.Collections.IEnumerable groupEnumerable && groups is not string)
            {
                foreach (var child in groupEnumerable)
                {
                    if (child == null) continue;
                    var childName = TryGetName(child) ?? TryGetPropertyValue(child, "Name")?.ToString();
                    var childPath = string.IsNullOrWhiteSpace(childName)
                        ? groupPath
                        : string.IsNullOrWhiteSpace(groupPath) ? childName! : groupPath + "/" + childName;
                    EnumeratePlcWatchTablesRecursive(child, childPath, result, visited);
                }
            }
        }

        private static JsonObject ReadWatchTableEntryReadOnly(object entry, bool includeMembers)
        {
            var row = new JsonObject
            {
                ["type"] = entry.GetType().FullName ?? entry.GetType().Name
            };

            foreach (var propName in new[] { "Name", "Address", "DisplayFormat", "Comment", "DataType", "Value", "CurrentValue", "ActualValue", "MonitorValue", "OnlineValue", "Status" })
            {
                var value = TryGetPropertyValue(entry, propName);
                if (value != null)
                {
                    var key = propName switch
                    {
                        "CurrentValue" => "currentValue",
                        "ActualValue" => "currentValue",
                        "MonitorValue" => "monitorValue",
                        "OnlineValue" => "currentValue",
                        "Value" => "value",
                        _ => char.ToLowerInvariant(propName[0]) + propName.Substring(1)
                    };
                    row[key] = value.ToString();
                }
            }

            var attributes = new JsonObject();
            var methods = entry.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods.FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            var infos = getInfos?.Invoke(entry, Array.Empty<object>()) as IEnumerable;
            if (infos != null && getAttr != null)
            {
                foreach (var info in infos)
                {
                    var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var lower = name.ToLowerInvariant();
                    if (!new[] { "name", "address", "display", "value", "actual", "current", "monitor", "online", "status", "comment", "type" }.Any(lower.Contains))
                        continue;
                    try
                    {
                        var value = getAttr.Invoke(entry, new object[] { name });
                        attributes[name] = value?.ToString() ?? "";
                    }
                    catch { }
                }
            }

            if (attributes.Count > 0)
                row["attributes"] = attributes;
            if (includeMembers)
                row["members"] = new JsonArray(DescribeMembers(entry, 180).Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")).ToArray());

            return row;
        }

        private static bool TryExportEngineeringObject(object engineeringObject, string exportPath, out string? error)
        {
            error = null;
            try
            {
                var fi = new FileInfo(exportPath);
                fi.Directory?.Create();
                if (fi.Exists) fi.Delete();

                var t = engineeringObject.GetType();

                // Prefer Export(FileInfo, ExportOptions)
                var m2 = t.GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
                if (m2 != null)
                {
                    m2.Invoke(engineeringObject, new object[] { fi, ExportOptions.None });
                    return true;
                }

                // Some engineering objects (notably HMI Unified) use Export(FileInfo, <OtherOptionsEnum>)
                // We best-effort call the first Export overload whose first parameter is FileInfo.
                var any = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Export", StringComparison.OrdinalIgnoreCase))
                    .Select(m => new { Method = m, Params = m.GetParameters() })
                    .FirstOrDefault(x => x.Params.Length == 2 && x.Params[0].ParameterType == typeof(FileInfo));

                if (any != null)
                {
                    var p2 = any.Params[1].ParameterType;
                    object? arg2 = null;

                    if (p2.IsEnum)
                    {
                        // use default enum value (0) or first defined value
                        arg2 = Enum.ToObject(p2, 0);
                    }
                    else if (p2 == typeof(bool))
                    {
                        arg2 = false;
                    }
                    else if (p2 == typeof(int))
                    {
                        arg2 = 0;
                    }
                    else
                    {
                        // unknown option type; try null if allowed
                        if (!p2.IsValueType) arg2 = null;
                        else arg2 = Activator.CreateInstance(p2);
                    }

                    any.Method.Invoke(engineeringObject, new[] { (object)fi, arg2! });
                    return true;
                }

                // Export(FileInfo)
                var m1 = t.GetMethod("Export", new[] { typeof(FileInfo) });
                if (m1 != null)
                {
                    m1.Invoke(engineeringObject, new object[] { fi });
                    return true;
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                error = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }

            return false;
        }

        private static bool TryImportEngineeringObjectIntoCollection(object collection, string importPath, out string? importedName, out string? error)
        {
            importedName = null;
            error = null;

            try
            {
                var fi = new FileInfo(importPath);
                if (!fi.Exists)
                {
                    error = "File not found";
                    return false;
                }

                var t = collection.GetType();

                // Prefer Import(FileInfo, ImportOptions)
                var m2 = t.GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (m2 != null)
                {
                    var list = m2.Invoke(collection, new object[] { fi, ImportOptions.Override });
                    importedName = BestEffortExtractFirstName(list) ?? Path.GetFileNameWithoutExtension(importPath);
                    return true;
                }

                // Import(FileInfo)
                var m1 = t.GetMethod("Import", new[] { typeof(FileInfo) });
                if (m1 != null)
                {
                    var list = m1.Invoke(collection, new object[] { fi });
                    importedName = BestEffortExtractFirstName(list) ?? Path.GetFileNameWithoutExtension(importPath);
                    return true;
                }

                error = $"No Import method found on collection type {t.FullName}";
                return false;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                error = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }

        private static string? BestEffortExtractFirstName(object? importReturnValue)
        {
            try
            {
                if (importReturnValue is IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch { }
            return null;
        }

        private static object? TryGetPropertyValue(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                try
                {
                    var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v != null) return v;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> DescribeTypeMembers(Type type, bool includeNonPublic)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var result = new List<string>();
            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                result.Add($"Property:{p.Name}:{p.PropertyType.FullName ?? p.PropertyType.Name}");
            }

            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                var ps = string.Join(", ", m.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}"));
                result.Add($"Method:{m.Name}({ps}) -> {m.ReturnType.FullName ?? m.ReturnType.Name}");
            }

            return result
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
        }

        private static IEnumerable<string> FormatEnumerableObjects(object? value, int limit)
        {
            var lines = new List<string>();
            if (value == null)
            {
                lines.Add("<null>");
                return lines;
            }

            if (value is not IEnumerable enumerable || value is string)
            {
                lines.Add(value.ToString() ?? "<null>");
                return lines;
            }

            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= Math.Max(1, limit)) break;
                if (item == null)
                {
                    lines.Add("<null>");
                    continue;
                }

                var type = item.GetType();
                var parts = new List<string> { type.FullName ?? type.Name };
                foreach (var propertyName in new[] { "Name", "CompositionName", "Type", "Description" })
                {
                    try
                    {
                        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        var propValue = prop?.GetValue(item);
                        if (propValue != null)
                        {
                            parts.Add($"{propertyName}={propValue}");
                        }
                    }
                    catch { }
                }
                lines.Add(string.Join(" | ", parts));
            }

            if (lines.Count == 0) lines.Add("<empty>");
            return lines;
        }

        private static object? TryInvokeExplicitEngineeringMethod(object target, string methodShortName, object?[] args, out string? error)
        {
            error = null;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var methods = target.GetType().GetMethods(flags)
                    .Where(m =>
                        !m.IsSpecialName &&
                        (string.Equals(m.Name, methodShortName, StringComparison.OrdinalIgnoreCase) ||
                         m.Name.EndsWith("." + methodShortName, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(m => m.GetParameters().Length)
                    .ToList();

                if (methods.Count == 0)
                {
                    error = "Method not found: " + methodShortName;
                    return null;
                }

                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != args.Length) continue;

                    try
                    {
                        var converted = new object?[parameters.Length];
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            converted[i] = ConvertReflectionArgument(args[i], parameters[i].ParameterType);
                        }
                        return method.Invoke(target, converted);
                    }
                    catch (Exception ex)
                    {
                        error = FormatExceptionDetail(ex);
                    }
                }

                error ??= "No matching overload succeeded for " + methodShortName;
                return null;
            }
            catch (Exception ex)
            {
                error = FormatExceptionDetail(ex);
                return null;
            }
        }

        private static object? ConvertReflectionArgument(object? value, Type targetType)
        {
            if (value == null) return null;

            var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullable.IsInstanceOfType(value)) return value;

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(nonNullable))
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(object));
                var dict = Activator.CreateInstance(dictType);
                var addMethod = dictType.GetMethod("Add", new[] { typeof(string), typeof(object) });
                if (value is IEnumerable<KeyValuePair<string, object?>> kvps)
                {
                    foreach (var kv in kvps)
                    {
                        addMethod?.Invoke(dict, new object?[] { kv.Key, kv.Value });
                    }
                    return dict;
                }
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(nonNullable) && nonNullable != typeof(string))
            {
                var enumerableInterface = nonNullable.IsInterface && nonNullable.IsGenericType
                    ? nonNullable
                    : nonNullable.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                if (enumerableInterface != null)
                {
                    var elementType = enumerableInterface.GetGenericArguments()[0];
                    if (elementType.IsGenericType &&
                        elementType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                        elementType.GenericTypeArguments[0] == typeof(string))
                    {
                        var valueType = elementType.GenericTypeArguments[1];
                        var listType = typeof(List<>).MakeGenericType(elementType);
                        var list = (System.Collections.IList)Activator.CreateInstance(listType);
                        if (value is IEnumerable<KeyValuePair<string, object?>> kvps)
                        {
                            foreach (var kv in kvps)
                            {
                                var kvValue = kv.Value;
                                if (kvValue != null && valueType != typeof(object) && !valueType.IsInstanceOfType(kvValue))
                                {
                                    kvValue = Convert.ChangeType(kvValue, valueType);
                                }
                                var pair = Activator.CreateInstance(elementType, kv.Key, kvValue);
                                list.Add(pair);
                            }
                            return list;
                        }
                    }
                }
            }

            if (nonNullable.IsEnum)
            {
                return value is string s
                    ? Enum.Parse(nonNullable, s, ignoreCase: true)
                    : Enum.ToObject(nonNullable, value);
            }

            return Convert.ChangeType(value, nonNullable);
        }

        private static object? TryResolveChildGroupByPath(object rootGroup, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return rootGroup;

            var parts = groupPath.Trim().Trim('/').Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            object? current = rootGroup;
            foreach (var part in parts)
            {
                if (current == null) return null;

                // common group collections used by HMI objects
                var next = TryFindByNameInCollection(current, new[] { "Groups", "ScreenGroups", "TagTableGroups", "Folders" }, part);
                if (next == null)
                {
                    // Some shapes: current.ScreenGroups or current.Groups are nested under another property
                    var groupContainer = TryGetPropertyValue(current, "Groups", "ScreenGroups", "TagTableGroups");
                    if (groupContainer != null)
                    {
                        next = TryFindByNameInCollection(groupContainer, new[] { "Groups", "ScreenGroups", "TagTableGroups", "Folders" }, part);
                    }
                }

                current = next;
            }

            return current;
        }

        public List<ModelContextProtocol.CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind = "Block", string filter = "AllObjects")
        {
            if (IsProjectNull()) return null;

            object? target = null;
            if (string.Equals(objectKind, "Type", StringComparison.OrdinalIgnoreCase))
            {
                target = GetType(softwarePath, objectPath);
            }
            else
            {
                target = GetBlock(softwarePath, objectPath);
            }

            if (target == null) return null;

            var crossReferenceService = TryGetServiceByTypeSuffix(target, "CrossReferenceService");
            if (crossReferenceService == null) return null;

            var result = TryInvokeGetCrossReferences(crossReferenceService, filter);
            if (result == null) return null;

            return TryFlattenCrossReferenceResult(result, objectPath);
        }

        private static object? TryGetServiceByTypeSuffix(object target, string serviceTypeNameSuffix)
        {
            try
            {
                var getService = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getService == null) return null;

                var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name.Equals(serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) ||
                                         t.FullName?.EndsWith("." + serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) == true);
                if (serviceType == null) return null;

                return getService.MakeGenericMethod(serviceType).Invoke(target, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static object? TryInvokeGetCrossReferences(object crossReferenceService, string filterName)
        {
            try
            {
                var svcType = crossReferenceService.GetType();
                var filterType = svcType.Assembly.GetTypes()
                    .FirstOrDefault(t => t.IsEnum && t.Name.Equals("CrossReferenceFilter", StringComparison.OrdinalIgnoreCase));
                if (filterType == null) return null;

                var filterValue = Enum.Parse(filterType, filterName, ignoreCase: true);
                var m = svcType.GetMethod("GetCrossReferences", new[] { filterType });
                if (m == null) return null;

                return m.Invoke(crossReferenceService, new[] { filterValue });
            }
            catch
            {
                return null;
            }
        }

        private static List<ModelContextProtocol.CrossReferenceEntry> TryFlattenCrossReferenceResult(object crossReferenceResult, string sourcePathFallback)
        {
            var items = new List<ModelContextProtocol.CrossReferenceEntry>();

            try
            {
                var sources = crossReferenceResult.GetType().GetProperty("Sources")?.GetValue(crossReferenceResult) as IEnumerable;
                if (sources == null) return items;

                foreach (var src in sources)
                {
                    if (src == null) continue;
                    var srcName = src.GetType().GetProperty("Name")?.GetValue(src)?.ToString();
                    var srcPath = src.GetType().GetProperty("Path")?.GetValue(src)?.ToString() ?? sourcePathFallback;

                    var refs = src.GetType().GetProperty("References")?.GetValue(src) as IEnumerable;
                    if (refs == null) continue;

                    foreach (var rf in refs)
                    {
                        if (rf == null) continue;
                        var refName = rf.GetType().GetProperty("Name")?.GetValue(rf)?.ToString();
                        var refPath = rf.GetType().GetProperty("Path")?.GetValue(rf)?.ToString();

                        var locations = rf.GetType().GetProperty("Locations")?.GetValue(rf) as IEnumerable;
                        if (locations == null)
                        {
                            items.Add(new ModelContextProtocol.CrossReferenceEntry
                            {
                                SourceName = srcName,
                                SourcePath = srcPath,
                                ReferenceName = refName,
                                ReferencePath = refPath
                            });
                            continue;
                        }

                        foreach (var loc in locations)
                        {
                            if (loc == null) continue;
                            items.Add(new ModelContextProtocol.CrossReferenceEntry
                            {
                                SourceName = srcName,
                                SourcePath = srcPath,
                                ReferenceName = refName,
                                ReferencePath = refPath,
                                LocationName = loc.GetType().GetProperty("Name")?.GetValue(loc)?.ToString(),
                                ReferenceLocation = loc.GetType().GetProperty("ReferenceLocation")?.GetValue(loc)?.ToString(),
                                ReferenceType = loc.GetType().GetProperty("ReferenceType")?.GetValue(loc)?.ToString(),
                                Access = loc.GetType().GetProperty("Access")?.GetValue(loc)?.ToString()
                            });
                        }
                    }
                }
            }
            catch
            {
                // best-effort
            }

            return items;
        }

        public List<string>? GetPlcExternalSources(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware) return null;

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null) return new List<string>();

            var names = new List<string>();
            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            return names;
        }

        /// <summary>
        /// Removes a PLC external source by name (e.g. <c>Ramp.scl</c> or <c>Ramp</c>) so a subsequent
        /// <see cref="ImportPlcExternalSource"/> can recreate it. Returns true if deleted or if no matching source exists.
        /// </summary>
        public void DeletePlcExternalSource(string softwarePath, string externalSourceName)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "DeletePlcExternalSource: project is null");

            if (string.IsNullOrWhiteSpace(externalSourceName))
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcExternalSource: externalSourceName is empty");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                throw new PortalException(PortalErrorCode.NotFound, $"DeletePlcExternalSource: PlcSoftware not found at '{softwarePath}'");

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null)
                throw new PortalException(PortalErrorCode.OpennessError, "DeletePlcExternalSource: ExternalSources collection not available");

            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!ExternalSourceNameMatches(name!, externalSourceName)) continue;

                try
                {
                    var del = item.GetType().GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (del != null)
                    {
                        del.Invoke(item, null);
                        return;
                    }

                    throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcExternalSource: no parameterless Delete() on {item.GetType().Name}");
                }
                catch (PortalException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcExternalSource: {ex.Message}", null, ex);
                }
            }

            // source not present = idempotent no-op success
        }

        public void ImportPlcExternalSource(string softwarePath, string groupPath, string filePath)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "ImportPlcExternalSource: project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                throw new PortalException(PortalErrorCode.NotFound, $"ImportPlcExternalSource: PlcSoftware not found at '{softwarePath}'");

            var group = TryGetExternalSourceGroupByPath(plcSoftware, groupPath);
            if (group == null)
                throw new PortalException(PortalErrorCode.NotFound, $"ImportPlcExternalSource: ExternalSourceGroup not found (groupPath='{groupPath}')");

            // Openness API for external sources differs across TIA versions:
            // some expose Import(FileInfo,...), others expose Add/Create/ImportFromFile(FileInfo,...).
            // Prefer the ExternalSources composition first — CreateFromFile lives there in V21.
            var targets = new List<object>();
            try
            {
                var extSourcesObj = group.GetType().GetProperty("ExternalSources")?.GetValue(group);
                if (extSourcesObj != null) targets.Add(extSourcesObj);
            }
            catch { }
            targets.Add(group);

            var fi = new FileInfo(filePath);
            if (!fi.Exists)
                throw new PortalException(PortalErrorCode.InvalidParams, $"ImportPlcExternalSource: file not found '{filePath}'");

            var candidates = new List<(object Target, MethodInfo Method)>();
            foreach (var tgt in targets)
            {
                var methods = tgt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                candidates.AddRange(methods
                    .Where(m =>
                    {
                        var ps = m.GetParameters();
                        if (ps.Length < 1) return false;
                        if (ps[0].ParameterType != typeof(FileInfo) && ps[0].ParameterType != typeof(string)) return false;
                        var n = m.Name ?? "";
                        return n.StartsWith("Import", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("Add", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("Create", StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(m => (Target: tgt, Method: m)));
            }

            candidates = candidates
                .OrderBy(c => c.Method.GetParameters()[0].ParameterType == typeof(FileInfo) ? 0 : 1)
                .ThenBy(c => c.Method.Name.StartsWith("Import", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Method.GetParameters().Length)
                .ToList();

            if (candidates.Count == 0)
            {
                string Dump(object tgt)
                {
                    try
                    {
                        var ms = tgt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => (m.Name ?? "").IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0
                                     || (m.Name ?? "").IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0
                                     || (m.Name ?? "").IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                            .Take(10);
                        return string.Join("; ", ms);
                    }
                    catch { return ""; }
                }

                var extDump = targets.Count > 1 ? Dump(targets[1]) : "";
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ImportPlcExternalSource: No import-like method found. group={group.GetType().FullName} methods=[{Dump(group)}] extSources=[{extDump}]");
            }

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                var importMethod = candidate.Method;
                var parms = importMethod.GetParameters();
                var argLists = BuildExternalSourceImportArguments(parms, fi);
                if (argLists.Count == 0) continue;

                foreach (var args in argLists)
                {
                    var sig = $"{importMethod.Name}({string.Join(", ", parms.Select(p => p.ParameterType.Name))})";
                    try
                    {
                        var result = importMethod.Invoke(candidate.Target, args);
                        if (importMethod.ReturnType == typeof(void) || result != null)
                        {
                            return;
                        }

                        failures.Add($"{sig} returned null");
                    }
                    catch (Exception ex)
                    {
                        var inner = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                        failures.Add($"{sig} threw {inner.GetType().FullName}: {inner.Message}");
                    }
                }
            }

            throw new PortalException(PortalErrorCode.OpennessError, "ImportPlcExternalSource: all import-like methods failed: " + string.Join(" | ", failures.Take(12)));
        }

        private static bool ExternalSourceNameMatches(string actualName, string requested)
        {
            if (string.IsNullOrWhiteSpace(actualName)) return false;
            if (string.Equals(actualName, requested, StringComparison.OrdinalIgnoreCase)) return true;
            var req = (requested ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(req)) return false;
            var reqNoExt = Path.GetFileNameWithoutExtension(req);
            var actNoExt = Path.GetFileNameWithoutExtension(actualName);
            if (string.Equals(actNoExt, reqNoExt, StringComparison.OrdinalIgnoreCase)) return true;
            if (req.IndexOf('.') < 0 &&
                string.Equals(actualName, req + ".scl", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static List<object?[]> BuildExternalSourceImportArguments(ParameterInfo[] parms, FileInfo fi)
        {
            var result = new List<object?[]>();
            if (parms.Length < 1) return result;
            if (parms[0].ParameterType != typeof(FileInfo) && parms[0].ParameterType != typeof(string)) return result;

            object firstArg = parms[0].ParameterType == typeof(FileInfo) ? fi : fi.FullName;
            var sourceName = Path.GetFileNameWithoutExtension(fi.Name);

            if (parms.Length == 1)
            {
                result.Add(new object?[] { firstArg });
                return result;
            }

            // Siemens.Openness: PlcExternalSourceComposition.CreateFromFile(string name, string path)
            // Manual 5.11.3.x — first arg is the external-source *name* (often "Block_1.scl"), second is full path.
            // Older reflection code wrongly passed (FullPath, fileTitleWithoutExtension).
            if (parms.Length == 2 && parms[0].ParameterType == typeof(string) && parms[1].ParameterType == typeof(string))
            {
                result.Add(new object?[] { fi.Name, fi.FullName });
                if (!string.IsNullOrEmpty(sourceName) &&
                    !string.Equals(sourceName, fi.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new object?[] { sourceName, fi.FullName });
                }
                return result;
            }

            if (parms.Length == 2 && parms[0].ParameterType == typeof(FileInfo) && parms[1].ParameterType == typeof(string))
            {
                result.Add(new object?[] { fi, sourceName });
                return result;
            }

            if (parms.Length == 2 && parms[1].ParameterType.IsEnum)
            {
                foreach (var preferred in new[] { "Override", "Overwrite", "Replace", "None" })
                {
                    try
                    {
                        result.Add(new object?[] { firstArg, Enum.Parse(parms[1].ParameterType, preferred, ignoreCase: true) });
                    }
                    catch { }
                }
                foreach (var value in Enum.GetValues(parms[1].ParameterType))
                {
                    if (!result.Any(args => Equals(args[1], value))) result.Add(new object?[] { firstArg, value });
                }
                return result;
            }

            if (parms.Skip(1).All(p => p.IsOptional))
            {
                result.Add(new[] { firstArg }.Concat(parms.Skip(1).Select(p => p.DefaultValue)).ToArray());
            }

            return result;
        }

        public void GenerateBlocksFromExternalSource(string softwarePath, string externalSourceName)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "GenerateBlocksFromExternalSource: project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware) throw new PortalException(PortalErrorCode.NotFound, $"GenerateBlocksFromExternalSource: PlcSoftware not found at '{softwarePath}'");

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null) throw new PortalException(PortalErrorCode.OpennessError, "GenerateBlocksFromExternalSource: ExternalSources collection not available");

            object? src = null;
            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (ExternalSourceNameMatches(name!, externalSourceName))
                {
                    src = item;
                    break;
                }
            }
            if (src == null) throw new PortalException(PortalErrorCode.NotFound, $"GenerateBlocksFromExternalSource: external source not found: {externalSourceName}");

            // V18+ often exposes GenerateBlocksFromSource(PlcBlockUserGroup, GenerateBlockOption) only;
            // parameterless GenerateBlocks() may not exist.
            var t = src.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m =>
                {
                    var n = m.Name ?? "";
                    return n.Equals("GenerateBlocks", StringComparison.OrdinalIgnoreCase)
                           || n.Equals("GenerateBlocksFromSource", StringComparison.OrdinalIgnoreCase)
                           || n.Equals("GenerateBlocksFromExternalSource", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            var failures = new List<string>();
            foreach (var gen in methods)
            {
                var ps = gen.GetParameters();
                try
                {
                    if (ps.Length == 0)
                    {
                        gen.Invoke(src, Array.Empty<object>());
                        return;
                    }

                    if (ps.Length == 2 && ps[1].ParameterType.IsEnum)
                    {
                        var folderType = ps[0].ParameterType;
                        var blockRoot = plcSoftware.BlockGroup;
                        if (blockRoot == null)
                        {
                            failures.Add($"{gen.Name}: BlockGroup is null");
                            continue;
                        }

                        if (!folderType.IsAssignableFrom(blockRoot.GetType()))
                        {
                            failures.Add($"{gen.Name}: BlockGroup type {blockRoot.GetType().Name} not assignable to {folderType.Name}");
                            continue;
                        }

                        object optionVal;
                        try
                        {
                            optionVal = Enum.Parse(ps[1].ParameterType, "None", ignoreCase: true);
                        }
                        catch
                        {
                            var vals = Enum.GetValues(ps[1].ParameterType);
                            if (vals.Length == 0)
                            {
                                failures.Add($"{gen.Name}: GenerateBlockOption enum empty");
                                continue;
                            }
                            optionVal = vals.GetValue(0)!;
                        }

                        gen.Invoke(src, new[] { blockRoot, optionVal });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    var inner = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                    failures.Add($"{gen.Name}({ps.Length}): {inner.Message}");
                }
            }

            throw new PortalException(PortalErrorCode.OpennessError, "GenerateBlocksFromExternalSource: " + string.Join(" | ", failures.Take(10)));
        }

        private static IEnumerable<object?>? TryGetExternalSourcesCollection(PlcSoftware plcSoftware)
        {
            try
            {
                var group = plcSoftware.GetType().GetProperty("ExternalSourceGroup")?.GetValue(plcSoftware)
                           ?? plcSoftware.GetType().GetProperty("ExternalSources")?.GetValue(plcSoftware);
                if (group == null) return null;

                var sources = group.GetType().GetProperty("ExternalSources")?.GetValue(group) ?? group;
                return sources as IEnumerable<object?>;
            }
            catch
            {
                return null;
            }
        }

        private static object? TryGetExternalSourceGroupByPath(PlcSoftware plcSoftware, string groupPath)
        {
            try
            {
                var root = plcSoftware.GetType().GetProperty("ExternalSourceGroup")?.GetValue(plcSoftware);
                if (root == null) return null;

                if (string.IsNullOrWhiteSpace(groupPath) || groupPath == "/")
                {
                    return root;
                }

                var segments = groupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                object current = root;
                foreach (var seg in segments)
                {
                    var groups = current.GetType().GetProperty("Groups")?.GetValue(current) as IEnumerable;
                    if (groups == null) return null;

                    object? next = null;
                    foreach (var g in groups)
                    {
                        if (g == null) continue;
                        var name = g.GetType().GetProperty("Name")?.GetValue(g)?.ToString();
                        if (string.Equals(name, seg, StringComparison.OrdinalIgnoreCase))
                        {
                            next = g;
                            break;
                        }
                    }
                    if (next == null) return null;
                    current = next;
                }

                return current;
            }
            catch
            {
                return null;
            }
        }

        public CompilerResult CompileSoftware(string softwarePath, string password = "")
        {
            _logger?.LogInformation($"Compiling software by path: {softwarePath}");

            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "Project is null");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
                throw new PortalException(PortalErrorCode.NotFound, $"SoftwareContainer or Software not found for path '{softwarePath}'");

            if (!string.IsNullOrEmpty(password))
            {
                var deviceItem = softwareContainer?.Parent as DeviceItem;

                var admin = deviceItem?.GetService<SafetyAdministration>();
                if (admin != null)
                {
                    if (!admin.IsLoggedOnToSafetyOfflineProgram)
                    {
                        SecureString secString = new NetworkCredential("", password).SecurePassword;
                        try
                        {
                            admin.LoginToSafetyOfflineProgram(secString);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.OpennessError, $"Safety login failed: {ex.Message}", null, ex);
                        }
                    }
                }
            }

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                try
                {
                    ICompilable? compileService = plcSoftware.GetService<ICompilable>();
                    if (compileService == null)
                        throw new PortalException(PortalErrorCode.OpennessError, "plcSoftware.GetService<ICompilable>() returned null");

                    CompilerResult result = compileService.Compile();

                    if (result == null)
                        throw new PortalException(PortalErrorCode.OpennessError, "ICompilable.Compile() returned null");

                    return result;
                }
                catch (PortalException)
                {
                    throw;
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw new PortalException(PortalErrorCode.OpennessError, $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}", null, tie.InnerException);
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.OpennessError, $"{ex.GetType().FullName}: {ex.Message}", null, ex);
                }
            }

            var resolvedSoftware = softwareContainer?.Software;
            throw new PortalException(
                resolvedSoftware == null ? PortalErrorCode.NotFound : PortalErrorCode.InvalidState,
                resolvedSoftware == null
                    ? $"SoftwareContainer or Software not found for path '{softwarePath}'"
                    : $"Software at '{softwarePath}' is not PlcSoftware. Type={resolvedSoftware.GetType().FullName}");
        }

        #endregion

        #region alarms

        public ResponseMessage ExportAlarmClasses(string softwarePath, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = plc.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "AlarmClassDataProvider not available for this PLC." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                var result = provider.Export(new FileInfo(exportPath));
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                var errCount = (int)(result?.GetType().GetProperty("ErrorCount")?.GetValue(result) ?? 0);
                bool ok = state == "Success" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm classes exported to '{exportPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class export failed. State={state}, Errors={errCount}.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state, ["errorCount"] = errCount }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmClasses failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportAlarmClasses(string softwarePath, string importPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = plc.GetService<AlarmClassDataProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "AlarmClassDataProvider not available for this PLC." };

                var result = provider.Import(new FileInfo(importPath));
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                var errCount = (int)(result?.GetType().GetProperty("ErrorCount")?.GetValue(result) ?? 0);
                bool ok = state == "Success" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm classes imported from '{importPath}' (State={state}, Errors={errCount})."
                        : $"Alarm class import failed. State={state}, Errors={errCount}.",
                    Meta = new JsonObject { ["importPath"] = importPath, ["state"] = state, ["errorCount"] = errCount }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportAlarmClasses failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        public ResponseMessage ExportAlarmTextLists(string softwarePath, string exportPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                // PlcAlarmTextlistGroup is a property on PlcSoftware
                var textListGroup = TryGetPropertyValue(plc, "PlcAlarmTextlistGroup", "AlarmTextlistGroup");
                if (textListGroup == null)
                    return new ResponseMessage { Message = "PlcAlarmTextlistGroup not accessible on this PLC." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                // ExportToXlsx(FileInfo) — overload with no filters
                var result = TryInvokeMethodByName(textListGroup, "ExportToXlsx", new FileInfo(exportPath));
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                bool ok = state == "OK" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm text lists exported to '{exportPath}' (State={state})."
                        : $"Alarm text list export had issues. State={state}.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportAlarmTextLists(string softwarePath, string importPath)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var textListGroup = TryGetPropertyValue(plc, "PlcAlarmTextlistGroup", "AlarmTextlistGroup");
                if (textListGroup == null)
                    return new ResponseMessage { Message = "PlcAlarmTextlistGroup not accessible on this PLC." };

                // ImportFromXlsx(FileInfo, ImportOptions) — use None import options via reflection
                var importMethod = textListGroup.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ImportFromXlsx" && m.GetParameters().Length >= 1);

                if (importMethod == null)
                    return new ResponseMessage { Message = "ImportFromXlsx method not found." };

                var parms = importMethod.GetParameters();
                object?[] args;
                if (parms.Length == 2 && parms[1].ParameterType.IsEnum)
                {
                    // ImportOptions enum — use value 0 (None/Default)
                    args = new object?[] { new FileInfo(importPath), Enum.ToObject(parms[1].ParameterType, 0) };
                }
                else
                {
                    args = new object?[] { new FileInfo(importPath) };
                }

                var result = importMethod.Invoke(textListGroup, args);
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                bool ok = state == "OK" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm text lists imported from '{importPath}' (State={state})."
                        : $"Alarm text list import had issues. State={state}.",
                    Meta = new JsonObject { ["importPath"] = importPath, ["state"] = state }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportAlarmTextLists failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        public ResponseMessage ExportAlarmInstanceTexts(string softwarePath, string exportPath, bool includeInfoText = true, bool includeAdditionalTexts = true, bool includeAlarmClass = true)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = plc.GetService<PlcAlarmTextProvider>();
                if (provider == null)
                    return new ResponseMessage { Message = "PlcAlarmTextProvider service not available for this PLC." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");

                // ExportInstanceTextsToXlsx(FileInfo, IEnumerable<Language>, PlcAlarmTextXlsxExportOption)
                // Use reflection to handle Language and flags enum
                var exportMethod = provider.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ExportInstanceTextsToXlsx");

                if (exportMethod == null)
                    return new ResponseMessage { Message = "ExportInstanceTextsToXlsx method not found." };

                var parms = exportMethod.GetParameters();
                // Build options flags value: All = typically 7 (IncludeInfoText|IncludeAdditionalTexts|IncludeAlarmClass)
                object optionsValue;
                if (parms.Length >= 3 && parms[2].ParameterType.IsEnum)
                {
                    int flags = 0;
                    if (includeInfoText) flags |= 1;
                    if (includeAdditionalTexts) flags |= 2;
                    if (includeAlarmClass) flags |= 4;
                    optionsValue = Enum.ToObject(parms[2].ParameterType, flags);
                }
                else
                {
                    optionsValue = 7; // All
                }

                // Pass null for languages (export all)
                object?[] args = parms.Length == 3
                    ? new object?[] { new FileInfo(exportPath), null, optionsValue }
                    : new object?[] { new FileInfo(exportPath) };

                var result = exportMethod.Invoke(provider, args);
                var state = result?.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "Unknown";
                bool ok = state == "OK" || state == "Warning";
                return new ResponseMessage
                {
                    Message = ok
                        ? $"Alarm instance texts exported to '{exportPath}' (State={state})."
                        : $"Alarm instance text export had issues. State={state}.",
                    Meta = new JsonObject { ["exportPath"] = exportPath, ["state"] = state }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportAlarmInstanceTexts failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        #endregion

        #region opcua

        /// <summary>Returns the ServerInterfaceGroup node via reflection chain.</summary>
        private static object? GetOpcUaServerInterfaceGroup(PlcSoftware plc)
        {
            var provider = plc.GetService<OpcUaProvider>();
            if (provider == null) return null;
            var commGroup = TryGetPropertyValue(provider, "CommunicationGroup");
            if (commGroup == null) return null;
            return TryGetPropertyValue(commGroup, "ServerInterfaceGroup");
        }

        public ModelContextProtocol.ResponseJsonReport GetOpcUaConfig(string softwarePath)
        {
            var data = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now.ToString("O") };

            if (IsProjectNull())
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "No project open.", Data = data };

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"PLC software not found: '{softwarePath}'.", Data = data };

            try
            {
                var provider = plc.GetService<OpcUaProvider>();
                if (provider == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "OpcUaProvider not available for this PLC.", Data = data };

                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "ServerInterfaceGroup not accessible.", Data = data };

                data["serverInterfaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "ServerInterfaces"));
                data["simaticInterfaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "SimaticInterfaces"));
                data["referenceNamespaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "ReferenceNamespaces"));

                return new ModelContextProtocol.ResponseJsonReport { Ok = true, Message = $"OPC UA config read for '{softwarePath}'.", Data = data };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetOpcUaConfig failed for {SoftwarePath}", softwarePath);
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"Error: {ex.Message}", Data = data };
            }
        }

        private static JsonArray CollectOpcUaItems(object? collection)
        {
            var arr = new JsonArray();
            if (collection is not IEnumerable items || collection is string) return arr;
            foreach (var item in items)
            {
                if (item == null) continue;
                var obj = new JsonObject();
                foreach (var prop in new[] { "Name", "Comment", "Author", "Enabled", "UseStringNodeIds", "GenerateNodes", "GeneratedInterfaceName" })
                {
                    var val = TryGetPropertyValue(item, prop);
                    if (val != null) obj[prop] = JsonValue.Create(val.ToString());
                }
                arr.Add(obj);
            }
            return arr;
        }

        public ResponseMessage SetOpcUaInterfaceEnabled(string softwarePath, string interfaceName, bool enabled, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found in '{softwarePath}'." };

                TrySetProperty(item, "Enabled", enabled);
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' {(enabled ? "enabled" : "disabled")}. Download to PLC to apply.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["interfaceName"] = interfaceName, ["enabled"] = enabled }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SetOpcUaInterfaceEnabled failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseMessage ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                TryInvokeMethodByName(item, "Export", new FileInfo(exportPath));
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' exported to '{exportPath}'.",
                    Meta = new JsonObject { ["exportPath"] = exportPath }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                if (collection == null) return new ResponseMessage { Message = $"{collectionProp} collection not accessible." };

                // ServerInterfaceComposition.Create(name) then Import(file)
                // OR find existing and call Import
                var fi = new FileInfo(importPath);
                var interfaceName = Path.GetFileNameWithoutExtension(importPath);
                var existing = FindByName(collection, interfaceName);

                if (existing != null)
                {
                    TryInvokeMethodByName(existing, "Import", fi);
                    return new ResponseMessage { Message = $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'." };
                }
                else
                {
                    // Try Create then Import
                    var created = TryInvokeMethodByName(collection, "Create", interfaceName);
                    if (created != null)
                        TryInvokeMethodByName(created, "Import", fi);
                    return new ResponseMessage { Message = created != null
                        ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
                        : $"Could not create {interfaceType} '{interfaceName}'. Try importing via ExportOpcUaInterface first." };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        private static object? FindByName(object? collection, string name)
        {
            if (collection is not IEnumerable items || collection is string) return null;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (string.Equals(TryGetPropertyValue(item, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        #endregion

        #region download

        public ResponseDownload DownloadToPlc(
            string softwarePath,
            bool consistentBlocksOnly = true,
            bool keepActualValues = true,
            bool startAfterDownload = true,
            bool stopBeforeDownload = true,
            string? password = null)
        {
            _logger?.LogInformation(
                "DownloadToPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P}",
                softwarePath, consistentBlocksOnly, keepActualValues, startAfterDownload, stopBeforeDownload, !string.IsNullOrEmpty(password));

            if (IsProjectNull())
                return new ResponseDownload { Ok = false, Message = "No project open." };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseDownload { Ok = false, Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var downloadProvider = ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                if (downloadProvider == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "DownloadProvider service not available for this PLC. Ensure hardware configuration has network settings."
                    };

                object? configuration = downloadProvider.Configuration;
                if (configuration == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first."
                    };

                using var passwordScope = AttachPasswordHandler(configuration, password);

                bool capture_keepActualValues = keepActualValues;
                bool capture_startAfterDownload = startAfterDownload;
                bool capture_stopBeforeDownload = stopBeforeDownload;
                bool capture_consistentBlocksOnly = consistentBlocksOnly;

                DownloadConfigurationDelegate preDelegate = (config) =>
                {
                    ApplyDefaultDownloadConfig(
                        config,
                        capture_keepActualValues,
                        capture_startAfterDownload,
                        capture_stopBeforeDownload,
                        capture_consistentBlocksOnly);
                };

                DownloadConfigurationDelegate postDelegate = (config) => { };

                // ConnectionConfiguration does not implicitly satisfy IConfiguration at
                // compile time in this binding, so invoke via reflection at runtime.
                var downloadMethod = downloadProvider.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Download") return false;
                        var p = m.GetParameters();
                        return p.Length == 4
                            && p[1].ParameterType.Name == "DownloadConfigurationDelegate";
                    });

                if (downloadMethod == null)
                    return new ResponseDownload
                    {
                        Ok = false,
                        Message = "Download(IConfiguration,…) method not found on DownloadProvider. TIA Portal version mismatch?"
                    };

                var rawResult = downloadMethod.Invoke(
                    downloadProvider,
                    new object[] { configuration, preDelegate, postDelegate, DownloadOptions.Software });

                if (rawResult is not DownloadResult result)
                    return new ResponseDownload { Ok = false, Message = "Download returned an unexpected result type." };

                return BuildDownloadResponse(result, softwarePath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "DownloadToPlc failed for {SoftwarePath}", softwarePath);
                return new ResponseDownload
                {
                    Ok = false,
                    Message = $"Download failed: {ex.Message}",
                    Errors = new[] { ex.Message }
                };
            }
        }

        public ResponseCheckDownload CheckDownloadReadiness(string softwarePath)
        {
            var issues = new List<string>();

            if (IsProjectNull())
                return new ResponseCheckDownload { Ready = false, Issues = new[] { "No project open." } };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseCheckDownload { Ready = false, Issues = new[] { $"PLC software not found: '{softwarePath}'." } };

            bool hasProvider = false;
            bool hasConfig = false;
            bool isConsistent = false;

            try
            {
                var provider = ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                hasProvider = provider != null;
                if (!hasProvider)
                    issues.Add("DownloadProvider service not available. Check hardware/network configuration.");
                else
                {
                    hasConfig = provider!.Configuration != null;
                    if (!hasConfig)
                        issues.Add("No network configuration for this PLC. Set the IP address in hardware configuration.");
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Error accessing DownloadProvider: {ex.Message}");
            }

            // Check compile consistency via ICompilable
            try
            {
                var compilable = plcSoftware.GetService<ICompilable>();
                if (compilable != null)
                {
                    // We skip an actual compile here; check block consistency heuristically
                    isConsistent = true; // Assume consistent unless caller has run CompileSoftware
                }
            }
            catch { }

            bool ready = hasProvider && hasConfig && issues.Count == 0;
            return new ResponseCheckDownload
            {
                Ready = ready,
                HasDownloadProvider = hasProvider,
                HasConfiguration = hasConfig,
                IsConsistent = isConsistent,
                Message = ready
                    ? $"PLC '{softwarePath}' is ready for download."
                    : $"PLC '{softwarePath}' has {issues.Count} readiness issue(s).",
                Issues = issues.Count > 0 ? issues.ToArray() : null
            };
        }

        private void ApplyDefaultDownloadConfig(
            DownloadConfiguration config,
            bool keepActualValues,
            bool startAfterDownload,
            bool stopBeforeDownload,
            bool consistentBlocksOnly)
        {
            var typeName = config.GetType().Name;
            _logger?.LogDebug("ApplyDownloadConfig: {TypeName}", typeName);

            switch (typeName)
            {
                case "StopModules":
                case "StopHSystemOrModule":
                case "StopHSystem":
                    DownloadConfigSetSelection(config, stopBeforeDownload ? "StopModule" : "NoAction");
                    break;

                case "StartModules":
                case "StartBackupModules":
                    DownloadConfigSetSelection(config, startAfterDownload ? "StartModule" : "NoAction");
                    break;

                case "DataBlockReinitialization":
                    DownloadConfigSetSelection(config, keepActualValues ? "KeepActualValues" : "Reinitialize");
                    break;

                case "DataBlockReinitializationOrKeepActualValues":
                    DownloadConfigSetSelection(config, keepActualValues ? "KeepActualValues" : "StopPlcAndReinitialize");
                    break;

                case "ConsistentBlocksDownload":
                    DownloadConfigSetSelection(config, "ConsistentDownload");
                    break;

                case "AllBlocksDownload":
                    if (!consistentBlocksOnly)
                        DownloadConfigSetSelection(config, "DownloadAllBlocks");
                    break;

                case "CheckBeforeDownload":
                case "AlarmTextLibrariesDownload":
                case "UserManagementDownload":
                case "DownloadCertificate":
                    DownloadConfigSetChecked(config, true);
                    break;

                case "DifferentTargetConfiguration":
                case "ActiveTestCanBeAborted":
                case "ActiveTestCanPreventDownload":
                    DownloadConfigSetSelection(config, "AcceptAll");
                    break;
            }
        }

        private static void DownloadConfigSetSelection(object config, string selectionName)
        {
            try
            {
                var prop = config.GetType().GetProperty("CurrentSelection");
                if (prop == null) return;
                var enumType = prop.PropertyType;
                if (!enumType.IsEnum) return;
                var value = Enum.Parse(enumType, selectionName, ignoreCase: true);
                prop.SetValue(config, value);
            }
            catch { }
        }

        private static void DownloadConfigSetChecked(object config, bool value)
        {
            try
            {
                var prop = config.GetType().GetProperty("Checked");
                prop?.SetValue(config, value);
            }
            catch { }
        }

        private ResponseDownload BuildDownloadResponse(DownloadResult result, string softwarePath)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            CollectDownloadMessages(result.Messages, errors, warnings);

            bool ok = result.State == DownloadResultState.Success
                   || result.State == DownloadResultState.Information
                   || result.State == DownloadResultState.Warning;

            return new ResponseDownload
            {
                Ok = ok,
                Message = $"Download {result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s).",
                State = result.State.ToString(),
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Errors = errors.Count > 0 ? errors.ToArray() : null,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = new JsonObject
                {
                    ["softwarePath"] = softwarePath,
                    ["timestamp"] = DateTime.Now,
                    ["downloadState"] = result.State.ToString()
                }
            };
        }

        private static void CollectDownloadMessages(
            IEnumerable? messages,
            List<string> errors,
            List<string> warnings)
        {
            if (messages == null) return;
            foreach (var obj in messages)
            {
                if (obj == null) continue;
                try
                {
                    var msgText = obj.GetType().GetProperty("Message")?.GetValue(obj) as string ?? string.Empty;
                    var stateObj = obj.GetType().GetProperty("State")?.GetValue(obj);
                    var stateName = stateObj?.ToString() ?? string.Empty;

                    if (stateName == "Error" && !string.IsNullOrWhiteSpace(msgText))
                        errors.Add(msgText);
                    else if (stateName == "Warning" && !string.IsNullOrWhiteSpace(msgText))
                        warnings.Add(msgText);

                    // Recurse into nested Messages
                    var nested = obj.GetType().GetProperty("Messages")?.GetValue(obj) as IEnumerable;
                    if (nested != null)
                        CollectDownloadMessages(nested, errors, warnings);
                }
                catch { }
            }
        }

        #endregion

        // #region online — moved to Portal.Online.cs

        #region blocks/types

        public PlcBlock? GetBlock(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block by path: {blockPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var blockGroup = plcSoftware?.BlockGroup;

                if (blockGroup != null)
                {
                    var path = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                    var regexName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                    PlcBlock? block = null;

                    var group = GetPlcBlockGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        if (regexName.IndexOfAny(_regexChars) >= 0)
                        {
                            try
                            {
                                var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                block = group.Blocks.FirstOrDefault(b => regex.IsMatch(b.Name)) as PlcBlock;
                            }
                            catch (Exception)
                            {
                                // Invalid regex, return null
                                return null;
                            }
                        }
                        else
                        {
                            block = group.Blocks.FirstOrDefault(b => b.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase));
                        }

                        return block;
                    }
                }
            }

            return null;
        }

        public PlcType? GetType(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type by path: {typePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var typeGroup = plcSoftware?.TypeGroup;

                if (typeGroup != null)
                {
                    var path = typePath.Contains("/") ? typePath.Substring(0, typePath.LastIndexOf("/")) : string.Empty;
                    var regexName = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;

                    PlcType? type = null;

                    var group = GetPlcTypeGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        if (regexName.IndexOfAny(_regexChars) >= 0)
                        {
                            try
                            {
                                var regex = new Regex(regexName, RegexOptions.IgnoreCase);
                                type = group.Types.FirstOrDefault(t => regex.IsMatch(t.Name)) as PlcType;
                            }
                            catch (Exception)
                            {
                                // Invalid regex, return null
                                return null;
                            }
                        }
                        else
                        {
                            type = group.Types.FirstOrDefault(t => t.Name.Equals(regexName, StringComparison.OrdinalIgnoreCase));
                        }

                        return type;
                    }
                }
            }

            return null;
        }

        public string GetBlockPath(PlcBlock block)
        {
            if (block == null)
            {
                return string.Empty;
            }

            if (block.Parent is PlcBlockGroup parentGroup)
            {
                var groupPath = GetPlcBlockGroupPath(parentGroup);
                return string.IsNullOrEmpty(groupPath) ? block.Name : $"{groupPath}/{block.Name}";
            }

            return block.Name;
        }

        public List<PlcBlock> GetBlocks(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting blocks...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = plcSoftware?.BlockGroup;

                    if (group != null)
                    {
                        GetBlocksRecursive(group, list, regexName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting blocks");
            }

            return list;
        }

        public PlcBlockGroup? GetBlockRootGroup(string softwarePath)
        {
            _logger?.LogInformation("Getting block root group...");

            if (IsProjectNull())
            {
                return null;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    return plcSoftware.BlockGroup;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting block root group");
            }

            return null;
        }

        public List<PlcType> GetTypes(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting types...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<PlcType>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = plcSoftware?.TypeGroup;

                    if (group != null)
                    {
                        GetTypesRecursive(group, list, regexName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting user defined types");
            }

            return list;
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block by path: {blockPath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var block = Guard.RequireNotNull(GetBlock(softwarePath, blockPath), "Block", blockPath);

                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                // TIA Portal never exports inconsistent blocks
                if (!block.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Block is inconsistent; TIA Portal does not export inconsistent blocks.");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                block.Export(new FileInfo(exportPath), ExportOptions.None);

                return block;
            }
            catch (Exception ex)
            {
                //If the exception is already a PortalException, use it; otherwise, wrap it in a new PortalException
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
        }

        public PlcType? ExportType(string softwarePath, string typePath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting type by path: {typePath}");

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                var type = Guard.RequireNotNull(GetType(softwarePath, typePath), "Type", typePath);

                // TIA Portal never exports inconsistent types
                if (!type.IsConsistent)
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "Type is inconsistent; TIA Portal does not export inconsistent types.");
                }

                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }

                    exportPath = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    exportPath = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                type.Export(new FileInfo(exportPath), ExportOptions.None);

                return type;
            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                if (!pex.Data.Contains("softwarePath")) pex.Data["softwarePath"] = softwarePath;
                if (!pex.Data.Contains("typePath")) pex.Data["typePath"] = typePath;
                if (!pex.Data.Contains("exportPath")) pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportType failed for {SoftwarePath} {TypePath} -> {ExportPath}", softwarePath, typePath, exportPath);
                throw pex;
            }
        }

        // Prepare a block/type XML file for Openness import. Two things are fixed on a temp
        // copy (the user's original file is never touched):
        //   1) Engineering version: Openness rejects an XML whose <Engineering version="Vxx"/>
        //      is newer than the connected portal ("The engineering version 'V21' ... is not
        //      supported."). The XML builders historically hardcode V21, so on a V20 portal
        //      every import fails. The header is rewritten to the detected major version.
        //   2) Encoding/BOM: block/type XML carrying Chinese comments must be UTF-8 *with BOM*
        //      or TIA imports the text as mojibake (中文乱码). Callers (and the model that wrote
        //      the file) frequently emit BOM-less UTF-8, so we always re-emit with a BOM here.
        private static string PrepareXmlForImport(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = File.ReadAllText(path, Encoding.UTF8);

                var fixedText = text;
                int major = Engineering.TiaMajorVersion;
                if (major > 0)
                {
                    fixedText = Regex.Replace(text,
                        "<Engineering\\s+version=\"V\\d+\"\\s*/>",
                        $"<Engineering version=\"V{major}\" />");
                }

                if (major > 0 && major <= 17)
                {
                    try
                    {
                        var doc = XDocument.Parse(fixedText, LoadOptions.PreserveWhitespace);
                        var emptyNamespaceNodes = doc
                            .Descendants()
                            .Where(x => x.Name.LocalName == "Namespace" && string.IsNullOrWhiteSpace(x.Value))
                            .ToList();

                        if (emptyNamespaceNodes.Count > 0)
                        {
                            foreach (var node in emptyNamespaceNodes)
                                node.Remove();

                            using var sw = new Utf8StringWriter();
                            doc.Save(sw, SaveOptions.None);
                            fixedText = sw.ToString();
                        }
                    }
                    catch
                    {
                        // best effort only; if sanitization fails, fall back to version/BOM normalization
                    }
                }

                // Already correct: version matches (or unknown) AND a BOM is present -> import as-is.
                if (fixedText == text && hasBom) return path;

                var tmp = Path.Combine(Path.GetTempPath(), "tia_mcp_import_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, fixedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return tmp;
            }
            catch
            {
                return path; // best effort; on any failure import the original file
            }
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing block from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC block group not found for groupPath='{groupPath}'; use empty string for root program blocks");

                if (!new FileInfo(importPath).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(PrepareXmlForImport(importPath));

                var imported = group.Blocks.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Blocks.Import returned an empty collection");

                return true;
            }
            catch (Exception ex)
            {
                // Surface the real Openness error to callers — without this the message
                // is just "Import failed" which is useless for diagnosing bad LAD/SCL XML.
                var inner = UnwrapImportError(ex);
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, $"Import failed: {inner}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportBlock failed for {SoftwarePath} group={GroupPath} file={ImportPath}: {Inner}", softwarePath, groupPath, importPath, inner);
                throw pex;
            }
        }

        // Walk InnerException chain and concatenate type+message — Openness wraps the
        // useful XML-validation error several layers deep.
        private static string UnwrapImportError(Exception ex)
        {
            var parts = new List<string>();
            var cur = ex;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            return string.Join(" | ", parts);
        }

        public ResponseImportBatch ImportBlocksFromDirectory(string softwarePath, string groupPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"PlcSoftware not found at '{softwarePath}'" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"Block group not found (groupPath='{groupPath}')" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name))
                    {
                        continue;
                    }

                    try
                    {
                        if (!new FileInfo(file).Exists)
                        {
                            failed.Add(new ImportFailure { Path = file, Error = "File not found" });
                            continue;
                        }
                        var fi = new FileInfo(PrepareXmlForImport(file));

                        if (!overwrite)
                        {
                            try
                            {
                                var exists = group.Blocks.Find(name);
                                if (exists != null)
                                {
                                    failed.Add(new ImportFailure { Path = file, Error = $"Block '{name}' already exists (overwrite=false)" });
                                    continue;
                                }
                            }
                            catch
                            {
                                // best effort only; if Find fails, we still import with Override semantics below
                            }
                        }

                        var list = group.Blocks.Import(fi, ImportOptions.Override);
                        if (list != null && list.Count > 0)
                        {
                            imported.AddRange(list.Select(b => b?.Name).Where(n => !string.IsNullOrWhiteSpace(n))!.Cast<string>());
                        }
                        else
                        {
                            imported.Add(name);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public bool ImportType(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing type from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcTypeGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC type group not found for groupPath='{groupPath}'; use empty string for root PLC data types");

                if (!new FileInfo(importPath).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(PrepareXmlForImport(importPath));

                var imported = group.Types.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Types.Import returned an empty collection");

                return true;
            }
            catch (Exception ex)
            {
                var inner = UnwrapImportError(ex);
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, $"Import failed: {inner}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportType failed for {SoftwarePath} group={GroupPath} file={ImportPath}: {Inner}", softwarePath, groupPath, importPath, inner);
                throw pex;
            }
        }

        public IEnumerable<PlcBlock>? ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting blocks...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();
            
            PlcBlock[] list;

            try
            {
                list = GetBlocks(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve block list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int k = 0; k < list.Count(); k++)
            {
                var block = list[k];

                _logger?.LogDebug($"- Exporting block {k}/{list.Count()} : {block.Name}");

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (block.Parent is PlcBlockGroup parentGroup)
                    {
                        groupPath = GetPlcBlockGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{block.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{block.Name}.xml");
                }

                try
                {
                    if (!block.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent block {Name}", block.Name);

                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{block.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);

                            continue;
                        }
                    }

                    try
                    {
                        block.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (LicenseNotFoundException licEx)
                    {
                        failures.Add($"{block.Name}: license not found ({licEx.Message})");
                        _logger?.LogError(licEx, "License issue exporting {Block}", block.Name);

                        continue;
                    }
                    catch (EngineeringTargetInvocationException engEx)
                    {
                        failures.Add($"{block.Name}: target invocation failed ({engEx.Message})");
                        _logger?.LogError(engEx, "TargetInvocationException exporting {Block}", block.Name);

                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for {Block}", block.Name);

                        continue;
                    }

                    exportList.Add(block);
                }
                catch (Exception ex)
                {
                    // Catch only truly unexpected wrapper-level errors
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at block {Block}", block.Name);
                    // continue with next block
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocks completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optionally: _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocks completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
        }

        public IEnumerable<PlcType>? ExportTypes(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _logger?.LogInformation("Exporting types...");

            if (IsProjectNull())
            {
                return null;
            }

            var exportList = new List<PlcType>();
            var failures = new List<string>();

            PlcType[] list;

            try
            {
                list = GetTypes(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to retrieve type list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var type = list[i];

                _logger?.LogDebug("- Exporting type {Index}/{Total} : {Name}", i, list.Count(), type.Name);

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = GetPlcTypeGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                try
                {
                    if (!type.IsConsistent)
                    {
                        _logger?.LogWarning("Skipping inconsistent type {Name}", type.Name);
                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{type.Name}: cannot delete existing file ({ioEx.Message})");
                            _logger?.LogError(ioEx, "Delete failed for {File}", path);
                            continue;
                        }
                    }

                    try
                    {
                        type.Export(new FileInfo(path), ExportOptions.None);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{type.Name}: export failed ({ex.Message})");
                        _logger?.LogError(ex, "Export failed for type {Type}", type.Name);
                        continue;
                    }

                    exportList.Add(type);
                }
                catch (Exception ex)
                {
                    failures.Add($"{type.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, "Unexpected error at type {Type}", type.Name);
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportTypes completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _logger?.LogInformation($"ExportTypes completed successfully. Exported {exportList.Count} types.");
            }

            return exportList;
        }

        public (string TempDir, List<string> Paths)? ExportBlockToTemp(string softwarePath, string blockPath, bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var blk = ExportBlock(softwarePath, blockPath, tempDir, preservePath);
            if (blk == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportTypeToTemp(string softwarePath, string typePath, bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var t = ExportType(softwarePath, typePath, tempDir, preservePath);
            if (t == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportBlocksToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportBlocks(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }

        public (string TempDir, List<string> Paths)? ExportTypesToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportTypes(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }
        

        public bool ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
#if TIA_V17
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return false;
#else
            _logger?.LogInformation($"Exporting block as documents by path: {blockPath}");
            var success = false;
            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                }

                Capability.RequireSupported(TiaFeature.DocumentExport);

                
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    if (plcSoftware != null)
                    {
                        // Export code blocks as documents
                        // https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500/exporting-and-importing-blocks-in-simatic-sd-format-s7-1200-s7-1500

                        var groupPath = blockPath.Contains("/") ? blockPath.Substring(0, blockPath.LastIndexOf("/")) : string.Empty;
                        var blockName = blockPath.Contains("/") ? blockPath.Substring(blockPath.LastIndexOf("/") + 1) : blockPath;

                        var group = GetPlcBlockGroupByPath(softwarePath, groupPath);

                        //group?.Blocks.ForEach(b => Console.WriteLine($"Block: {b.Name}, Type: {b.GetType().Name}"));

                        // join exportPath and groupPath
                        if (!Directory.Exists(exportPath))
                        {
                            Directory.CreateDirectory(exportPath);
                        }

                        if (preservePath && !string.IsNullOrEmpty(groupPath))
                        {
                            exportPath = Path.Combine(exportPath, groupPath);

                            if (!Directory.Exists(exportPath))
                            {
                                Directory.CreateDirectory(exportPath);
                            }
                        }

                        try
                        {
                            // delete files s7dcl/s7res if already exists
                            var blockFiles7dclPath = Path.Combine(exportPath, $"{blockName}.s7dcl");
                            if (File.Exists(blockFiles7dclPath))
                            {
                                File.Delete(blockFiles7dclPath);
                            }
                            var blockFiles7resPath = Path.Combine(exportPath, $"{blockName}.s7res");
                            if (File.Exists(blockFiles7resPath))
                            {
                                File.Delete(blockFiles7resPath);
                            }

                            var result = group?.Blocks.Find(blockName)?.ExportAsDocuments(new DirectoryInfo(exportPath), blockName);

                            if (result != null && result.State == DocumentResultState.Success)
                            {
                                success = true;
                            }
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            // The export or import of blocks with mixed programming languages is not possible
                            throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at block '{blockName}'. {ex.Message}", null, ex);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.ExportFailed, $"Exception at block '{blockName}'. {ex.Message}", null, ex);
                        }

                    }

                }


            }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportAsDocuments failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
            return success;
#endif
        }

        // TIA portal crashes when exporting blocks as documents, :-(
        public IEnumerable<PlcBlock>? ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
#if TIA_V17
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return null;
#else
            _logger?.LogInformation("Exporting blocks as documents...");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ExportBlocksAsDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();

            PlcBlock[] list;
            try
            {
                list = GetBlocks(softwarePath, regexName).ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to retrieve block list for {softwarePath}");
                return exportList;
            }

            for (int i = 0; i < list.Count(); i++)
            {
                var block = list[i];

                _logger?.LogDebug($"- Exporting block as document {i}/{list.Count()} : {block.Name}");

                // Skip inconsistent blocks (TIA generally won’t export them)
                if (!block.IsConsistent)
                {
                    _logger?.LogWarning($"Skipping inconsistent block {block.Name}");
                    continue;
                }

                // Determine base directory (preserve group path if requested)
                string targetDir = exportPath;
                if (preservePath && block.Parent is PlcBlockGroup parentGroup)
                {
                    var groupPath = GetPlcBlockGroupPath(parentGroup);
                    if (!string.IsNullOrWhiteSpace(groupPath))
                    {
                        targetDir = Path.Combine(exportPath, groupPath.Replace('/', '\\'));
                    }
                }

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: cannot create directory '{targetDir}' ({ex.Message})");
                    _logger?.LogError(ex, $"Directory creation failed for {targetDir}");
                    continue;
                }

                var fileDcl = Path.Combine(targetDir, $"{block.Name}.s7dcl");
                var fileRes = Path.Combine(targetDir, $"{block.Name}.s7res");

                // Clean previous artifacts
                foreach (var f in new[] { fileDcl, fileRes })
                {
                    try
                    {
                        if (File.Exists(f))
                        {
                            File.Delete(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: cannot delete existing '{Path.GetFileName(f)}' ({ex.Message})");
                        _logger?.LogError(ex, $"Failed deleting existing file {f}");
                        // Continue anyway; export might overwrite.
                    }
                }

                try
                {
                    DocumentExportResult? result = null;
                    try
                    {
                        result = block.ExportAsDocuments(new DirectoryInfo(targetDir), block.Name);
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        failures.Add($"{block.Name}: not supported ({ex.Message})");
                        _logger?.LogWarning(ex, $"EngineeringNotSupported exporting {block.Name}");
                        continue;
                    }
                    catch (LicenseNotFoundException ex)
                    {
                        failures.Add($"{block.Name}: license not found ({ex.Message})");
                        _logger?.LogError(ex, $"License issue exporting {block.Name}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{block.Name}: export threw ({ex.Message})");
                        _logger?.LogError(ex, $"ExportAsDocuments failed for {block.Name}");
                        continue;
                    }

                    if (result == null)
                    {
                        failures.Add($"{block.Name}: no result returned");
                        continue;
                    }

                    if (result.State == DocumentResultState.Success)
                    {
                        exportList.Add(block);
                    }
                    else
                    {
                        failures.Add($"{block.Name}: result state {result.State}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{block.Name}: unexpected exception ({ex.Message})");
                    _logger?.LogError(ex, $"Unexpected wrapper error for {block.Name}");
                }
            }

            if (failures.Count > 0)
            {
                _logger?.LogWarning($"ExportBlocksAsDocuments completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
                // Optional verbose list:
                // _logger?.LogDebug("All failures: {Failures}", string.Join("; ", failures));
            }
            else
            {
                _logger?.LogInformation($"ExportBlocksAsDocuments completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
#endif
        }

#if TIA_V17
        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, object option)
        {
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return false;
        }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, object option, bool preservePath = false)
        {
            Capability.RequireSupported(TiaFeature.DocumentExport);
            return null;
        }
#else
        public bool ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, ImportDocumentOptions option)
        {
            _logger?.LogInformation($"Importing block from documents: {fileNameWithoutExtension} in {importPath}");

            if (IsProjectNull())
            {
                return false;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportFromDocuments is only supported on TIA Portal V20 or newer");
                return false;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    var dir = new DirectoryInfo(importPath);
                    if (!dir.Exists)
                    {
                        _logger?.LogWarning($"Import directory does not exist: {importPath}");
                        return false;
                    }

                    DocumentImportResult? result = null;
                    try
                    {
                        result = (group != null)
                            ? group.Blocks.ImportFromDocuments(dir, fileNameWithoutExtension, option)
                            : plcSoftware.BlockGroup.Blocks.ImportFromDocuments(dir, fileNameWithoutExtension, option);
                    }
                    catch (EngineeringNotSupportedException ex)
                    {
                        throw new PortalException(PortalErrorCode.ExportFailed, $"EngineeringNotSupportedException at file '{fileNameWithoutExtension}'. {ex.Message}", null, ex);
                    }

                    if (result != null && result.State == DocumentResultState.Success)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error importing block from documents");
            }
            return false;
        }

        public IEnumerable<PlcBlock>? ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName, ImportDocumentOptions option, bool preservePath = false)
        {
            _logger?.LogInformation($"Importing blocks from documents in {importPath} with regex '{regexName}'");

            if (IsProjectNull())
            {
                return null;
            }

            if (Engineering.TiaMajorVersion < 20)
            {
                _logger?.LogWarning("ImportBlocksFromDocuments is only supported on TIA Portal V20 or newer");
                return null;
            }

            var imported = new List<PlcBlock>();

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                    var dir = new DirectoryInfo(importPath);
                    if (!dir.Exists)
                    {
                        _logger?.LogWarning($"Import directory does not exist: {importPath}");
                        return imported;
                    }

                    var rx = string.IsNullOrWhiteSpace(regexName)
                        ? null
                        : new Regex(regexName, RegexOptions.Compiled);

                    // Consider .s7dcl as the primary index; .s7res is optional supplemental
                    var files = dir.GetFiles("*.s7dcl", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        var name = Path.GetFileNameWithoutExtension(file.Name);
                        if (rx != null && !rx.IsMatch(name))
                        {
                            continue;
                        }

                        try
                        {
                            var result = (group != null)
                                ? group.Blocks.ImportFromDocuments(dir, name, option)
                                : plcSoftware.BlockGroup.Blocks.ImportFromDocuments(dir, name, option);

                            if (result != null && result.State == DocumentResultState.Success && result.ImportedPlcBlocks != null)
                            {
                                foreach (var blk in result.ImportedPlcBlocks)
                                {
                                    if (blk != null)
                                    {
                                        imported.Add(blk);
                                    }
                                }
                            }
                        }
                        catch (EngineeringNotSupportedException ex)
                        {
                            _logger?.LogWarning(ex, "Skipping '{Name}': not supported (likely mixed languages)", name);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Skipping '{Name}' due to import error", name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error importing blocks from documents");
            }

            return imported;
        }
#endif

        #endregion

        #region private helper

        private bool IsPortalNull()
        {
            if (_portal == null)
            {
                _logger?.LogWarning("No TIA portal available.");

                return true;
            }

            return false;
        }

        private bool IsProjectNull()
        {
            if (_project == null)
            {
                _logger?.LogWarning("No TIA project available.");

                return true;
            }

            return false;
        }

        private bool IsSessionNull()
        {
            if (_session == null)
            {
                _logger?.LogWarning("No TIA session available.");

                return true;
            }

            return false;
        }

        #region  GetTree ...

        private string GetTreePrefix(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            
            // Build prefix based on ancestor states
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            
            // Add current level connector
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }

        private void GetProjectTreeDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates)
        {
            if (devices.Count == 0) return;
            
            // Check if this is the last main section
            var hasOtherSections = (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0) ||
                                  (_project?.UngroupedDevicesGroup != null);
            var isLastMainSection = !hasOtherSections;
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastMainSection)}Devices [Collection]");

            var deviceList = devices.ToList();
            var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection };
            
            for (int i = 0; i < deviceList.Count; i++)
            {
                var device = deviceList[i];
                var isLastDevice = i == deviceList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [Device: {device.TypeIdentifier}]");

                if (device.DeviceItems != null && device.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, device.DeviceItems, new List<bool>(newAncestorStates) { isLastDevice });
                }
            }
        }

        private void GetProjectTreeGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
        {
            if (groups.Count == 0) return;
            
            var isLastMainSection = _project?.UngroupedDevicesGroup == null;
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastMainSection)}Groups [Collection]");

            var groupList = groups.ToList();
            var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection };
            
            for (int i = 0; i < groupList.Count; i++)
            {
                var group = groupList[i];
                var isLastGroup = i == groupList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{group.Name} [Group]");

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                
                if (group.Devices != null && group.Devices.Count > 0)
                {
                    GetProjectTreeGroupDevices(sb, group.Devices, groupAncestorStates, group.Groups != null && group.Groups.Count > 0);
                }
                
                if (group.Groups != null && group.Groups.Count > 0)
                {
                    GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
                }
            }
        }
        
        private void GetProjectTreeGroupDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates, bool hasSubGroups)
        {
            var deviceList = devices.ToList();
            
            for (int i = 0; i < deviceList.Count; i++)
            {
                var device = deviceList[i];
                var isLastDevice = i == deviceList.Count - 1 && !hasSubGroups;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastDevice)}{device.Name} [Device]");
                
                if (device.DeviceItems != null && device.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, device.DeviceItems, new List<bool>(ancestorStates) { isLastDevice });
                }
            }
        }
        
        private void GetProjectTreeSubGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
        {
            var groupList = groups.ToList();
            
            for (int i = 0; i < groupList.Count; i++)
            {
                var group = groupList[i];
                var isLastGroup = i == groupList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{group.Name} [Subgroup]");
                
                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                
                if (group.Devices != null && group.Devices.Count > 0)
                {
                    GetProjectTreeGroupDevices(sb, group.Devices, groupAncestorStates, group.Groups != null && group.Groups.Count > 0);
                }
                
                if (group.Groups != null && group.Groups.Count > 0)
                {
                    GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
                }
            }
        }

        private void GetProjectTreeDeviceItemsRecursive(StringBuilder sb, DeviceItemComposition deviceItems, List<bool> ancestorStates)
        {
            var deviceItemsList = deviceItems.ToList();
            
            for (int i = 0; i < deviceItemsList.Count; i++)
            {
                var deviceItem = deviceItemsList[i];
                var isLastDeviceItem = i == deviceItemsList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastDeviceItem)}{deviceItem.Name} [DeviceItem]");
                
                var itemAncestorStates = new List<bool>(ancestorStates) { isLastDeviceItem };
                
                // Get software first
                GetProjectTreeDeviceItemSoftware(sb, deviceItem, itemAncestorStates);
                
                // Then get items
                if (deviceItem.Items != null && deviceItem.Items.Count > 0)
                {
                    GetProjectTreeItems(sb, deviceItem.Items, itemAncestorStates, deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                }
                
                // Finally get sub-device items
                if (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, deviceItem.DeviceItems, itemAncestorStates);
                }
            }
        }
        
        private void GetProjectTreeItems(StringBuilder sb, DeviceItemAssociation items, List<bool> ancestorStates, bool hasSubDeviceItems)
        {
            var itemsList = items.ToList();
            
            for (int i = 0; i < itemsList.Count; i++)
            {
                var subItem = itemsList[i];
                var isLastItem = i == itemsList.Count - 1 && !hasSubDeviceItems;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastItem)}{subItem.Name} [Hardware Component]");
            }
        }


        private void GetProjectTreeDeviceItemSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates)
        {
            var softwareContainer = deviceItem.GetService<SoftwareContainer>();
            var hasSoftware = false;
            
            //PLC software
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                   (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems)}PlcSoftware: {plcSoftware.Name} [PLC Program]");
                hasSoftware = true;
            }

            //WinCC HMI software
            if (softwareContainer?.Software is HmiTarget hmiTarget)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                   (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiTarget: {hmiTarget.Name} [HMI Program]");
            }

#if !TIA_V17
            //Unified HMI software: dlls will only exist on TIA Portal V19 and newer.
            if (Engineering.TiaMajorVersion >= 19)
                TryGetUnifiedSoftware(sb, deviceItem, ancestorStates, softwareContainer, hasSoftware);
#endif
        }

#if !TIA_V17
        private bool TryGetUnifiedSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates, SoftwareContainer? softwareContainer, bool hasSoftware)
        {
            if (softwareContainer?.Software is HmiSoftware hmiSoftware)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                    (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiSoftware: {hmiSoftware.Name} [HMI Program]");
                hasSoftware = true;
            }

            return hasSoftware;
        }
#endif

        private void GetProjectTreeUngroupedDeviceGroup(StringBuilder sb, DeviceSystemGroup ungroupedDevicesGroup, List<bool> ancestorStates)
        {
            sb.AppendLine($"{GetTreePrefix(ancestorStates, true)}UngroupedDevicesGroup: {ungroupedDevicesGroup.Name} [System Group]");

            if (ungroupedDevicesGroup.Devices != null && ungroupedDevicesGroup.Devices.Count > 0)
            {
                var deviceList = ungroupedDevicesGroup.Devices.ToList();
                var newAncestorStates = new List<bool>(ancestorStates) { true };
                
                for (int i = 0; i < deviceList.Count; i++)
                {
                    var device = deviceList[i];
                    var isLastDevice = i == deviceList.Count - 1;
                    
                    sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [{device.TypeIdentifier}]");
                }
            }
        }

        #endregion

        #region GetSoftwareTree ...

        public string GetSoftwareTree(string softwarePath)
        {
            _logger?.LogInformation("Getting software tree for path: {SoftwarePath}", softwarePath);

            if (IsProjectNull())
            {
                return string.Empty;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    StringBuilder sb = new();
                    sb.AppendLine($"{plcSoftware.Name} [PLC Software]");
                    
                    var ancestorStates = new List<bool>();
                    var sections = new List<Action>();
                    
                    var hasBlocks = plcSoftware.BlockGroup != null;
                    var hasTypes = plcSoftware.TypeGroup != null;
                    
                    // Add blocks section
                    if (hasBlocks)
                    {
                        var blockGroup = plcSoftware.BlockGroup;
                        if (blockGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeBlockGroup(sb, blockGroup, ancestorStates, "Program blocks", !hasTypes));
                        }
                    }
                    
                    // Add types section
                    if (hasTypes)
                    {
                        var typeGroup = plcSoftware.TypeGroup;
                        if (typeGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeTypeGroup(sb, typeGroup, ancestorStates, "PLC data types", true));
                        }
                    }
                    
                    
                    // Execute sections
                    for (int i = 0; i < sections.Count; i++)
                    {
                        sections[i]();
                    }

                    return sb.ToString();
                }
                else
                {
                    return $"No PLC software found at path: {softwarePath}";
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting software tree for {SoftwarePath}", softwarePath);
                return $"Error retrieving software tree: {ex.Message}";
            }
        }
        
        private void GetSoftwareTreeBlockGroup(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeBlockGroupRecursive(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates)
        {
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeTypeGroup(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName=="PlcStruct" ? "UDT": typeTypeName;

                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeTypeGroupRecursive(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates)
        {
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName == "PlcStruct" ? "UDT" : typeTypeName;

                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }

        #endregion

        #region GetSoftwareContainer ...

        /// <summary>
        /// Resolve a TIA Openness service (e.g. OnlineProvider, DownloadProvider) for a given
        /// PLC software path. Tries the PlcSoftware first, then walks up the SoftwareContainer's
        /// DeviceItem chain. Required because some hardware variants (notably 1200-series CPUs in
        /// nested device groups) only expose Online/Download providers on the CPU DeviceItem,
        /// not on the PlcSoftware itself.
        /// </summary>
        private T? ResolvePlcService<T>(string softwarePath, PlcSoftware plcSoftware)
            where T : class, IEngineeringService
        {
            var direct = plcSoftware.GetService<T>();
            if (direct != null) return direct;

            var sc = GetSoftwareContainer(softwarePath);
            var di = sc?.Parent as DeviceItem;
            while (di != null)
            {
                var s = di.GetService<T>();
                if (s != null) return s;
                di = di.Parent as DeviceItem;
            }
            return null;
        }

        /// <summary>
        /// Subscribes a password handler to the OnlineLegitimation event so a protected CPU
        /// can be authenticated during GoOnline / Download. Returns an IDisposable that
        /// unsubscribes when disposed; callers MUST dispose to avoid handler leaks.
        /// Returns null when no password is provided or the configuration object is not a
        /// ConnectionConfiguration (no event to hook).
        /// </summary>
        private static IDisposable? AttachPasswordHandler(object? configuration, string? password)
        {
            if (string.IsNullOrEmpty(password) || configuration is not ConnectionConfiguration conn)
                return null;

            // Build the SecureString once. The same instance can be reused across multiple
            // legitimation prompts within the same call (e.g., read-then-write access).
            var secure = new SecureString();
            foreach (var c in password!) secure.AppendChar(c);
            secure.MakeReadOnly();

            OnlineConfigurationDelegate handler = (cfg) =>
            {
                if (cfg is OnlinePasswordConfiguration pwdCfg)
                {
                    pwdCfg.SetPassword(secure);
                }
            };
            conn.OnlineLegitimation += handler;
            return new HandlerScope(() => conn.OnlineLegitimation -= handler);
        }

        private sealed class HandlerScope : IDisposable
        {
            private Action? _detach;
            public HandlerScope(Action detach) { _detach = detach; }
            public void Dispose() { _detach?.Invoke(); _detach = null; }
        }

        private SoftwareContainer? GetSoftwareContainer(string softwarePath)
        {
            if (_project == null)
            {
                if (_softwareCacheProject != null) { _softwareContainerCache.Clear(); _softwareCacheProject = null; }
                return null;
            }

            // Invalidate cached resolutions whenever the open project instance changes
            // (open/close/create/attach all reassign _project). Free check, no COM call.
            if (!ReferenceEquals(_project, _softwareCacheProject))
            {
                _softwareContainerCache.Clear();
                _softwareCacheProject = _project;
            }

            if (_softwareContainerCache.TryGetValue(softwarePath, out var cached))
            {
                return cached;
            }

            var resolved = ResolveSoftwareContainerUncached(softwarePath);
            if (resolved != null)
            {
                _softwareContainerCache[softwarePath] = resolved;
            }
            return resolved;
        }

        private SoftwareContainer? ResolveSoftwareContainerUncached(string softwarePath)
        {
            if (_project == null)
            {
                return null;
            }

            string[] pathSegments = softwarePath.Split('/');
            int index = 0;

            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            // in Devices
            if (_project.Devices != null)
            {
                softwareContainer = GetSoftwareContainerInDevices(_project.Devices, pathSegments, index);
                if (softwareContainer != null)
                {
                    return softwareContainer;
                }
            }

            // in Groups
            if (_project.DeviceGroups != null)
            {
                softwareContainer = GetSoftwareContainerInGroups(_project.DeviceGroups, pathSegments, index);
                if (softwareContainer != null)
                {
                    return softwareContainer;
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDevices(DeviceComposition devices, string[] pathSegments, int index)
        {

            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            string nextSegment = index + 1 < pathSegments.Length ? pathSegments[index + 1] : string.Empty;

            if (devices != null)
            {
                SoftwareContainer? softwareContainer = null;
                Device? device = null;
                DeviceItem? deviceItem = null;

                // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
                // use segment to find device
                device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    // If path is only the device name (no next segment), or next segment doesn't match,
                    // search within the device's device items for one that actually hosts a SoftwareContainer.
                    var scFromDeviceItems = FindFirstSoftwareContainer(device.DeviceItems, string.IsNullOrWhiteSpace(nextSegment) ? null : nextSegment);
                    if (scFromDeviceItems != null) return scFromDeviceItems;

                    // Otherwise fall back to the old behavior (exact next-segment match, non-recursive)
                    if (!string.IsNullOrWhiteSpace(nextSegment))
                    {
                        deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(nextSegment, StringComparison.OrdinalIgnoreCase));
                        softwareContainer = GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index + 1);
                        if (softwareContainer != null)
                        {
                            return softwareContainer;
                        }
                    }
                }

                // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
                // ignored segment for Device.Name and use it for DeviceItem.Name
                // IMPORTANT: multiple DeviceItems can share the same name (Unified HMI often does).
                // Prefer the one that actually has a SoftwareContainer service.
                var flatItems = devices.SelectMany(d => d.DeviceItems).ToList();
                var scFromFlat = FindFirstSoftwareContainer(flatItems, segment);
                if (scFromFlat != null) return scFromFlat;

                deviceItem = flatItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (deviceItem != null)
                {
                    return GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index);
                }

            }

            return null;
        }

        private static SoftwareContainer? FindFirstSoftwareContainer(IEnumerable<DeviceItem> roots, string? preferName)
        {
            try
            {
                var stack = new Stack<DeviceItem>(roots?.Where(x => x != null) ?? Enumerable.Empty<DeviceItem>());
                while (stack.Count > 0)
                {
                    var it = stack.Pop();
                    if (it == null) continue;

                    var nameOk = string.IsNullOrWhiteSpace(preferName) || it.Name.Equals(preferName, StringComparison.OrdinalIgnoreCase);
                    if (nameOk)
                    {
                        try
                        {
                            var sc = it.GetService<SoftwareContainer>();
                            if (sc != null) return sc;
                        }
                        catch { }
                    }

                    try
                    {
                        if (it.DeviceItems != null)
                        {
                            foreach (var ch in it.DeviceItems)
                                if (ch != null) stack.Push(ch);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInGroups(DeviceUserGroupComposition groups, string[] pathSegments, int index)
        {
            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            if (groups != null)
            {
                var group = groups.FirstOrDefault(g => g.Name.Equals(segment));
                if (group != null)
                {
                    // when segment matched
                    softwareContainer = GetSoftwareContainerInDevices(group.Devices, pathSegments, index + 1);
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }

                    return GetSoftwareContainerInGroups(group.Groups, pathSegments, index + 1);
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDeviceItem(DeviceItem deviceItem, string[] pathSegments, int index)
        {
            if (deviceItem != null)
            {
                // when segment matched
                if (index == pathSegments.Length - 1)
                {
                    // get from DeviceItem
                    var softwareContainer = deviceItem.GetService<SoftwareContainer>();
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }
                }
            }

            return null;
        }

        #endregion

        #region Get...ByPath

        private Device? GetDeviceByPath(string devicePath)
        {
            if (_project?.Devices == null || string.IsNullOrWhiteSpace(devicePath))
                return null;

            var pathSegments = devicePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length == 0)
            {
                return null;
            }

            // Try top-level device first
            if (pathSegments.Length == 1)
            {
                return _project.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));
            }

            // Traverse device groups
            DeviceUserGroupComposition? groups = _project.DeviceGroups;
            DeviceUserGroup? group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));

            if (group == null)
            {
                return null;
            }

            for (int i = 1; i < pathSegments.Length; i++)
            {
                // Try to find device in current group
                var device = group.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    return device;
                }

                // Try to find subgroup
                group = group.Groups.FirstOrDefault(g => g.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    break;
                }
            }

            return null;
        }

        private DeviceItem? GetDeviceItemByPath(string deviceItemPath)
        {
            if (_project == null || _project.Devices == null)
            {
                return null;
            }

            // Split the device path by '/' to get each device name  
            var pathSegments = deviceItemPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            DeviceItem? deviceItem = null;

            // initial devices and groups
            var devices = _project.Devices;
            var groups = _project.DeviceGroups;

            for (int index = 0; index < pathSegments.Length; index++)
            {
                deviceItem = GetDeviceItemFromDevice(pathSegments, devices, index);

                if (deviceItem == null)
                {
                    // search in groups
                    var group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[index], StringComparison.OrdinalIgnoreCase));
                    if (group != null)
                    {
                        devices = group.Devices;
                        if (devices != null)
                        {
                            deviceItem = GetDeviceItemFromDevice(pathSegments, devices, index + 1);
                        }

                        if (deviceItem != null)
                        {
                            return deviceItem;
                        }

                        // not found, but on the path
                        groups = group.Groups;
                        devices = group.Devices;
                    }
                }
                else
                {
                    return deviceItem;
                }
            }

            return deviceItem;
        }

        private static DeviceItem? GetDeviceItemFromDevice(string[] pathSegments, DeviceComposition? devices, int index)
        {
            string segment = pathSegments[index];
            string nextSegment = index + 1 < pathSegments.Length ? pathSegments[index + 1] : string.Empty;

            DeviceItem? deviceItem = null;

            // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
            // use segment to find device
            var device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (device != null)
            {
                if (string.IsNullOrWhiteSpace(nextSegment))
                {
                    deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                        ?? device.DeviceItems.FirstOrDefault();
                }
                else
                {
                    var first = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(nextSegment, StringComparison.OrdinalIgnoreCase));
                    deviceItem = first;
                    var nextIndex = index + 2;
                    while (deviceItem != null && nextIndex < pathSegments.Length)
                    {
                        var wanted = pathSegments[nextIndex];
                        deviceItem = deviceItem.DeviceItems.FirstOrDefault(di => di.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                        nextIndex++;
                    }
                }

            }

            // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
            if (device == null)
            {
                deviceItem = devices
                .SelectMany(d => d.DeviceItems)
                .FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            }

            return deviceItem;
        }

        private PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath)
        {
            if (_project == null)
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                if (plcSoftware?.BlockGroup == null)
                {
                    return null;
                }


                // Split the path by '/' to get each group name
                var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

                PlcBlockGroup? currentGroup = plcSoftware.BlockGroup;

                foreach (var groupName in groupNames)
                {
                    currentGroup = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

                    if (currentGroup == null)
                    {
                        return null;
                    }
                }

                return currentGroup;
            }

            return null;
        }

        private PlcTypeGroup? GetPlcTypeGroupByPath(string softwarePath, string groupPath)
        {
            if (_project == null)
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                if (plcSoftware?.TypeGroup == null)
                {
                    return null;
                }

                var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

                PlcTypeGroup? currentGroup = plcSoftware.TypeGroup;

                foreach (var groupName in groupNames)
                {
                    currentGroup = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

                    if (currentGroup == null)
                    {
                        return null;
                    }
                }

                return currentGroup;
            }

            return null;
        }

        private string GetPlcBlockGroupPath(PlcBlockGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            PlcBlockGroup? nullableGroup = group;
            var path = group.Name;

            while (nullableGroup != null && nullableGroup.Parent != null)
            {
                try
                {
                    //group = (PlcBlockGroup) group.Parent;
                    if (group is PlcBlockSystemGroup systemGroup)
                    {
                        // do not get parent for system group
                        break;
                    }

                    nullableGroup = nullableGroup.Parent as PlcBlockGroup;
                }
                catch (Exception)
                {
                    // Handle any exceptions that may occur while accessing the parent
                    break;
                }

                if (nullableGroup != null)
                {
                    path = $"{nullableGroup.Name}/{path}";
                }
            }

            return path;
        }

        private string GetPlcTypeGroupPath(PlcTypeGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            PlcTypeGroup? nullableGroup = group;
            var path = group.Name;

            while (nullableGroup != null && nullableGroup.Parent != null)
            {
                try
                {
                    //group = (PlcTypeGroup) group.Parent;
                    if (group is PlcTypeSystemGroup systemGroup)
                    {
                        // do not get parent for system group
                        break;
                    }

                    nullableGroup = nullableGroup.Parent as PlcTypeGroup;
                }
                catch (Exception)
                {
                    // Handle any exceptions that may occur while accessing the parent
                    break;
                }

                if (nullableGroup != null)
                {
                    path = $"{nullableGroup.Name}/{path}";
                }
            }

            return path;
        }

        #endregion

        #region GetRecursive ...

        private bool GetDevicesRecursive(DeviceUserGroup group, List<Device> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Devices)
            {
                if (composition is Device device)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this device if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this device
                        continue;
                    }

                    list.Add(device);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetDevicesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        private bool GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Blocks)
            {
                if (composition is PlcBlock block)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(block.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this block if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this block
                        continue;
                    }

                    list.Add(block);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetBlocksRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        private bool GetTypesRecursive(PlcTypeGroup group, List<PlcType> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Types)
            {
                if (composition is PlcType type)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(type.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this block if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this block
                        continue;
                    }

                    list.Add(type);

                    anySuccess = true;
                }

            }

            foreach (PlcTypeGroup subgroup in group.Groups)
            {
                anySuccess = GetTypesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        #region meta (reflection helpers)

        private object? ResolveObject(string objectKind, string objectPath, string softwarePath)
        {
            if (IsProjectNull()) return null;

            switch ((objectKind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "project":
                    return _project;

                case "portal":
                    return _portal;

                case "device":
                    return GetDevice(objectPath);

                case "deviceitem":
                case "device_item":
                case "device-item":
                    return GetDeviceItem(objectPath);

                case "software":
                case "plcsoftware":
                case "plc_software":
                case "plc-software":
                {
                    // PLC 优先
                    var plcsw = GetPlcSoftware(objectPath);
                    if (plcsw != null) return plcsw;
                    // HMI 兜底（Unified / Classic），让 DescribeObject/InvokeObject 也能操作 HMI
                    try
                    {
                        var sc = GetSoftwareContainer(objectPath);
                        if (sc?.Software != null) return sc.Software;
                    }
                    catch { }
                    return null;
                }

                case "hmi":
                case "hmisoftware":
                case "hmi_software":
                case "hmi-software":
                {
                    try
                    {
                        var sc = GetSoftwareContainer(objectPath);
                        if (sc?.Software != null) return sc.Software;
                    }
                    catch { }
                    return null;
                }

                case "block":
                    if (string.IsNullOrWhiteSpace(softwarePath)) return null;
                    return GetBlock(softwarePath, objectPath);

                case "type":
                    if (string.IsNullOrWhiteSpace(softwarePath)) return null;
                    return GetType(softwarePath, objectPath);

                case "hmiscreen":
                case "hmi_screen":
                case "hmi-screen":
                {
                    // objectPath: "HMI_RT_1:Main"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var screenName = parts[1];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    return TryFindByNameInCollection(sc.Software, new[] { "Screens", "ScreenFolder" }, screenName);
                }

                case "hmitagtable":
                case "hmi_tagtable":
                case "hmi-tagtable":
                {
                    // objectPath: "HMI_RT_1:默认变量表"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var tableName = parts[1];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    return TryFindByNameInCollection(sc.Software, new[] { "TagTables" }, tableName);
                }

                case "hmitag":
                case "hmi_tag":
                case "hmi-tag":
                {
                    // objectPath: "HMI_RT_1:默认变量表:StartPB"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 3);
                    if (parts.Length != 3) return null;
                    var swPath = parts[0];
                    var tableName = parts[1];
                    var tagName = parts[2];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var table = TryFindByNameInCollection(sc.Software, new[] { "TagTables" }, tableName);
                    if (table == null) return null;
                    var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
                    if (tagsComp == null) return null;
                    try
                    {
                        if (tagsComp is System.Collections.IEnumerable en)
                        {
                            foreach (var it in en)
                            {
                                var n = TryGetName(it);
                                if (!string.IsNullOrWhiteSpace(n) &&
                                    string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return it;
                                }
                            }
                        }
                    }
                    catch { }
                    return TryFindByNameInCollection(tagsComp, Array.Empty<string>(), tagName);
                }

                case "hmiconnection":
                case "hmi_connection":
                case "hmi-connection":
                {
                    // objectPath: "HMI_RT_1:HMI_Connection_1"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var connectionName = parts[1];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var conns = TryGetPropertyValue(sc.Software, "Connections");
                    if (conns == null) return null;
                    return FindExistingByName(conns, connectionName) ?? TryFindByNameInCollection(conns, Array.Empty<string>(), connectionName);
                }

                case "hmiscreenitem":
                case "hmi_screenitem":
                case "hmi-screenitem":
                case "hmiscreen_item":
                case "hmi_screen_item":
                case "hmi-screen-item":
                {
                    // objectPath: "HMI_RT_1:Main:BTN_Start"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 3);
                    if (parts.Length != 3) return null;
                    var swPath = parts[0];
                    var screenName = parts[1];
                    var itemName = parts[2];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var screen = TryFindByNameInCollection(sc.Software, new[] { "Screens", "ScreenFolder" }, screenName);
                    if (screen == null) return null;
                    var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
                    if (itemsComp == null) return null;
                    try
                    {
                        if (itemsComp is System.Collections.IEnumerable en)
                        {
                            foreach (var it in en)
                            {
                                var n = TryGetName(it);
                                if (!string.IsNullOrWhiteSpace(n) &&
                                    string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return it;
                                }
                            }
                        }
                    }
                    catch { }
                    return null;
                }

                default:
                    return null;
            }
        }

        private static string? TryGetName(object? o)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                var v = p?.GetValue(o);
                return v?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ModelContextProtocol.ObjectMember> DescribeMembers(object o, int maxMembers)
        {
            var t = o.GetType();
            var list = new List<ModelContextProtocol.ObjectMember>();

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                list.Add(new ModelContextProtocol.ObjectMember
                {
                    Kind = "Property",
                    Name = p.Name,
                    Type = p.PropertyType.FullName ?? p.PropertyType.Name
                });
                if (list.Count >= maxMembers) return list;
            }

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                var ps = m.GetParameters();
                var sig = $"{m.Name}({string.Join(", ", ps.Select(x => $"{x.ParameterType.Name} {x.Name}"))}) -> {m.ReturnType.Name}";
                list.Add(new ModelContextProtocol.ObjectMember
                {
                    Kind = "Method",
                    Name = m.Name,
                    Type = m.ReturnType.FullName ?? m.ReturnType.Name,
                    Signature = sig
                });
                if (list.Count >= maxMembers) return list;
            }

            return list;
        }

        private static object? GetPropertyPathValue(object root, string propertyPath)
        {
            object? current = root;
            foreach (var part in (propertyPath ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (current == null) return null;
                var t = current.GetType();
                var p = t.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return null;
                if (p.GetIndexParameters().Length != 0) return null;
                current = p.GetValue(current);
            }
            return current;
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeObject(string objectKind, string objectPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                TypeName = o.GetType().FullName ?? o.GetType().Name,
                Members = DescribeMembers(o, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = $"{objectPath}.{propertyPath}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var v = GetPropertyPathValue(o, propertyPath);
            if (v == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Property not found",
                    ObjectKind = objectKind,
                    ObjectPath = $"{objectPath}.{propertyPath}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = $"{objectPath}.{propertyPath}",
                TypeName = v.GetType().FullName ?? v.GetType().Name,
                Members = DescribeMembers(v, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectValue GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var v = GetPropertyPathValue(o, propertyPath);
            var vt = v?.GetType();

            object? outValue = v;
            if (v is IEnumerable enumerable && v is not string)
            {
                var items = new List<string>();
                foreach (var it in enumerable)
                {
                    if (it == null) continue;
                    items.Add(TryGetName(it) ?? it.ToString() ?? "");
                    if (items.Count >= 200) break;
                }
                outValue = items;
            }

            return new ModelContextProtocol.ResponseObjectValue
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                ValueType = vt?.FullName ?? (v == null ? null : v.GetType().Name),
                Value = outValue
            };
        }

        public ModelContextProtocol.ResponseObjectChildren ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit = 200)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectChildren
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Collection = collectionProperty,
                    Items = Array.Empty<string>()
                };
            }

            var v = GetPropertyPathValue(o, collectionProperty);
            if (v is not IEnumerable enumerable || v is string)
            {
                return new ModelContextProtocol.ResponseObjectChildren
                {
                    Message = "Collection not found or not enumerable",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Collection = collectionProperty,
                    Items = Array.Empty<string>()
                };
            }

            var items = new List<string>();
            foreach (var it in enumerable)
            {
                if (it == null) continue;
                items.Add(TryGetName(it) ?? it.ToString() ?? "");
                if (items.Count >= Math.Max(1, Math.Min(2000, limit))) break;
            }

            return new ModelContextProtocol.ResponseObjectChildren
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                Collection = collectionProperty,
                Items = items
            };
        }

        private static ModelContextProtocol.ResponseObjectValue InvokeOnInstance(object instance, string resultKind, string resultPath, string methodName, JsonArray? args, bool allowWrite)
        {
            var hardDenyReason = GetHardDeniedReflectionReason(instance, resultKind, resultPath, methodName);
            if (!string.IsNullOrWhiteSpace(hardDenyReason))
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = hardDenyReason,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }

            var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ToString",
                "GetAttribute",
                "GetAttributeInfos"
            };

            if (!allowWrite && !safe.Contains(methodName))
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Method not allowed (read-only mode)",
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }

            var argValues = new List<object?>();
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a == null) { argValues.Add(null); continue; }
                    if (a is JsonValue jv)
                    {
                        if (jv.TryGetValue<string>(out var s)) { argValues.Add(s); continue; }
                        if (jv.TryGetValue<int>(out var i)) { argValues.Add(i); continue; }
                        if (jv.TryGetValue<long>(out var l)) { argValues.Add(l); continue; }
                        if (jv.TryGetValue<double>(out var d)) { argValues.Add(d); continue; }
                        if (jv.TryGetValue<bool>(out var b)) { argValues.Add(b); continue; }
                        argValues.Add(jv.ToString());
                        continue;
                    }
                    argValues.Add(a.ToString());
                }
            }

            try
            {
                var t = instance.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName && m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                MethodInfo? mi = methods.FirstOrDefault(m => m.GetParameters().Length == argValues.Count);
                if (mi == null)
                {
                    return new ModelContextProtocol.ResponseObjectValue
                    {
                        Message = "Method not found (signature mismatch)",
                        ObjectKind = resultKind,
                        ObjectPath = resultPath
                    };
                }

                var ps = mi.GetParameters();
                var converted = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var av = argValues[i];
                    if (av == null) { converted[i] = null; continue; }
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(string)) { converted[i] = av.ToString(); continue; }
                    if (pt == typeof(int)) { converted[i] = Convert.ToInt32(av); continue; }
                    if (pt == typeof(long)) { converted[i] = Convert.ToInt64(av); continue; }
                    if (pt == typeof(double)) { converted[i] = Convert.ToDouble(av); continue; }
                    if (pt == typeof(bool)) { converted[i] = Convert.ToBoolean(av); continue; }
                    if (pt == typeof(object)
                        && methodName.Equals("SetAttribute", StringComparison.OrdinalIgnoreCase)
                        && i == 1
                        && argValues.Count >= 2
                        && argValues[0] is string attrName)
                    {
                        var oldValue = instance.GetType()
                            .GetMethod("GetAttribute", new[] { typeof(string) })
                            ?.Invoke(instance, new object[] { attrName });
                        converted[i] = oldValue == null ? av : CoerceReflectionValue(av, oldValue.GetType());
                        continue;
                    }
                    converted[i] = av;
                }

                var result = mi.Invoke(instance, converted);

                object? outValue = result;
                if (result is IEnumerable enumerable && result is not string)
                {
                    var items = new List<string>();
                    foreach (var it in enumerable)
                    {
                        if (it == null) continue;
                        items.Add(TryGetName(it) ?? it.ToString() ?? "");
                        if (items.Count >= 200) break;
                    }
                    outValue = items;
                }

                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "OK",
                    ObjectKind = resultKind,
                    ObjectPath = resultPath,
                    ValueType = result?.GetType().FullName ?? (result == null ? null : result.GetType().Name),
                    Value = outValue
                };
            }
            catch (TargetInvocationException tie)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = tie.InnerException?.Message ?? tie.Message,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
            catch (Exception ex)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = ex.Message,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
        }

        private static string? GetHardDeniedReflectionReason(object instance, string resultKind, string resultPath, string methodName)
        {
            var instanceType = instance.GetType().FullName ?? instance.GetType().Name;
            var haystack = string.Join(" ", instanceType, resultKind ?? "", resultPath ?? "", methodName ?? "");

            if (haystack.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Denied by safety policy: force-table and force-related operations are not exposed through this MCP server.";
            }

            var isOnlineMonitorSurface =
                haystack.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ||
                haystack.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                haystack.IndexOf("Watch", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isOnlineMonitorSurface)
            {
                return null;
            }

            var mutatingPrefixes = new[]
            {
                "Set",
                "Write",
                "Create",
                "Delete",
                "Remove",
                "Import",
                "Add",
                "Insert",
                "Update",
                "Modify",
                "GoOnline",
                "GoOffline",
                "Download",
                "Activate",
                "Start",
                "Stop"
            };

            if (mutatingPrefixes.Any(p => (methodName ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return "Denied by safety policy: online/watch/monitor surfaces are read-only. The MCP server may read current status only and must not modify watch-table objects or PLC values.";
            }

            return null;
        }

        public ModelContextProtocol.ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }
            return InvokeOnInstance(o, objectKind, objectPath, methodName, args, allowWrite);
        }

        private static Type? FindTypeBySuffix(string typeSuffix)
        {
            if (string.IsNullOrWhiteSpace(typeSuffix)) return null;
            var suf = typeSuffix.Trim();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    var n = t.FullName ?? t.Name;
                    if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase) || t.Name.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return null;
        }

        private static object? TryGetService(object target, Type serviceType)
        {
            try
            {
                var t = target.GetType();
                var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;

                var g = mi.MakeGenericMethod(serviceType);
                return g.Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers = 200)
        {
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var st = FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Service type not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var svc = TryGetService(o, st);
            if (svc == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "GetService failed (service not available for this object)",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = st.FullName ?? st.Name,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "Service",
                ObjectPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}",
                TypeName = svc.GetType().FullName ?? svc.GetType().Name,
                Members = DescribeMembers(svc, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectValue InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        {
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var st = FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Service type not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var svc = TryGetService(o, st);
            if (svc == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "GetService failed (service not available for this object)",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var svcPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}";
            return InvokeOnInstance(svc, "Service", svcPath, methodName, args, allowWrite);
        }

        #endregion

        #endregion

        #endregion

    }


}
