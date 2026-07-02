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
            if (IsProjectNull())
                return new ResponseOnlineState { State = "Offline", IsOnline = false, IsReachable = false, Message = "No project open." };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseOnlineState { State = "Offline", IsOnline = false, IsReachable = false, Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                    return new ResponseOnlineState { State = "Offline", IsOnline = false, IsReachable = false, Message = "OnlineProvider service not available." };

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
            if (IsProjectNull())
                return new ResponseOnlineState { State = "Offline", IsOnline = false, IsReachable = false, Message = "No project open." };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseOnlineState { State = "Offline", IsOnline = false, IsReachable = false, Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                    return new ResponseOnlineState { State = "Offline", IsOnline = false, IsReachable = false, Message = "OnlineProvider service not available." };

                using var passwordScope = AttachPasswordHandler(provider.Configuration, password);

                OnlineState resultState;
                if (!string.IsNullOrWhiteSpace(ipAddress))
                {
                    var goOnlineWithAddr = provider.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GoOnline" && m.GetParameters().Length == 1);

                    if (goOnlineWithAddr != null)
                    {
                        var addrType = goOnlineWithAddr.GetParameters()[0].ParameterType;
                        var addrCtor = addrType.GetConstructor(new[] { typeof(string) });
                        if (addrCtor != null)
                        {
                            var addr = addrCtor.Invoke(new object[] { ipAddress! });
                            var rawState = goOnlineWithAddr.Invoke(provider, new[] { addr });
                            resultState = rawState is OnlineState os ? os : OnlineState.Offline;
                        }
                        else
                        {
                            resultState = provider.GoOnline();
                        }
                    }
                    else
                    {
                        resultState = provider.GoOnline();
                    }
                }
                else
                {
                    resultState = provider.GoOnline();
                }

                var stateName = resultState.ToString();
                bool isOnline = stateName == "Online";
                return new ResponseOnlineState
                {
                    State = stateName,
                    IsOnline = isOnline,
                    IsReachable = isOnline || stateName == "Protected",
                    Message = BuildOnlineStateMessage(stateName, softwarePath)
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GoOnline failed for {SoftwarePath}", softwarePath);
                return new ResponseOnlineState { State = "NotReachable", IsOnline = false, IsReachable = false, Message = $"GoOnline failed: {ex.Message}" };
            }
        }

        public ResponseMessage GoOffline(string softwarePath)
        {
            if (IsProjectNull())
                return new ResponseMessage { Message = "No project open." };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var provider = ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                provider?.GoOffline();
                return new ResponseMessage { Message = $"'{softwarePath}' is now offline." };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GoOffline failed for {SoftwarePath}", softwarePath);
                return new ResponseMessage { Message = $"GoOffline error: {ex.Message}" };
            }
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
            if (IsProjectNull())
                return new ResponseCompare { Message = "No project open.", IsOnline = false };

            var plcSoftware = GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new ResponseCompare { Message = $"PLC software not found: '{softwarePath}'.", IsOnline = false };

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
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _logger?.LogError(tie.InnerException, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                return new ResponseCompare { Message = $"Compare failed: {tie.InnerException.Message}", IsOnline = true };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                return new ResponseCompare { Message = $"Compare failed: {ex.Message}" };
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
            bool isDifference = status != "Equal" && status != "None" && status != "Unknown";
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
