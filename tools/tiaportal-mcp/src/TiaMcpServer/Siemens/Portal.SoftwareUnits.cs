using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
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
