using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 2.7.10: #60 (walk cross-reference Children), #61 (block listing covers software units),
    /// #62 (type paths in software units + declared field projection), and the PLC-HMI sync check
    /// that used to pass a package of absolute-address tags with nothing checked.
    /// </summary>
    internal static class Release2710Tests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            RunCrossReferenceChildren(check);
            RunTypeMembers(check);
            RunAbsoluteSync(check);
            RunWiring(check, skip);
        }

        // ---------------- #60 Children ----------------

        private sealed class Loc { public string Name => "NW1"; public string ReferenceLocation => "@FB_Pump NW1"; public string ReferenceType => "UsedBy"; public string Access => "Read"; }
        private sealed class Ref { public string Name { get; set; } = "FB_Pump"; public string Path => "PLC_1\\Program blocks"; public IEnumerable<Loc>? Locations { get; set; } = new[] { new Loc() }; }
        private sealed class Node
        {
            public string Name { get; set; } = "";
            public string Path => "PLC_1\\Program blocks";
            public Func<IEnumerable<Ref>?> RefsGetter { get; set; } = () => Array.Empty<Ref>();
            public Func<IEnumerable<Node>?> ChildrenGetter { get; set; } = () => Array.Empty<Node>();
            public IEnumerable<Ref>? References => RefsGetter();
            public IEnumerable<Node>? Children => ChildrenGetter();
        }
        private sealed class Result { public IEnumerable<Node>? Sources { get; set; } }
        private sealed class NonRecoverableException : Exception { internal NonRecoverableException(string m) : base(m) { } }

        private static Node Leaf(string name, int refs) => new Node { Name = name, RefsGetter = () => Enumerable.Range(0, refs).Select(_ => new Ref()).ToArray() };

        private static void RunCrossReferenceChildren(Action<bool, string> check)
        {
            // V21 data block shape: the top node has no references, the members (children) do.
            var motor = new Node { Name = "Motor", ChildrenGetter = () => new[] { Leaf("Speed", 1) } };
            var db = new Node { Name = "DB_HMI", ChildrenGetter = () => new[] { Leaf("Start", 2), motor } };
            var items = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { db } }, "fb", out var t, out var unread);
            check(t == null && unread.Count == 0 && items.Count == 3, "#60 数据块形状：成员（子分支）里的引用全部读出且标完整，实得 " + items.Count + " 条，截断=" + (t ?? "null"));
            check(items.Count(i => i.SourceHierarchy == "DB_HMI.Start") == 2 && items.Any(i => i.SourceHierarchy == "DB_HMI.Motor.Speed" && i.SourceName == "Speed"),
                "#60 每条引用带层级路径（DB_HMI.Start / DB_HMI.Motor.Speed）");

            // one branch fails: stop that branch, keep the siblings, say which branch was not read.
            var bad = new Node { Name = "Bad", RefsGetter = () => throw new InvalidOperationException("unexpected result") };
            var db2 = new Node { Name = "DB_X", ChildrenGetter = () => new[] { Leaf("A", 1), bad, Leaf("C", 1) } };
            var items2 = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { db2 } }, "fb", out var t2, out var unread2);
            check(items2.Count == 2 && unread2.Count == 1 && unread2[0].StartsWith("DB_X.Bad"), "#60 一个分支读失败：只停这个分支，兄弟分支照读，并列出没读的分支（实得 " + string.Join(" | ", unread2) + "）");
            check(t2 != null && t2.Contains("DB_X.Bad") && t2.Contains("INCOMPLETE") && t2.Contains("unexpected result"), "#60 截断说明列出分支和原生原因");

            var brokenChildren = new Node { Name = "DB_Y", ChildrenGetter = () => throw new InvalidOperationException("children unavailable") };
            CrossReferenceFlattener.Flatten(new Result { Sources = new[] { brokenChildren } }, "fb", out var t3, out var unread3);
            check(t3 != null && unread3.Count == 1 && unread3[0].StartsWith("DB_Y"), "#60 Children 本身读不出：标不完整并点名");

            Node chain = Leaf("deepest", 1);
            for (var i = 0; i < CrossReferenceFlattener.MaxDepth + 2; i++) { var inner = chain; chain = new Node { Name = "L" + i, ChildrenGetter = () => new[] { inner } }; }
            CrossReferenceFlattener.Flatten(new Result { Sources = new[] { chain } }, "fb", out var t4, out var unread4);
            check(t4 != null && unread4.Any(u => u.Contains("deeper than")), "#60 超过深度上限：不无限展开，标不完整");

            var many = new Node { Name = "Big", RefsGetter = () => Enumerable.Range(0, CrossReferenceFlattener.MaxEntries + 5).Select(_ => new Ref { Locations = null }).ToArray() };
            var items5 = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { many } }, "fb", out var t5, out _);
            check(items5.Count == CrossReferenceFlattener.MaxEntries && t5 != null && t5.Contains("limit"), "#60 超过条数上限：停下并标不完整");

            var lost = new Node { Name = "DB_Z", ChildrenGetter = () => new[] { new Node { Name = "M", RefsGetter = () => throw new NonRecoverableException("TIA Portal has been closed") } } };
            var rethrown = false;
            try { CrossReferenceFlattener.Flatten(new Result { Sources = new[] { lost } }, "fb", out _, out _); }
            catch (Exception ex) { rethrown = PortalFailureClassifier.IsPortalProcessLost(ex); }
            check(rethrown, "#60 子分支里博途进程没了：直接抛，不当部分结果");

            var plain = CrossReferenceFlattener.Flatten(new Result { Sources = new[] { Leaf("FB_Pump", 3) } }, "fb", out var t6, out var unread6);
            check(t6 == null && unread6.Count == 0 && plain.Count == 3 && plain[0].SourceHierarchy == "FB_Pump", "[反向哨兵] 没有子分支的块照旧：3 条、完整");
        }

        // ---------------- #62 fields ----------------

        private const string Ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

        private static string Udt(string members) =>
            "<Document><SW.Types.PlcStruct ID=\"0\"><AttributeList><Interface><Sections xmlns=\"" + Ns + "\"><Section Name=\"None\">"
            + members + "</Section></Sections></Interface><Name>UDT_T</Name></AttributeList></SW.Types.PlcStruct></Document>";

        private static void RunTypeMembers(Action<bool, string> check)
        {
            // The shape from issue #62: Member -> Sections -> Section -> Member.
            var r = TypeMemberReader.Read(Udt("<Member Name=\"status\" Datatype=\"Struct\"><Sections><Section Name=\"None\"><Member Name=\"ready\" Datatype=\"Bool\" /></Section></Sections></Member>"));
            var paths = (r["members"] as JsonArray)!.Select(m => m!["path"]!.ToString()).ToArray();
            check(r["complete"]!.GetValue<bool>() && paths.SequenceEqual(new[] { "status", "status.ready" }), "#62 Member→Sections→Section→Member 形状：status.ready 不许漏，实得 " + string.Join(",", paths));

            var r2 = TypeMemberReader.Read(Udt(
                "<Member Name=\"speed\" Datatype=\"Real\"><Comment><MultiLanguageText Lang=\"zh-CN\">转速</MultiLanguageText></Comment><StartValue>1.5</StartValue></Member>"
                + "<Member Name=\"motor\" Datatype=\"Struct\"><Member Name=\"run\" Datatype=\"Bool\"><StartValue>true</StartValue></Member></Member>"
                + "<Member Name=\"arr\" Datatype=\"Array[0..1] of Int\"><Subelement Path=\"1\"><StartValue>7</StartValue></Subelement></Member>"));
            var m2 = (r2["members"] as JsonArray)!.OfType<JsonObject>().ToDictionary(m => m["path"]!.ToString());
            check(r2["complete"]!.GetValue<bool>() && m2.Count == 4, "#62 直接嵌套 Struct：4 个字段、完整");
            check(m2["speed"]["startValue"]?.ToString() == "1.5" && m2["speed"]["comment"]?.ToString() == "转速" && m2["speed"]["dataType"]?.ToString() == "Real", "#62 类型 / 显式初值 / 注释都带上");
            check(m2["motor.run"]["startValue"]?.ToString() == "true", "#62 嵌套字段的初值");
            check(m2["arr"]["elementStartValues"]?[0]?["index"]?.ToString() == "1" && m2["arr"]["elementStartValues"]?[0]?["startValue"]?.ToString() == "7", "#62 数组元素初值（Subelement）");

            var r3 = TypeMemberReader.Read(Udt("<Member Name=\"x\" Datatype=\"Struct\"><Weird><Member Name=\"hidden\" Datatype=\"Bool\" /></Weird></Member>"));
            check(r3["complete"]!.GetValue<bool>() == false && r3["issues"]!.ToJsonString().Contains("unvisited-members"),
                "#62 认不出的容器里还有字段：complete=false 并说明，不许静默漏");

            var r4 = TypeMemberReader.Read("<Document><SW.Blocks.FC /></Document>");
            check(r4["complete"]!.GetValue<bool>() == false, "#62 不是类型导出（没有 Interface）：complete=false");
            var r5 = TypeMemberReader.Read("<not xml");
            check(r5["complete"]!.GetValue<bool>() == false && r5["issues"]!.ToJsonString().Contains("xml-parse-error"), "#62 XML 坏了：complete=false，不抛");
        }

        // ---------------- PLC-HMI sync for absolute tags ----------------

        private static string Package(string tagsJson) => @"{ ""Name"": ""Abs_Sync"",
  ""TagTable"": { ""Name"": ""T"", ""ConnectionName"": ""C1"", ""Tags"": " + tagsJson + @" },
  ""ScreenDesign"": { ""Screen"": {""Name"":""S1"",""Width"":640,""Height"":480},
    ""Items"": [ {""Type"":""Lamp"",""Name"":""L1"",""Left"":20,""Top"":20,""Width"":40,""Height"":40,""Tag"":""Run""} ] } }";

        private static JsonObject Sync(string tagsJson, string symbolsJson)
        {
            var dir = Path.Combine(Path.GetTempPath(), "tia_sync_" + Guid.NewGuid().ToString("N"));
            try
            {
                ClassicHmiMinimalPackageBuilder.WriteFiles(Package(tagsJson), dir);
                return ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(dir, symbolsJson);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static void RunAbsoluteSync(Action<bool, string> check)
        {
            const string tags = @"[ {""Name"":""Run"",""DataType"":""Bool"",""LogicalAddress"":""%M10.0""}, {""Name"":""Speed"",""DataType"":""Int"",""LogicalAddress"":""%MW30""} ]";
            var ok = Sync(tags, @"[ {""Name"":""Motor_Run"",""Address"":""%M10.0""}, {""Name"":""Motor_Speed"",""Address"":""%MW30""} ]");
            check(ok["ok"]!.GetValue<bool>() && ok["absoluteCheckedCount"]!.GetValue<int>() == 2 && ok["unusedPlcSymbolCount"]!.GetValue<int>() == 0,
                "[反向哨兵] 绝对地址都在 PLC 清单里：通过，2 个已核对，对应符号不算「未用」（" + ok["errors"]!.ToJsonString() + "）");

            var noAddr = Sync(tags, @"[""Motor_Run"", ""Motor_Speed""]");
            check(noAddr["warnings"]!.ToJsonString().Contains("absolute-bindings-unchecked") && noAddr["absoluteCheckedCount"]!.GetValue<int>() == 0,
                "同步自检：全是绝对地址但清单不带地址，不许静默通过（明确警告 0 个已核对）");

            var missing = Sync(tags, @"[ {""Name"":""Motor_Run"",""Address"":""%M10.0""} ]");
            check(missing["warnings"]!.ToJsonString().Contains("no-plc-tag-at-address: Speed = %MW30") && missing["noPlcTagAtAddressCount"]!.GetValue<int>() == 1,
                "同步自检：PLC 里没有变量的绝对地址要点名（绝对寻址合法，所以是警告不是错误）");

            // The GetPlcTags result passed as-is.
            var getPlcTags = @"{ ""message"": ""ok"", ""data"": { ""truncated"": false, ""tags"": [ {""name"":""Motor_Run"",""address"":""%M10.0"",""dataType"":""Bool""}, {""name"":""Motor_Speed"",""address"":""%MW30"",""dataType"":""Int""} ] } }";
            var asIs = Sync(tags, getPlcTags);
            check(asIs["ok"]!.GetValue<bool>() && asIs["absoluteCheckedCount"]!.GetValue<int>() == 2 && asIs["plcSymbolCount"]!.GetValue<int>() == 2,
                "同步自检：GetPlcTags 的结果原样传入就能核对（" + asIs["errors"]!.ToJsonString() + asIs["warnings"]!.ToJsonString() + "）");
            var truncated = Sync(tags, getPlcTags.Replace("\"truncated\": false", "\"truncated\": true"));
            check(truncated["warnings"]!.ToJsonString().Contains("plc-symbol-list-truncated"), "同步自检：清单自称被截断时提醒「缺的可能只是没列出来」");

            var german = Sync(@"[ {""Name"":""Run"",""DataType"":""Bool"",""LogicalAddress"":""%E0.0""} ]", @"[ {""Name"":""In0"",""Address"":""%I0.0""} ]");
            check(german["absoluteCheckedCount"]!.GetValue<int>() == 1 && german["noPlcTagAtAddressCount"]!.GetValue<int>() == 0, "同步自检：德式 %E0.0 与 %I0.0 视为同一地址");

            var single = Sync(@"[ {""Name"":""Run"",""DataType"":""Bool"",""LogicalAddress"":""%M10.0""} ]", @"{""Name"":""Motor_Run"",""Address"":""%M10.0""}");
            check(single["absoluteCheckedCount"]!.GetValue<int>() == 1 && single["noPlcTagAtAddressCount"]!.GetValue<int>() == 0, "同步自检：只传单个符号对象也能核对地址");

            var db = Sync(@"[ {""Name"":""Run"",""DataType"":""Bool"",""LogicalAddress"":""%DB1.DBX0.0""} ]", @"[ {""Name"":""X"",""Address"":""%M0.0""} ]");
            check(db["ok"]!.GetValue<bool>() && db["uncheckedAbsoluteCount"]!.GetValue<int>() == 1 && db["warnings"]!.ToJsonString().Contains("absolute-address-unchecked"),
                "DB 地址离线核对不了：不报错，但明确列为未核对");
        }

        // ---------------- wiring in Siemens-dependent files ----------------

        private static void RunWiring(Action<bool, string> check, Action<string, string> skip)
        {
            var blocks = FindSource(Path.Combine("ModelContextProtocol", "McpServer.Blocks.cs"));
            var types = FindSource(Path.Combine("ModelContextProtocol", "McpServer.Types.cs"));
            var helpers = FindSource(Path.Combine("Siemens", "Portal.Helpers.cs"));
            if (blocks == null || types == null || helpers == null)
            {
                skip("#61/#62 接线（源码形状）", "找不到引擎源码目录（只在仓内跑得了）");
                return;
            }
            var b = File.ReadAllText(blocks);
            var i = b.IndexOf("Name = \"GetBlocksWithHierarchy\"", StringComparison.Ordinal);
            var body = i < 0 ? "" : b.Substring(i, Math.Min(4000, b.Length - i));
            check(body.Contains("GetSoftwareUnitBlockGroups") && body.Contains("[\"complete\"] = unitFailure == null"), "#61 列全部块要并入软件单元，读不出单元时标不完整");

            var t = File.ReadAllText(types);
            check(t.Contains("Portal.GetTypesWithPaths(") && t.Contains("TypePath = typePath"), "#62 GetTypes 带出每个类型的路径（含软件单元）");
            check(t.Contains("bool includeMembers = false") && t.Contains("TypeMemberReader.Read("), "#62 GetTypeInfo 可选读字段");

            var h = File.ReadAllText(helpers);
            var j = h.IndexOf("private PlcTypeGroup? GetPlcTypeGroupByPath", StringComparison.Ordinal);
            var tg = j < 0 ? "" : h.Substring(j, Math.Min(2500, h.Length - j));
            check(tg.Contains("IsSoftwareUnitsSegment") && tg.Contains("unit?.TypeGroup"), "#62 类型路径认 Software units/<单元>/ 前缀");
        }

        private static string? FindSource(string relative)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var k = 0; k < 10 && dir != null; k++)
            {
                var candidate = Path.Combine(dir, "src", "TiaMcpServer", relative);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }
    }
}
