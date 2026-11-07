using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Flattens an Openness CrossReferenceResult (Sources → [Children …] → References → Locations) into
    /// entries, and reports whether the walk reached the end (issue #60).
    ///
    /// Why the completeness flag exists: Openness proxy objects can throw half-way through an enumeration.
    /// The old code wrapped everything in one silent catch and handed back whatever had been collected;
    /// the MCP layer only checked for null, so a block referenced 40 times could be reported with 4
    /// references (or none) and "success" — and DeletePlcBlock tells callers to look at
    /// GetCrossReferences before deleting.
    ///
    /// Children: a SourceObject has its own Children (SourceObjectComposition). On V21 every data block
    /// (global and instance DB) comes back this way — the members' references live in the children, the
    /// top-level node has none. They are walked recursively. A branch that fails is recorded by its
    /// hierarchy path and not expanded further; its siblings are still read, so the caller learns what
    /// was read and what was not. Only Name / Path / References / Children / Locations are touched — never
    /// UnderlyingObject — and nothing is retried. A lost TIA Portal process is rethrown, never reported as
    /// a partial result. A node that is really TIA's own diagnostic message (IsDiagnosticNode) is not read
    /// at all and makes the result incomplete.
    ///
    /// Pure reflection, no Siemens types, so the offline test suite can drive it with fakes.
    /// </summary>
    internal static class CrossReferenceFlattener
    {
        internal const int MaxDepth = 32;
        internal const int MaxEntries = 50000;
        private const int MaxListedBranches = 10;

        /// <param name="truncation">null = the walk completed; otherwise why the list is partial.</param>
        /// <param name="unreadBranches">hierarchy paths ("DB_HMI.Motor.Start") of branches that could not be read.</param>
        internal static List<CrossReferenceEntry> Flatten(object crossReferenceResult, string sourcePathFallback,
            out string? truncation, out List<string> unreadBranches)
        {
            var walk = new Walk(sourcePathFallback);
            truncation = null;
            unreadBranches = walk.Unread;

            IEnumerable? sources;
            try
            {
                sources = crossReferenceResult.GetType().GetProperty("Sources")?.GetValue(crossReferenceResult) as IEnumerable;
            }
            catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
            {
                truncation = "Reading CrossReferenceResult.Sources failed (" + Describe(ex) + "); no references were enumerated.";
                return walk.Items;
            }

            if (sources == null)
            {
                truncation = "CrossReferenceResult has no Sources collection (unexpected shape for this TIA version); no references were enumerated.";
                return walk.Items;
            }

            walk.Nodes(sources, new List<string>(), 0, "(sources)");

            if (walk.HitEntryLimit)
            {
                truncation = $"Stopped after {MaxEntries} entries (size limit); the list is INCOMPLETE.";
            }
            else if (walk.Unread.Count > 0)
            {
                var listed = string.Join("; ", walk.Unread.Take(MaxListedBranches));
                var more = walk.Unread.Count > MaxListedBranches ? $" (+{walk.Unread.Count - MaxListedBranches} more)" : "";
                truncation = $"{walk.Unread.Count} branch(es) could not be read: {listed}{more}. {walk.Items.Count} entries were collected from the branches that were read. "
                           + "This list is INCOMPLETE - do not conclude that nothing uses the object, and do not delete it based on this list.";
            }

            return walk.Items;
        }

        private sealed class Walk
        {
            private readonly string _fallbackPath;
            internal readonly List<CrossReferenceEntry> Items = new List<CrossReferenceEntry>();
            internal readonly List<string> Unread = new List<string>();
            internal bool HitEntryLimit;

            internal Walk(string fallbackPath) { _fallbackPath = fallbackPath; }

            /// <summary>Enumerate a SourceObject collection; a failure stops this collection only.</summary>
            internal void Nodes(IEnumerable nodes, List<string> parentHierarchy, int depth, string label)
            {
                try
                {
                    foreach (var node in nodes)
                    {
                        if (HitEntryLimit) return;
                        if (node != null) Node(node, parentHierarchy, depth);
                    }
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    Unread.Add(label + " [enumeration failed: " + Describe(ex) + "]");
                }
            }

            private void Node(object src, List<string> parentHierarchy, int depth)
            {
                var hierarchy = new List<string>(parentHierarchy);
                string label = parentHierarchy.Count == 0 ? "(source)" : string.Join(".", parentHierarchy) + ".?";
                try
                {
                    var srcName = Prop(src, "Name");
                    var rawPath = Prop(src, "Path");
                    if (IsDiagnosticNode(srcName, rawPath))
                    {
                        // TIA puts its own error text where a source object should be and does not throw.
                        // Its References / Children are empty, which would read as "nothing uses this".
                        // Stop here without touching them and report the branch as unread.
                        var at = parentHierarchy.Count == 0 ? "(source)" : string.Join(".", parentHierarchy);
                        Unread.Add(at + " [TIA returned a cross-reference diagnostic instead of a source object: \"" + Shorten(srcName!) + "\"]");
                        return;
                    }
                    hierarchy.Add(string.IsNullOrEmpty(srcName) ? "?" : srcName!);
                    label = string.Join(".", hierarchy);
                    var srcPath = rawPath ?? _fallbackPath;

                    var refs = src.GetType().GetProperty("References")?.GetValue(src) as IEnumerable;
                    if (refs == null)
                    {
                        // Dropping a node's references without a trace is under-reporting too.
                        Unread.Add(label + " [no readable References]");
                    }
                    else
                    {
                        foreach (var rf in refs)
                        {
                            if (rf == null) continue;
                            if (!AddReference(rf, srcName, srcPath, label)) return;
                        }
                    }

                    var children = src.GetType().GetProperty("Children")?.GetValue(src) as IEnumerable;
                    if (children == null) return;
                    if (depth + 1 > MaxDepth)
                    {
                        if (HasAny(children)) Unread.Add(label + $" [children deeper than {MaxDepth} levels not read]");
                        return;
                    }
                    Nodes(children, hierarchy, depth + 1, label + ".*");
                }
                catch (Exception ex) when (!PortalFailureClassifier.IsPortalProcessLost(ex))
                {
                    // Stop at this branch: do not expand it further, keep its siblings.
                    Unread.Add(label + " [" + Describe(ex) + "]");
                }
            }

            /// <returns>false when the entry limit was hit.</returns>
            private bool AddReference(object rf, string? srcName, string srcPath, string hierarchy)
            {
                var refName = Prop(rf, "Name");
                var refPath = Prop(rf, "Path");
                var locations = rf.GetType().GetProperty("Locations")?.GetValue(rf) as IEnumerable;
                if (locations == null)
                {
                    return Add(new CrossReferenceEntry { SourceName = srcName, SourcePath = srcPath, SourceHierarchy = hierarchy, ReferenceName = refName, ReferencePath = refPath });
                }

                foreach (var loc in locations)
                {
                    if (loc == null) continue;
                    if (!Add(new CrossReferenceEntry
                    {
                        SourceName = srcName,
                        SourcePath = srcPath,
                        SourceHierarchy = hierarchy,
                        ReferenceName = refName,
                        ReferencePath = refPath,
                        LocationName = Prop(loc, "Name"),
                        ReferenceLocation = Prop(loc, "ReferenceLocation"),
                        ReferenceType = Prop(loc, "ReferenceType"),
                        Access = Prop(loc, "Access")
                    })) return false;
                }
                return true;
            }

            private bool Add(CrossReferenceEntry e)
            {
                if (Items.Count >= MaxEntries) { HitEntryLimit = true; return false; }
                Items.Add(e);
                return true;
            }
        }

        private static string? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o)?.ToString();

        /// <summary>
        /// A node that carries TIA's "cross-reference information is out of date / re-create it" message as its
        /// Name (issue #60, observed on V21: Chinese UI text, empty Path, empty References and Children, no
        /// exception). Both conditions are required: an empty Path alone happens on ordinary nodes, and the
        /// message keywords alone could in theory be part of a (very odd) object name. The keyword lists cover
        /// the Chinese, English and German UI texts; anything else falls through to the normal walk.
        /// </summary>
        internal static bool IsDiagnosticNode(string? name, string? path)
        {
            if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(path)) return false;
            var n = name!;
            if (n.IndexOf("交叉引用", StringComparison.Ordinal) >= 0
                && (n.IndexOf("重新创建", StringComparison.Ordinal) >= 0 || n.IndexOf("意外", StringComparison.Ordinal) >= 0))
                return true;
            if ((n.IndexOf("cross-reference", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("cross reference", StringComparison.OrdinalIgnoreCase) >= 0)
                && (n.IndexOf("recreate", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("re-create", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("unexpected", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            if (n.IndexOf("Querverweis", StringComparison.OrdinalIgnoreCase) >= 0
                && (n.IndexOf("neu erstellen", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("unerwartet", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        private static string Shorten(string s) => s.Length <= 80 ? s : s.Substring(0, 80) + "...";

        private static bool HasAny(IEnumerable e)
        {
            var it = e.GetEnumerator();
            try { return it.MoveNext(); }
            finally { (it as IDisposable)?.Dispose(); }
        }

        private static string Describe(Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
