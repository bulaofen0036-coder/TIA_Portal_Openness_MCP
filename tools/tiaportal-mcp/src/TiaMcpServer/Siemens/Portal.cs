using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
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
            worker.Start();
            if (!worker.Join(timeoutMs))
            {
                _logger?.LogWarning($"Attach to TIA Portal PID={proc.Id} exceeded {timeoutMs}ms; skipping (likely orphaned/dying instance).");
                return null;
            }
            if (error != null) throw error;
            return result;
        }

        public bool ConnectPortal()
        {
            _logger?.LogInformation("Connecting to TIA Portal...");

            try
            {
                LastConnectError = null;
                _project = null;
                _session = null;
                _portal = null;

                // connect to running TIA Portal
                var processes = TiaPortal.GetProcesses();
                _logger?.LogInformation($"TIA Portal process count: {processes.Count()}");
                if (processes.Any())
                {
                    // IMPORTANT: multiple Siemens.Automation.Portal.exe can run at once.
                    // Attaching to processes.First() is unstable and often attaches to an instance
                    // without the user's open project, causing "project already opened by user" errors.
                    //
                    // Strategy:
                    // - Try attach each process
                    // - Prefer the first instance that exposes LocalSessions/Projects (i.e. has an open project)
                    // - Otherwise fall back to the first attachable instance
                    TiaPortal? firstAttachable = null;
                    string? firstAttachableInfo = null;

                    foreach (var proc in processes)
                    {
                        TiaPortal? candidate = null;
                        try
                        {
                            _logger?.LogInformation($"Trying attach to TIA Portal process PID={proc.Id}");
                            candidate = AttachWithTimeout(proc, 30000);
                            _logger?.LogInformation(candidate == null
                                ? $"Attach returned null/timed out for PID={proc.Id} — skipping"
                                : $"Attach succeeded for PID={proc.Id}");
                            if (candidate == null) continue;

                            // record first attachable in case none has projects
                            if (firstAttachable == null)
                            {
                                firstAttachable = candidate;
                                firstAttachableInfo = $"PID={proc.Id}";
                            }

                            // Prefer instance with an open project/session
                            bool hasSession = false;
                            bool hasProject = false;
                            try { hasSession = candidate.LocalSessions.Any(); } catch { }
                            try { hasProject = candidate.Projects.Any(); } catch { }
                            _logger?.LogInformation($"Portal PID={proc.Id}: hasSession={hasSession}, hasProject={hasProject}");

                            if (hasSession || hasProject)
                            {
                                _portal = candidate;
                                _logger?.LogInformation($"Selected attached TIA Portal PID={proc.Id}");

                                if (hasSession)
                                {
                                    try
                                    {
                                        _session = _portal.LocalSessions.First();
                                        _project = _session.Project;
                                    }
                                    catch { }
                                }

                                if (_project == null && hasProject)
                                {
                                    try { _project = _portal.Projects.First(); } catch { }
                                }

                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, $"Attach failed for TIA Portal PID={proc.Id}");
                            LastConnectError = ex.ToString();
                        }
                        finally
                        {
                            // If this candidate wasn't selected and isn't firstAttachable, dispose it.
                            if (candidate != null && candidate != _portal && candidate != firstAttachable)
                            {
                                try { candidate.Dispose(); } catch { }
                            }
                        }
                    }

                    // fallback to first attachable instance
                    if (firstAttachable != null)
                    {
                        _portal = firstAttachable;
                        _logger?.LogInformation($"Falling back to first attachable TIA Portal ({firstAttachableInfo})");
                        LastConnectError = $"Attached to first available portal ({firstAttachableInfo}), but it has no visible projects/sessions.";
                        return true;
                    }

                    LastConnectError = "No attachable TIA Portal process found; starting a new TIA Portal instance.";
                    _logger?.LogInformation(LastConnectError);
                }

                // start new TIA Portal. Headless (WithoutUserInterface) is the default because it
                // starts far faster than booting the full GUI; --with-ui flips it for visual inspection.
                var launchMode = Engineering.LaunchWithUserInterface
                    ? TiaPortalMode.WithUserInterface
                    : TiaPortalMode.WithoutUserInterface;
                _logger?.LogInformation($"Starting a new TIA Portal instance ({launchMode}).");
                _portal = new TiaPortal(launchMode);

                return true;
            }
            catch (Exception ex)
            {
                // 统一错误处理：硬失败抛结构化异常，替代 return false + LastConnectError 侧信道
                throw new PortalException(PortalErrorCode.OpennessError, $"ConnectPortal failed: {FormatExceptionDetail(ex)}", inner: ex);
            }
        }

        public List<string> ListPortalProcessProjects()
        {
            var lines = new List<string>();
            IReadOnlyList<TiaPortalProcess> processes;
            try
            {
                processes = TiaPortal.GetProcesses().ToList();
            }
            catch (Exception ex)
            {
                lines.Add("GetProcesses error: " + FormatExceptionDetail(ex));
                return lines;
            }

            lines.Add("TIA Portal process count: " + processes.Count);
            foreach (var proc in processes)
            {
                TiaPortal? candidate = null;
                try
                {
                    lines.Add("PID=" + proc.Id + " attach: trying");
                    candidate = proc.Attach();
                    if (candidate == null)
                    {
                        lines.Add("PID=" + proc.Id + " attach: <null>");
                        continue;
                    }

                    lines.Add("PID=" + proc.Id + " attach: OK");
                    try
                    {
                        var any = false;
                        foreach (var s in candidate.LocalSessions)
                        {
                            any = true;
                            lines.Add("PID=" + proc.Id + " sessionProject=" + (s.Project?.Name ?? "<null>"));
                        }
                        if (!any) lines.Add("PID=" + proc.Id + " sessions=<empty>");
                    }
                    catch (Exception ex)
                    {
                        lines.Add("PID=" + proc.Id + " sessions error: " + FormatExceptionDetail(ex));
                    }

                    try
                    {
                        var any = false;
                        foreach (var p in candidate.Projects)
                        {
                            any = true;
                            lines.Add("PID=" + proc.Id + " project=" + (p?.Name ?? "<null>"));
                        }
                        if (!any) lines.Add("PID=" + proc.Id + " projects=<empty>");
                    }
                    catch (Exception ex)
                    {
                        lines.Add("PID=" + proc.Id + " projects error: " + FormatExceptionDetail(ex));
                    }
                }
                catch (Exception ex)
                {
                    lines.Add("PID=" + proc.Id + " attach error: " + FormatExceptionDetail(ex));
                }
                finally
                {
                    if (candidate != null && candidate != _portal)
                    {
                        try { candidate.Dispose(); } catch { }
                    }
                }
            }

            return lines;
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
            _logger?.LogInformation($"Attaching to open project: {projectName}");

            if (string.IsNullOrWhiteSpace(projectName)) return false;
            projectName = projectName.Trim();

            // Connect 之后 TIA 的 LocalSessions / Projects 是异步填充的，
            // 这里轮询最多 15s，避免 Connect+Attach 并行或刚启动时刷出 false。
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                try
                {
                    if (_portal != null && TryAttachProjectInPortal(_portal, projectName))
                    {
                        return true;
                    }

                    foreach (var proc in TiaPortal.GetProcesses())
                    {
                        try
                        {
                            var candidate = proc.Attach();
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
                        catch { }
                    }
                }
                catch { }

                if (DateTime.UtcNow >= deadline) break;
                System.Threading.Thread.Sleep(500);
            }

            return false;
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

            if (IsPortalNull())
            {
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
                return _project != null;
            }
            catch (Exception ex)
            {
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

        // #region devices — moved to Portal.Devices.cs

        // #region software — moved to Portal.Software.cs

        // #region alarms — moved to Portal.Alarms.cs

        // #region opcua — moved to Portal.OpcUa.cs

        // #region download — moved to Portal.Download.cs

        // #region online — moved to Portal.Online.cs

        // #region blocks/types — moved to Portal.Blocks.cs

        // #region private helper — moved to Portal.Helpers.cs

    }


}
