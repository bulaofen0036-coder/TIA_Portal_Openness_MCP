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
    // Partial: software. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
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

            // Low-barrier fallback: tolerate a sloppy softwarePath (wrong case / extra spaces /
            // a single-PLC project / a unique substring like "PLC" -> "PLC_1"). Exact resolution
            // above is tried first, so this only runs when it misses.
            return ResolvePlcSoftwareFuzzy(softwarePath);
        }

        private PlcSoftware? ResolvePlcSoftwareFuzzy(string softwarePath)
        {
            var all = GetAllPlcSoftware();
            if (all.Count == 0) return null;

            var matched = Guard.MatchPlcName(all.Select(p => p.Name).ToList(), softwarePath);
            if (matched == null) return null;

            return all.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.Ordinal))
                ?? all.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.OrdinalIgnoreCase));
        }

        // Enumerate every PlcSoftware in the open project (devices + device groups), de-duplicated by
        // name. Used for tolerant softwarePath resolution and "Available PLC paths" error hints.
        public List<PlcSoftware> GetAllPlcSoftware()
        {
            var result = new List<PlcSoftware>();
            if (_project == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Collect(IEnumerable<DeviceItem>? roots)
            {
                if (roots == null) return;
                var stack = new Stack<DeviceItem>(roots.Where(x => x != null));
                while (stack.Count > 0)
                {
                    var it = stack.Pop();
                    if (it == null) continue;
                    try
                    {
                        if (it.GetService<SoftwareContainer>()?.Software is PlcSoftware plc &&
                            !string.IsNullOrEmpty(plc.Name) && seen.Add(plc.Name))
                        {
                            result.Add(plc);
                        }
                    }
                    catch { }
                    try { if (it.DeviceItems != null) foreach (var ch in it.DeviceItems) if (ch != null) stack.Push(ch); } catch { }
                }
            }

            void WalkDevices(DeviceComposition? devices)
            {
                if (devices == null) return;
                foreach (var d in devices) { try { Collect(d.DeviceItems); } catch { } }
            }
            void WalkGroups(DeviceUserGroupComposition? groups)
            {
                if (groups == null) return;
                foreach (var g in groups) { try { WalkDevices(g.Devices); WalkGroups(g.Groups); } catch { } }
            }

            try { WalkDevices(_project.Devices); } catch { }
            try { WalkGroups(_project.DeviceGroups); } catch { }
            return result;
        }

        // " Available PLC paths: a, b, c" suffix for not-found error messages (empty when none).
        public string AvailablePlcPathsSuffix()
        {
            try
            {
                var names = GetAllPlcSoftware().Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                return names.Count > 0 ? " Available PLC paths: " + string.Join(", ", names) : string.Empty;
            }
            catch { return string.Empty; }
        }

        public List<string>? GetPlcTagTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;

            // common shapes: plc.TagTables OR plc.TagTableGroup.TagTables
            object? tables =
                TryGetPropertyValue(plc, "TagTables") ??
                TryGetPropertyValue(TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ?? plc, "TagTables");

            if (tables == null) return new List<string>();
            return TryListNamesFromCollection(tables, new[] { "TagTables" }, "TagTables");
        }

        public bool ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            if (IsProjectNull()) return false;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return false;

            object? tablesRoot = TryGetPropertyValue(plc, "TagTables") ??
                                 TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ??
                                 plc;

            var table = TryFindByNameInCollection(tablesRoot, new[] { "TagTables" }, tagTableName);
            if (table == null) return false;
            return TryExportEngineeringObject(table, exportPath, out _);
        }

        public void ImportPlcTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");

            try
            {
                object root = TryGetPropertyValue(plc, "TagTableGroup", "TagTableFolder") ?? plc;
                var group = TryResolveChildGroupByPath(root, folderPath) ?? root;

                // TagTables collection lives on group
                var tables = TryGetPropertyValue(group, "TagTables") ?? TryGetPropertyValue(root, "TagTables");
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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

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

            // Unified (HmiSoftware)
            if (sw is HmiSoftware unified)
            {
                return (unified.Name, "Unified", TryListScreens(unified));
            }

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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

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
    }
}
