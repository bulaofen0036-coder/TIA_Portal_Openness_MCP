using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: HMI attribute reads (GetHmiTagDetails / GetHmiScreenItemDetails).
    //
    // The server could already list HMI tag and screen-item NAMES, and describe their API shape,
    // but a VALUE — datatype, connection, address, PLC binding, position, size — could only be
    // fetched one attribute per call through InvokeObject: thousands of round trips for one
    // project, over a bridge that can also write and therefore cannot be handed to a read-only
    // agent. These two tools do that loop server-side and are read-only by construction.
    //
    // The reflection itself lives in ModelContextProtocol/HmiDetailRead.cs so the offline suite
    // can test it without TIA Portal installed.
    public partial class Portal
    {
        /// <summary>
        /// Attribute values for every tag of one HMI tag table, or of all tag tables when
        /// <paramref name="tagTableName"/> is empty. Read-only.
        /// </summary>
        public ResponseHmiTagDetails GetHmiTagDetails(string softwarePath, string tagTableName = "", string attributes = "")
        {
            var sw = RequireHmiSoftware(softwarePath);
            var attributeNames = HmiDetailRead.ParseAttributes(attributes, HmiDetailRead.DefaultTagAttributes);

            var items = new List<HmiDetailItem>();
            var failed = new List<HmiDetailFailure>();

            var tables = new List<object>();
            if (!string.IsNullOrWhiteSpace(tagTableName))
            {
                var table = HmiDetailRead.FindTagTable(sw, tagTableName);
                if (table == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"HMI tag table not found: '{tagTableName}' in '{softwarePath}'."
                        + Guard.DidYouMean(HmiDetailRead.EnumerateTagTables(sw)
                            .Select(HmiDetailRead.GetName)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .Take(20)));
                }
                tables.Add(table);
            }
            else
            {
                tables.AddRange(HmiDetailRead.EnumerateTagTables(sw));

                // No tag table at all is not an empty success: every WinCC HMI has at least a
                // default table, so "none found" means the walk did not reach the collection.
                if (tables.Count == 0)
                {
                    failed.Add(new HmiDetailFailure
                    {
                        Name = softwarePath,
                        Container = softwarePath,
                        Reason = $"no tag table collection found on {sw.GetType().Name}"
                    });
                }
            }

            foreach (var table in tables)
            {
                var tableName = HmiDetailRead.GetName(table) ?? "";
                var tags = HmiDetailRead.EnumerateChildren(table, "Tags", out var enumerationError);
                if (enumerationError != null)
                {
                    failed.Add(new HmiDetailFailure { Name = "", Container = tableName, Reason = enumerationError });
                }

                foreach (var tag in tags)
                {
                    if (HmiDetailRead.TryReadItem(tag, tableName, attributeNames, out var item, out var reason) && item != null)
                    {
                        items.Add(item);
                    }
                    else
                    {
                        failed.Add(new HmiDetailFailure
                        {
                            Name = SafeName(tag),
                            Container = tableName,
                            Reason = reason ?? "tag could not be read"
                        });
                    }
                }
            }

            var success = HmiDetailRead.IsSuccess(items.Count, failed.Count);
            var scope = string.IsNullOrWhiteSpace(tagTableName) ? "all tables" : $"table='{tagTableName}'";

            return new ResponseHmiTagDetails
            {
                Items = items,
                Failed = failed,
                Message = $"HMI tag details read for '{softwarePath}' ({scope}): {items.Count} of {items.Count + failed.Count}",
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = success,
                    ["tagCount"] = items.Count,
                    ["failedCount"] = failed.Count,
                    ["tagTableCount"] = tables.Count,
                    ["softwareKind"] = HmiSoftwareKind(sw),
                    ["attributes"] = string.Join(",", attributeNames)
                }
            };
        }

        /// <summary>
        /// Attribute values for every item on one HMI screen, plus the screen's own size — without
        /// it a position cannot be judged. Read-only.
        /// </summary>
        public ResponseHmiScreenItemDetails GetHmiScreenItemDetails(string softwarePath, string screenName, string attributes = "")
        {
            var sw = RequireHmiSoftware(softwarePath);

            // Same walk as every other screen tool: screens filed in a ScreenGroup are found (PR #41).
            var screen = TryFindScreenByName(sw, screenName);
            if (screen == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI screen not found: '{screenName}' in '{softwarePath}'."
                    + Guard.DidYouMean(TryListScreens(sw).Take(20)));
            }

            var attributeNames = HmiDetailRead.ParseAttributes(attributes, HmiDetailRead.DefaultScreenItemAttributes);

            var items = new List<HmiDetailItem>();
            var failed = new List<HmiDetailFailure>();
            var resolvedScreenName = HmiDetailRead.GetName(screen) ?? screenName;

            var screenItems = HmiDetailRead.EnumerateChildren(screen, "ScreenItems", out var enumerationError);
            if (enumerationError != null)
            {
                failed.Add(new HmiDetailFailure { Name = "", Container = resolvedScreenName, Reason = enumerationError });
            }

            foreach (var screenItem in screenItems)
            {
                if (HmiDetailRead.TryReadItem(screenItem, resolvedScreenName, attributeNames, out var item, out var reason) && item != null)
                {
                    items.Add(item);
                }
                else
                {
                    failed.Add(new HmiDetailFailure
                    {
                        Name = SafeName(screenItem),
                        Container = resolvedScreenName,
                        Reason = reason ?? "screen item could not be read"
                    });
                }
            }

            var success = HmiDetailRead.IsSuccess(items.Count, failed.Count);

            return new ResponseHmiScreenItemDetails
            {
                Screen = new HmiScreenGeometry
                {
                    Name = resolvedScreenName,
                    Width = HmiDetailRead.Normalize(HmiDetailRead.ReadAttribute(screen, "Width")),
                    Height = HmiDetailRead.Normalize(HmiDetailRead.ReadAttribute(screen, "Height"))
                },
                Items = items,
                Failed = failed,
                Message = $"HMI screen item details read for '{softwarePath}:{resolvedScreenName}': {items.Count} of {items.Count + failed.Count}",
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = success,
                    ["itemCount"] = items.Count,
                    ["failedCount"] = failed.Count,
                    ["softwareKind"] = HmiSoftwareKind(sw),
                    ["attributes"] = string.Join(",", attributeNames)
                }
            };
        }

        /// <summary>
        /// The HMI software behind a path, or a NotFound naming the HMI paths this project does have.
        /// A wrong path must never read as "this HMI is empty".
        /// </summary>
        private object RequireHmiSoftware(string softwarePath)
        {
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "No project is open. Call Connect and then OpenProject or AttachToOpenProject first.");
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"HMI software not found: '{softwarePath}'."
                    + Guard.DidYouMean(ListHmiSoftwareNames())
                    + " Resolve the exact path with GetProjectTree.");
            }

            return softwareContainer.Software;
        }

        /// <summary>HMI software names in the project, for the "Did you mean" hint. Best-effort.</summary>
        private List<string> ListHmiSoftwareNames()
        {
            var names = new List<string>();
            try
            {
                foreach (var device in EnumerateAllDevices())
                {
                    CollectHmiSoftwareNames(device.DeviceItems, names);
                }
            }
            catch
            {
                // a hint that cannot be built is still only a hint
            }
            return names;
        }

        private static void CollectHmiSoftwareNames(DeviceItemComposition? items, List<string> names)
        {
            if (items == null) return;

            foreach (var item in items)
            {
                try
                {
                    var container = item.GetService<SoftwareContainer>();
                    var software = container?.Software;
                    if (software is HmiTarget classic && !string.IsNullOrWhiteSpace(classic.Name)) names.Add(classic.Name);
                    else if (software is HmiSoftware unified && !string.IsNullOrWhiteSpace(unified.Name)) names.Add(unified.Name);
                }
                catch
                {
                    // a device item without a software container is the normal case
                }

                CollectHmiSoftwareNames(item.DeviceItems, names);
            }
        }

        private static string HmiSoftwareKind(object software) => software switch
        {
            HmiSoftware => "Unified",
            HmiTarget => "Classic",
            _ => "Unknown"
        };

        private static string SafeName(object? target)
        {
            try
            {
                return HmiDetailRead.GetName(target) ?? "<unnamed>";
            }
            catch
            {
                return "<unnamed>";
            }
        }
    }
}
