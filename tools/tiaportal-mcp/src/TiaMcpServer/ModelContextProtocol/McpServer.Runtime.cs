using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.ModelContextProtocol
{
    // Runtime (live-value) read-only tools. These use direct CPU protocols
    // (S7 / OPC UA), NOT TIA Openness — Openness is an engineering API and cannot
    // read live values. All tools here are strictly read-only: no writes, no force,
    // no CPU mode change.
    public static partial class McpServer
    {
        internal static List<string> ParseItemSpecs(string itemsJson)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(itemsJson)) return list;
            string s = itemsJson.Trim();
            if (s.StartsWith("["))
            {
                try
                {
                    var arr = JsonNode.Parse(s) as JsonArray;
                    if (arr != null)
                    {
                        foreach (var n in arr)
                        {
                            var v = n?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) list.Add(v!.Trim());
                        }
                        return list;
                    }
                }
                catch { /* fall through to delimiter split */ }
            }
            foreach (var part in s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = part.Trim();
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        [McpServerTool(Name = "ProbeS7CpuIdentity"), Description("[L2][Online-Monitoring] Read-only: connect to a physical CPU over the S7 protocol (ISO-on-TCP, port 102) and read its identification (module type, serial, names). Does NOT write, force, or change CPU mode. Use this first to confirm the IP belongs to the intended PLC before reading values. S7-1200/1500 use rack 0, slot 1.")]
        public static ResponseJsonReport ProbeS7CpuIdentity(
            [Description("ip: CPU IP address, e.g. '192.168.0.32'.")] string ip,
            [Description("rack: hardware rack number. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot number. S7-1200/1500 = 1; S7-300/400 often 2.")] int slot = 1)
        {
            try
            {
                var id = S7LiveReader.ProbeIdentity(ip, rack, slot);
                var data = new JsonObject
                {
                    ["ip"] = ip,
                    ["rack"] = rack,
                    ["slot"] = slot,
                    ["connected"] = id.Connected,
                    ["moduleTypeName"] = id.ModuleTypeName,
                    ["asName"] = id.AsName,
                    ["moduleName"] = id.ModuleName,
                    ["serialNumber"] = id.SerialNumber,
                    ["pduLength"] = id.PduLength,
                    ["channel"] = "S7 / ISO-on-TCP (read-only)"
                };
                if (id.Error != null) data["error"] = id.Error;
                if (id.SzlError != null) data["szlError"] = id.SzlError;
                bool ok = id.Connected && id.Error == null;
                string idText = string.IsNullOrWhiteSpace(id.ModuleTypeName)
                    ? "connected (CPU model not reported by SZL — normal on S7-1200)"
                    : id.ModuleTypeName;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? $"S7 CPU at {ip}: {idText}" : (id.Error ?? "Could not connect to CPU."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"S7 identity probe failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ReadPlcLiveValuesS7"), Description("[L2][Online-Monitoring] Read-only FAST live values from a physical CPU over the S7 protocol (port 102), independent of TIA Openness. Give absolute S7 addresses (DB10.DBD0:REAL, DB1.DBX2.3, M0.0, MW12, DB5.DBD8:DINT). Returns current values in one round-trip (typically tens of ms). NEVER writes/forces. Preconditions for S7-1200/1500: enable 'Permit PUT/GET access' on the CPU and read NON-optimized DBs (M/I/Q are unrestricted). Use expectModuleContains to hard-guard the target identity.")]
        public static ResponseJsonReport ReadPlcLiveValuesS7(
            [Description("ip: CPU IP address, e.g. '192.168.0.32'.")] string ip,
            [Description("itemsJson: JSON array or comma-separated list of absolute S7 addresses, e.g. [\"DB10.DBD0:REAL\",\"DB10.DBD4:REAL\",\"M0.0\"].")] string itemsJson,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("expectModuleContains: optional identity guard. If set (e.g. '1211C'), the read aborts unless the CPU module type contains this substring.")] string expectModuleContains = "",
            [Description("devicePath: optional device name (e.g. 'PLC_1') for a PUT/GET precheck. When set, the read is skipped with a clear, actionable message if the CPU has remote PUT/GET access disabled, instead of failing with a TCP timeout.")] string devicePath = "")
        {
            try
            {
                var specs = ParseItemSpecs(itemsJson);
                if (specs.Count == 0)
                    return new ResponseJsonReport { Ok = false, Message = "No addresses supplied in itemsJson." };

                // Dependency check: an S7 DB read is impossible if the CPU forbids remote PUT/GET.
                // Surface that as an actionable result rather than a bare TCP timeout.
                if (!string.IsNullOrWhiteSpace(devicePath))
                {
                    try
                    {
                        var pg = Portal.GetPutGetAccess(devicePath);
                        if ((pg["found"]?.GetValue<bool>() ?? false) && !(pg["enabled"]?.GetValue<bool>() ?? false))
                        {
                            return new ResponseJsonReport
                            {
                                Ok = false,
                                Message = $"PUT/GET access is DISABLED on '{devicePath}' (attribute '{pg["attributeName"]}'). S7 absolute DB reads will fail. " +
                                          $"Enable it: SetPutGetAccess(devicePath:'{devicePath}', enable:true) then DownloadToPlc — or read M/I/Q which are unrestricted.",
                                Data = new JsonObject { ["putGetAccess"] = pg, ["precheck"] = "putget-disabled" },
                                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                            };
                        }
                    }
                    catch { /* precheck is best-effort; fall through to the read */ }
                }

                var r = S7LiveReader.ReadItems(ip, rack, slot, specs, string.IsNullOrWhiteSpace(expectModuleContains) ? null : expectModuleContains);

                var items = new JsonArray();
                foreach (var it in r.Items)
                {
                    var o = new JsonObject
                    {
                        ["spec"] = it.Spec,
                        ["area"] = it.Area,
                        ["type"] = it.Type
                    };
                    if (it.Area == "DB") o["db"] = it.Db;
                    o["byte"] = it.ByteOffset;
                    if (it.Type == "BOOL") o["bit"] = it.BitOffset;
                    if (it.Error != null) o["error"] = it.Error;
                    else o["value"] = JsonValue.Create(it.Value);
                    items.Add(o);
                }

                var data = new JsonObject
                {
                    ["ip"] = ip,
                    ["rack"] = rack,
                    ["slot"] = slot,
                    ["channel"] = "S7 / ISO-on-TCP (read-only)",
                    ["elapsedMs"] = r.ElapsedMs,
                    ["identityConfirmed"] = r.IdentityConfirmed,
                    ["identity"] = new JsonObject
                    {
                        ["moduleTypeName"] = r.Identity.ModuleTypeName,
                        ["asName"] = r.Identity.AsName,
                        ["moduleName"] = r.Identity.ModuleName,
                        ["serialNumber"] = r.Identity.SerialNumber,
                        ["szlError"] = r.Identity.SzlError
                    },
                    ["items"] = items,
                    ["safety"] = new JsonObject { ["readOnly"] = true, ["writesValues"] = false, ["usesForce"] = false, ["changesCpuMode"] = false }
                };
                if (r.Error != null) data["error"] = r.Error;

                bool ok = r.Ok && r.Error == null && r.Items.All(i => i.Error == null);
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = r.Error != null
                        ? r.Error + "  HINT: if the CPU IS reachable, this is commonly because remote PUT/GET access is disabled " +
                          "(TIA: CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication'), or a wrong rack/slot."
                        : (ok
                            ? $"Read {r.Items.Count} live value(s) from {ip} in {r.ElapsedMs} ms."
                            : $"Connected to {ip}, but one or more items failed — a DB read failing here usually means the DB is OPTIMIZED " +
                              "(no absolute access) or PUT/GET is disabled; M/I/Q are unrestricted. See items[].error."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"S7 live read failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SamplePlcLiveValuesS7"), Description("[L2][Online-Monitoring] Read-only TREND sampling over the S7 protocol (port 102): on one open connection, read a set of absolute S7 addresses every intervalMs for up to durationMs (hard-capped at 120 s / 5000 samples), returning a time series per address plus min/max/avg of the numeric ones. Ideal for capturing a PID step response or watching a signal move over time. NEVER writes/forces. The call BLOCKS for ~durationMs while it samples. Same address syntax and identity guard as ReadPlcLiveValuesS7.")]
        public static ResponseJsonReport SamplePlcLiveValuesS7(
            [Description("ip: CPU IP address, e.g. '192.168.0.32'.")] string ip,
            [Description("itemsJson: JSON array or comma-separated list of absolute S7 addresses, e.g. [\"DB10.DBD0:REAL\",\"M0.0\"].")] string itemsJson,
            [Description("intervalMs: sampling period in ms (clamped to >= 20).")] int intervalMs = 100,
            [Description("durationMs: total sampling window in ms (clamped to <= 120000).")] int durationMs = 5000,
            [Description("maxSamples: hard cap on sample count (default 600, max 5000).")] int maxSamples = 600,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("expectModuleContains: optional identity guard substring (e.g. '1211C').")] string expectModuleContains = "")
        {
            try
            {
                var specs = ParseItemSpecs(itemsJson);
                if (specs.Count == 0)
                    return new ResponseJsonReport { Ok = false, Message = "No addresses supplied in itemsJson." };

                var r = S7LiveReader.SampleItems(ip, rack, slot, specs, intervalMs, durationMs, maxSamples,
                    string.IsNullOrWhiteSpace(expectModuleContains) ? null : expectModuleContains);

                var series = new JsonArray();
                foreach (var s in r.Series)
                {
                    var o = new JsonObject { ["spec"] = s.Spec, ["area"] = s.Area, ["type"] = s.Type };
                    if (s.Area == "DB") o["db"] = s.Db;
                    if (s.Error != null) o["error"] = s.Error;
                    else
                    {
                        var vals = new JsonArray();
                        foreach (var v in s.Values) vals.Add(v == null ? null : JsonValue.Create(v));
                        o["values"] = vals;
                        if (s.Min != null) { o["min"] = s.Min; o["max"] = s.Max; o["avg"] = s.Avg; }
                    }
                    series.Add(o);
                }
                var tsArr = new JsonArray();
                foreach (var t in r.TimestampsMs) tsArr.Add(t);

                var data = new JsonObject
                {
                    ["ip"] = ip,
                    ["channel"] = "S7 / ISO-on-TCP (read-only trend)",
                    ["sampleCount"] = r.SampleCount,
                    ["requestedIntervalMs"] = r.RequestedIntervalMs,
                    ["actualElapsedMs"] = r.ActualElapsedMs,
                    ["identityConfirmed"] = r.IdentityConfirmed,
                    ["timestampsMs"] = tsArr,
                    ["series"] = series,
                    ["safety"] = new JsonObject { ["readOnly"] = true, ["writesValues"] = false, ["usesForce"] = false, ["changesCpuMode"] = false }
                };
                if (r.Error != null) data["error"] = r.Error;

                bool ok = r.Ok && r.Error == null && r.Series.All(s => s.Error == null);
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = r.Error ?? $"Sampled {r.Series.Count} signal(s) x {r.SampleCount} samples over {r.ActualElapsedMs} ms from {ip}.",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"S7 trend sampling failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "TraceTagCause"), Description("[L2][Online-Monitoring] Answer 'why is tag X this value / what sets it' by static analysis of the OFFLINE project. Read-only: exports code blocks to SimaticML and finds every network that WRITES the tag (LAD coils S/R/=, or StructuredText ':=' assignments) plus the gating condition operands in those networks. Cross-reference service is not needed. Then live-read the returned gatingConditions with ReadPlcLiveValuesS7 to see which condition is currently driving the value. Tip: pass blockScope to limit which blocks are scanned (faster). NOTE: block export needs the project OFFLINE in TIA — Openness cannot export blocks while it is connected online to the PLC.")]
        public static ResponseJsonReport TraceTagCause(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. '安全PLC'.")] string softwarePath,
            [Description("tag: symbol to trace. Accepts a full path ('Crew_Data.Saddle_locationX') or a member name ('故障代码'). Quotes/whitespace are ignored.")] string tag,
            [Description("blockScope: optional regex to limit scanned code blocks by name (e.g. 'FC|Main'). Empty scans all code blocks.")] string blockScope = "")
        {
            try
            {
                var result = Portal.TraceTagCause(softwarePath, tag, blockScope);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"TraceTagCause failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "TraceTagCauseLive"), Description("[L2][Online-Monitoring] Live causal trace: runs the offline TraceTagCause to find what writes a tag and its gating conditions, then resolves each gating operand to an absolute address via the PLC tag table and LIVE-READS the resolvable ones over S7 (port 102) — so you see which interlock/condition is currently TRUE and driving the value. Read-only. Operands that are DB members / optimized / symbolic have no absolute PLC-tag address and are returned unresolved (read those via OPC UA). Requires the CPU ip; for the offline-only trace use TraceTagCause. NOTE: the offline block export needs the project OFFLINE in TIA (Openness cannot export in online mode); the S7 live-read is a separate direct connection, unaffected by going offline.")]
        public static ResponseJsonReport TraceTagCauseLive(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. '安全PLC'.")] string softwarePath,
            [Description("tag: symbol to trace (full path or member name; quotes/whitespace ignored).")] string tag,
            [Description("ip: CPU IP address for the live read, e.g. '192.168.0.32'.")] string ip,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("blockScope: optional regex to limit scanned code blocks by name. Empty scans all.")] string blockScope = "",
            [Description("expectModuleContains: optional identity guard substring (e.g. '1211C').")] string expectModuleContains = "")
        {
            try
            {
                var result = Portal.TraceTagCauseLive(softwarePath, tag, ip, rack, slot, blockScope, expectModuleContains);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"TraceTagCauseLive failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ReadPlcLiveValuesOpcUa"), Description("[L2][Online-Monitoring] Read-only live values from a CPU's OPC UA server (default opc.tcp port 4840), independent of TIA Openness. Anonymous, no-security session; reads the Value attribute of each node. NEVER writes or calls methods. Node IDs use OPC UA syntax, e.g. 'ns=3;s=\"DB10\".\"X\"' or 'i=2258'. Precondition: the CPU's OPC UA server must be enabled and the variables exposed (Runtime license on S7-1200/1500). If the server is off, connection is refused (reported cleanly).")]
        public static ResponseJsonReport ReadPlcLiveValuesOpcUa(
            [Description("endpointUrl: OPC UA endpoint, e.g. 'opc.tcp://192.168.0.10:4840'.")] string endpointUrl,
            [Description("nodeIdsJson: JSON array or comma-separated list of OPC UA NodeIds, e.g. [\"ns=3;s=\\\"DB10\\\".\\\"X\\\"\",\"ns=3;s=\\\"DB10\\\".\\\"Y\\\"\"].")] string nodeIdsJson,
            [Description("timeoutMs: overall read timeout in milliseconds.")] int timeoutMs = 5000)
        {
            try
            {
                var ids = ParseItemSpecs(nodeIdsJson);
                if (ids.Count == 0)
                    return new ResponseJsonReport { Ok = false, Message = "No node IDs supplied in nodeIdsJson." };

                var r = OpcUaLiveReader.ReadNodes(endpointUrl, ids, timeoutMs);
                var items = new JsonArray();
                foreach (var it in r.Items)
                {
                    var o = new JsonObject { ["nodeId"] = it.NodeId };
                    if (it.Error != null) o["error"] = it.Error;
                    else o["value"] = JsonValue.Create(it.Value?.ToString());
                    if (it.StatusCode != null) o["statusCode"] = it.StatusCode;
                    items.Add(o);
                }

                var data = new JsonObject
                {
                    ["endpoint"] = endpointUrl,
                    ["channel"] = "OPC UA (read-only, anonymous, no-security)",
                    ["elapsedMs"] = r.ElapsedMs,
                    ["reusedSession"] = r.ReusedSession,
                    ["items"] = items,
                    ["safety"] = new JsonObject { ["readOnly"] = true, ["writesValues"] = false, ["callsMethods"] = false }
                };
                if (r.Error != null) data["error"] = r.Error;

                bool ok = r.Ok && r.Error == null && r.Items.All(i => i.Error == null);
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = r.Error ?? (ok
                        ? $"Read {r.Items.Count} node(s) from {endpointUrl} in {r.ElapsedMs} ms."
                        : $"Connected to {endpointUrl}, but one or more nodes failed (see items[].error)."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"OPC UA live read failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcRunStateS7"), Description("[L2][Online-Monitoring] Read-only: read the CPU operating mode (RUN / STOP / UNKNOWN) over the S7 protocol (port 102) — something TIA Openness cannot do. Also returns the CPU clock and a best-effort raw diagnostic-buffer dump (event IDs in hex + raw bytes; full event text needs TIA's text database, and SZL may be unavailable on some S7-1200 CPUs — reported cleanly). Never writes/forces/changes mode.")]
        public static ResponseJsonReport GetPlcRunStateS7(
            [Description("ip: CPU IP address, e.g. '192.168.0.32'.")] string ip,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("maxDiagEntries: max diagnostic-buffer records to return (default 20; 0 = all returned by CPU).")] int maxDiagEntries = 20,
            [Description("expectModuleContains: optional identity guard substring (e.g. '1211C').")] string expectModuleContains = "")
        {
            try
            {
                var r = S7LiveReader.ReadRunState(ip, rack, slot, maxDiagEntries,
                    string.IsNullOrWhiteSpace(expectModuleContains) ? null : expectModuleContains);

                var diag = new JsonArray();
                foreach (var e in r.DiagEntries)
                    diag.Add(new JsonObject { ["index"] = e.Index, ["eventId"] = e.EventId, ["raw"] = e.Raw });

                var data = new JsonObject
                {
                    ["ip"] = ip,
                    ["channel"] = "S7 / ISO-on-TCP (read-only)",
                    ["runState"] = r.Status,
                    ["plcDateTime"] = r.PlcDateTime,
                    ["moduleTypeName"] = r.Identity.ModuleTypeName,
                    ["szlError"] = r.Identity.SzlError,
                    ["diagnosticRecordCount"] = r.DiagRecordCount,
                    ["diagnosticEntries"] = diag,
                    ["diagnosticNote"] = r.DiagNote,
                    ["elapsedMs"] = r.ElapsedMs,
                    ["safety"] = new JsonObject { ["readOnly"] = true, ["writesValues"] = false, ["usesForce"] = false, ["changesCpuMode"] = false }
                };
                if (r.Error != null) data["error"] = r.Error;

                bool ok = r.Connected && r.Error == null;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = r.Error ?? $"CPU at {ip} is {r.Status}" + (r.DiagRecordCount > 0 ? $"; {r.DiagRecordCount} diagnostic record(s)." : "."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPlcRunStateS7 failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "MonitorWatchTableLiveS7"), Description("[L2][Online-Monitoring] Read-only: live-monitor an existing TIA watch table. Reads the table's entry addresses (Openness, read-only) and live-reads them over the S7 protocol (port 102). Returns name/address/value rows. Never edits the watch table, writes, or forces. Absolute-address entries (e.g. %DB1.DBW0, %MW4) are read; symbolic/optimized entries are listed as unresolved (use OPC UA for those). Identity guard via expectModuleContains. Use GetPlcWatchTables to list table names.")]
        public static ResponseJsonReport MonitorWatchTableLiveS7(
            [Description("softwarePath: PLC software path, e.g. '安全PLC'.")] string softwarePath,
            [Description("watchTableName: existing watch table name from GetPlcWatchTables.")] string watchTableName,
            [Description("ip: CPU IP address, e.g. '192.168.0.32'.")] string ip,
            [Description("rack: hardware rack. S7-1200/1500 = 0.")] int rack = 0,
            [Description("slot: hardware slot. S7-1200/1500 = 1.")] int slot = 1,
            [Description("expectModuleContains: optional identity guard (e.g. '1211C'); aborts on a positive module-type mismatch.")] string expectModuleContains = "")
        {
            try
            {
                var result = Portal.MonitorWatchTableLiveS7(softwarePath, watchTableName, ip, rack, slot, expectModuleContains);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"MonitorWatchTableLiveS7 failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
