using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// PLC Builder MCP 工具的 JSON 适配层。
    /// 这里集中做字段校验和错误定位，避免工具层继续堆散乱解析逻辑。
    /// </summary>
    internal static class PlcBuilderToolJson
    {
        public static JsonObject BuildUdt(string json)
        {
            var root = ParseObject(json, "$");
            var udtName = ReadString(root, "$.name", "name", "udtName");
            var members = ReadArray(root, "members", "$.members")
                .Select((x, i) => new PlcUdtMemberDefinition(
                    ReadString(AsObject(x, "$.members[" + i + "]"), "$.members[" + i + "].name", "name"),
                    ReadString(AsObject(x, "$.members[" + i + "]"), "$.members[" + i + "].datatype", "datatype", "dataType"),
                    ReadBool(AsObject(x, "$.members[" + i + "]"), "externalWritable", defaultValue: false),
                    ReadOptionalString(AsObject(x, "$.members[" + i + "]"), "commentZhCn", "comment", "commentZh")))
                .ToArray();

            var xml = PlcUdtXmlBuilder.BuildXml(udtName, members);
            return BuildResult("plc-build-udt-xml", xml, new JsonObject
            {
                ["udtName"] = udtName,
                ["memberCount"] = members.Length
            });
        }

        public static JsonObject BuildTagTable(string json)
        {
            var root = ParseObject(json, "$");
            var tableName = ReadString(root, "$.tableName", "tableName", "name");
            var tags = ReadArray(root, "tags", "$.tags")
                .Select((x, i) =>
                {
                    var tag = AsObject(x, "$.tags[" + i + "]");
                    return new PlcTagDefinition(
                        ReadString(tag, "$.tags[" + i + "].name", "name"),
                        ReadString(tag, "$.tags[" + i + "].dataTypeName", "dataTypeName", "datatype", "dataType"),
                        ReadString(tag, "$.tags[" + i + "].logicalAddress", "logicalAddress", "address"));
                })
                .ToArray();

            var xml = PlcTagTableXmlBuilder.BuildXml(tableName, tags);
            return BuildResult("plc-build-tag-table-xml", xml, new JsonObject
            {
                ["tableName"] = tableName,
                ["tagCount"] = tags.Length
            });
        }

        private static readonly string[] GlobalDbKeys =
        {
            "dbName", "name", "dbNumber", "number", "staticMembers", "members", "optimized", "memoryLayout"
        };

        public static JsonObject BuildGlobalDb(string json)
        {
            var root = ParseObject(json, "$");
            // 未知键直接报错：拼错的 "optimised" 如果被静默忽略，调用方会拿到一个布局不对的 DB 却毫不知情。
            RejectUnknownKeys(root, "$", GlobalDbKeys);
            var memoryLayout = ReadGlobalDbMemoryLayout(root);
            var dbName = ReadString(root, "$.dbName", "dbName", "name");
            var dbNumber = ReadInt(root, "$.dbNumber", "dbNumber", "number");
            var membersNode = root["staticMembers"] as JsonArray ?? root["members"] as JsonArray;
            if (membersNode == null)
                throw new ArgumentException("Missing required JSON array: $.staticMembers (or $.members)");

            var members = membersNode
                .Select((x, i) =>
                {
                    var member = AsObject(x, "$.staticMembers[" + i + "]");
                    return new PlcDbMemberDefinition(
                        ReadString(member, "$.staticMembers[" + i + "].name", "name"),
                        ReadString(member, "$.staticMembers[" + i + "].datatype", "datatype", "dataType"),
                        ReadNullableBool(member, "externalWritable"),
                        ReadOptionalString(member, "commentZhCn", "comment", "commentZh"),
                        ReadOptionalString(member, "startValue"));
                })
                .ToArray();

            var xml = PlcGlobalDbXmlBuilder.BuildXml(dbName, dbNumber, members, memoryLayout);
            return BuildResult("plc-build-global-db-xml", xml, new JsonObject
            {
                ["dbName"] = dbName,
                ["dbNumber"] = dbNumber,
                ["memberCount"] = members.Length,
                ["memoryLayout"] = memoryLayout
            });
        }

        /// <summary>
        /// $.optimized（issue #31 的写法）与 $.memoryLayout（模板里的写法）二选一；
        /// 两个都给且互相矛盾时报错，而不是悄悄挑一个。都不给时保持一贯的 Standard。
        /// </summary>
        private static string ReadGlobalDbMemoryLayout(JsonObject root)
        {
            var optimized = ReadNullableBool(root, "optimized");
            string? layout = null;
            if (root.TryGetPropertyValue("memoryLayout", out var node) && node != null)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
                    throw new ArgumentException("Expected JSON string at $.memoryLayout: \"Standard\" or \"Optimized\".");
                layout = PlcGlobalDbXmlBuilder.NormalizeMemoryLayout(text);
            }

            if (optimized.HasValue)
            {
                var fromFlag = optimized.Value ? "Optimized" : "Standard";
                if (layout != null && layout != fromFlag)
                    throw new ArgumentException($"$.optimized={optimized.Value.ToString().ToLowerInvariant()} contradicts $.memoryLayout=\"{layout}\". Give only one of them.");
                return fromFlag;
            }

            return layout ?? PlcGlobalDbXmlBuilder.DefaultMemoryLayout;
        }

        private static void RejectUnknownKeys(JsonObject root, string path, IReadOnlyList<string> known)
        {
            foreach (var entry in root)
            {
                if (known.Contains(entry.Key, StringComparer.Ordinal)) continue;
                var near = ArgDiagnostics.NearestName(entry.Key, known);
                throw new ArgumentException(
                    $"Unknown JSON property {path}.{entry.Key}" +
                    (near != null ? $" — did you mean '{near}'?" : "") +
                    $" Allowed: {string.Join(", ", known)}.");
            }
        }

        public static JsonObject BuildStructuredText(string json, bool innerOnly = false)
        {
            var root = ParseObject(json, "$");
            var builder = BuildStructuredTextBuilder(root);
            var xml = innerOnly ? builder.BuildInnerXml() : builder.BuildStructuredTextXml();
            return BuildResult("plc-build-structured-text-xml", xml, new JsonObject
            {
                ["innerOnly"] = innerOnly,
                ["operationCount"] = (root["operations"] as JsonArray)?.Count ?? 0
            });
        }

        public static JsonObject BuildFlgNetCall(string json)
        {
            var root = ParseObject(json, "$");
            var callName = ReadString(root, "$.callName", "callName", "name");
            var parameters = ReadArray(root, "parameters", "$.parameters")
                .Select((x, i) => ReadFlgNetParameter(AsObject(x, "$.parameters[" + i + "]"), i))
                .ToArray();

            var xml = FlgNetCallXmlBuilder.BuildXml(callName, parameters);
            return BuildResult("plc-build-flgnet-call-xml", xml, new JsonObject
            {
                ["callName"] = callName,
                ["parameterCount"] = parameters.Length
            });
        }

        public static JsonObject ComposeFcBlock(string json)
        {
            var root = ParseObject(json, "$");
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadMembers(root, "inputs", "$.inputs");
            var outputs = ReadMembers(root, "outputs", "$.outputs");
            var structuredTextInnerXml = ReadStructuredTextInnerXml(root);
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
            var netComment = ReadOptionalString(root, "networkCommentZhCn", "networkComment");
            var netTitle = ReadOptionalString(root, "networkTitleZhCn", "networkTitle");

            var xml = PlcFcBlockXmlComposer.ComposeXml(blockName, blockNumber, inputs, outputs, structuredTextInnerXml,
                blockComment, blockTitle, netComment, netTitle);
            return BuildResult("plc-compose-fc-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length
            });
        }

        public static JsonObject ComposeLadFcBlock(string json)
        {
            var root = ParseObject(json, "$");
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadOptionalMembers(root, "inputs", "$.inputs");
            var outputs = ReadOptionalMembers(root, "outputs", "$.outputs");
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");

            var nets = ReadArray(root, "networks", "$.networks");
            if (nets.Count == 0)
                throw new ArgumentException("LAD FC 至少需要 1 个 networks[] 元素");

            var built = new System.Collections.Generic.List<PlcLadFcBlockXmlComposer.LadNetwork>();
            for (var i = 0; i < nets.Count; i++)
            {
                var n = AsObject(nets[i], "$.networks[" + i + "]");
                var netTitle = ReadOptionalString(n, "titleZhCn", "title");
                var netComment = ReadOptionalString(n, "commentZhCn", "comment");
                // 每个 network 是一个 FC 调用：用 FlgNetCallXmlBuilder
                var callJson = n["callJson"] as JsonObject ?? n["call"] as JsonObject;
                if (callJson == null)
                    throw new ArgumentException("$.networks[" + i + "] 缺少 callJson 对象");
                var callName = ReadString(callJson, "$.networks[" + i + "].callJson.callName", "callName", "name");
                var parametersArray = callJson["parameters"] as JsonArray;
                var parameters = parametersArray == null
                    ? Array.Empty<FlgNetCallParameter>()
                    : parametersArray
                        .Select((x, j) => ReadFlgNetParameter(AsObject(x, "$.networks[" + i + "].callJson.parameters[" + j + "]"), j))
                        .ToArray();
                var flgNet = FlgNetCallXmlBuilder.BuildFlgNet(callName, parameters);
                built.Add(new PlcLadFcBlockXmlComposer.LadNetwork(flgNet, netTitle, netComment));
            }

            var xml = PlcLadFcBlockXmlComposer.ComposeXml(blockName, blockNumber, inputs, outputs, built, blockComment, blockTitle);
            return BuildResult("plc-compose-lad-fc-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["networkCount"] = built.Count,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length
            });
        }

        public static JsonObject ComposeFbBlock(string json)
        {
            var root = ParseObject(json, "$");
            var blockName = ReadString(root, "$.blockName", "blockName", "name");
            var blockNumber = ReadInt(root, "$.blockNumber", "blockNumber", "number");
            var inputs = ReadOptionalMembers(root, "inputs", "$.inputs");
            var outputs = ReadOptionalMembers(root, "outputs", "$.outputs");
            var inouts = ReadOptionalMembers(root, "inouts", "$.inouts", "inOuts", "inOut");
            var statics = ReadOptionalMembers(root, "statics", "$.statics", "staticMembers", "static");
            var temps = ReadOptionalMembers(root, "temps", "$.temps", "tempMembers", "temp");
            var structuredTextInnerXml = ReadStructuredTextInnerXml(root);
            var blockComment = ReadOptionalString(root, "commentZhCn", "blockCommentZhCn", "comment");
            var blockTitle = ReadOptionalString(root, "titleZhCn", "blockTitleZhCn", "title");
            var netComment = ReadOptionalString(root, "networkCommentZhCn", "networkComment");
            var netTitle = ReadOptionalString(root, "networkTitleZhCn", "networkTitle");

            var xml = PlcFbBlockXmlComposer.ComposeXml(blockName, blockNumber, inputs, outputs, inouts, statics, temps, structuredTextInnerXml,
                blockComment, blockTitle, netComment, netTitle);
            return BuildResult("plc-compose-fb-block-xml", xml, new JsonObject
            {
                ["blockName"] = blockName,
                ["blockNumber"] = blockNumber,
                ["inputCount"] = inputs.Length,
                ["outputCount"] = outputs.Length,
                ["inOutCount"] = inouts.Length,
                ["staticCount"] = statics.Length,
                ["tempCount"] = temps.Length
            });
        }

        private static StructuredTextXmlBuilder BuildStructuredTextBuilder(JsonObject root)
        {
            var firstUid = ReadOptionalInt(root, "firstUid") ?? 21;
            var builder = new StructuredTextXmlBuilder(firstUid);
            var operations = ReadArray(root, "operations", "$.operations");
            for (var i = 0; i < operations.Count; i++)
            {
                var op = AsObject(operations[i], "$.operations[" + i + "]");
                var kind = ReadString(op, "$.operations[" + i + "].op", "op", "kind", "type").Trim().ToLowerInvariant();
                var indent = ReadOptionalInt(op, "indent") ?? 0;
                switch (kind)
                {
                    case "if":
                    case "ifheader":
                        builder.IfHeader(ReadString(op, "$.operations[" + i + "].condition", "condition", "conditionVariable", "variable"), indent);
                        break;
                    case "elsif":
                    case "elseif":
                    case "elsifheader":
                        builder.ElsIfHeader(ReadString(op, "$.operations[" + i + "].condition", "condition", "conditionVariable", "variable"), indent);
                        break;
                    case "else":
                        builder.ElseLine(indent);
                        break;
                    case "endif":
                    case "end_if":
                        builder.EndIf(indent);
                        break;
                    case "assign":
                    case "assignment":
                        {
                            var target = ReadString(op, "$.operations[" + i + "].target", "target");
                            var src = ReadOptionalString(op, "source", "fromSymbol");
                            if (!string.IsNullOrWhiteSpace(src))
                                builder.AssignFromSymbol(target, src, indent);
                            else
                                builder.Assignment(target, ReadString(op, "$.operations[" + i + "].literalValue", "literalValue", "value"), indent);
                        }
                        break;
                    case "token":
                        if (indent > 0) builder.Blank(indent);
                        builder.Token(ReadString(op, "$.operations[" + i + "].text", "text"));
                        break;
                    case "blank":
                        builder.Blank(ReadOptionalInt(op, "count") ?? 1);
                        break;
                    case "newline":
                    case "new_line":
                        builder.NewLine();
                        break;
                    case "global":
                        if (indent > 0) builder.Blank(indent);
                        builder.GlobalVariable(ReadString(op, "$.operations[" + i + "].name", "name").Split('.'));
                        break;
                    case "local":
                        if (indent > 0) builder.Blank(indent);
                        builder.LocalVariable(ReadString(op, "$.operations[" + i + "].name", "name"));
                        break;
                    case "symbol":
                        // 智能识别 scope："x" 全局 / x 局部
                        if (indent > 0) builder.Blank(indent);
                        builder.Symbol(ReadString(op, "$.operations[" + i + "].name", "name"));
                        break;
                    case "literal":
                        if (indent > 0) builder.Blank(indent);
                        builder.LiteralConstant(ReadString(op, "$.operations[" + i + "].value", "value", "literalValue"));
                        break;
                    case "line":
                        // 自由表达式行：items[] 数组，每项可为：
                        //   {sym:"name"}     局部 / 引号则全局，由 Symbol() 智能识别
                        //   {token:"AND"}    关键字 / 运算符 token（前后自动加 Blank，除 ( ) , ; ）
                        //   {lit:"FALSE"}    字面常量
                        //   {raw:"("}        裸 token（不自动加 Blank）
                        // 末尾自动追加 ; + newline（除非最后一项已是 ; 或 raw=";"）
                        if (indent > 0) builder.Blank(indent);
                        EmitLine(builder, op, i);
                        break;
                    default:
                        throw new ArgumentException("Unsupported StructuredText operation at $.operations[" + i + "].op: " + kind);
                }
            }

            return builder;
        }

        // SCL 自由表达式行：items[] 解析 + 自动 Blank（紧贴标点除外）
        private static void EmitLine(StructuredTextXmlBuilder builder, JsonObject lineOp, int opIndex)
        {
            var items = lineOp["items"] as JsonArray
                ?? throw new ArgumentException("$.operations[" + opIndex + "].items 缺失");
            string? lastTokenText = null;
            for (var k = 0; k < items.Count; k++)
            {
                var item = items[k] as JsonObject
                    ?? throw new ArgumentException("$.operations[" + opIndex + "].items[" + k + "] 必须是对象");
                bool tightBefore = false; // 当前项前是否需要紧贴（如 ) ; , ）

                if (item["sym"] is JsonNode symN)
                {
                    if (k > 0 && !tightBefore) builder.Blank();
                    builder.Symbol(symN.ToString());
                    lastTokenText = null;
                }
                else if (item["token"] is JsonNode tokN)
                {
                    var t = tokN.ToString();
                    tightBefore = t == ")" || t == "," || t == ";";
                    if (k > 0 && !tightBefore) builder.Blank();
                    builder.Token(t);
                    lastTokenText = t;
                }
                else if (item["lit"] is JsonNode litN)
                {
                    if (k > 0) builder.Blank();
                    builder.LiteralConstant(litN.ToString());
                    lastTokenText = null;
                }
                else if (item["raw"] is JsonNode rawN)
                {
                    builder.Token(rawN.ToString()); // 不自动加 Blank
                    lastTokenText = rawN.ToString();
                }
                else
                {
                    throw new ArgumentException("$.operations[" + opIndex + "].items[" + k + "] 需要 sym / token / lit / raw 之一");
                }
            }
            // 末尾若不是 ; 则补 ;
            if (lastTokenText != ";")
                builder.Token(";");
            builder.NewLine();
        }

        private static FlgNetCallParameter ReadFlgNetParameter(JsonObject parameter, int index)
        {
            var name = ReadString(parameter, "$.parameters[" + index + "].name", "parameterName", "name");
            var section = ReadString(parameter, "$.parameters[" + index + "].section", "section");
            var dataType = ReadString(parameter, "$.parameters[" + index + "].dataType", "dataType", "datatype", "type");
            var sourceKind = ReadOptionalString(parameter, "sourceKind", "source", "kind").Trim().ToLowerInvariant();
            if (sourceKind == "constant" || sourceKind == "literal" || sourceKind == "literalconstant")
            {
                return FlgNetCallParameter.Constant(
                    name,
                    section,
                    dataType,
                    ReadString(parameter, "$.parameters[" + index + "].value", "value", "constantValue"));
            }

            var path = parameter["symbolPath"] as JsonArray;
            if (path != null)
            {
                return FlgNetCallParameter.Global(name, section, dataType, path.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
            }

            var symbol = ReadString(parameter, "$.parameters[" + index + "].symbol", "symbol", "path", "plcTag");
            return FlgNetCallParameter.Global(name, section, dataType, symbol.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static PlcBlockMemberDefinition[] ReadMembers(JsonObject root, string arrayName, string path)
        {
            return ReadArray(root, arrayName, path)
                .Select((x, i) =>
                {
                    var member = AsObject(x, path + "[" + i + "]");
                    return new PlcBlockMemberDefinition(
                        ReadString(member, path + "[" + i + "].name", "name"),
                        ReadString(member, path + "[" + i + "].datatype", "datatype", "dataType"),
                        ReadOptionalString(member, "commentZhCn", "comment", "commentZh"));
                })
                .ToArray();
        }

        private static PlcBlockMemberDefinition[] ReadOptionalMembers(JsonObject root, string defaultArrayName, string defaultPath, params string[] aliases)
        {
            var names = new[] { defaultArrayName }.Concat(aliases ?? Array.Empty<string>()).ToArray();
            var foundName = names.FirstOrDefault(name => root[name] is JsonArray);
            if (foundName == null)
                return Array.Empty<PlcBlockMemberDefinition>();

            return ReadMembers(root, foundName, "$." + foundName);
        }

        private static string ReadStructuredTextInnerXml(JsonObject root)
        {
            var direct = ReadOptionalString(root, "structuredTextInnerXml", "structuredTextXml", "sclInnerXml");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            if (root["structuredText"] is JsonObject structuredText)
                return BuildStructuredTextBuilder(structuredText).BuildInnerXml();

            throw new ArgumentException("Missing required StructuredText content: $.structuredTextInnerXml or $.structuredText.operations");
        }

        private static JsonObject BuildResult(string mode, string xml, JsonObject summary)
        {
            var parseOk = true;
            string error = "";
            try
            {
                XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                try
                {
                    // StructuredText inner XML 是供 Block Composer 嵌入的片段，允许多个同级节点。
                    XDocument.Parse("<Fragment>" + xml + "</Fragment>");
                }
                catch
                {
                    parseOk = false;
                    error = ex.Message;
                }
            }

            return new JsonObject
            {
                ["ok"] = parseOk,
                ["mode"] = mode,
                ["offlineOnly"] = true,
                ["xmlParseOk"] = parseOk,
                ["error"] = string.IsNullOrWhiteSpace(error) ? null : JsonValue.Create(error),
                ["xml"] = xml,
                ["summary"] = summary,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线 XML 生成：不连接 TIA Portal，不打开项目，不导入 PLC 对象。",
                    ["write"] = "本工具只返回 XML 字符串，不写工程、不写交付包。"
                }
            };
        }

        private static JsonObject ParseObject(string json, string path)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Missing required JSON object: " + path);

            var node = JsonNode.Parse(json);
            return AsObject(node, path);
        }

        private static JsonObject AsObject(JsonNode? node, string path)
        {
            return node as JsonObject ?? throw new ArgumentException("Expected JSON object at " + path);
        }

        private static JsonArray ReadArray(JsonObject root, string name, string path)
        {
            return root[name] as JsonArray ?? throw new ArgumentException("Missing required JSON array: " + path);
        }

        private static string ReadString(JsonObject root, string path, params string[] names)
        {
            var value = ReadOptionalString(root, names);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing required JSON string: " + path);
            return value;
        }

        private static string ReadOptionalString(JsonObject root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetPropertyValue(name, out var node) && node != null)
                    return node.ToString();
            }

            return "";
        }

        private static int ReadInt(JsonObject root, string path, params string[] names)
        {
            var value = ReadOptionalInt(root, names);
            if (!value.HasValue)
                throw new ArgumentException("Missing required JSON integer: " + path);
            return value.Value;
        }

        private static int? ReadOptionalInt(JsonObject root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetPropertyValue(name, out var node) || node == null)
                    continue;

                if (node is JsonValue value && value.TryGetValue<int>(out var intValue))
                    return intValue;
                if (int.TryParse(node.ToString(), out var parsed))
                    return parsed;
            }

            return null;
        }

        private static bool ReadBool(JsonObject root, string name, bool defaultValue)
        {
            return ReadNullableBool(root, name) ?? defaultValue;
        }

        private static bool? ReadNullableBool(JsonObject root, string name)
        {
            if (!root.TryGetPropertyValue(name, out var node) || node == null)
                return null;
            if (node is JsonValue value && value.TryGetValue<bool>(out var boolValue))
                return boolValue;
            if (bool.TryParse(node.ToString(), out var parsed))
                return parsed;
            throw new ArgumentException("Expected JSON boolean at property '" + name + "'.");
        }
    }
}
