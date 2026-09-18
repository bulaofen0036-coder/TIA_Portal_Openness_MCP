using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// GetHmiTagDetails / GetHmiScreenItemDetails: reading the *properties* of HMI tags and screen
    /// items, not just their names. The reading itself is pure reflection over the Openness object
    /// graph, so it lives in a zero-dependency file and is fed fake graphs here — the same reason
    /// HmiScreenWalk was extracted in PR #41.
    ///
    /// What these cases watch for (each one is a failure that stays silent in production):
    ///  - an attribute that does not exist on this object type must blank that ONE value, never
    ///    fail the whole call;
    ///  - an item that cannot be read must land in Failed[] with a reason, never be dropped;
    ///  - a value that is not a primitive (MultilingualText, enums, colors) must come back as a
    ///    string: serializing the live object throws "possible object cycle" and takes the whole
    ///    response with it;
    ///  - tag tables filed in a TagTableGroup must be found, the same way screens in a ScreenGroup
    ///    had to be (PR #41).
    /// </summary>
    internal static class HmiDetailReadTests
    {
        internal static void Run(Action<bool, string> check)
        {
            RunAttributeListParsing(check);
            RunValueNormalization(check);
            RunItemReading(check);
            RunTagTableWalk(check);
            RunCollectionEnumeration(check);
            RunOutcome(check);
        }

        private static void RunAttributeListParsing(Action<bool, string> check)
        {
            var defaults = new[] { "Name", "DataType" };

            check(HmiDetailRead.ParseAttributes("", defaults).SequenceEqual(defaults),
                "empty attribute list falls back to the default set");
            check(HmiDetailRead.ParseAttributes("   ", defaults).SequenceEqual(defaults),
                "whitespace-only attribute list falls back to the default set");
            check(HmiDetailRead.ParseAttributes(null, defaults).SequenceEqual(defaults),
                "null attribute list falls back to the default set");

            var parsed = HmiDetailRead.ParseAttributes(" Address , PlcTag ,Address, ", defaults);
            check(parsed.SequenceEqual(new[] { "Address", "PlcTag" }),
                "caller list is trimmed, de-duplicated and kept in order (actual: " + string.Join(",", parsed) + ")");
            check(HmiDetailRead.ParseAttributes("Left,left", defaults).Count == 1,
                "de-duplication ignores case");
        }

        private static void RunValueNormalization(Action<bool, string> check)
        {
            check((HmiDetailRead.Normalize("%DB1001.DBW48") as string) == "%DB1001.DBW48",
                "strings pass through unchanged");
            check(HmiDetailRead.Normalize(15) is int i && i == 15, "ints pass through unchanged");
            check(HmiDetailRead.Normalize(true) is bool b && b, "bools pass through unchanged");
            check(HmiDetailRead.Normalize(null) == null, "null stays null");

            check((HmiDetailRead.Normalize(FakeDataType.Word) as string) == "Word",
                "enums come back as their name, not as a number or an object");

            // The live killer: MultilingualText.Items[].Language.Culture.Parent.Parent... is a cycle,
            // and System.Text.Json throws at depth 64. One such attribute must not sink the response.
            var cyclic = new FakeCyclic("Start");
            cyclic.Parent = cyclic;
            var normalized = HmiDetailRead.Normalize(cyclic);
            check(normalized is string s && s == "Start",
                "a self-referencing object is flattened to a string (actual type: "
                + (normalized?.GetType().Name ?? "null") + ")");

            check(HmiDetailRead.Normalize(new FakeThrowingToString()) is string,
                "an object whose ToString() throws still yields a string, not an exception");
        }

        private static void RunItemReading(Action<bool, string> check)
        {
            var tag = new FakeAttributeHolder("DB_Positioning_Actualstep", new Dictionary<string, object?>
            {
                ["Name"] = "DB_Positioning_Actualstep",
                ["DataType"] = FakeDataType.Word,
                ["Connection"] = "HMI_Connection_3",
                ["Address"] = "%DB1001.DBW48",
                ["PlcTag"] = "",
            });

            var ok = HmiDetailRead.TryReadItem(tag, "SP_Motors",
                new[] { "Name", "DataType", "Address", "PlcTag", "NoSuchAttribute" },
                out var item, out var reason);

            check(ok && item != null, "a readable tag is read (reason: " + (reason ?? "-") + ")");
            check(item?.Name == "DB_Positioning_Actualstep", "the item carries its name");
            check(item?.Container == "SP_Motors", "the item carries its container (tag table / screen)");
            check(item?.Attributes["Address"] as string == "%DB1001.DBW48", "Address is read through GetAttribute");
            check(item?.Attributes["DataType"] as string == "Word", "an enum attribute is read as its name");
            check(item?.Attributes.ContainsKey("NoSuchAttribute") == true && item.Attributes["NoSuchAttribute"] == null,
                "an attribute this type does not have is present as null, not omitted and not an error");
            check(item?.Attributes.Count == 5, "every requested attribute has a key, in one pass");

            // Property fallback: Unified screens expose Width/Height as properties; not every object
            // answers GetAttribute for everything.
            var propertyOnly = new FakePropertyOnlyItem { Name = "Button_1", Left = 15 };
            check(HmiDetailRead.TryReadItem(propertyOnly, "MainScreen", new[] { "Left" }, out var propItem, out _)
                  && propItem?.Attributes["Left"] is int left && left == 15,
                "an object without GetAttribute is read through its CLR property instead");

            // An item that cannot even be named is a failure with a reason — never silently dropped.
            var broken = new FakeThrowingNameItem();
            var brokenOk = HmiDetailRead.TryReadItem(broken, "MainScreen", new[] { "Left" }, out var brokenItem, out var brokenReason);
            check(!brokenOk, "an item whose name cannot be read is reported as failed");
            check(brokenItem == null, "a failed item yields no half-filled entry");
            check(!string.IsNullOrWhiteSpace(brokenReason), "a failed item carries a reason");

            check(HmiDetailRead.TypeSuffix(new FakeButton()) == "FakeButton",
                "the CLR type name is reported so a reviewer can tell a button from a label");
        }

        private static void RunTagTableWalk(Action<bool, string> check)
        {
            var nested = new FakeTagTable("SP_Motors");
            var innerGroup = new FakeTagTableGroup("Motors", new[] { nested });
            var outerGroup = new FakeTagTableGroup("Machine", Array.Empty<FakeTagTable>(), new[] { innerGroup });
            var hmi = new FakeUnifiedHmi(
                new[] { new FakeTagTable("Default tag table"), new FakeTagTable("General") },
                new[] { outerGroup });

            var tables = HmiDetailRead.EnumerateTagTables(hmi);
            var names = tables.Select(HmiDetailRead.GetName).ToList();
            check(names.SequenceEqual(new[] { "Default tag table", "General", "SP_Motors" }),
                "root tag tables and tables nested two groups deep are all listed (actual: " + string.Join(",", names) + ")");

            check(HmiDetailRead.GetName(HmiDetailRead.FindTagTable(hmi, "SP_Motors")) == "SP_Motors",
                "a tag table inside a group is found by name");
            check(HmiDetailRead.GetName(HmiDetailRead.FindTagTable(hmi, "sp_motors")) == "SP_Motors",
                "finding a tag table by name ignores case");
            check(HmiDetailRead.FindTagTable(hmi, "NoSuchTable") == null,
                "a name that does not exist returns null instead of some other table");
            check(HmiDetailRead.FindTagTable(hmi, "Machine") == null,
                "a group's own name is not mistaken for a tag table");

            // A group that contains itself must not loop forever, and must not list twice.
            var loop = new FakeTagTableGroup("Loop", new[] { new FakeTagTable("Once") });
            loop.Groups = new[] { loop };
            check(HmiDetailRead.EnumerateTagTables(new FakeUnifiedHmi(Array.Empty<FakeTagTable>(), new[] { loop })).Count == 1,
                "a self-referencing tag table group is walked once");
        }

        private static void RunCollectionEnumeration(Action<bool, string> check)
        {
            var table = new FakeTagTable("SP_Motors");
            table.Tags = new List<object> { new FakeAttributeHolder("A", new Dictionary<string, object?>()) };
            var items = HmiDetailRead.EnumerateChildren(table, "Tags", out var error);
            check(items.Count == 1 && error == null, "a plain collection is enumerated without error");

            var missing = HmiDetailRead.EnumerateChildren(table, "NoSuchCollection", out var missingError);
            check(missing.Count == 0 && !string.IsNullOrWhiteSpace(missingError),
                "a collection this object does not have is an explicit error, not an empty success");

            // Half a collection read is not a clean read: the caller must be told.
            var partial = HmiDetailRead.EnumerateChildren(new FakeThrowingCollectionOwner(), "Items", out var partialError);
            check(partial.Count == 1, "items read before the enumeration broke are kept");
            check(!string.IsNullOrWhiteSpace(partialError),
                "an enumeration that breaks half way reports why instead of passing as complete");
        }

        private static void RunOutcome(Action<bool, string> check)
        {
            check(HmiDetailRead.IsSuccess(readCount: 9, failedCount: 0), "a clean read is a success");
            check(HmiDetailRead.IsSuccess(readCount: 0, failedCount: 0),
                "a genuinely empty screen is still a success (nothing failed)");
            check(!HmiDetailRead.IsSuccess(readCount: 40, failedCount: 1),
                "one failed item makes the call a failure — a caller trusting success must not file a partial inventory as complete");
            check(!HmiDetailRead.IsSuccess(readCount: 0, failedCount: 41),
                "nothing read and everything failed is never a success (ExportHmiProgram's bug)");
        }

        private enum FakeDataType { Word }

        private sealed class FakeCyclic
        {
            public FakeCyclic(string text) { Text = text; }
            public string Text { get; }
            public FakeCyclic? Parent { get; set; }
            public override string ToString() => Text;
        }

        private sealed class FakeThrowingToString
        {
            public override string ToString() => throw new InvalidOperationException("no text here");
        }

        /// <summary>Openness shape: everything is read through GetAttribute(name), unknown names throw.</summary>
        private sealed class FakeAttributeHolder
        {
            private readonly Dictionary<string, object?> _values;
            public FakeAttributeHolder(string name, Dictionary<string, object?> values)
            {
                Name = name;
                _values = values;
            }
            public string Name { get; }
            public object? GetAttribute(string name)
            {
                if (!_values.TryGetValue(name, out var v))
                    throw new InvalidOperationException($"'{name}' is not supported by type FakeAttributeHolder");
                return v;
            }
        }

        private sealed class FakePropertyOnlyItem
        {
            public string Name { get; set; } = "";
            public int Left { get; set; }
        }

        private sealed class FakeThrowingNameItem
        {
            public string Name => throw new InvalidOperationException("the underlying object is gone");
        }

        private sealed class FakeButton
        {
        }

        private sealed class FakeTagTable
        {
            public FakeTagTable(string name) { Name = name; }
            public string Name { get; }
            public List<object> Tags { get; set; } = new List<object>();
        }

        private sealed class FakeTagTableGroup
        {
            public FakeTagTableGroup(string name, IEnumerable<FakeTagTable> tables, IEnumerable<FakeTagTableGroup>? groups = null)
            {
                Name = name;
                TagTables = tables;
                Groups = groups ?? Array.Empty<FakeTagTableGroup>();
            }
            public string Name { get; }
            public IEnumerable<FakeTagTable> TagTables { get; }
            public IEnumerable<FakeTagTableGroup> Groups { get; set; }
        }

        private sealed class FakeUnifiedHmi
        {
            public FakeUnifiedHmi(IEnumerable<FakeTagTable> tables, IEnumerable<FakeTagTableGroup> groups)
            {
                TagTables = tables;
                TagTableGroups = groups;
            }
            public IEnumerable<FakeTagTable> TagTables { get; }
            public IEnumerable<FakeTagTableGroup> TagTableGroups { get; }
        }

        private sealed class FakeThrowingCollectionOwner
        {
            public IEnumerable Items => Enumerate();
            private static IEnumerable Enumerate()
            {
                yield return new FakePropertyOnlyItem { Name = "First" };
                throw new InvalidOperationException("collection broke half way");
            }
        }
    }
}
