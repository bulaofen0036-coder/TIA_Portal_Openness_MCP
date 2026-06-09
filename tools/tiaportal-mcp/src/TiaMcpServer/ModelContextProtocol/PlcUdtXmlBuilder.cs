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
    /// PLC UDT/PlcStruct XML 构造器。
    /// 先覆盖 TIA V21 导出里最常见的扁平成员、中文注释和 ExternalWritable 属性。
    /// </summary>
    public static class PlcUdtXmlBuilder
    {
        private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

        public static XDocument BuildDocument(string udtName, IEnumerable<PlcUdtMemberDefinition> members)
        {
            if (string.IsNullOrWhiteSpace(udtName))
                throw new ArgumentException("UDT 名称不能为空。", nameof(udtName));
            var normalizedMembers = members?.ToList() ?? throw new ArgumentNullException(nameof(members));
            if (normalizedMembers.Count == 0)
                throw new ArgumentException("UDT 至少需要 1 个成员。", nameof(members));

            ValidateMembers(normalizedMembers);

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", "V21")),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("SW.Types.PlcStruct",
                        new XAttribute("ID", "0"),
                        new XElement("AttributeList",
                            new XElement("Interface",
                                new XElement(InterfaceNs + "Sections",
                                    new XElement(InterfaceNs + "Section",
                                        new XAttribute("Name", "None"),
                                        normalizedMembers.Select(BuildMember)))),
                            new XElement("Name", udtName)))));
        }

        // Backwards-compat: callers without a UDT name fall back to a placeholder.
        public static XDocument BuildDocument(IEnumerable<PlcUdtMemberDefinition> members)
            => BuildDocument("UDT_Generated", members);

        public static string BuildXml(string udtName, IEnumerable<PlcUdtMemberDefinition> members)
        {
            using var writer = new Utf8StringWriter();
            BuildDocument(udtName, members).Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        public static string BuildXml(IEnumerable<PlcUdtMemberDefinition> members)
            => BuildXml("UDT_Generated", members);

        public static JsonObject RunProbe(string fixtureDirectory, string reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var goldenPath = Path.Combine(fixtureDirectory, "UDT_Fault.xml");
            var generatedPath = Path.Combine(reportDirectory, "UDT_Fault.minimal.generated_" + stamp + ".xml");
            var jsonPath = Path.Combine(reportDirectory, "plc_udt_builder_probe_" + stamp + ".json");
            var mdPath = Path.Combine(reportDirectory, "plc_udt_builder_probe_" + stamp + ".md");

            var golden = AnalyzeUdt(goldenPath, maxMembers: 4);
            var probeMembers = ReadMembers(goldenPath)
                .Take(4)
                .Select(x => new PlcUdtMemberDefinition(
                    x.Name,
                    x.Datatype,
                    x.ExternalWritable,
                    x.CommentZhCn))
                .ToArray();

            File.WriteAllText(generatedPath, BuildXml(probeMembers), Encoding.UTF8);
            var generated = AnalyzeUdt(generatedPath, maxMembers: 4);
            var semanticEqual = CompareMembers(golden, generated);

            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["mode"] = "offline-plc-udt-builder-probe",
                ["goldenPath"] = goldenPath,
                ["generatedPath"] = generatedPath,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线生成和解析 XML，不连接 TIA Portal，不导入 PLC 数据类型。",
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

        public static JsonObject AnalyzeUdt(string path, int maxMembers = int.MaxValue)
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
                var members = ReadMembers(path).Take(maxMembers).ToArray();
                var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
                root["ok"] = doc.Descendants().Any(x => x.Name.LocalName == "SW.Types.PlcStruct") &&
                           members.Length > 0 &&
                           members.All(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Datatype));
                root["memberCount"] = members.Length;
                root["members"] = new JsonArray(members.Select(MemberToJson).ToArray());
                return root;
            }
            catch (Exception ex)
            {
                root["ok"] = false;
                root["error"] = ex.Message;
                return root;
            }
        }

        private static XElement BuildMember(PlcUdtMemberDefinition member)
        {
            var element = new XElement(InterfaceNs + "Member",
                new XAttribute("Name", member.Name),
                new XAttribute("Datatype", member.Datatype),
                new XElement(InterfaceNs + "AttributeList",
                    new XElement(InterfaceNs + "BooleanAttribute",
                        new XAttribute("Name", "ExternalWritable"),
                        new XAttribute("SystemDefined", "true"),
                        member.ExternalWritable ? "true" : "false")));

            if (!string.IsNullOrWhiteSpace(member.CommentZhCn))
            {
                element.Add(new XElement(InterfaceNs + "Comment",
                    new XElement(InterfaceNs + "MultiLanguageText",
                        new XAttribute("Lang", "zh-CN"),
                        member.CommentZhCn)));
            }

            return element;
        }

        private static PlcUdtMemberFact[] ReadMembers(string path)
        {
            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            return doc.Descendants()
                .Where(x => x.Name.LocalName == "Member")
                .Select(x => new PlcUdtMemberFact(
                    x.Attribute("Name")?.Value ?? "",
                    x.Attribute("Datatype")?.Value ?? "",
                    ReadExternalWritable(x),
                    x.Descendants().FirstOrDefault(y => y.Name.LocalName == "MultiLanguageText" &&
                                                        (y.Attribute("Lang")?.Value ?? "") == "zh-CN")?.Value ?? ""))
                .ToArray();
        }

        private static bool ReadExternalWritable(XElement member)
        {
            var attr = member.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "BooleanAttribute" &&
                                     (x.Attribute("Name")?.Value ?? "") == "ExternalWritable");
            return string.Equals(attr?.Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static JsonObject MemberToJson(PlcUdtMemberFact member)
        {
            return new JsonObject
            {
                ["name"] = member.Name,
                ["datatype"] = member.Datatype,
                ["externalWritable"] = member.ExternalWritable,
                ["commentZhCn"] = member.CommentZhCn
            };
        }

        private static bool CompareMembers(JsonObject golden, JsonObject generated)
        {
            return NormalizeMembers(golden).SequenceEqual(NormalizeMembers(generated), StringComparer.Ordinal);
        }

        private static IEnumerable<string> NormalizeMembers(JsonObject root)
        {
            return (root["members"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(x => x["name"] + "|" + x["datatype"] + "|" + x["externalWritable"] + "|" + x["commentZhCn"]);
        }

        private static string BuildProbeMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# PLC UDT Builder Probe");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- 离线生成和解析 XML，不连接 TIA Portal，不导入 PLC 数据类型。");
            md.AppendLine("- 只写 reports 目录下的生成样本和探针报告，不修改 TMP_EXPORT 或交付包。");
            md.AppendLine();
            md.AppendLine("## Summary");
            md.AppendLine("- OK: " + root["ok"]);
            md.AppendLine("- Semantic equal to golden first members: " + root["semanticEqual"]);
            md.AppendLine("- Golden: " + root["goldenPath"]);
            md.AppendLine("- Generated: " + root["generatedPath"]);
            md.AppendLine();
            md.AppendLine("## Generated Members");
            if (root["generated"] is JsonObject generated && generated["members"] is JsonArray members)
            {
                foreach (var member in members.OfType<JsonObject>())
                {
                    md.AppendLine("- " + member["name"] + ": " + member["datatype"] +
                                  ", ExternalWritable=" + member["externalWritable"] +
                                  ", 注释=" + (string.IsNullOrWhiteSpace(member["commentZhCn"]?.ToString()) ? "<none>" : member["commentZhCn"]));
                }
            }

            return md.ToString();
        }

        private static void ValidateMembers(IReadOnlyCollection<PlcUdtMemberDefinition> members)
        {
            var duplicates = members.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToArray();
            if (duplicates.Length > 0)
                throw new ArgumentException("UDT 成员名重复: " + string.Join(", ", duplicates));

            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    throw new ArgumentException("UDT 成员名不能为空。");
                if (string.IsNullOrWhiteSpace(member.Datatype))
                    throw new ArgumentException("UDT 成员数据类型不能为空: " + member.Name);
            }
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }

        private sealed class PlcUdtMemberFact
        {
            public PlcUdtMemberFact(string name, string datatype, bool externalWritable, string commentZhCn)
            {
                Name = name;
                Datatype = datatype;
                ExternalWritable = externalWritable;
                CommentZhCn = commentZhCn;
            }

            public string Name { get; }
            public string Datatype { get; }
            public bool ExternalWritable { get; }
            public string CommentZhCn { get; }
        }
    }

    public sealed class PlcUdtMemberDefinition
    {
        public PlcUdtMemberDefinition(string name, string datatype, bool externalWritable = false, string commentZhCn = "")
        {
            Name = name;
            Datatype = datatype;
            ExternalWritable = externalWritable;
            CommentZhCn = commentZhCn;
        }

        public string Name { get; }
        public string Datatype { get; }
        public bool ExternalWritable { get; }
        public string CommentZhCn { get; }
    }
}
