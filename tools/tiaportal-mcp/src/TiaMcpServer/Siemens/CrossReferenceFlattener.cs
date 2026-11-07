using System;
using System.Collections;
using System.Collections.Generic;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Flattens an Openness CrossReferenceResult (Sources → References → Locations) into entries,
    /// and reports whether the walk reached the end.
    ///
    /// Why the completeness flag exists (issue #60): Openness proxy objects can throw half-way
    /// through an enumeration. The old code wrapped all three loops in one silent catch and handed
    /// back whatever had been collected so far; the MCP layer only checked for null, so a block
    /// referenced 40 times could be reported with 4 references (or none) and "success" — and
    /// DeletePlcBlock tells callers to look at GetCrossReferences before deleting.
    ///
    /// Pure reflection, no Siemens types, so the offline test suite can drive it with fakes.
    /// </summary>
    internal static class CrossReferenceFlattener
    {
        /// <param name="truncation">null = the walk completed; otherwise why the list is partial.</param>
        internal static List<CrossReferenceEntry> Flatten(object crossReferenceResult, string sourcePathFallback, out string? truncation)
        {
            var items = new List<CrossReferenceEntry>();
            truncation = null;
            var skippedSources = 0;
            var unexpandedChildren = 0;

            try
            {
                var sources = crossReferenceResult.GetType().GetProperty("Sources")?.GetValue(crossReferenceResult) as IEnumerable;
                if (sources == null)
                {
                    truncation = "CrossReferenceResult has no Sources collection (unexpected shape for this TIA version); no references were enumerated.";
                    return items;
                }

                foreach (var src in sources)
                {
                    if (src == null) continue;
                    // Child branches (e.g. members of a composite InOut parameter) are not walked yet (issue #60,
                    // second half). Their references would be missing, so a source with children is not "complete".
                    if (HasChildren(src)) unexpandedChildren++;
                    var srcName = src.GetType().GetProperty("Name")?.GetValue(src)?.ToString();
                    var srcPath = src.GetType().GetProperty("Path")?.GetValue(src)?.ToString() ?? sourcePathFallback;

                    var refs = src.GetType().GetProperty("References")?.GetValue(src) as IEnumerable;
                    if (refs == null)
                    {
                        // Dropping a source without a trace is under-reporting too.
                        skippedSources++;
                        continue;
                    }

                    foreach (var rf in refs)
                    {
                        if (rf == null) continue;
                        var refName = rf.GetType().GetProperty("Name")?.GetValue(rf)?.ToString();
                        var refPath = rf.GetType().GetProperty("Path")?.GetValue(rf)?.ToString();

                        var locations = rf.GetType().GetProperty("Locations")?.GetValue(rf) as IEnumerable;
                        if (locations == null)
                        {
                            items.Add(new CrossReferenceEntry
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
                            items.Add(new CrossReferenceEntry
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
            catch (Exception ex)
            {
                // The TIA Portal process is gone: every handle is dead. That is not a partial result to act on.
                if (PortalFailureClassifier.IsPortalProcessLost(ex)) throw;
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                truncation = $"Cross-reference enumeration failed part-way ({inner.GetType().Name}: {inner.Message}) after {items.Count} entries. "
                           + "This list is INCOMPLETE - do not conclude that nothing uses the object, and do not delete it based on this list.";
            }

            if (truncation == null && skippedSources > 0)
            {
                truncation = $"{skippedSources} source(s) had no readable References collection and were skipped; the list may be incomplete.";
            }

            if (truncation == null && unexpandedChildren > 0)
            {
                truncation = $"{unexpandedChildren} source(s) have child branches (Children) that are not expanded; references held there are not listed, so the list may be incomplete.";
            }

            return items;
        }

        /// <summary>True when the node exposes a non-empty Children collection (or reading it fails: unknown counts as "may have").</summary>
        private static bool HasChildren(object node)
        {
            try
            {
                if (!(node.GetType().GetProperty("Children")?.GetValue(node) is IEnumerable children)) return false;
                var e = children.GetEnumerator();
                try { return e.MoveNext(); }
                finally { (e as IDisposable)?.Dispose(); }
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                return true;
            }
        }
    }
}
