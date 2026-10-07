using Microsoft.Extensions.Logging;
using Siemens.Engineering.Online;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: online operations (GoOnline / GoOffline / GetOnlineState / Compare).
    // Extracted from Portal.cs to keep that monolith from growing further.
    public partial class Portal
    {
        public ResponseOnlineState GetOnlineState(string softwarePath)
        {
            // 这两条原来都返回 State="Offline"。那是**给一个没测过的问题一个确定的答案**：
            // 调用方读 isOnline=false 会当成「已确认这台 PLC 不在线」，而真相是
            // 「没连项目」或「路径写错，压根没这台 PLC」。比不回答更糟。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetOnlineState: no project is open, so the online state was NOT measured. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GetOnlineState: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    // 服务拿不到 = 测不了，不是"离线"。用 Unknown 如实表达（本文件下方
                    // 的异常分支早就是这么写的，这里跟它对齐）。
                    return new ResponseOnlineState
                    {
                        State = "Unknown",
                        IsOnline = false,
                        IsReachable = false,
                        Message = "OnlineProvider service not available on this PLC, so the online state was NOT measured. "
                                + "State 'Unknown' means undetermined — it does NOT mean the CPU is offline."
                    };
                }

                var state = provider.State;
                var stateName = state.ToString();
                bool isOnline = stateName == "Online";
                bool isReachable = isOnline || stateName == "Protected";

                return new ResponseOnlineState
                {
                    State = stateName,
                    IsOnline = isOnline,
                    IsReachable = isReachable,
                    Message = BuildOnlineStateMessage(stateName, softwarePath)
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetOnlineState failed for {SoftwarePath}", softwarePath);
                return new ResponseOnlineState { State = "Unknown", IsOnline = false, IsReachable = false, Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseOnlineState GoOnline(string softwarePath, string? ipAddress = null, string? password = null)
        {
            // 这两条原来都返回 State="Offline"。那是**给一个没测过的问题一个确定的答案**：
            // 调用方读 isOnline=false 会当成「已确认这台 PLC 不在线」，而真相是
            // 「没连项目」或「路径写错，压根没这台 PLC」。比不回答更糟。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GoOnline: no project is open, so the online state was NOT measured. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GoOnline: PLC software not found at '{softwarePath}', so the online state was NOT measured."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    // 服务拿不到 = 测不了，不是"离线"。用 Unknown 如实表达（本文件下方
                    // 的异常分支早就是这么写的，这里跟它对齐）。
                    return new ResponseOnlineState
                    {
                        State = "Unknown",
                        IsOnline = false,
                        IsReachable = false,
                        Message = "OnlineProvider service not available on this PLC, so the online state was NOT measured. "
                                + "State 'Unknown' means undetermined — it does NOT mean the CPU is offline."
                    };
                }

                using var passwordScope = AttachPasswordHandler(provider.Configuration, password);

                // OnlineProvider.GoOnline() takes no address. The documented way to pick the target
                // is ConnectionConfiguration.ApplyConfiguration(...) on OnlineProvider.Configuration,
                // then GoOnline() (Openness manual, "Setting connection parameters"). This used to look
                // for a GoOnline(address) overload that does not exist and silently fell back to the
                // configured address, so an ipAddress override was ignored without a word.
                string routeNote = "";
                if (!string.IsNullOrWhiteSpace(ipAddress))
                {
                    // ApplyConfiguration throws while a connection is up (same manual page).
                    if (provider.State == OnlineState.Online)
                        throw new PortalException(PortalErrorCode.InvalidState,
                            $"GoOnline: '{softwarePath}' is already online, so the route to {ipAddress} cannot be applied. "
                            + "Call GoOffline first, then GoOnline with the ipAddress.");

                    var route = SelectDownloadRoute(provider.Configuration, null, ipAddress);
                    if (route.Error != null)
                        throw new PortalException(PortalErrorCode.InvalidParams,
                            "GoOnline: " + route.Error + ". Openness can only choose among the target addresses the "
                            + "project already configures; to reach a different IP, change it in the hardware configuration first.");
                    if (!route.Applied)
                        throw new PortalException(PortalErrorCode.OpennessError,
                            $"GoOnline: ApplyConfiguration did not accept any route to {ipAddress} "
                            + $"(candidates: {DescribeRoutes(route.Candidates)}), so the connection was NOT attempted.");
                    routeNote = " Route: " + route.Description + ".";
                }

                OnlineState resultState;
                try
                {
                    resultState = provider.GoOnline();
                }
                catch (Exception ex) when (ex is not PortalException)
                {
                    // An exception here is not the NotReachable state: the attempt itself failed.
                    _logger?.LogError(ex, "GoOnline failed for {SoftwarePath}", softwarePath);
                    throw new PortalException(PortalErrorCode.OpennessError,
                        $"GoOnline failed on '{softwarePath}': {ex.Message}.{routeNote}", inner: ex);
                }

                var stateName = resultState.ToString();
                bool isOnline = stateName == "Online";
                return new ResponseOnlineState
                {
                    State = stateName,
                    IsOnline = isOnline,
                    IsReachable = isOnline || stateName == "Protected",
                    Message = BuildOnlineStateMessage(stateName, softwarePath) + routeNote
                };
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GoOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError, $"GoOnline failed on '{softwarePath}': {ex.Message}", inner: ex);
            }
        }

        public ResponseMessage GoOffline(string softwarePath)
        {
            // 下线失败必须看得见。原来这三条都返回一条 isError=false 的普通消息，
            // 其中 provider 为 null 那条更是**什么都没做**却回一句「is now offline」——
            // 调用方据此认为在线会话已经断开，实际它还挂着：后续 CompileSoftware / Export*
            // 会被「not permitted in online mode」挡住，现场 CPU 的编程连接也一直被占。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GoOffline: no project is open, so nothing was taken offline. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"GoOffline: PLC software not found at '{softwarePath}', so nothing was taken offline."
                    + AvailablePlcPathsSuffix());
            }

            var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
            if (provider == null)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GoOffline: OnlineProvider service is not available on '{softwarePath}', "
                    + "so the offline transition was NOT performed. Any live online session is still open — "
                    + "disconnect it in the TIA Portal UI before compiling or exporting.");
            }

            // The throw above used to sit inside a catch-all that turned it (and any Openness
            // failure) back into an ordinary "GoOffline error: ..." message with isError=false.
            try
            {
                provider.GoOffline();
            }
            catch (Exception ex) when (ex is not PortalException)
            {
                _logger?.LogError(ex, "GoOffline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GoOffline failed on '{softwarePath}': {ex.Message}. The online session may still be open.", inner: ex);
            }

            // Read the state back instead of assuming the call worked.
            string after;
            try { after = provider.State.ToString(); }
            catch (Exception ex) { after = "Unknown (" + ex.Message + ")"; }
            if (after == "Online")
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GoOffline: '{softwarePath}' still reports State=Online after GoOffline(). "
                    + "Another online session (for example one opened in the TIA Portal UI) may be holding it; try GoOfflineAll.");
            }
            return new ResponseMessage { Message = $"'{softwarePath}' is now offline (state: {after})." };
        }

        // Take EVERY PLC in the project offline, not just one. A UI-initiated online session,
        // or a project with more than one online PLC, will NOT release the compile/export/import
        // lock when GoOffline is called for a single softwarePath. This iterates all PLCs using
        // the same provider resolution as GoOffline, so the agent never has to ask the user to
        // toggle online/offline in the TIA UI. Returns per-PLC before/after state.
        public JsonObject GoOfflineAll()
        {
            var plcs = new JsonArray();
            if (IsProjectNull())
                return new JsonObject { ["message"] = "No project open.", ["allOffline"] = true, ["plcs"] = plcs };

            bool allOffline = true;
            foreach (var plc in GetAllPlcSoftware())
            {
                var entry = new JsonObject { ["name"] = plc.Name };
                try
                {
                    var provider = ResolvePlcService<OnlineProvider>(plc.Name, plc);
                    entry["before"] = provider?.State.ToString() ?? "Unknown";
                    provider?.GoOffline();
                    var after = provider?.State.ToString() ?? "Unknown";
                    entry["after"] = after;
                    entry["ok"] = after != "Online";
                    if (after == "Online") allOffline = false;
                }
                catch (Exception ex)
                {
                    entry["ok"] = false;
                    entry["error"] = ex.Message;
                    allOffline = false;
                }
                plcs.Add(entry);
            }

            return new JsonObject
            {
                ["message"] = $"GoOfflineAll: {plcs.Count} PLC(s) processed; allOffline={allOffline}.",
                ["allOffline"] = allOffline,
                ["plcs"] = plcs
            };
        }

        public ResponseCompare CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
        {
            // 这两条原来返回 Entries=null / Summary=null 的普通响应，调用方读到的是
            // 「0 处差异」→ 得出「在线离线一致，不用下载」。本工具的全部价值就是回答
            // 「要不要下载」，把「没比成」说成「一致」是这里能犯的最贵的错。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "CompareSoftwareToOnline: no project is open, so NO comparison was performed. "
                    + "Do not read this as 'offline and online are identical'. "
                    + "Call Connect + OpenProject (or AttachToOpenProject) first.");
            }

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"CompareSoftwareToOnline: PLC software not found at '{softwarePath}', "
                    + "so NO comparison was performed. Do not read this as 'identical'."
                    + AvailablePlcPathsSuffix());
            }

            try
            {
                // Note: don't gate on OnlineProvider here. The provider service is sometimes
                // unavailable on PlcSoftware even when the PLC is online via the TIA Portal UI
                // (depends on hardware variant / project layout). Let CompareToOnline itself
                // throw if the connection isn't actually live — the exception path below
                // captures that with the original TIA error message.
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                var probeState = provider?.State.ToString() ?? "Unknown";

                var compareMethod = plcSoftware.GetType().GetMethod("CompareToOnline", Type.EmptyTypes);
                if (compareMethod == null)
                    return new ResponseCompare { Message = "CompareToOnline method not found on PlcSoftware (TIA Openness API mismatch)." };

                var result = compareMethod.Invoke(plcSoftware, null);
                if (result == null)
                    return new ResponseCompare { Message = "CompareToOnline returned null.", IsOnline = true };

                var rootElement = result.GetType().GetProperty("RootElement")?.GetValue(result);
                var entries = new List<CompareEntry>();
                var summary = new Dictionary<string, int>();
                bool truncated = WalkCompareTree(rootElement, "", 0, maxDepth, maxEntries, entries, summary);

                return new ResponseCompare
                {
                    Message = $"Compare complete: {entries.Count} differences" + (truncated ? " (truncated)." : "."),
                    IsOnline = true,
                    Entries = entries.ToArray(),
                    Summary = summary,
                    Truncated = truncated
                };
            }
            // 比对**失败**时原来返回一条 isError=false 的普通 ResponseCompare：
            // Entries=null、Summary=null，调用方读到的又是「0 处差异」→「一致，不用下载」。
            // 而 TargetInvocationException 那一路还硬写 IsOnline=true —— 实测拿到的正是
            // 「The operation is not permitted in offline mode」，即根本没在线，
            // 消息和字段自相矛盾。比不出来就是比不出来，必须抛。
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _logger?.LogError(tie.InnerException, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {tie.InnerException.Message} "
                    + "NO comparison result is available — do not read this as 'offline and online are identical'. "
                    + "Go online first (GoOnline) and retry.", null, tie.InnerException);
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {ex.Message} "
                    + "NO comparison result is available — do not read this as 'identical'.", null, ex);
            }
        }

        private static bool WalkCompareTree(object? element, string path, int depth, int maxDepth, int maxEntries,
            List<CompareEntry> entries, Dictionary<string, int> summary)
        {
            if (element == null) return false;
            if (entries.Count >= maxEntries) return true;

            var t = element.GetType();
            string leftName = t.GetProperty("LeftName")?.GetValue(element)?.ToString() ?? string.Empty;
            string rightName = t.GetProperty("RightName")?.GetValue(element)?.ToString() ?? string.Empty;
            string status = t.GetProperty("ComparisonResult")?.GetValue(element)?.ToString() ?? "Unknown";
            string? details = t.GetProperty("DetailedInformation")?.GetValue(element)?.ToString();

            string displayName = !string.IsNullOrEmpty(leftName) ? leftName : rightName;
            string fullPath = string.IsNullOrEmpty(path)
                ? displayName
                : (string.IsNullOrEmpty(displayName) ? path : path + "/" + displayName);

            if (summary.ContainsKey(status)) summary[status]++;
            else summary[status] = 1;

            // Skip "Equal"/"None" entries; only report differences
            // 实测出现过的真差异状态："RightMissing"（只在离线侧，没下载过）、
            // "FolderContentsDifferent"；同族还有 "LeftMissing" / "ObjectsDifferent"。
            // 以 "Identical" 结尾的状态是**一致**，漏掉这一条会把一致报成
            // 「Compare complete: N differences」，诱发一次不必要的下载。
            bool isDifference = status != "Equal" && status != "None" && status != "Unknown"
                && !status.EndsWith("Identical", StringComparison.Ordinal);
            if (depth > 0 && isDifference)
            {
                entries.Add(new CompareEntry
                {
                    Path = fullPath,
                    LeftName = leftName,
                    RightName = rightName,
                    Status = status,
                    Details = string.IsNullOrEmpty(details) ? null : details
                });
                if (entries.Count >= maxEntries) return true;
            }

            if (depth >= maxDepth) return false;

            var children = t.GetProperty("Elements")?.GetValue(element);
            if (children is IEnumerable enumerable)
            {
                foreach (var child in enumerable)
                {
                    if (WalkCompareTree(child, fullPath, depth + 1, maxDepth, maxEntries, entries, summary))
                        return true;
                }
            }
            return false;
        }

        private static string BuildOnlineStateMessage(string state, string softwarePath)
        {
            return state switch
            {
                "Online" => $"'{softwarePath}' is online and reachable.",
                "Offline" => $"'{softwarePath}' is offline. Call GoOnline first.",
                "Connecting" => $"'{softwarePath}' is connecting...",
                "Incompatible" => $"'{softwarePath}' online but firmware/config mismatch. Download required.",
                "NotReachable" => $"'{softwarePath}' not reachable. Check IP address and network.",
                "Protected" => $"'{softwarePath}' is password-protected. Authentication required.",
                "Disconnecting" => $"'{softwarePath}' is disconnecting.",
                _ => $"'{softwarePath}' online state: {state}."
            };
        }
    }
}
