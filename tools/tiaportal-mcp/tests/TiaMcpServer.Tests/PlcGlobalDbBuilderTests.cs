using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// issue #31：GlobalDB 的存储器布局可选，并且导入后能读回确认。
    /// 以前 MemoryLayout 写死 Standard、JSON 里的未知键一律静默丢弃 —— 模板里的 "memoryLayout"
    /// 和拼错的 "optimised" 都不会报错，调用方拿到的 DB 布局和他以为的可能不一样，
    /// 而 PUT/GET 只在现场才会因此失败。
    /// </summary>
    internal static class PlcGlobalDbBuilderTests
    {
        private const string Members = "\"staticMembers\":[{\"name\":\"Run\",\"datatype\":\"Bool\"}]";

        internal static void Run(Action<bool, string> check)
        {
            // ---- 默认值不变：一直生成的就是 Standard（反向哨兵：老调用方的输入照常通过）----
            var plain = PlcBuilderToolJson.BuildGlobalDb("{\"dbName\":\"DB_A\",\"dbNumber\":10," + Members + "}");
            check(LayoutOf(plain) == "Standard", "no layout given → MemoryLayout Standard (unchanged default)");
            check(plain["summary"]?["memoryLayout"]?.GetValue<string>() == "Standard", "summary reports the layout it wrote");

            // ---- 两种写法都能选布局 ----
            check(LayoutOf(Build("\"optimized\":true")) == "Optimized", "optimized:true → Optimized");
            check(LayoutOf(Build("\"optimized\":false")) == "Standard", "optimized:false → Standard");
            check(LayoutOf(Build("\"memoryLayout\":\"Optimized\"")) == "Optimized", "memoryLayout:\"Optimized\" → Optimized");
            check(LayoutOf(Build("\"memoryLayout\":\"optimized\"")) == "Optimized", "memoryLayout is case-insensitive and normalised to TIA's spelling");
            check(LayoutOf(Build("\"optimized\":true,\"memoryLayout\":\"Optimized\"")) == "Optimized",
                "both given and consistent → accepted (reverse sentinel for the conflict check)");

            // ---- 矛盾、非法值、未知键都要报错，而不是悄悄挑一个 ----
            check(Throws(() => Build("\"optimized\":true,\"memoryLayout\":\"Standard\""), "contradicts"),
                "optimized:true + memoryLayout:Standard → error naming the contradiction");
            check(Throws(() => Build("\"memoryLayout\":\"Fast\""), "Standard"),
                "memoryLayout:\"Fast\" → error listing the valid values");
            check(Throws(() => Build("\"memoryLayout\":true"), "memoryLayout"),
                "memoryLayout must be a string");
            check(Throws(() => Build("\"optimised\":true"), "did you mean 'optimized'"),
                "misspelt key 'optimised' → rejected with a did-you-mean hint (used to be silently ignored)");
            check(Throws(() => Build("\"Optimized\":true"), "Unknown JSON property"),
                "wrong-case key is unknown too (keys are matched exactly)");

            // ---- 仓库自带的 globaldb 模板必须仍能通过严格键检查（反向哨兵）----
            var templateDir = ToolSafetyTests.FindRepoFile(Path.Combine("templates", "plc", "plcbuild-json", "db_basic_status.json"));
            if (templateDir == null)
            {
                check(false, "templates/plc/plcbuild-json not found from the test binary");
            }
            else
            {
                foreach (var path in Directory.GetFiles(Path.GetDirectoryName(templateDir)!, "*.json"))
                {
                    var wrapper = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                    if (wrapper["kind"]?.GetValue<string>() != "globaldb") continue;
                    var inner = wrapper["json"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : wrapper["json"]!.ToJsonString();
                    var name = Path.GetFileName(path);
                    try
                    {
                        var built = PlcBuilderToolJson.BuildGlobalDb(inner);
                        var declared = (wrapper["json"] as JsonObject)?["memoryLayout"]?.GetValue<string>();
                        check(built["ok"]?.GetValue<bool>() == true
                              && (declared == null || string.Equals(LayoutOf(built), declared, StringComparison.OrdinalIgnoreCase)),
                            $"template {name} builds and keeps its declared layout");
                    }
                    catch (Exception ex)
                    {
                        check(false, $"template {name} must still build under the strict key check: {ex.Message}");
                    }
                }
            }

            // ---- 读回比对：布局写进 XML 就必须读回一致 ----
            var expected = BlockSnapshotCompare.ReadExpectedBlockFromXml(plain["xml"]!.GetValue<string>());
            check(expected != null && expected.MemoryLayout == "Standard" && expected.Number == 10 && expected.Name == "DB_A",
                "expected snapshot carries Name/Number/MemoryLayout from the generated XML");

            if (expected != null)
            {
                var same = BlockSnapshotCompare.CompareBlockSnapshots(expected, Actual("DB_A", 10, "Standard"));
                check(same.State == PlcBlockVerificationState.Verified, "read-back Standard for a Standard DB → Verified");
                check(same.Actual?.MemoryLayout == "Standard", "outcome hands the read-back snapshot to the caller");

                var flipped = BlockSnapshotCompare.CompareBlockSnapshots(expected, Actual("DB_A", 10, "Optimized"));
                check(flipped.State == PlcBlockVerificationState.Mismatch && flipped.Detail.Contains("MemoryLayout expected 'Standard' actual 'Optimized'"),
                    "read-back Optimized for a Standard DB → Mismatch naming MemoryLayout");

                var unknown = BlockSnapshotCompare.CompareBlockSnapshots(expected, Actual("DB_A", 10, null));
                check(unknown.State == PlcBlockVerificationState.Verified && unknown.Detail.Contains("MemoryLayout (not exposed on read-back)"),
                    "layout not readable → not a mismatch, but listed as not verifiable");

                var missing = BlockSnapshotCompare.CompareBlockSnapshots(expected, null);
                check(missing.State == PlcBlockVerificationState.Mismatch && missing.Detail.Contains("NOT found"),
                    "block absent after import → Mismatch");
            }

            // ---- 老 XML 没写 MemoryLayout：不比这一项（反向哨兵）----
            var noLayout = BlockSnapshotCompare.ReadExpectedBlockFromXml(
                "<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Name>FC_X</Name><Number>5</Number></AttributeList></SW.Blocks.FC></Document>");
            check(noLayout != null && noLayout.MemoryLayout == null
                  && BlockSnapshotCompare.CompareBlockSnapshots(noLayout, Actual("FC_X", 5, "Optimized", "FC")).State == PlcBlockVerificationState.Verified,
                "XML without MemoryLayout → layout is not compared");
        }

        private static JsonObject Build(string extra) =>
            PlcBuilderToolJson.BuildGlobalDb("{\"dbName\":\"DB_A\",\"dbNumber\":10," + extra + "," + Members + "}");

        private static string? LayoutOf(JsonObject built) =>
            XDocument.Parse(built["xml"]!.GetValue<string>()).Descendants("MemoryLayout").FirstOrDefault()?.Value;

        private static PlcBlockAttributeSnapshot Actual(string name, int number, string? layout, string kind = "GlobalDB") =>
            new PlcBlockAttributeSnapshot { Name = name, Number = number, BlockKind = kind, MemoryLayout = layout };

        private static bool Throws(Action action, string messageFragment)
        {
            try { action(); return false; }
            catch (ArgumentException ex) { return ex.Message.IndexOf(messageFragment, StringComparison.Ordinal) >= 0; }
        }
    }
}
