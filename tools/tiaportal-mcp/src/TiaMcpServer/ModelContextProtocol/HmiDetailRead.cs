using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>One HMI tag or screen item with the attribute values that were asked for.</summary>
    public sealed class HmiDetailItem
    {
        public string Name { get; set; } = "";
        /// <summary>Tag table name (tags) or screen name (screen items).</summary>
        public string Container { get; set; } = "";
        /// <summary>CLR type name, e.g. HmiButton / HmiIOField. Null for tags.</summary>
        public string? Type { get; set; }
        public Dictionary<string, object?> Attributes { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>An object that could not be read. Never dropped silently.</summary>
    public sealed class HmiDetailFailure
    {
        public string Name { get; set; } = "";
        public string Container { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Reads attribute VALUES off Openness objects (HMI tags, screen items) by reflection.
    ///
    /// Before this, the server could list their names and describe their API shape, but a value
    /// could only be fetched one attribute per call through the InvokeObject reflection bridge —
    /// thousands of round trips for one project, over a tool that can also write.
    ///
    /// Zero-dependency on purpose (same reason as HmiScreenWalk): the offline suite feeds it fake
    /// object graphs, which is impossible inside the Siemens.Engineering-bound Portal files.
    /// </summary>
    internal static class HmiDetailRead
    {
        /// <summary>Where a tag points and how it is read. Order is the order of the response.</summary>
        public static readonly string[] DefaultTagAttributes =
        {
            "Name", "DataType", "HmiDataType", "Connection", "Address", "PlcTag", "PlcName", "AcquisitionCycle"
        };

        /// <summary>Where a screen item sits and whether it is shown at all.</summary>
        public static readonly string[] DefaultScreenItemAttributes =
        {
            "Name", "Left", "Top", "Width", "Height", "Visible", "Enabled"
        };

        private static readonly string[] TagTableCollectionProperties = { "TagTables", "HmiTagTables", "Tables" };
        private static readonly string[] TagTableGroupProperties = { "TagTableGroups", "Groups", "Folders" };
        private static readonly string[] TagTableRootProperties =
        {
            "TagTableFolder", "TagFolder", "HmiTagTableFolder", "HmiTagFolder", "TagTableGroup", "HmiTagTableGroup"
        };

        /// <summary>
        /// Caller list wins, empty falls back to <paramref name="defaults"/>. Trimmed, de-duplicated
        /// case-insensitively, order preserved — the response columns follow this list.
        /// </summary>
        public static List<string> ParseAttributes(string? csv, IReadOnlyList<string> defaults)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(csv))
            {
                foreach (var raw in csv!.Split(','))
                {
                    var name = raw.Trim();
                    if (name.Length == 0) continue;
                    if (seen.Add(name)) result.Add(name);
                }
            }

            if (result.Count > 0) return result;

            foreach (var name in defaults)
            {
                if (seen.Add(name)) result.Add(name);
            }
            return result;
        }

        /// <summary>
        /// Everything that is not a JSON primitive becomes a string.
        ///
        /// Not cosmetic: a MultilingualText (Text, ToolTipText, DisplayName) carries
        /// Items[].Language.Culture.Parent.Parent… and System.Text.Json aborts the WHOLE response
        /// with "a possible object cycle was detected" at depth 64. One exotic attribute must not
        /// be able to sink a 200-item inventory.
        /// </summary>
        public static object? Normalize(object? value)
        {
            if (value == null) return null;

            switch (value)
            {
                case string s: return s;
                case bool b: return b;
                case byte v: return (int)v;
                case sbyte v: return (int)v;
                case short v: return (int)v;
                case ushort v: return (int)v;
                case int v: return v;
                case uint v: return (long)v;
                case long v: return v;
                case ulong v: return v <= long.MaxValue ? (object)(long)v : v.ToString();
                case float v: return (double)v;
                case double v: return v;
                case decimal v: return v;
            }

            try
            {
                return value.ToString() ?? value.GetType().Name;
            }
            catch (Exception ex)
            {
                // A property getter that throws is still a fact about the object; say so instead of
                // failing the item.
                return $"<unreadable: {ex.GetType().Name}>";
            }
        }

        /// <summary>
        /// Reads every requested attribute off one object.
        ///
        /// An attribute this type does not have blanks that ONE value (null) — Openness throws for
        /// unsupported names, and a missing property is not a reason to lose the other seven.
        /// An object that cannot even be named is a failure: the caller gets it in Failed[].
        /// </summary>
        public static bool TryReadItem(object target, string container, IReadOnlyList<string> attributes,
            out HmiDetailItem? item, out string? failureReason)
        {
            item = null;
            failureReason = null;

            if (target == null)
            {
                failureReason = "object is null";
                return false;
            }

            string? name;
            try
            {
                name = GetName(target);
            }
            catch (Exception ex)
            {
                failureReason = Describe(ex);
                return false;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                failureReason = "name could not be read";
                return false;
            }

            var read = new HmiDetailItem
            {
                Name = name!,
                Container = container ?? "",
                Type = TypeSuffix(target)
            };

            foreach (var attribute in attributes)
            {
                read.Attributes[attribute] = Normalize(ReadAttribute(target, attribute));
            }

            item = read;
            return true;
        }

        /// <summary>
        /// Openness answers through GetAttribute(name); plain CLR properties are the fallback so the
        /// same reader works on objects that expose only properties. Unreadable = null.
        /// </summary>
        public static object? ReadAttribute(object target, string attributeName)
        {
            try
            {
                var getAttribute = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                if (getAttribute != null)
                {
                    try
                    {
                        return getAttribute.Invoke(target, new object[] { attributeName });
                    }
                    catch
                    {
                        // unsupported attribute name: fall through to the property route
                    }
                }

                return target.GetType()
                    .GetProperty(attributeName, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// All tag tables of an HMI software, including the ones filed in a TagTableGroup — the tag
        /// counterpart of the screen-group walk in HmiScreenWalk (PR #41). Depth-first, cycle-safe.
        /// </summary>
        public static List<object> EnumerateTagTables(object hmiSoftware)
        {
            var result = new List<object>();
            if (hmiSoftware == null) return result;

            var visited = new HashSet<object>(ReferenceComparer.Instance);
            var pending = new Stack<object>();

            foreach (var root in TagRoots(hmiSoftware))
            {
                pending.Push(root);
            }

            var ordered = new List<object>();
            while (pending.Count > 0)
            {
                var container = pending.Pop();
                if (!visited.Add(container)) continue;
                ordered.Add(container);

                var children = new List<object>();
                foreach (var propName in TagTableGroupProperties)
                {
                    if (GetProperty(container, propName) is IEnumerable groups && !(groups is string))
                    {
                        try
                        {
                            foreach (var group in groups)
                            {
                                if (group != null) children.Add(group);
                            }
                        }
                        catch
                        {
                            // best-effort: keep what the walk already reached
                        }
                    }
                }
                for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }

            var seenTables = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var container in ordered)
            {
                foreach (var propName in TagTableCollectionProperties)
                {
                    if (!(GetProperty(container, propName) is IEnumerable tables) || tables is string) continue;
                    try
                    {
                        foreach (var table in tables)
                        {
                            if (table != null && seenTables.Add(table)) result.Add(table);
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                    break;   // first collection that exists on this container wins
                }
            }

            return result;
        }

        /// <summary>One tag table by name (case-insensitive), groups included. Null when absent.</summary>
        public static object? FindTagTable(object hmiSoftware, string tableName)
        {
            if (hmiSoftware == null || string.IsNullOrWhiteSpace(tableName)) return null;

            foreach (var table in EnumerateTagTables(hmiSoftware))
            {
                if (string.Equals(GetName(table), tableName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return table;
            }
            return null;
        }

        /// <summary>
        /// Items of one collection property. A property that does not exist, is not a collection, or
        /// breaks half way through is reported in <paramref name="error"/> — "read nothing" and
        /// "there is nothing" must not look the same.
        /// </summary>
        public static List<object> EnumerateChildren(object owner, string propertyName, out string? error)
        {
            error = null;
            var result = new List<object>();

            if (owner == null)
            {
                error = "owner object is null";
                return result;
            }

            object? collection;
            try
            {
                collection = GetProperty(owner, propertyName);
            }
            catch (Exception ex)
            {
                error = $"reading '{propertyName}' failed: {Describe(ex)}";
                return result;
            }

            if (collection == null)
            {
                error = $"'{propertyName}' not found on {owner.GetType().Name}";
                return result;
            }

            if (!(collection is IEnumerable enumerable) || collection is string)
            {
                error = $"'{propertyName}' on {owner.GetType().Name} is not a collection";
                return result;
            }

            try
            {
                foreach (var item in enumerable)
                {
                    if (item != null) result.Add(item);
                }
            }
            catch (Exception ex)
            {
                error = $"enumeration of '{propertyName}' stopped after {result.Count} item(s): {Describe(ex)}";
            }

            return result;
        }

        /// <summary>CLR type name, e.g. HmiButton — what tells a button from a label.</summary>
        public static string? TypeSuffix(object? target) => target?.GetType().Name;

        /// <summary>Name of an Openness object, or null when it has none. Throws only if the getter does.</summary>
        public static string? GetName(object? target)
        {
            if (target == null) return null;
            var prop = target.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return null;
            return prop.GetValue(target)?.ToString();
        }

        /// <summary>
        /// Success means: nothing failed. Reading 40 of 41 is not a success — a caller that trusts
        /// the flag would file a partial inventory as a complete one (the bug ExportHmiProgram and
        /// ExportAlarmClasses both have today).
        /// </summary>
        public static bool IsSuccess(int readCount, int failedCount) => failedCount == 0;

        private static IEnumerable<object> TagRoots(object hmiSoftware)
        {
            yield return hmiSoftware;

            foreach (var propName in TagTableRootProperties)
            {
                object? folder = null;
                try
                {
                    folder = GetProperty(hmiSoftware, propName);
                }
                catch
                {
                    // best-effort
                }
                if (folder != null) yield return folder;
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

        private static string Describe(Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            return $"{inner.GetType().Name}: {inner.Message}";
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
