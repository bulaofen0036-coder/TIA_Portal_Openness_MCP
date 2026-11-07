using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Walks an HMI software's screens including every nested screen group / folder
    /// (Unified: ScreenGroups → Groups; Classic: ScreenFolder → Folders).
    ///
    /// Before PR #41 every screen tool read only the root <c>Screens</c> composition, so a screen
    /// filed in a group was "not found" with nothing pointing at the folder as the cause.
    ///
    /// Zero-dependency on purpose: pure reflection, so the offline test suite can feed it
    /// fake object graphs. Inside the Siemens.Engineering-bound Portal files it could not be tested
    /// without TIA installed.
    /// </summary>
    internal static class HmiScreenWalk
    {
        private static readonly string[] ChildFolderProperties = { "ScreenGroups", "Folders", "Groups" };

        /// <summary>Names of all screens, depth-first. Best-effort: a failing walk returns what it got so far.</summary>
        public static List<string> ListNames(object? hmiRoot)
        {
            var result = new List<string>();
            try
            {
                foreach (var screen in EnumerateScreens(hmiRoot))
                {
                    var name = GetName(screen);
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
                }
            }
            catch
            {
                // best-effort only
            }
            return result;
        }

        /// <summary>First screen whose name matches (case-insensitive), or null. Never throws.</summary>
        public static object? FindByName(object? hmiRoot, string wantedName)
        {
            try
            {
                foreach (var screen in EnumerateScreens(hmiRoot))
                {
                    if (string.Equals(GetName(screen), wantedName, StringComparison.OrdinalIgnoreCase))
                        return screen;
                }
            }
            catch
            {
                // a failed walk reads as "not found", same contract as TryFindByNameInCollection
            }
            return null;
        }

        private static IEnumerable<object> EnumerateScreens(object? root)
        {
            if (root == null) yield break;

            var visited = new HashSet<object>(ReferenceComparer.Instance);
            var pending = new Stack<object>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var container = pending.Pop();
                if (!visited.Add(container)) continue;

                if (GetProperty(container, "Screens") is IEnumerable screens && !(screens is string))
                {
                    foreach (var item in screens)
                    {
                        if (item != null) yield return item;
                    }
                }

                // Push in reverse so children are visited in declaration order.
                var children = new List<object>();

                // Classic HmiTarget: root.ScreenFolder is a single folder object, not a collection.
                var singleFolder = GetProperty(container, "ScreenFolder");
                if (singleFolder != null) children.Add(singleFolder);

                foreach (var propName in ChildFolderProperties)
                {
                    if (GetProperty(container, propName) is IEnumerable groups && !(groups is string))
                    {
                        foreach (var group in groups)
                        {
                            if (group != null) children.Add(group);
                        }
                    }
                }

                for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
        }

        private static object? GetProperty(object obj, string name)
        {
            try
            {
                return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        private static string? GetName(object item) => GetProperty(item, "Name")?.ToString();

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
