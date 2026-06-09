using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// PLC 全局 DB XML 构造器。
    /// 第一版覆盖 Static 成员、数据类型、ExternalWritable、中文注释和 StartValue。
    /// </summary>
    public static class PlcGlobalDbXmlBuilder
    {
        private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

        public static XDocument BuildDocument(string dbName, int dbNumber, IEnumerable<PlcDbMemberDefinition> staticMembers)
        {
            if (string.IsNullOrWhiteSpace(dbName))
                throw new ArgumentException("DB 名称不能为空。", nameof(dbName));
            if (dbNumber <= 0)
                throw new ArgumentException("DB 编号必须大于 0。", nameof(dbNumber));

            var members = staticMembers?.ToArray() ?? throw new ArgumentNullException(nameof(staticMembers));
            if (members.Length == 0)
                throw new ArgumentException("全局 DB 至少需要 1 个 Static 成员。", nameof(staticMembers));
            ValidateMembers(members);

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", "V21")),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("SW.Blocks.GlobalDB",
                        new XAttribute("ID", "0"),
                        new XElement("AttributeList",
                            new XElement("Interface",
                                new XElement(InterfaceNs + "Sections",
                                    new XElement(InterfaceNs + "Section",
                                        new XAttribute("Name", "Static"),
                                        members.Select(BuildMember)))),
                            new XElement("MemoryLayout", "Standard"),
                            new XElement("Name", dbName),
                            new XElement("Number", dbNumber),
                            new XElement("ProgrammingLanguage", "DB")))));
        }

        public static string BuildXml(string dbName, int dbNumber, IEnumerable<PlcDbMemberDefinition> staticMembers)
        {
            using var writer = new Utf8StringWriter();
            BuildDocument(dbName, dbNumber, staticMembers).Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        public static JsonObject RunProbe(string fixtureDirectory, string reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var goldenPath = Path.Combine(fixtureDirectory, "Sim_Data_roundtrip.xml", "Sim_Data.xml");
            var generatedPath = Path.Combine(reportDirectory, "Sim_Data.minimal.generated_" + stamp + ".xml");
            var jsonPath = Path.Combine(reportDirectory, "plc_global_db_builder_probe_" + stamp + ".json");
            var mdPath = Path.Combine(reportDirectory, "plc_global_db_builder_probe_" + stamp + ".md");

            var golden = AnalyzeGlobalDb(goldenPath, maxMembers: 25);
            var probeMembers = ReadMembers(goldenPath)
                .Take(25)
                .Select(x => new PlcDbMemberDefinition(
                    x.Name,
                    x.Datatype,
                    x.ExternalWritable,
                    x.CommentZhCn,
                    x.StartValue))
                .ToArray();

            File.WriteAllText(generatedPath, BuildXml("Sim_Data", 16, probeMembers), Encoding.UTF8);
            var generated = AnalyzeGlobalDb(generatedPath, maxMembers: 25);
            var semanticEqual = CompareSemantics(golden, generated);

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "offline-plc-global-db-builder-probe",
                ["goldenPath"] = goldenPath,
                ["generatedPath"] = generatedPath,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线生成和解析 GlobalDB XML，不连接 TIA Portal，不导入 PLC DB。",
                    ["write"] = "只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。"
                },
                ["golden"] = golden,
                ["generated"] = generated,
                ["semanticEqual"] = semanticEqual,
                ["ok"] = golden["ok"]?.GetValue<bool>() == true &&
                       generated["ok"]?.GetValue<bool>() == true &&
                       semanticEqual
            };

            File.WriteAllText(jsonPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
            File.WriteAllText(mdPath, BuildProbeMarkdown(root, jsonPath), Encoding.UTF8);
            root["jsonPath"] = jsonPath;
            root["markdownPath"] = mdPath;
            return root;
        }

        public static JsonObject AnalyzeGlobalDb(string path, int maxMembers = int.MaxValue)
        {
            var root = new JsonObject
            {
                ["path"] = path,
                ["exists"] = File.Exists(path)
            };
            if (!File.Exists(path))
            {
                root["ok"] = false;
                root["error"] = "文件不存在。";
                return root;
            }

            try
            {
                var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                var db = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "SW.Blocks.GlobalDB");
                var attrs = db?.Element("AttributeList");
                var members = ReadMembers(path).Take(maxMembers).Select(MemberToJson).ToArray();

                root["ok"] = db != null && attrs != null && members.Length > 0;
                root["dbName"] = attrs?.Element("Name")?.Value ?? "";
                root["number"] = attrs?.Element("Number")?.Value ?? "";
                root["programmingLanguage"] = attrs?.Element("ProgrammingLanguage")?.Value ?? "";
                root["memoryLayout"] = attrs?.Element("MemoryLayout")?.Value ?? "";
                root["memberCount"] = members.Length;
                root["members"] = new JsonArray(members);
                return root;
            }
            catch (Exception ex)
            {
                root["ok"] = false;
                root["error"] = ex.Message;
                return root;
            }
        }

        private static XElement BuildMember(PlcDbMemberDefinition member)
        {
            var element = new XElement(InterfaceNs + "Member",
                new XAttribute("Name", member.Name),
                new XAttribute("Datatype", member.Datatype));

            var hasAttributes = member.ExternalWritable.HasValue;
            if (hasAttributes)
            {
                element.Add(new XElement(InterfaceNs + "AttributeList",
                    new XElement(InterfaceNs + "BooleanAttribute",
                        new XAttribute("Name", "ExternalWritable"),
                        new XAttribute("SystemDefined", "true"),
                        member.ExternalWritable == true ? "true" : "false")));
            }

            if (!string.IsNullOrWhiteSpace(member.CommentZhCn))
            {
                element.Add(new XElement(InterfaceNs + "Comment",
                    new XElement(InterfaceNs + "MultiLanguageText",
                        new XAttribute("Lang", "zh-CN"),
                        member.CommentZhCn)));
            }

            if (!string.IsNullOrWhiteSpace(member.StartValue))
            {
                element.Add(new XElement(InterfaceNs + "StartValue", member.StartValue));
            }

            return element;
        }

        private static PlcDbMemberFact[] ReadMembers(string path)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            return doc.Descendants()
                .Where(x => x.Name.LocalName == "Section" && (x.Attribute("Name")?.Value ?? "") == "Static")
                .SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "Member"))
                .Select(x => new PlcDbMemberFact(
                    x.Attribute("Name")?.Value ?? "",
                    x.Attribute("Datatype")?.Value ?? "",
                    ReadExternalWritable(x),
                    x.Descendants().FirstOrDefault(y => y.Name.LocalName == "MultiLanguageText" &&
                                                        (y.Attribute("Lang")?.Value ?? "") == "zh-CN")?.Value ?? "",
                    x.Elements().FirstOrDefault(y => y.Name.LocalName == "StartValue")?.Value ?? ""))
                .ToArray();
        }

        private static bool? ReadExternalWritable(XElement member)
        {
            var attr = member.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "BooleanAttribute" &&
                                     (x.Attribute("Name")?.Value ?? "") == "ExternalWritable");
            if (attr == null)
                return null;
            return string.Equals(attr.Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static JsonObject MemberToJson(PlcDbMemberFact member)
        {
            return new JsonObject
            {
                ["name"] = member.Name,
                ["datatype"] = member.Datatype,
                ["externalWritable"] = member.ExternalWritable.HasValue ? JsonValue.Create(member.ExternalWritable.Value) : null,
                ["commentZhCn"] = member.CommentZhCn,
                ["startValue"] = member.StartValue
            };
        }

        private static bool CompareSemantics(JsonObject golden, JsonObject generated)
        {
            return golden["dbName"]?.ToString() == generated["dbName"]?.ToString() &&
                   golden["number"]?.ToString() == generated["number"]?.ToString() &&
                   golden["programmingLanguage"]?.ToString() == generated["programmingLanguage"]?.ToString() &&
                   golden["memoryLayout"]?.ToString() == generated["memoryLayout"]?.ToString() &&
                   NormalizeMembers(golden).SequenceEqual(NormalizeMembers(generated), StringComparer.Ordinal);
        }

        private static string[] NormalizeMembers(JsonObject root)
        {
            return (root["members"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(x => x["name"] + "|" + x["datatype"] + "|" + (x["externalWritable"]?.ToString() ?? "") + "|" + x["commentZhCn"] + "|" + x["startValue"])
                .ToArray();
        }

        private static string BuildProbeMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# PLC Global DB Builder Probe");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- 离线生成和解析 GlobalDB XML，不连接 TIA Portal，不导入 PLC DB。");
            md.AppendLine("- 只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Semantic equal to golden first members: " + root["semanticEqual"]);
            md.AppendLine("- Golden: " + root["goldenPath"]);
            md.AppendLine("- Generated: " + root["generatedPath"]);
            md.AppendLine();
            if (root["generated"] is JsonObject generated && generated["members"] is JsonArray members)
            {
                md.AppendLine("## Generated Members");
                foreach (var member in members.OfType<JsonObject>().Take(30))
                {
                    md.AppendLine("- " + member["name"] + ": " + member["datatype"] +
                                  ", StartValue=" + (string.IsNullOrWhiteSpace(member["startValue"]?.ToString()) ? "<none>" : member["startValue"]) +
                                  ", 注释=" + (string.IsNullOrWhiteSpace(member["commentZhCn"]?.ToString()) ? "<none>" : member["commentZhCn"]));
                }
            }

            return md.ToString();
        }

        private static void ValidateMembers(IReadOnlyCollection<PlcDbMemberDefinition> members)
        {
            var duplicates = members.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToArray();
            if (duplicates.Length > 0)
                throw new ArgumentException("DB 成员名重复: " + string.Join(", ", duplicates));

            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    throw new ArgumentException("DB 成员名不能为空。");
                if (string.IsNullOrWhiteSpace(member.Datatype))
                    throw new ArgumentException("DB 成员数据类型不能为空: " + member.Name);
            }
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }

        private sealed class PlcDbMemberFact
        {
            public PlcDbMemberFact(string name, string datatype, bool? externalWritable, string commentZhCn, string startValue)
            {
                Name = name;
                Datatype = datatype;
                ExternalWritable = externalWritable;
                CommentZhCn = commentZhCn;
                StartValue = startValue;
            }

            public string Name { get; }
            public string Datatype { get; }
            public bool? ExternalWritable { get; }
            public string CommentZhCn { get; }
            public string StartValue { get; }
        }
    }

    public sealed class PlcDbMemberDefinition
    {
        public PlcDbMemberDefinition(string name, string datatype, bool? externalWritable = null, string commentZhCn = "", string startValue = "")
        {
            Name = name;
            Datatype = datatype;
            ExternalWritable = externalWritable;
            CommentZhCn = commentZhCn;
            StartValue = startValue;
        }

        public string Name { get; }
        public string Datatype { get; }
        public bool? ExternalWritable { get; }
        public string CommentZhCn { get; }
        public string StartValue { get; }
    }
}
