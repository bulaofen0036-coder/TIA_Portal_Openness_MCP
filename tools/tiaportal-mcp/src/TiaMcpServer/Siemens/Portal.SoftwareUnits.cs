using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// PLC software units (#55). Openness does not model them as children of PlcSoftware: the only
    /// route is PlcSoftware.GetService&lt;PlcUnitProvider&gt;().UnitGroup.Units, so GetSoftwareTree and
    /// every block tool used to see none of the blocks inside a unit.
    ///
    /// Blocks inside a unit are addressed with the group path prefix "Software units/&lt;unit&gt;/...",
    /// resolved in GetPlcBlockGroupByPath, so ExportBlock / GetBlockInfo / DescribeBlockLogic and the
    /// other tools built on GetBlock reach them without a parameter of their own.
    /// </summary>
    public partial class Portal
    {
        internal const string SoftwareUnitsRoot = "Software units";

        private static bool IsSoftwareUnitsSegment(string segment)
            => segment.Equals(SoftwareUnitsRoot, StringComparison.OrdinalIgnoreCase)
               || segment.Equals("软件单元", StringComparison.OrdinalIgnoreCase);

        /// <summary>The units of a PLC software. failure is set when the provider or the collection
        /// could not be read, so "could not read" is never reported as "has no units".</summary>
        private static List<PlcUnit> GetPlcUnits(PlcSoftware software, out string? failure)
        {
            failure = null;
            try
            {
                var provider = software.GetService<PlcUnitProvider>();
                if (provider == null)
                {
                    failure = "this PLC software has no PlcUnitProvider (software units are not supported for this CPU)";
                    return new List<PlcUnit>();
                }
                return provider.UnitGroup?.Units?.ToList() ?? new List<PlcUnit>();
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return new List<PlcUnit>();
            }
        }

        public ResponseMessage GetSoftwareUnits(string softwarePath, bool includeBlocks = true)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState,
                    "GetSoftwareUnits: no project is open. Call Connect + OpenProject (or AttachToOpenProject) first.");

            if (GetSoftwareContainer(softwarePath)?.Software is not PlcSoftware software)
                throw new PortalException(PortalErrorCode.NotFound, $"PLC software '{softwarePath}' not found. Use GetProjectTree for the exact name.");

            var units = GetPlcUnits(software, out var failure);
            var list = new JsonArray();
            var totalBlocks = 0;
            foreach (var unit in units)
            {
                var prefix = $"{SoftwareUnitsRoot}/{unit.Name}";
                var blocks = new JsonArray();
                var count = 0;
                if (unit.BlockGroup != null)
                {
                    foreach (var (group, block) in EnumerateUnitBlocks(unit.BlockGroup, prefix))
                    {
                        count++;
                        if (includeBlocks)
                            blocks.Add(new JsonObject
                            {
                                ["blockPath"] = $"{group}/{block.Name}",
                                ["type"] = block.GetType().Name,
                                ["number"] = block.Number,
                                ["language"] = block.ProgrammingLanguage.ToString(),
                            });
                    }
                }
                totalBlocks += count;
                var entry = new JsonObject { ["name"] = unit.Name, ["groupPath"] = prefix, ["blockCount"] = count };
                if (includeBlocks) entry["blocks"] = blocks;
                list.Add(entry);
            }

            var message = failure != null
                ? $"Software units of '{softwarePath}' could not be read: {failure}."
                : units.Count == 0
                    ? $"'{softwarePath}' has no software units."
                    : $"'{softwarePath}' has {units.Count} software unit(s) with {totalBlocks} block(s): {string.Join(", ", units.Select(u => u.Name))}. "
                      + "Pass a block's blockPath to ExportBlock / GetBlockInfo / DescribeBlockLogic.";

            return new ResponseMessage
            {
                Message = message,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = failure == null,
                    ["unitCount"] = units.Count,
                    ["units"] = list,
                    ["failure"] = failure,
                }
            };
        }

        /// <summary>
        /// The block group of every software unit (#61), for listings that claim to cover "all blocks".
        /// failure != null: the units could not be read, so a root-only listing must not be presented as complete.
        /// A CPU without a unit provider simply has no units (not a failure).
        /// </summary>
        public List<(string Name, PlcBlockGroup Group)> GetSoftwareUnitBlockGroups(string softwarePath, out string? failure)
        {
            failure = null;
            if (IsProjectNull() || GetSoftwareContainer(softwarePath)?.Software is not PlcSoftware software)
                return new List<(string, PlcBlockGroup)>();
            var units = GetPlcUnitsOrFailure(software, out failure);
            return units.Where(u => u.BlockGroup != null).Select(u => (u.Name, (PlcBlockGroup)u.BlockGroup)).ToList();
        }

        /// <summary>
        /// Every user type of the PLC with its path: root types as "Group/Type", unit types as
        /// "Software units/&lt;unit&gt;/Group/Type" (#62). The paths are what GetTypeInfo / ExportType accept.
        /// unitFailure != null: the units could not be read; the list then covers the PLC root only.
        /// </summary>
        public List<(string Path, string Owner, PlcType Type)>? GetTypesWithPaths(string softwarePath, string regexName, out string? unitFailure)
        {
            unitFailure = null;
            if (IsProjectNull()) return null;
            if (GetSoftwareContainer(softwarePath)?.Software is not PlcSoftware software)
                throw new PortalException(PortalErrorCode.NotFound, $"GetTypes: PLC software not found at '{softwarePath}'." + AvailablePlcPathsSuffix());
            if (software.TypeGroup == null)
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetTypes: PLC '{softwarePath}' resolved, but its TypeGroup is not available — the type list could NOT be read. This is not the same as 'the PLC has no types'.");

            var filter = CompileNameFilterOrThrow(regexName);
            var list = new List<(string, string, PlcType)>();
            try
            {
                CollectTypesWithPaths(software.TypeGroup, "", softwarePath, filter, list);
                foreach (var unit in GetPlcUnitsOrFailure(software, out unitFailure))
                    if (unit.TypeGroup != null)
                        CollectTypesWithPaths(unit.TypeGroup, $"{SoftwareUnitsRoot}/{unit.Name}", $"{SoftwareUnitsRoot}/{unit.Name}", filter, list);
            }
            catch (PortalException) { throw; }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"GetTypes: type enumeration failed after {list.Count} type(s) in '{softwarePath}'; the returned list would have been INCOMPLETE, so it is not returned at all. Root cause: {ex.Message}", null, ex);
            }
            return list;
        }

        private static void CollectTypesWithPaths(PlcTypeGroup group, string groupPath, string owner, System.Text.RegularExpressions.Regex? filter, List<(string, string, PlcType)> list)
        {
            foreach (var composition in group.Types)
                if (composition is PlcType type && (filter == null || filter.IsMatch(type.Name)))
                    list.Add((groupPath.Length == 0 ? type.Name : $"{groupPath}/{type.Name}", owner, type));
            foreach (PlcTypeGroup sub in group.Groups)
                CollectTypesWithPaths(sub, groupPath.Length == 0 ? sub.Name : $"{groupPath}/{sub.Name}", owner, filter, list);
        }

        /// <summary>Like GetPlcUnits, but a CPU without a unit provider is "no units", not a failure.</summary>
        private static List<PlcUnit> GetPlcUnitsOrFailure(PlcSoftware software, out string? failure)
        {
            failure = null;
            try
            {
                var provider = software.GetService<PlcUnitProvider>();
                if (provider == null) return new List<PlcUnit>();
                return provider.UnitGroup?.Units?.ToList() ?? new List<PlcUnit>();
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return new List<PlcUnit>();
            }
        }

        private static IEnumerable<(string GroupPath, PlcBlock Block)> EnumerateUnitBlocks(PlcBlockGroup group, string groupPath)
        {
            foreach (var block in group.Blocks)
                yield return (groupPath, block);
            foreach (var sub in group.Groups)
                foreach (var item in EnumerateUnitBlocks(sub, $"{groupPath}/{sub.Name}"))
                    yield return item;
        }
    }
}
