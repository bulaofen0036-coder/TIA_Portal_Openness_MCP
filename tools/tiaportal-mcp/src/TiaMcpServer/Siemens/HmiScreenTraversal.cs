using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.Siemens
{
    // Reflection keeps Classic and Unified on the same traversal, testable without TIA.
    internal static class HmiScreenTraversal
    {
        private static readonly string[] ChildCollections = { "ScreenGroups", "Groups", "Folders" };

        internal static List<string> ListNames(object root)
        {
            var names = new List<string>();
            foreach (var screen in EnumerateScreens(root, new HashSet<object>(ReferenceComparer.Instance)))
            {
                var name = ReadProperty(screen, "Name")?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            return names;
        }

        internal static object? FindByName(object root, string wantedName)
        {
            foreach (var screen in EnumerateScreens(root, new HashSet<object>(ReferenceComparer.Instance)))
            {
                var name = ReadProperty(screen, "Name")?.ToString();
                if (string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase)) return screen;
            }
            return null;
        }

        private static IEnumerable<object> EnumerateScreens(object root, HashSet<object> visited)
        {
            if (!visited.Add(root)) yield break;

            // Keep root-level matches first. Only Screens contains screen objects;
            // group names, screen items and templates must never be returned as screens.
            foreach (var screen in ReadCollection(root, "Screens"))
            {
                if (visited.Add(screen)) yield return screen;
            }

            // Classic: HmiTarget.ScreenFolder.Screens, then ScreenFolder.Folders recursively.
            var folder = ReadProperty(root, "ScreenFolder");
            if (folder != null)
            {
                foreach (var screen in EnumerateScreens(folder, visited)) yield return screen;
            }

            // Unified: HmiSoftware.ScreenGroups, then HmiScreenGroup.Groups recursively.
            foreach (var property in ChildCollections)
            {
                foreach (var child in ReadCollection(root, property))
                {
                    foreach (var screen in EnumerateScreens(child, visited)) yield return screen;
                }
            }
        }

        private static IEnumerable<object> ReadCollection(object root, string property)
        {
            var value = ReadProperty(root, property);
            if (value is IEnumerable items && value is not string)
            {
                foreach (var item in items)
                {
                    if (item != null) yield return item;
                }
            }
        }

        // Missing properties are normal across API variants. Failed reads must surface:
        // returning "not found" could make EnsureUnifiedHmiScreen create a duplicate.
        private static object? ReadProperty(object root, string property)
            => root.GetType().GetProperty(property)?.GetValue(root);

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
