using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// issue #30：PlcBuildAndImport kind=ob。
    /// 对照的是 V21 Upd2 真实导出（fixtures/ob/*.export.xml，InstalledProducts 已抹掉）：
    /// SecondaryType 的写法、CyclicTime 在 AttributeList 里的位置、块号与类别的配对规则。
    /// 配对写错时 TIA 在导入期才报 "'101' is not a valid OB number" —— 构建器要在生成 XML 前就拦下。
    /// </summary>
    internal static class PlcObBuilderTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // ---- 按块号推断类别（反向哨兵：最常见的写法不用多给参数）----
            var ob100 = Build("{\"blockName\":\"Startup\",\"blockNumber\":100}");
            check(Attr(ob100, "SecondaryType") == "Startup" && Attr(ob100, "Number") == "100", "OB100 without eventClass → Startup");
            check(Attr(Build("{\"blockName\":\"Main2\",\"blockNumber\":1}"), "SecondaryType") == "ProgramCycle", "OB1 without eventClass → ProgramCycle");
            check(Attr(Build("{\"name\":\"Cyc\",\"number\":35,\"cyclicTimeUs\":100000}"), "SecondaryType") == "CyclicInterrupt",
                "OB35 without eventClass → CyclicInterrupt (aliases name/number accepted)");
            check(ob100["summary"]?["hasBody"]?.GetValue<bool>() == false && ob100["ok"]?.GetValue<bool>() == true,
                "an OB without a body is valid (placeholder OBs are common)");

            // ---- 循环中断：周期必须给、只对循环中断有效 ----
            check(Throws(() => Build("{\"blockName\":\"C\",\"blockNumber\":35}"), "needs cyclicTimeUs"),
                "OB35 without cyclicTimeUs → error");
            var ob31 = Build("{\"blockName\":\"C\",\"blockNumber\":31,\"eventClass\":\"CyclicInterrupt\",\"cyclicTimeUs\":50000}");
            check(Attr(ob31, "CyclicTime") == "50000", "cyclicTimeUs=50000 → <CyclicTime>50000</CyclicTime>");
            check(Attr(Build("{\"blockName\":\"C\",\"blockNumber\":31,\"cyclicTimeMs\":50}"), "CyclicTime") == "50000",
                "cyclicTimeMs=50 is converted to 50000 µs");
            check(Throws(() => Build("{\"blockName\":\"C\",\"blockNumber\":31,\"cyclicTimeMs\":50,\"cyclicTimeUs\":40000}"), "contradicts"),
                "cyclicTimeMs and cyclicTimeUs that disagree → error");
            check(Throws(() => Build("{\"blockName\":\"S\",\"blockNumber\":100,\"cyclicTimeUs\":1000}"), "only apply to CyclicInterrupt"),
                "cyclicTimeUs on a Startup OB → error");
            check(Throws(() => Build("{\"blockName\":\"C\",\"blockNumber\":31,\"cyclicTimeUs\":1000,\"phaseOffsetUs\":1000}"), "phaseOffsetUs"),
                "phaseOffsetUs >= cyclicTimeUs → error");
            check(Attr(Build("{\"blockName\":\"C\",\"blockNumber\":31,\"cyclicTimeUs\":1000,\"phaseOffsetUs\":250}"), "PhaseOffset") == "250",
                "a valid phaseOffsetUs is written");

            // ---- 块号与类别的配对（TIA 导入期同一规则，实测 OB101+CyclicInterrupt 被拒）----
            check(Throws(() => Build("{\"blockName\":\"X\",\"blockNumber\":100,\"eventClass\":\"CyclicInterrupt\",\"cyclicTimeUs\":1000}"), "cannot be a CyclicInterrupt"),
                "OB100 declared as CyclicInterrupt → error before any XML is written");
            check(Throws(() => Build("{\"blockName\":\"X\",\"blockNumber\":101,\"eventClass\":\"CyclicInterrupt\",\"cyclicTimeUs\":1000}"), "30..38 or 123..32767"),
                "OB101 as CyclicInterrupt (rejected by TIA live) → rejected by the builder with the allowed range");
            check(Throws(() => Build("{\"blockName\":\"X\",\"blockNumber\":2}"), "eventClass is required"),
                "OB2 without eventClass → error asking for it");
            foreach (var cls in new[] { "ProgramCycle", "Startup" })
                check(Attr(Build("{\"blockName\":\"X\",\"blockNumber\":123,\"eventClass\":\"" + cls + "\"}"), "SecondaryType") == cls,
                    $"OB123 is a free number for {cls}");
            check(Attr(Build("{\"blockName\":\"X\",\"blockNumber\":200,\"eventClass\":\"CyclicInterrupt\",\"cyclicTimeUs\":10000}"), "Number") == "200",
                "OB200 is a free number for CyclicInterrupt");
            check(Throws(() => Build("{\"blockName\":\"X\",\"blockNumber\":123}"), "eventClass is required"),
                "a free number needs an explicit eventClass");

            // ---- 类别名的写法 ----
            check(Attr(Build("{\"blockName\":\"X\",\"blockNumber\":1,\"eventClass\":\"Program cycle\"}"), "SecondaryType") == "ProgramCycle",
                "'Program cycle' (TIA's display name) is accepted");
            check(Attr(Build("{\"blockName\":\"X\",\"blockNumber\":100,\"secondaryType\":\"startup\"}"), "SecondaryType") == "Startup",
                "secondaryType alias + lower case accepted");
            check(Throws(() => Build("{\"blockName\":\"X\",\"blockNumber\":40,\"eventClass\":\"HardwareInterrupt\"}"), "ExportBlock"),
                "unsupported classes are refused with the ExportBlock/ImportBlock route");
            check(Throws(() => Build("{\"blockName\":\"X\",\"blockNumber\":100,\"eventClas\":\"Startup\"}"), "did you mean 'eventClass'"),
                "unknown key → rejected with a did-you-mean hint");

            // ---- 程序体与临时变量 ----
            var withBody = Build("{\"blockName\":\"S\",\"blockNumber\":100,\"temps\":[{\"name\":\"i\",\"datatype\":\"Int\"}],"
                                 + "\"structuredText\":{\"operations\":[{\"op\":\"assignment\",\"target\":\"i\",\"value\":\"0\"}]}}");
            var doc = XDocument.Parse(withBody["xml"]!.GetValue<string>());
            check(doc.Descendants().Any(e => e.Name.LocalName == "Section" && (string?)e.Attribute("Name") == "Temp"
                                             && e.Elements().Any(m => (string?)m.Attribute("Name") == "i")),
                "temps land in the Temp section");
            check(doc.Descendants().Any(e => e.Name.LocalName == "StructuredText" && e.HasElements), "the body is emitted as StructuredText");
            check(doc.Descendants().Where(e => e.Name.LocalName == "Section" && (string?)e.Attribute("Name") == "Input").All(s => !s.HasElements),
                "the Input section is left empty for TIA to fill with the class's start information");
            check(Throws(() => Build("{\"blockName\":\"S\",\"blockNumber\":100,\"temps\":[{\"name\":\"i\",\"datatype\":\"Int\"},{\"name\":\"I\",\"datatype\":\"Int\"}]}"), "duplicated"),
                "duplicate temp names → error");

            // ---- 标题/注释只在给了文字时才写：zh-CN 文本导进没有中文项目语言的工程会整块失败（V21 实测）----
            check(!XDocument.Parse(ob100["xml"]!.GetValue<string>()).Descendants("MultilingualText").Any(),
                "no comment/title given → no MultilingualText at all (imports on projects without zh-CN)");
            var titled = Build("{\"blockName\":\"S\",\"blockNumber\":100,\"titleZhCn\":\"启动\",\"networkCommentZhCn\":\"初始化\"}");
            var texts = XDocument.Parse(titled["xml"]!.GetValue<string>()).Descendants("MultilingualText").ToArray();
            check(texts.Length == 2 && texts.Any(t => (string?)t.Attribute("ID") == "8") && texts.Any(t => (string?)t.Attribute("ID") == "4"),
                "given title/comment → only those texts, with the shared ObjectList IDs");

            // ---- 与 V21 真实导出对拍：属性顺序、SecondaryType 写法 ----
            CompareWithExport(check, "OB100_Startup.export.xml", ob100);
            CompareWithExport(check, "OB31_Cyclic_50ms.export.xml", ob31);

            // ---- 读回比对：OB 类型、周期、块种类 ----
            var expected = BlockSnapshotCompare.ReadExpectedBlockFromXml(ob31["xml"]!.GetValue<string>())!;
            check(expected.SecondaryType == "CyclicInterrupt" && expected.CyclicTimeUs == 50000 && expected.BlockKind == "SW.Blocks.OB",
                "expected snapshot carries SecondaryType, CyclicTime and the block kind");
            var ok = BlockSnapshotCompare.CompareBlockSnapshots(expected, Snap("C", 31, "OB", "CyclicInterrupt", 50000));
            check(ok.State == PlcBlockVerificationState.Verified, "matching OB read-back → Verified");
            var wrongPeriod = BlockSnapshotCompare.CompareBlockSnapshots(expected, Snap("C", 31, "OB", "CyclicInterrupt", 100000));
            check(wrongPeriod.State == PlcBlockVerificationState.Mismatch && wrongPeriod.Detail.Contains("CyclicTime expected '50000'"),
                "CyclicTime not applied → Mismatch naming it");
            var demoted = BlockSnapshotCompare.CompareBlockSnapshots(expected, Snap("C", 31, "OB", "ProgramCycle", null));
            check(demoted.State == PlcBlockVerificationState.Mismatch && demoted.Detail.Contains("SecondaryType"),
                "an OB that landed as ProgramCycle → Mismatch (the .s7dcl failure mode)");
            var wrongKind = BlockSnapshotCompare.CompareBlockSnapshots(expected, Snap("C", 31, "GlobalDB", null, null));
            check(wrongKind.State == PlcBlockVerificationState.Mismatch && wrongKind.Detail.Contains("block kind"),
                "a DB with the same number is not the OB → Mismatch (DB100/FC100/OB100 share numbers)");
            check(BlockSnapshotCompare.KindMatches("SW.Blocks.OB", "OB") && BlockSnapshotCompare.KindMatches("SW.Blocks.GlobalDB", "GlobalDB")
                  && !BlockSnapshotCompare.KindMatches("SW.Blocks.OB", "GlobalDB") && BlockSnapshotCompare.KindMatches(null, "OB"),
                "KindMatches: XML element vs .NET type name; unknown side does not decide");
        }

        private static void CompareWithExport(Action<bool, string> check, string fixture, JsonObject built)
        {
            var path = ToolSafetyTests.FindRepoFile(Path.Combine("tools", "tiaportal-mcp", "tests", "TiaMcpServer.Tests", "fixtures", "ob", fixture));
            if (path == null) { check(false, $"fixture {fixture} not found"); return; }

            var export = XDocument.Load(path);
            var mine = XDocument.Parse(built["xml"]!.GetValue<string>());
            var exportAttrs = AttributeNames(export);
            var mineAttrs = AttributeNames(mine);
            check(exportAttrs.SequenceEqual(mineAttrs),
                $"{fixture}: AttributeList element order matches the TIA export ({string.Join(",", mineAttrs)} vs {string.Join(",", exportAttrs)})");
            foreach (var name in new[] { "SecondaryType", "Number", "CyclicTime", "MemoryLayout", "ProgrammingLanguage" })
            {
                var e = AttrOf(export, name);
                if (e == null) continue;
                check(AttrOf(mine, name) == e, $"{fixture}: {name} = '{e}' as in the export");
            }
        }

        private static string[] AttributeNames(XDocument doc) =>
            doc.Descendants("SW.Blocks.OB").First().Element("AttributeList")!.Elements().Select(e => e.Name.LocalName).ToArray();

        private static string? AttrOf(XDocument doc, string name) =>
            doc.Descendants("SW.Blocks.OB").First().Element("AttributeList")?.Element(name)?.Value;

        private static JsonObject Build(string json) => PlcBuilderToolJson.BuildOb(json);

        private static string? Attr(JsonObject built, string name) =>
            AttrOf(XDocument.Parse(built["xml"]!.GetValue<string>()), name);

        private static PlcBlockAttributeSnapshot Snap(string name, int number, string kind, string? secondaryType, int? cyclicUs) =>
            new PlcBlockAttributeSnapshot
            {
                Name = name, Number = number, BlockKind = kind, SecondaryType = secondaryType,
                CyclicTimeUs = cyclicUs, MemoryLayout = "Optimized"
            };

        private static bool Throws(Action action, string messageFragment)
        {
            try { action(); return false; }
            catch (ArgumentException ex) { return ex.Message.IndexOf(messageFragment, StringComparison.Ordinal) >= 0; }
        }
    }
}
