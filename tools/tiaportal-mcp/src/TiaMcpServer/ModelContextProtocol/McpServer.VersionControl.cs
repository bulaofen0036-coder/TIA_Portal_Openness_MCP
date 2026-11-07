#if !TIA_V19
using ModelContextProtocol.Server;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // TIA Portal V21's Version Control Interface (VCI), reached from Openness rather than the UI.
    //
    // A TIA project is a binary blob that Git cannot diff. VCI fixes that: a *workspace* is a
    // plain folder holding one text file per mapped object (.s7dcl / .xml), which IS diffable and
    // commitable. Openness can create workspaces and synchronize in both directions, so the whole
    // "export → commit → review → restore" loop can run unattended.
    //
    // MAPPING IS AUTOMATABLE — an earlier reading of this API said otherwise and was wrong.
    // MappedObjectComposition indeed exposes only Find, but the create path does not live on the
    // composition: it is Workspace.ConnectObject(obj, relativeDir, fileName, fileFormat), with
    // Workspace.GetSupportedFileFormats(obj) telling you whether an object can be mapped at all.
    // ConnectProjectToWorkspace below uses both to put a WHOLE project under version control with
    // no UI interaction. Lesson: before declaring an Openness capability missing, enumerate every
    // member of the namespace — 'Create' is often on the parent, not on the collection.
    //
    // The generic reflection tools cannot reach any of this: they navigate properties from an
    // object, and VersionControlInterface is a *service*, so the traversal dead-ends immediately.
    // Hence a purpose-built toolset.
    public static partial class McpServer
    {
        // The VCI service must be kept alive for the whole session. Openness disposes the objects
        // reached through a service once that service instance is collected, so re-acquiring it on
        // every call makes workspaces obtained in an earlier call throw
        // "Access to a disposed object of type Workspace" — observed, not theoretical.
        private static object? _vciOwnerProject;
        private static VersionControlInterface? _vciCached;

        private static VersionControlInterface RequireVci()
        {
            var project = Portal.CurrentProject;
            if (project == null)
            {
                _vciOwnerProject = null;
                _vciCached = null;
                _vciKeepAlive.Clear();
                throw new InvalidOperationException(
                    "No project is open. Call Connect, then AttachToOpenProject / OpenProject first.");
            }
            if (_vciCached != null && ReferenceEquals(_vciOwnerProject, project))
                return _vciCached;

            var vci = (project as IEngineeringServiceProvider)?.GetService<VersionControlInterface>();
            if (vci == null)
                throw new InvalidOperationException(
                    "This project exposes no VersionControlInterface. VCI requires TIA Portal V21 or later.");
            _vciKeepAlive.Clear();
            _vciOwnerProject = project;
            _vciCached = vci;
            Keep(vci);
            return vci;
        }

        // Openness hands out COM-backed proxies whose lifetime is tied to the parent they came from.
        // Let an intermediate (WorkspaceGroup, or the service itself) get collected and every object
        // reached through it dies with it — "Access to a disposed object of type Workspace". So every
        // intermediate stays rooted here for as long as the project is open. Observed, not theoretical.
        private static readonly List<object> _vciKeepAlive = new List<object>();

        private static T Keep<T>(T o) where T : class
        {
            if (o != null) _vciKeepAlive.Add(o);
            return o!;
        }

        /// <summary>All workspaces, walking the system group and any nested user groups. Nothing here is lazy:
        /// a yield-return iterator would let the groups be collected between MoveNext calls.</summary>
        private static List<Workspace> AllWorkspaces(VersionControlInterface vci)
        {
            var found = new List<Workspace>();
            var pending = new Stack<WorkspaceGroup>();
            pending.Push(Keep(vci.WorkspaceGroup));
            while (pending.Count > 0)
            {
                var g = pending.Pop();
                foreach (var w in Keep(g.Workspaces)) found.Add(Keep(w));
                foreach (var sub in Keep(g.Groups)) pending.Push(Keep(sub));
            }
            return found;
        }

        private static Workspace FindWorkspace(VersionControlInterface vci, string name)
        {
            var all = AllWorkspaces(vci).ToList();
            if (all.Count == 0)
                throw new InvalidOperationException(
                    "This project has no version control workspace yet. Create one with " +
                    "CreateVersionControlWorkspace, then map objects into it with " +
                    "ConnectProjectToWorkspace (no UI interaction needed).");
            if (string.IsNullOrWhiteSpace(name)) return all[0];
            var hit = all.FirstOrDefault(w => string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                throw new InvalidOperationException(
                    "No workspace named '" + name + "'. Available: " +
                    string.Join(", ", all.Select(w => w.Name)));
            return hit;
        }

        [McpServerTool(Name = "GetVersionControlWorkspaces"), Description(
            "[L1][VersionControl] List this project's version control (VCI) workspaces: name, folder on disk, " +
            "language, and how many objects are mapped. A workspace is the plain-text mirror of the project " +
            "that Git can actually diff and commit. Read-only. Requires TIA V21+ and an open project.")]
        public static ResponseStringList GetVersionControlWorkspaces()
        {
            try
            {
                var vci = RequireVci();
                var lines = new List<string>();
                int n = 0;
                foreach (var w in AllWorkspaces(vci))
                {
                    n++;
                    int mapped = 0;
                    try { mapped = w.MappedObjects.Count; } catch { }
                    string root = "";
                    try { root = w.RootPath?.FullName ?? ""; } catch { }
                    lines.Add(string.Format(
                        "{0} | folder={1} | mappedObjects={2} | language={3}",
                        w.Name, root, mapped, SafeLanguage(w)));
                }
                return new ResponseStringList
                {
                    Message = n == 0
                        ? "No version control workspace exists in this project yet. Create one with " +
                          "CreateVersionControlWorkspace, then map objects into it in the TIA UI."
                        : n + " version control workspace(s).",
                    Items = lines,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "GetVersionControlWorkspaces failed: " + ex.Message,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                };
            }
        }

        private static string SafeLanguage(Workspace w)
        {
            try { return w.WorkspaceLanguage?.ToString() ?? "-"; }
            catch { return "-"; }
        }

        [McpServerTool(Name = "CreateVersionControlWorkspace"), Description(
            "[L2][VersionControl] Create a VCI workspace pointing at a folder on disk — normally the working " +
            "tree of a Git repository, so every synchronized export lands where Git can commit it. " +
            "Creating the workspace does NOT map any objects into it — call ConnectProjectToWorkspace " +
            "afterwards to map the whole project (or one device) automatically. " +
            "Requires TIA V21+ and an open project.")]
        public static ResponseMessage CreateVersionControlWorkspace(
            [Description("workspaceName: name shown in the TIA project tree, e.g. 'git'.")] string workspaceName,
            [Description("folderPath: existing folder the text files are written to, e.g. 'D:\\\\repos\\\\crane-plc'. Use your Git working tree.")] string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workspaceName))
                    throw new ArgumentException("workspaceName is required.");
                if (string.IsNullOrWhiteSpace(folderPath))
                    throw new ArgumentException("folderPath is required.");
                var dir = new DirectoryInfo(folderPath.Trim());
                if (!dir.Exists)
                    throw new DirectoryNotFoundException(
                        "folderPath does not exist: " + dir.FullName + ". Create the folder (or clone the repo) first.");

                var vci = RequireVci();
                var existing = AllWorkspaces(vci)
                    .FirstOrDefault(w => string.Equals(w.Name, workspaceName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    return new ResponseMessage
                    {
                        Message = "A workspace named '" + workspaceName + "' already exists. " +
                                  "Use GetVersionControlWorkspaces to inspect it.",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                    };

                var group = Keep(vci.WorkspaceGroup);
                var ws = Keep(Keep(group.Workspaces).Create(workspaceName.Trim(), dir));
                return new ResponseMessage
                {
                    Message = "Created workspace '" + ws.Name + "' at " + dir.FullName +
                              ". Next: ConnectProjectToWorkspace to map the project's objects into it, " +
                              "then SyncVersionControlWorkspace to write them out.",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true },
                };
            }
            catch (Exception ex)
            {
                return new ResponseMessage
                {
                    Message = "CreateVersionControlWorkspace failed: " + ex.Message,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                };
            }
        }

        [McpServerTool(Name = "GetVersionControlStatus"), Description(
            "[L1][VersionControl] Per-object status of a VCI workspace: which mapped objects differ between the " +
            "TIA project and the text files on disk. This is the input for a change log — it names exactly what " +
            "changed before you commit. Read-only, changes nothing. " +
            "Status values: Equal (in sync), Unequal (project and file differ), WorkspaceFileMissing (never exported), Unknown.")]
        public static ResponseStringList GetVersionControlStatus(
            [Description("workspaceName: which workspace. Empty = the first one in the project.")] string workspaceName = "",
            [Description("changedOnly: default true — list only objects that are NOT in sync. false lists every mapped object.")] bool changedOnly = true)
        {
            try
            {
                var vci = RequireVci();
                var ws = FindWorkspace(vci, workspaceName);

                var lines = new List<string>();
                int total = 0, differing = 0;
                foreach (var mo in Keep(ws.MappedObjects))
                {
                    total++;
                    string status;
                    // GetStatus() returns an IndividualObjectCompareResult; ToString() on it yields the type
                    // name, not the verdict. The verdict is CompareState (Equal / Unequal / WorkspaceFileMissing).
                    try { status = mo.GetStatus().CompareState.ToString(); }
                    catch (Exception ex) { status = "Unknown(" + ex.Message + ")"; }
                    bool inSync = string.Equals(status, "Equal", StringComparison.OrdinalIgnoreCase);
                    if (!inSync) differing++;
                    if (changedOnly && inSync) continue;
                    lines.Add(string.Format("{0} | {1} | file={2}{3}",
                        SafeName(mo), status, SafeFile(mo), SafeFormat(mo)));
                }

                return new ResponseStringList
                {
                    Message = string.Format(
                        "Workspace '{0}': {1} mapped object(s), {2} differ from the workspace files.{3}",
                        ws.Name, total, differing,
                        differing == 0
                            ? " Project and workspace are in sync — nothing to commit."
                            : " Call SyncVersionControlWorkspace(direction='ProjectToWorkspace') to write the changes out, then commit."),
                    Items = lines,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "GetVersionControlStatus failed: " + ex.Message,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                };
            }
        }

        private static string SafeName(MappedObject mo)
        {
            try { return mo.FileNameWithoutExtension ?? "?"; } catch { return "?"; }
        }

        private static string SafeFile(MappedObject mo)
        {
            try
            {
                string d = "";
                try { d = mo.DirectoryPath?.FullName ?? ""; } catch { }
                string f = mo.FileNameWithoutExtension ?? "";
                return string.IsNullOrEmpty(d) ? f : d.TrimEnd('\\', '/') + "\\" + f;
            }
            catch { return "?"; }
        }

        private static string SafeFormat(MappedObject mo)
        {
            try { return " | format=" + mo.FileFormat; } catch { return ""; }
        }

        [McpServerTool(Name = "SyncVersionControlWorkspace"), Description(
            "[L1][VersionControl] Synchronize a VCI workspace. direction='ProjectToWorkspace' writes the TIA " +
            "project's objects out as text files (do this before `git commit`); 'WorkspaceToProject' reads the " +
            "text files back INTO the project (do this after `git pull` / to restore a reviewed version). " +
            "DEFAULTS TO dryRun=true: the default call only reports what WOULD be synchronized. " +
            "WorkspaceToProject OVERWRITES blocks in the open project — compile and save afterwards, " +
            "and it requires a Pro license (exporting is free).")]
        public static ResponseStringList SyncVersionControlWorkspace(
            [Description("direction: 'ProjectToWorkspace' (export, for committing) or 'WorkspaceToProject' (import, for restoring).")] string direction = "ProjectToWorkspace",
            [Description("workspaceName: which workspace. Empty = the first one in the project.")] string workspaceName = "",
            [Description("dryRun: DEFAULT true — only reports what would change. Pass false to actually synchronize.")] bool dryRun = true,
            [Description("changedOnly: default true — synchronize only objects whose status is not Equal. false forces every mapped object.")] bool changedOnly = true)
        {
            try
            {
                SynchronizationMode mode;
                string d = (direction ?? "").Trim();
                if (d.Equals("ProjectToWorkspace", StringComparison.OrdinalIgnoreCase)) mode = SynchronizationMode.ProjectToWorkspace;
                else if (d.Equals("WorkspaceToProject", StringComparison.OrdinalIgnoreCase)) mode = SynchronizationMode.WorkspaceToProject;
                else
                    return new ResponseStringList
                    {
                        Message = "direction must be 'ProjectToWorkspace' (export for commit) or " +
                                  "'WorkspaceToProject' (import to restore); got '" + direction + "'.",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                    };

                // Tiering is by DIRECTION, and it is the same boundary in both builds: exporting
                // (project -> text files) is free; importing (text files -> project) OVERWRITES blocks
                // in the engineer's project, so it belongs to the commercial build.
                if (mode == SynchronizationMode.WorkspaceToProject
#if COMMERCIAL
                    && !Licensing.Entitlement.IsProTier()
#endif
                    )
                    return new ResponseStringList
                    {
                        Message = "direction='WorkspaceToProject' (restoring a Git version back INTO the project) " +
                                  "overwrites blocks in the open project and is a commercial-tier operation. " +
                                  "Everything else is free — CreateVersionControlWorkspace, ConnectProjectToWorkspace, " +
                                  "GetVersionControlStatus and SyncVersionControlWorkspace(direction='ProjectToWorkspace') " +
                                  "— so you can put the project under version control, see exactly what changed, " +
                                  "export it as text and commit it.",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                    };

                var vci = RequireVci();
                var ws = FindWorkspace(vci, workspaceName);

                // Openness REFUSES to synchronize a mapping whose compare status is Equal
                // ("Synchronize cannot be called on a workspace mapping that has a compare status of
                // equal"), so "force every object" is not a thing that exists — asking for it just
                // produced one failure per object. Equal is always skipped; changedOnly only decides
                // whether objects whose status could not be determined are attempted anyway.
                var targets = new List<MappedObject>();
                int skippedEqual = 0;
                foreach (var mo in Keep(ws.MappedObjects))
                {
                    string st;
                    try { st = mo.GetStatus().CompareState.ToString(); } catch { st = "Unknown"; }
                    if (string.Equals(st, "Equal", StringComparison.OrdinalIgnoreCase)) { skippedEqual++; continue; }
                    if (changedOnly && string.Equals(st, "Unknown", StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(mo);
                }

                if (targets.Count == 0)
                    return new ResponseStringList
                    {
                        Message = "Workspace '" + ws.Name + "': nothing to synchronize — every mapped object is already in sync.",
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true },
                    };

                var lines = new List<string>();
                if (dryRun)
                {
                    foreach (var mo in targets) lines.Add(SafeName(mo) + " | would sync " + mode);
                    return new ResponseStringList
                    {
                        Message = string.Format(
                            "DRY RUN — nothing was written. {0} object(s) would be synchronized {1} in workspace '{2}' " +
                            "(folder {3}). Call again with dryRun=false to do it.",
                            targets.Count, mode, ws.Name, SafeRoot(ws)),
                        Items = lines,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true },
                    };
                }

                int ok = 0, failed = 0;
                foreach (var mo in targets)
                {
                    try { mo.Synchronize(mode); ok++; lines.Add(SafeName(mo) + " | synchronized"); }
                    catch (Exception ex) { failed++; lines.Add(SafeName(mo) + " | FAILED: " + ex.Message); }
                }

                return new ResponseStringList
                {
                    Message = string.Format(
                        "Workspace '{0}' ({1}): {2} synchronized, {3} failed, {6} already equal (skipped). Folder: {4}.{5}",
                        ws.Name, mode, ok, failed, SafeRoot(ws),
                        mode == SynchronizationMode.ProjectToWorkspace
                            ? " The text files are updated — `git add -A && git commit` from that folder."
                            : " The project now holds the workspace's version — compile and save to persist it.",
                        skippedEqual),
                    Items = lines,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = failed == 0 },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "SyncVersionControlWorkspace failed: " + ex.Message,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                };
            }
        }

        // ----------------------------------------------------------- whole-project auto mapping

        private sealed class VcNode
        {
            public IEngineeringObject Obj = null!;
            public string Label = "";    // human path in the project tree
            public string RelDir = "";   // folder inside the workspace ("" = workspace root)
            public bool Descendable;     // may we walk into it when VCI cannot map it as a unit?
        }

        private static string Flatten(string s)
            => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string ObjName(IEngineeringObject o)
        {
            try
            {
                var v = o.GetAttribute("Name");
                var text = v?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text!;
            }
            catch { }
            return o.GetType().Name;
        }

        private static string SanitizePathPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "_";
            var bad = Path.GetInvalidFileNameChars();
            return new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        /// <summary>Pick the most Git-friendly of the formats VCI offers for an object.</summary>
        private static string? PreferredFormat(IList<string> formats)
        {
            return formats.FirstOrDefault(f => f.IndexOf("s7dcl", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault(f => f.IndexOf("simatic", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault(f => f.IndexOf("xml", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? formats.FirstOrDefault();
        }

        /// <summary>
        /// Typed children of a node. The generic reflection bridge (GetComposition) is deliberately NOT used:
        /// it hands back transient proxies that Openness disposes immediately, so every object reached that way
        /// throws "Access to a disposed object" on first use. Typed compositions stay valid.
        /// </summary>
        private static List<VcNode> TypedChildren(VcNode node)
        {
            var kids = new List<VcNode>();
            string dir = string.IsNullOrEmpty(node.RelDir) ? "" : node.RelDir;

            void Add(IEngineeringObject o, string subDir, bool descendable)
            {
                string nm = ObjName(o);
                kids.Add(new VcNode
                {
                    Obj = o,
                    Label = node.Label + "/" + nm,
                    RelDir = subDir,
                    Descendable = descendable,
                });
            }

            string Under(string name)
            {
                string part = SanitizePathPart(name);
                if (string.IsNullOrEmpty(dir)) return part;
                // Device, its DeviceItem and its PlcSoftware are usually all called "PLC_1" — one level is enough.
                if (string.Equals(Path.GetFileName(dir), part, StringComparison.OrdinalIgnoreCase)) return dir;
                return Path.Combine(dir, part);
            }

            switch (node.Obj)
            {
                case Project proj:
                    foreach (var d in proj.Devices) Add(d, dir, true);
                    foreach (var g in proj.DeviceGroups) Add(g, dir, true);
                    break;

                case DeviceUserGroup dg:
                    foreach (var d in dg.Devices) Add(d, Under(ObjName(dg)), true);
                    foreach (var g in dg.Groups) Add(g, Under(ObjName(dg)), true);
                    break;

                case Device dev:
                    foreach (var di in dev.DeviceItems) Add(di, Under(ObjName(dev)), true);
                    break;

                case DeviceItem di2:
                    foreach (var sub in di2.DeviceItems) Add(sub, dir, true);
                    var sc = di2.GetService<SoftwareContainer>();
                    var sw = sc?.Software as IEngineeringObject;
                    if (sw != null) Add(sw, dir, true);
                    break;

                case PlcSoftware plc:
                    Add(plc.BlockGroup, Under(ObjName(plc)), true);
                    Add(plc.TagTableGroup, Under(ObjName(plc)), true);
                    Add(plc.TypeGroup, Under(ObjName(plc)), true);
                    break;

                case PlcBlockGroup bg:
                    foreach (var b in bg.Blocks) Add(b, dir, false);
                    foreach (var g in bg.Groups) Add(g, Under(ObjName(bg)), true);
                    break;

                case PlcTagTableGroup tg:
                    foreach (var t in tg.TagTables) Add(t, dir, false);
                    foreach (var g in tg.Groups) Add(g, Under(ObjName(tg)), true);
                    break;

                case PlcTypeGroup ty:
                    foreach (var t in ty.Types) Add(t, dir, false);
                    foreach (var g in ty.Groups) Add(g, Under(ObjName(ty)), true);
                    break;
            }
            return kids;
        }

        [McpServerTool(Name = "ConnectProjectToWorkspace"), Description(
            "[L2][VersionControl] Put a WHOLE project under version control automatically - no TIA UI clicks. " +
            "Walks the project tree, asks every object whether VCI can map it (Workspace.GetSupportedFileFormats) " +
            "and maps each supported object with Workspace.ConnectObject. COARSE-FIRST: when a device or PLC " +
            "software object is mappable as one unit it is mapped whole and its children are not visited, so you " +
            "get the fewest mappings that still cover everything. Objects VCI does not support (typically hardware " +
            "configuration) are reported, never silently dropped. " +
            "DEFAULTS TO dryRun=true: the default call only reports what it WOULD map. " +
            "After a real run call SyncVersionControlWorkspace(ProjectToWorkspace, dryRun=false), then git commit.")]
        public static ResponseStringList ConnectProjectToWorkspace(
            [Description("workspaceName: which workspace to map into. Empty = the first one in the project.")] string workspaceName = "",
            [Description("dryRun: DEFAULT true - reports what would be mapped and changes nothing. Pass false to actually map.")] bool dryRun = true,
            [Description("deviceFilter: map only this device (exact name, e.g. 'PLC_1'). Empty = the whole project.")] string deviceFilter = "",
            [Description("maxObjects: safety cap on how many tree nodes are visited. Default 3000.")] int maxObjects = 3000,
            [Description("walkTrace: write one stderr line per visited node. Diagnostic only.")] bool walkTrace = false)
        {
            try
            {
                var project = Portal.CurrentProject;
                if (project == null) throw new InvalidOperationException("No project is open.");

                var ws = FindWorkspace(RequireVci(), workspaceName);
                string wsName = ws.Name;
                string wsRootPath = SafeRoot(ws);

                // An Openness call that throws DISPOSES the objects involved: after one
                // "The Object is not supported" the Workspace handle itself is dead. Since asking about an
                // unsupported object is a normal part of the sweep, re-acquire the handle after every failure.
                Workspace ReAcquire()
                {
                    _vciCached = null;
                    _vciOwnerProject = null;
                    _vciKeepAlive.Clear();
                    return FindWorkspace(RequireVci(), wsName);
                }

                var lines = new List<string>();
                int mapped = 0, already = 0, failed = 0, unsupported = 0, visited = 0;
                bool truncated = false;

                var stack = new Stack<VcNode>();
                stack.Push(new VcNode
                {
                    Obj = (IEngineeringObject)project,
                    Label = ObjName((IEngineeringObject)project),
                    RelDir = "",
                    Descendable = true,
                });

                while (stack.Count > 0)
                {
                    if (visited >= maxObjects) { truncated = true; break; }
                    var node = stack.Pop();
                    visited++;

                    if (walkTrace)
                        Console.Error.WriteLine("[VCI-walk] #" + visited + " " + node.Obj.GetType().Name + " :: " + node.Label);

                    if (!string.IsNullOrWhiteSpace(deviceFilter)
                        && node.Obj is Device
                        && !string.Equals(ObjName(node.Obj), deviceFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    IList<string> formats;
                    try
                    {
                        var f = ws.GetSupportedFileFormats(node.Obj);
                        formats = f == null ? new List<string>() : f.ToList();
                    }
                    catch (Exception ex)
                    {
                        formats = new List<string>();
                        if (walkTrace) Console.Error.WriteLine("[VCI-walk]     query threw: " + ex.Message.Split('\n')[0]);
                        ws = ReAcquire();   // the throw killed the handle
                    }

                    if (walkTrace)
                        Console.Error.WriteLine("[VCI-walk]     formats=[" + string.Join(",", formats) + "] descendable=" + node.Descendable);

                    if (formats.Count > 0)
                    {
                        string fmt = PreferredFormat(formats) ?? formats[0];
                        string name = SanitizePathPart(ObjName(node.Obj));

                        MappedObject? existing = null;
                        try { existing = ws.MappedObjects.Find(node.Obj); }
                        catch { ws = ReAcquire(); }

                        if (existing != null)
                        {
                            already++;
                            lines.Add(node.Label + " | already mapped");
                            continue;
                        }

                        if (dryRun)
                        {
                            mapped++;
                            lines.Add(node.Label + " | would map | format=" + fmt +
                                      " | dir=" + (string.IsNullOrEmpty(node.RelDir) ? "<root>" : node.RelDir));
                        }
                        else
                        {
                            try
                            {
                                // ExportObject - not ConnectObject - is the call that maps an object: it writes
                                // the text file AND creates the mapping. ConnectObject only binds an object to
                                // files that ALREADY exist ("Missing Mandatory files") and rejects relative paths
                                // ("cannot be a relative path") despite the parameter name. Verified against V21.
                                // Sub-folders are refused on this build ("Relative Directory Path is Invalid"),
                                // so fall back to a flat layout at the workspace root, folding the project path
                                // into the file name so nothing collides.
                                string rel = node.RelDir ?? "";
                                bool flat = false;
                                if (!string.IsNullOrEmpty(rel))
                                {
                                    string abs = Path.Combine(wsRootPath, rel);
                                    try
                                    {
                                        Directory.CreateDirectory(abs);
                                        ws.ExportObject(node.Obj, new DirectoryInfo(abs), name, fmt);
                                    }
                                    catch (Exception subEx)
                                    {
                                        if (walkTrace) Console.Error.WriteLine("[VCI-walk]     subdir refused (" + Flatten(subEx.Message) + ") -> flat");
                                        ws = ReAcquire();
                                        flat = true;
                                    }
                                }
                                else flat = true;

                                if (flat)
                                {
                                    string flatName = SanitizePathPart(
                                        (string.IsNullOrEmpty(rel) ? "" : rel.Replace(Path.DirectorySeparatorChar, '_') + "_") + ObjName(node.Obj));
                                    if (walkTrace)
                                        Console.Error.WriteLine("[VCI-walk]     ExportObject root name='" + flatName + "' fmt='" + fmt + "'");
                                    ws.ExportObject(node.Obj, new DirectoryInfo(wsRootPath), flatName, fmt);
                                    name = flatName;
                                }
                                mapped++;
                                lines.Add(node.Label + " | mapped | format=" + fmt +
                                          " | dir=" + (string.IsNullOrEmpty(node.RelDir) ? "<root>" : node.RelDir));
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                lines.Add(node.Label + " | FAILED: " + Flatten(ex.Message) +
                                          (ex.InnerException != null ? " || inner: " + Flatten(ex.InnerException.Message) : ""));
                                ws = ReAcquire();
                            }
                        }
                        continue;   // coarse-first: a mapped object owns its children
                    }

                    if (!node.Descendable)
                    {
                        unsupported++;
                        lines.Add(node.Label + " | not supported by VCI (" + node.Obj.GetType().Name + ")");
                        continue;
                    }

                    List<VcNode> kids;
                    try { kids = TypedChildren(node); }
                    catch (Exception ex)
                    {
                        kids = new List<VcNode>();
                        lines.Add(node.Label + " | could not enumerate children: " + ex.Message.Split('\n')[0]);
                    }
                    if (walkTrace) Console.Error.WriteLine("[VCI-walk]     children=" + kids.Count);
                    foreach (var kid in kids) stack.Push(kid);
                }

                string head = dryRun
                    ? string.Format("DRY RUN - nothing was mapped. {0} object(s) would be mapped into workspace '{1}' ({2}); " +
                                    "{3} already mapped, {4} not supported by VCI, {5} tree nodes visited.{6} " +
                                    "Call again with dryRun=false to map them.",
                                    mapped, wsName, wsRootPath, already, unsupported, visited,
                                    truncated ? " ** stopped at maxObjects - raise maxObjects for full coverage **" : "")
                    : string.Format("Workspace '{0}' ({1}): {2} newly mapped, {3} already mapped, {4} failed, " +
                                    "{5} not supported by VCI, {6} tree nodes visited.{7} " +
                                    "Next: SyncVersionControlWorkspace(direction='ProjectToWorkspace', dryRun=false), then git commit. " +
                                    "Save the project to persist the mappings.",
                                    wsName, wsRootPath, mapped, already, failed, unsupported, visited,
                                    truncated ? " ** stopped at maxObjects - raise maxObjects for full coverage **" : "");

                return new ResponseStringList
                {
                    Message = head,
                    Items = lines,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = failed == 0 },
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "ConnectProjectToWorkspace failed: " + ex.Message,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false },
                };
            }
        }

        private static string SafeRoot(Workspace ws)
        {
            try { return ws.RootPath?.FullName ?? "?"; } catch { return "?"; }
        }
    }
}
#endif
