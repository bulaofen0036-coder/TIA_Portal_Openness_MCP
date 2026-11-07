using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// PLC FB XML 组合器。
    /// 只负责离线生成 SCL FB XML；真实导入、编译和实例 DB 生成必须交给 TIA Portal 验证。
    /// </summary>
    public static class PlcFbBlockXmlComposer
    {
        private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
        private static readonly XNamespace StructuredTextNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";

        public static XDocument Compose(
            string blockName,
            int blockNumber,
            IEnumerable<PlcBlockMemberDefinition> inputMembers,
            IEnumerable<PlcBlockMemberDefinition> outputMembers,
            IEnumerable<PlcBlockMemberDefinition> inOutMembers,
            IEnumerable<PlcBlockMemberDefinition> staticMembers,
            IEnumerable<PlcBlockMemberDefinition> tempMembers,
            string structuredTextInnerXml,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string networkCommentZhCn = "",
            string networkTitleZhCn = "")
        {
            if (string.IsNullOrWhiteSpace(blockName))
                throw new ArgumentException("FB 块名称不能为空。", nameof(blockName));
            if (blockNumber <= 0)
                throw new ArgumentException("FB 块编号必须大于 0。", nameof(blockNumber));
            if (string.IsNullOrWhiteSpace(structuredTextInnerXml))
                throw new ArgumentException(
                    "StructuredText 内容不能为空：JSON 里的 $.structuredText.operations 是空数组，"
                    + "$.structuredTextInnerXml 也没给。至少给一条操作，例如 "
                    + "{\"op\":\"line\",\"text\":\"MyTemp := TRUE;\"}。"
                    + "（报错里的参数名是构建器内部的形参，不是你要填的 JSON 字段名。）",
                    nameof(structuredTextInnerXml));

            var inputs = inputMembers?.ToArray() ?? throw new ArgumentNullException(nameof(inputMembers));
            var outputs = outputMembers?.ToArray() ?? throw new ArgumentNullException(nameof(outputMembers));
            var inouts = inOutMembers?.ToArray() ?? throw new ArgumentNullException(nameof(inOutMembers));
            var statics = staticMembers?.ToArray() ?? throw new ArgumentNullException(nameof(staticMembers));
            var temps = tempMembers?.ToArray() ?? throw new ArgumentNullException(nameof(tempMembers));
            ValidateMembers(inputs.Concat(outputs).Concat(inouts).Concat(statics).Concat(temps).ToArray());

            var st = XElement.Parse("<StructuredText xmlns=\"" + StructuredTextNs + "\">" + structuredTextInnerXml + "</StructuredText>");

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", "V21")),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("SW.Blocks.FB",
                        new XAttribute("ID", "0"),
                        new XElement("AttributeList",
                            new XElement("Interface",
                                new XElement(InterfaceNs + "Sections",
                                    BuildSection("Input", inputs),
                                    BuildSection("Output", outputs),
                                    BuildSection("InOut", inouts),
                                    BuildSection("Static", statics),
                                    BuildSection("Temp", temps),
                                    BuildSection("Constant", Array.Empty<PlcBlockMemberDefinition>()))),
                            new XElement("MemoryLayout", "Optimized"),
                            new XElement("Name", blockName),
                            new XElement("Namespace"),
                            new XElement("Number", blockNumber),
                            new XElement("ProgrammingLanguage", "SCL"),
                            new XElement("SetENOAutomatically", "false")),
                        BuildObjectList(blockCommentZhCn, blockTitleZhCn, networkCommentZhCn, networkTitleZhCn, st))));
        }

        private static XElement BuildObjectList(string blockComment, string blockTitle, string networkComment, string networkTitle, XElement st)
        {
            // CompileUnit 的 ObjectList: 网络级 Comment + Title
            var compileUnitObjList = new XElement("ObjectList",
                PlcBlockXmlHelpers.BuildMultilingualText("4", "5", "Comment", networkComment),
                PlcBlockXmlHelpers.BuildMultilingualText("6", "7", "Title", networkTitle));
            var compileUnit = new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", "3"),
                new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    new XElement("NetworkSource", st),
                    new XElement("ProgrammingLanguage", "SCL")),
                compileUnitObjList);
            // 块级 ObjectList: Comment → CompileUnit → Title（与 TIA 真实导出 schema 顺序一致）
            return new XElement("ObjectList",
                PlcBlockXmlHelpers.BuildMultilingualText("1", "2", "Comment", blockComment),
                compileUnit,
                PlcBlockXmlHelpers.BuildMultilingualText("8", "9", "Title", blockTitle));
        }

        public static string ComposeXml(
            string blockName,
            int blockNumber,
            IEnumerable<PlcBlockMemberDefinition> inputMembers,
            IEnumerable<PlcBlockMemberDefinition> outputMembers,
            IEnumerable<PlcBlockMemberDefinition> inOutMembers,
            IEnumerable<PlcBlockMemberDefinition> staticMembers,
            IEnumerable<PlcBlockMemberDefinition> tempMembers,
            string structuredTextInnerXml,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string networkCommentZhCn = "",
            string networkTitleZhCn = "")
        {
            using var writer = new Utf8StringWriter();
            Compose(blockName, blockNumber, inputMembers, outputMembers, inOutMembers, staticMembers, tempMembers, structuredTextInnerXml,
                blockCommentZhCn, blockTitleZhCn, networkCommentZhCn, networkTitleZhCn).Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private static XElement BuildSection(string name, IReadOnlyCollection<PlcBlockMemberDefinition> members)
        {
            if (members.Count == 0)
                return new XElement(InterfaceNs + "Section", new XAttribute("Name", name));
            var section = new XElement(InterfaceNs + "Section", new XAttribute("Name", name));
            foreach (var m in members)
            {
                var memEl = new XElement(InterfaceNs + "Member",
                    new XAttribute("Name", m.Name),
                    new XAttribute("Datatype", m.Datatype));
                PlcBlockXmlHelpers.AppendMemberCommentIfAny(memEl, m.CommentZhCn);
                section.Add(memEl);
            }
            return section;
        }

        private static void ValidateMembers(IReadOnlyCollection<PlcBlockMemberDefinition> members)
        {
            var duplicates = members.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToArray();
            if (duplicates.Length > 0)
                throw new ArgumentException("FB 接口成员名重复: " + string.Join(", ", duplicates));

            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    throw new ArgumentException("FB 接口成员名不能为空。");
                if (string.IsNullOrWhiteSpace(member.Datatype))
                    throw new ArgumentException("FB 接口成员数据类型不能为空: " + member.Name);
            }
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
