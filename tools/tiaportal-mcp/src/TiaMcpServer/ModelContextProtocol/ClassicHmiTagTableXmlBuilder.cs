using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Classic/Basic HMI 变量表 XML 离线构建器。
    /// 变量三种：内部变量（只声明 HMI 类型）、符号寻址（Connection + ControllerTag/PlcTag）、
    /// 绝对寻址（Connection + LogicalAddress/Address，如 %M10.0）。连接名可在表级 ConnectionName 给出、变量级覆盖。
    /// 不认识的键直接报错并列出支持的键（issue #57：原来静默忽略，绝对地址变量被生成成内部变量）。
    /// </summary>
    public static class ClassicHmiTagTableXmlBuilder
    {
        private static readonly string[] TableKeys = { "Name", "TableName", "Tags", "ConnectionName", "Connection", "Absolute" };
        private static readonly string[] TagKeys = { "Name", "DataType", "Length", "Connection", "ControllerTag", "PlcTag", "LogicalAddress", "Address" };

        public static JsonObject BuildFromJson(string tableJson)
        {
            var root = JsonNode.Parse(tableJson) as JsonObject
                ?? throw new ArgumentException("Classic HMI tag table JSON root must be an object.", nameof(tableJson));
            var doc = BuildDocument(root);
            var xml = ToXml(doc);
            var analysis = AnalyzeXml(xml);
            return new JsonObject
            {
                ["format"] = "tia-classic-hmi-tag-table-xml-offline-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "Offline XML generation only; TIA Portal is not connected.",
                    ["write"] = "No project, reference project, or delivery package content is modified.",
                    ["binding"] = "ControllerTag / LogicalAddress are requested PLC bindings; they are not verified until read back from TIA.",
                    ["apply"] = "Import only into a temporary Classic/Basic HMI project first, then read back tags, connections, controller tags, and compile/diagnose."
                },
                ["ok"] = analysis["ok"]?.GetValue<bool>() == true,
                ["analysis"] = analysis,
                ["xml"] = xml
            };
        }

        public static XDocument BuildDocument(JsonObject root)
        {
            var tableName = GetString(root, "Name", "name", GetString(root, "TableName", "tableName", "Classic_HMI_Tags"));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Classic HMI tag table name is required.");
            RejectUnknownKeys(root, TableKeys, "table");
            var tags = (root["Tags"] as JsonArray ?? root["tags"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToArray();
            var tableConnection = GetString(root, "ConnectionName", "connectionName", GetString(root, "Connection", "connection", ""));
            var absoluteTable = GetBool(root, "Absolute", "absolute");
            ValidateTags(tags, tableConnection, absoluteTable);

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", "V21")),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("Hmi.Tag.TagTable",
                        new XAttribute("ID", "0"),
                        new XElement("AttributeList",
                            new XElement("Name", tableName)),
                        new XElement("ObjectList",
                            tags.Select((tag, index) => BuildTag(index + 1, tag, tableConnection))))));
        }

        public static JsonObject AnalyzeXml(string xml)
        {
            var errors = new JsonArray();
            var warnings = new JsonArray();
            var result = new JsonObject
            {
                ["ok"] = false,
                ["tableName"] = "",
                ["tagCount"] = 0,
                ["symbolicBindingCount"] = 0,
                ["absoluteBindingCount"] = 0,
                ["errors"] = errors,
                ["warnings"] = warnings
            };

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                var table = doc.Descendants("Hmi.Tag.TagTable").FirstOrDefault();
                if (table == null)
                {
                    errors.Add("missing-tag-table: Hmi.Tag.TagTable element is required.");
                    return result;
                }

                result["tableName"] = table.Element("AttributeList")?.Element("Name")?.Value ?? "";
                var tags = doc.Descendants("Hmi.Tag.Tag").ToArray();
                result["tagCount"] = tags.Length;
                if (tags.Length == 0) warnings.Add("empty-tag-table: no HMI tags found.");

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var symbolic = 0;
                var absolute = 0;
                foreach (var tag in tags)
                {
                    var name = tag.Element("AttributeList")?.Element("Name")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(name)) errors.Add("unnamed-tag: Hmi.Tag.Tag has no Name.");
                    else if (!names.Add(name)) errors.Add("duplicate-tag-name: " + name);

                    var accessMode = tag.Element("AttributeList")?.Element("AddressAccessMode")?.Value ?? "";
                    var logicalAddress = tag.Element("AttributeList")?.Element("LogicalAddress")?.Value ?? "";
                    var controllerTag = tag.Element("LinkList")?.Element("ControllerTag")?.Element("Name")?.Value ?? "";
                    var connection = tag.Element("LinkList")?.Element("Connection")?.Element("Name")?.Value ?? "";
                    if (accessMode.Equals("Absolute", StringComparison.OrdinalIgnoreCase))
                    {
                        absolute++;
                        if (string.IsNullOrWhiteSpace(logicalAddress)) errors.Add("absolute-binding-missing-logical-address: " + name);
                        if (string.IsNullOrWhiteSpace(connection)) errors.Add("absolute-binding-missing-connection: " + name);
                    }
                    else if (!string.IsNullOrWhiteSpace(controllerTag) || !string.IsNullOrWhiteSpace(connection))
                    {
                        symbolic++;
                        if (string.IsNullOrWhiteSpace(controllerTag)) errors.Add("symbolic-binding-missing-controller-tag: " + name);
                        if (string.IsNullOrWhiteSpace(connection)) errors.Add("symbolic-binding-missing-connection: " + name);
                    }
                }

                result["symbolicBindingCount"] = symbolic;
                result["absoluteBindingCount"] = absolute;
                result["ok"] = errors.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                errors.Add("xml-parse-error: " + ex.Message);
                return result;
            }
        }

        private static XElement BuildTag(int id, JsonObject tag, string tableConnection)
        {
            var name = GetString(tag, "Name", "name", "");
            var dataType = GetString(tag, "DataType", "dataType", "Bool");
            var length = GetString(tag, "Length", "length", DefaultLength(dataType));
            var controllerTag = GetControllerTag(tag);
            var logicalAddress = GetLogicalAddress(tag);
            var tagConnection = GetString(tag, "Connection", "connection", "");
            var absolute = !string.IsNullOrWhiteSpace(logicalAddress);
            var symbolic = !absolute && (!string.IsNullOrWhiteSpace(tagConnection) || !string.IsNullOrWhiteSpace(controllerTag));
            var connection = string.IsNullOrWhiteSpace(tagConnection) ? tableConnection : tagConnection;
            var bound = absolute || symbolic;

            var attributes = new List<XElement>();
            if (bound)
            {
                attributes.Add(new XElement("AcquisitionTriggerMode", "Visible"));
                attributes.Add(new XElement("AddressAccessMode", absolute ? "Absolute" : "Symbolic"));
            }
            attributes.Add(new XElement("Length", length));
            if (bound) attributes.Add(new XElement("LogicalAddress", absolute ? logicalAddress.Trim() : ""));
            attributes.Add(new XElement("Name", SecurityElement.Escape(name)));

            var links = new List<XElement>();
            if (bound)
            {
                links.Add(OpenLink("AcquisitionCycle", "1 s"));
                links.Add(OpenLink("Connection", connection));
                if (symbolic) links.Add(OpenLink("ControllerTag", controllerTag));
            }
            links.Add(OpenLink("DataType", dataType));
            links.Add(OpenLink("HmiDataType", dataType));

            return new XElement("Hmi.Tag.Tag",
                new XAttribute("ID", id.ToString()),
                new XAttribute("CompositionName", "Tags"),
                new XElement("AttributeList", attributes),
                new XElement("LinkList", links));
        }

        private static XElement OpenLink(string elementName, string name)
        {
            return new XElement(elementName,
                new XAttribute("TargetID", "@OpenLink"),
                new XElement("Name", SecurityElement.Escape(name ?? "")));
        }

        private static void ValidateTags(JsonObject[] tags, string tableConnection, bool absoluteTable)
        {
            if (tags.Length == 0)
                throw new ArgumentException("Classic HMI tag table requires at least one tag.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                RejectUnknownKeys(tag, TagKeys, "tag");
                var name = GetString(tag, "Name", "name", "");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Classic HMI tag Name is required.");
                if (!names.Add(name))
                    throw new ArgumentException("Duplicate Classic HMI tag name: " + name);

                var tagConnection = GetString(tag, "Connection", "connection", "");
                var controllerTag = GetControllerTag(tag);
                var logicalAddress = GetLogicalAddress(tag);
                var hasAddress = !string.IsNullOrWhiteSpace(logicalAddress);
                var hasControllerTag = !string.IsNullOrWhiteSpace(controllerTag);

                if (hasAddress && hasControllerTag)
                    throw new ArgumentException("Classic HMI tag '" + name + "' has both LogicalAddress/Address and ControllerTag/PlcTag; use one addressing mode per tag.");
                if (absoluteTable && !hasAddress)
                    throw new ArgumentException("Absolute=true but tag '" + name + "' has no LogicalAddress/Address (e.g. \"%M10.0\").");
                if (hasAddress)
                {
                    if (!logicalAddress.Trim().StartsWith("%", StringComparison.Ordinal))
                        throw new ArgumentException("Classic HMI tag '" + name + "' LogicalAddress must be a PLC absolute address starting with %, e.g. %M10.0 / %DB1.DBX0.0; got '" + logicalAddress + "'.");
                    if (string.IsNullOrWhiteSpace(tagConnection) && string.IsNullOrWhiteSpace(tableConnection))
                        throw new ArgumentException("Classic HMI absolute tag '" + name + "' needs an HMI connection: set ConnectionName on the table or Connection on the tag.");
                    continue;
                }

                if (hasControllerTag && string.IsNullOrWhiteSpace(tagConnection) && string.IsNullOrWhiteSpace(tableConnection))
                    throw new ArgumentException("Classic HMI symbolic tag requires both Connection and ControllerTag/PlcTag: " + name);
                if (!string.IsNullOrWhiteSpace(tagConnection) && !hasControllerTag)
                    throw new ArgumentException("Classic HMI tag '" + name + "' has a Connection but neither ControllerTag/PlcTag (symbolic) nor LogicalAddress/Address (absolute).");
            }
        }

        /// <summary>
        /// Unknown keys used to be ignored, so a misspelt or unsupported field silently produced an internal tag (issue #57).
        /// Keys match case-insensitively on the first letter only (Name / name), same as the readers below.
        /// </summary>
        private static void RejectUnknownKeys(JsonObject obj, string[] supported, string where)
        {
            var unknown = obj.Select(kv => kv.Key)
                .Where(k => !supported.Any(s => k == s || k == char.ToLowerInvariant(s[0]) + s.Substring(1)))
                .ToArray();
            if (unknown.Length > 0)
                throw new ArgumentException("Unsupported " + where + " field(s): " + string.Join(", ", unknown)
                    + ". Supported " + where + " fields: " + string.Join(", ", supported) + " (PascalCase or camelCase).");
        }

        private static string GetControllerTag(JsonObject tag)
            => GetString(tag, "ControllerTag", "controllerTag", GetString(tag, "PlcTag", "plcTag", ""));

        private static string GetLogicalAddress(JsonObject tag)
            => GetString(tag, "LogicalAddress", "logicalAddress", GetString(tag, "Address", "address", ""));

        private static bool GetBool(JsonObject obj, string pascal, string camel)
        {
            var node = obj[pascal] ?? obj[camel];
            if (node == null) return false;
            if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
            if (bool.TryParse(node.ToString(), out var parsed)) return parsed;
            // A non-boolean must not silently switch the "every tag needs an address" guard off.
            throw new ArgumentException(pascal + " must be true or false; got " + node.ToJsonString() + ".");
        }

        private static string DefaultLength(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return "2";
            // V21 Classic HMI 要求 Length 与 PLC 数据类型字节数一致；不一致 import 直接拒绝。
            switch (dataType.Trim().ToUpperInvariant())
            {
                case "BOOL":  return "1";
                case "BYTE": case "USINT": case "SINT": case "CHAR": return "1";
                case "WORD": case "INT": case "UINT": return "2";
                case "DWORD": case "DINT": case "UDINT": case "REAL": case "TIME": case "TIME_OF_DAY": case "DATE_AND_TIME": case "TOD": return "4";
                case "LWORD": case "LINT": case "ULINT": case "LREAL": case "LTIME": case "DTL": return "8";
                default: return "2"; // 未识别类型给最常见值，调用方可手工传 Length 覆盖
            }
        }

        private static string GetString(JsonObject obj, string pascal, string camel, string fallback)
        {
            return obj[pascal]?.ToString() ?? obj[camel]?.ToString() ?? fallback;
        }

        private static string ToXml(XDocument document)
        {
            using var writer = new Utf8StringWriter();
            document.Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
