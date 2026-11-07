using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 2.7.9: issue #60 (partial cross references reported as success), #63 (structured context for
    /// refused single-object export), #57 (Classic HMI tag table: absolute addresses + reject unknown fields).
    /// </summary>
    internal static class Release279Tests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            RunCrossReferences(check);
            RunCrossReferenceWiring(check, skip);
            RunExportErrorContext(check);
            RunClassicHmiTagTable(check);
        }

        // ---------------- #60 ----------------

        private sealed class FakeLocation { public string Name => "NW1"; public string ReferenceLocation => "@FC1 NW1"; public string ReferenceType => "Uses"; public string Access => "Read"; }
        private sealed class FakeReference { public string Name { get; set; } = "FB_Pump"; public string Path => "Program blocks/FB_Pump"; public IEnumerable<FakeLocation>? Locations { get; set; } = new[] { new FakeLocation() }; }
        private sealed class FakeSource
        {
            public string Name { get; set; } = "FC_Main";
            public string Path => "Program blocks/FC_Main";
            public bool Throw { get; set; }
            public bool NullRefs { get; set; }
            public IEnumerable<FakeReference>? References
            {
                get
                {
                    if (Throw) throw new InvalidOperationException("Access to a disposed object");
                    return NullRefs ? null : new[] { new FakeReference() };
                }
            }
        }
        private sealed class FakeResult { public IEnumerable<FakeSource>? Sources { get; set; } }
        private sealed class FakeSourceWithChildren
        {
            public string Name => "DB_Pump";
            public string Path => "Program blocks/DB_Pump";
            public IEnumerable<FakeReference> References => new[] { new FakeReference() };
            public IEnumerable<object> Children { get; set; } = new object[] { new object() };
        }
        private sealed class ChildResult { public IEnumerable<object>? Sources { get; set; } }
        private sealed class LostSource
        {
            public string Name => "FC_Lost";
            public IEnumerable<FakeReference> References => throw new NonRecoverableException("TIA Portal has been closed");
        }
        private sealed class NoSourcesResult { public int Count => 0; }

        private static void RunCrossReferences(Action<bool, string> check)
        {
            var full = CrossReferenceFlattener.Flatten(new FakeResult { Sources = new[] { new FakeSource(), new FakeSource { Name = "FC_Aux" } } }, "fallback", out var t0, out _);
            check(t0 == null && full.Count == 2, "[反向哨兵] 完整枚举：不标截断，两条都在（实得 " + full.Count + "，截断=" + (t0 ?? "null") + "）");
            check(full[0].LocationName == "NW1" && full[0].ReferenceName == "FB_Pump", "[反向哨兵] 字段照旧填（Location / Reference）");

            var partial = CrossReferenceFlattener.Flatten(new FakeResult { Sources = new[] { new FakeSource(), new FakeSource { Throw = true } } }, "fallback", out var t1, out _);
            check(partial.Count == 1 && t1 != null, "#60 第二个源抛异常：返回已收集的 1 条，并且必须标截断");
            check(t1 != null && t1.Contains("INCOMPLETE") && t1.Contains("disposed"), "#60 截断原因要带原生异常信息（实得 " + (t1 ?? "null") + "）");

            var none = CrossReferenceFlattener.Flatten(new FakeResult { Sources = new[] { new FakeSource { Throw = true } } }, "fallback", out var t2, out _);
            check(none.Count == 0 && t2 != null, "#60 第一个源就抛：空表不许冒充「没人引用」");

            CrossReferenceFlattener.Flatten(new NoSourcesResult(), "fallback", out var t3, out _);
            check(t3 != null, "#60 结果对象没有 Sources：一条都没枚举，必须标截断");

            var skipped = CrossReferenceFlattener.Flatten(new FakeResult { Sources = new[] { new FakeSource(), new FakeSource { NullRefs = true } } }, "fallback", out var t4, out _);
            check(skipped.Count == 1 && t4 != null, "#60 某个源的 References 取不到：跳过也要标截断");

            CrossReferenceFlattener.Flatten(new FakeResult { Sources = Array.Empty<FakeSource>() }, "fallback", out var t5, out _);
            check(t5 == null, "[反向哨兵] 真的没有引用（空 Sources）是完整结果，不标截断");

            CrossReferenceFlattener.Flatten(new ChildResult { Sources = new object[] { new FakeSourceWithChildren { Children = Array.Empty<object>() } } }, "fallback", out var t7, out _);
            check(t7 == null, "[反向哨兵] Children 是空集合：照旧算完整");

            var rethrown = false;
            try { CrossReferenceFlattener.Flatten(new ChildResult { Sources = new object[] { new LostSource() } }, "fallback", out _, out _); }
            catch (Exception ex) { rethrown = PortalFailureClassifier.IsPortalProcessLost(ex); }
            check(rethrown, "#60 枚举中博途进程没了：直接抛出，不当成「部分结果」让删除预览继续走");
        }

        /// <summary>The MCP tool and the delete previews live in Siemens-dependent files: pin their shape from source.</summary>
        private static void RunCrossReferenceWiring(Action<bool, string> check, Action<string, string> skip)
        {
            var toolSrc = FindSource(Path.Combine("ModelContextProtocol", "McpServer.PlcSoftware.cs"));
            var delSrc = FindSource(Path.Combine("Siemens", "Portal.Deletion.cs"));
            var portalSrc = FindSource(Path.Combine("Siemens", "Portal.Software.cs"));
            if (toolSrc == null || delSrc == null || portalSrc == null)
            {
                skip("#60 交叉引用截断的接线（源码形状）", "找不到引擎源码目录（只在仓内跑得了）");
                return;
            }

            var tool = File.ReadAllText(toolSrc);
            var start = tool.IndexOf("Name = \"GetCrossReferences\"", StringComparison.Ordinal);
            var body = start < 0 ? "" : tool.Substring(start, Math.Min(3000, tool.Length - start));
            check(body.Contains("Portal.LastCrossReferenceTruncation") && body.Contains("[\"success\"] = false"),
                "#60 GetCrossReferences 工具必须读截断标记，截断时 success=false");

            var del = File.ReadAllText(delSrc);
            var uses = del.Split(new[] { "AddCrossReferenceCompleteness(result, warnings," }, StringSplitOptions.None).Length - 1;
            check(uses == 3, "#60 删除预览（块 / 变量表 / UDT）三处都要报交叉引用是否完整，实得 " + uses + " 处");

            var hmiStart = tool.IndexOf("Name = \"BuildClassicHmiTagTableXml\"", StringComparison.Ordinal);
            var hmiBody = hmiStart < 0 ? "" : tool.Substring(hmiStart, Math.Min(2500, tool.Length - hmiStart));
            check(hmiBody.Contains("catch (ArgumentException ex)") && hmiBody.Contains("McpErrorCode.InvalidParams"),
                "#57 参数校验错要报 InvalidParams，不许包成「Unexpected error / InternalError」");

            var portal = File.ReadAllText(portalSrc);
            check(!portal.Contains("TryFlattenCrossReferenceResult"), "#60 旧的静默吞异常整理函数不许留在 Portal 里");
            check(portal.Contains("LastCrossReferenceTruncation = null;"), "#60 每次调用开头要清掉上一次的截断标记");
        }

        // ---------------- #63 ----------------

        private sealed class NonRecoverableException : Exception { internal NonRecoverableException(string m) : base(m) { } }

        private static void RunExportErrorContext(Action<bool, string> check)
        {
            var refused = new Exception("Export failed", new Exception("The export of block 'SyntheticBlock' is not permitted."));
            var c = ExportErrorContext.Build("export", "Block", "PLC_1", "Program blocks/SyntheticBlock", refused);
            check(c["code"]?.ToString() == "ExportFailed" && c["phase"]?.ToString() == "export", "#63 code / phase 要在");
            check(c["objectKind"]?.ToString() == "Block" && c["objectPath"]?.ToString() == "Program blocks/SyntheticBlock" && c["softwarePath"]?.ToString() == "PLC_1", "#63 对象身份要在");
            check(c["scope"]?.ToString() == "object", "#63 单对象拒绝 scope=object");
            check(c["reason"]?.ToString() == "notPermitted" && c["rootCauseKnown"]?.GetValue<bool>() == false,
                "#63 只凭 not permitted 判不出权限还是能力：reason=notPermitted 且 rootCauseKnown=false");
            check(c["retryable"]?.GetValue<bool>() == false, "#63 不许标成可重试");
            check(c["nativeMessage"]?.ToString() == "The export of block 'SyntheticBlock' is not permitted.", "#63 保留博途原话（最内层消息，不是包装层）");

            var line = ExportErrorContext.Line("export", "Block", "PLC_1", "X", refused);
            JsonObject? parsed = null;
            try { parsed = JsonNode.Parse(line.Substring(ExportErrorContext.Prefix.Length)) as JsonObject; } catch { }
            check(line.StartsWith("ERROR_CONTEXT: ", StringComparison.Ordinal) && parsed != null && !line.Contains("\n"),
                "#63 附加行：固定前缀 + 单行可解析 JSON");

            var lost = ExportErrorContext.Build("export", "Block", "PLC_1", "X", new Exception("Export failed", new NonRecoverableException("TIA Portal has been closed")));
            check(lost["scope"]?.ToString() == "session" && lost["reason"]?.ToString() == "portalProcessLost",
                "#63 博途进程没了：scope=session，不能说成单对象问题");

            var inconsistent = ExportErrorContext.Build("precheck", "Block", "PLC_1", "X", new Exception("Block is inconsistent; TIA Portal does not export inconsistent blocks."));
            check(inconsistent["reason"]?.ToString() == "inconsistent" && inconsistent["rootCauseKnown"]?.GetValue<bool>() == true, "#63 不一致的块：reason=inconsistent");

            var noProject = ExportErrorContext.Build("precheck", "Block", "PLC_1", "X", new Exception("No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); ..."));
            check(noProject["scope"]?.ToString() == "session" && noProject["reason"]?.ToString() == "noProjectOpen", "#63 没打开工程是会话级问题，不许说成单对象问题");

            var other = ExportErrorContext.Build("export", "Type", "PLC_1", "UDT_X", new Exception("Export failed", new IOException("disk full")));
            check(other["reason"]?.ToString() == "unknown" && other["rootCauseKnown"]?.GetValue<bool>() == false, "[反向哨兵] 认不出的原因保持 unknown，不编");
        }

        // ---------------- #57 ----------------

        // Structure verified by a real V21 import + export in issue #13 (KTP700 Basic PN).
        private const string VerifiedAbsoluteTag = @"<Hmi.Tag.Tag ID=""1"" CompositionName=""Tags"">
  <AttributeList>
    <AcquisitionTriggerMode>Visible</AcquisitionTriggerMode>
    <AddressAccessMode>Absolute</AddressAccessMode>
    <Length>1</Length>
    <LogicalAddress>%M10.0</LogicalAddress>
    <Name>启动</Name>
  </AttributeList>
  <LinkList>
    <AcquisitionCycle TargetID=""@OpenLink""><Name>1 s</Name></AcquisitionCycle>
    <Connection TargetID=""@OpenLink""><Name>HMI_连接_1</Name></Connection>
    <DataType TargetID=""@OpenLink""><Name>Bool</Name></DataType>
    <HmiDataType TargetID=""@OpenLink""><Name>Bool</Name></HmiDataType>
  </LinkList>
</Hmi.Tag.Tag>";

        private static void RunClassicHmiTagTable(Action<bool, string> check)
        {
            // The exact input from issue #57.
            var issueInput = @"{ ""tableName"": ""默认变量表"", ""connectionName"": ""HMI_连接_1"", ""absolute"": true,
  ""tags"": [ { ""name"": ""启动"", ""dataType"": ""Bool"", ""logicalAddress"": ""%M10.0"" } ] }";
            var r = ClassicHmiTagTableXmlBuilder.BuildFromJson(issueInput);
            var doc = XDocument.Parse(r["xml"]!.ToString());
            var tag = doc.Descendants("Hmi.Tag.Tag").Single();
            check(XNode.DeepEquals(Normalize(tag), Normalize(XElement.Parse(VerifiedAbsoluteTag))),
                "#57 issue 原输入生成的变量要和 #13 真机导入验证过的结构逐元素一致，实得：" + tag);
            check(r["ok"]?.GetValue<bool>() == true && r["analysis"]?["absoluteBindingCount"]?.GetValue<int>() == 1,
                "#57 analysis 要数出 1 个绝对地址绑定且 ok");

            var viaAddress = ClassicHmiTagTableXmlBuilder.BuildFromJson(@"{ ""name"": ""T"", ""tags"": [ { ""name"": ""灯"", ""address"": ""%M30.0"", ""connection"": ""HMI_连接_1"" } ] }");
            check(viaAddress["xml"]!.ToString().Contains("<LogicalAddress>%M30.0</LogicalAddress>"), "#57 address 键名同样生效、变量级 connection 也行");

            check(Throws(@"{ ""name"": ""T"", ""tags"": [ { ""name"": ""A"", ""dataType"": ""Bool"", ""comment"": ""x"" } ] }", "comment", "LogicalAddress"),
                "#57 变量里不认识的键直接报错，并列出支持的键");
            check(Throws(@"{ ""name"": ""T"", ""connectionNmae"": ""C"", ""tags"": [ { ""name"": ""A"" } ] }", "connectionNmae", "ConnectionName"),
                "#57 表级拼错的键直接报错");
            check(Throws(@"{ ""name"": ""T"", ""tags"": [ { ""name"": ""A"", ""logicalAddress"": ""%M1.0"" } ] }", "needs an HMI connection"),
                "#57 绝对地址没给连接：报错，不生成内部变量");
            check(Throws(@"{ ""name"": ""T"", ""connectionName"": ""C"", ""tags"": [ { ""name"": ""A"", ""logicalAddress"": ""%M1.0"", ""plcTag"": ""DB1.X"" } ] }", "one addressing mode"),
                "#57 同时给地址和符号：报错");
            check(Throws(@"{ ""name"": ""T"", ""connectionName"": ""C"", ""absolute"": true, ""tags"": [ { ""name"": ""A"" } ] }", "Absolute=true"),
                "#57 absolute=true 但变量没地址：报错");
            check(Throws(@"{ ""name"": ""T"", ""connectionName"": ""C"", ""tags"": [ { ""name"": ""A"", ""logicalAddress"": ""M1.0"" } ] }", "starting with %"),
                "#57 地址不是 % 开头：报错");
            check(Throws(@"{ ""name"": ""T"", ""connectionName"": ""C"", ""absolute"": 1, ""tags"": [ { ""name"": ""A"" } ] }", "true or false"),
                "#57 absolute 写成 1：报错，不许静默关掉「每个变量要有地址」");
            check(Throws(@"{ ""name"": ""T"", ""connectionName"": ""C"", ""absolute"": ""yes"", ""tags"": [ { ""name"": ""A"" } ] }", "true or false"),
                "#57 absolute 写成字符串 yes：报错");

            // Reverse sentinels: the existing symbolic and internal forms must come out unchanged.
            var symbolic = ClassicHmiTagTableXmlBuilder.BuildFromJson(@"{ ""Name"": ""Motor"", ""Tags"": [
  {""Name"":""Motor_Start"",""DataType"":""Bool"",""Length"":""1"",""Connection"":""HMI_Connection_1"",""PlcTag"":""DB1_MotorData.Motor.Start""},
  {""Name"":""Speed_Set"",""DataType"":""Int"",""Length"":""2"",""Connection"":""HMI_Connection_1"",""PlcTag"":""DB1_MotorData.SpeedSet""} ] }");
            var sx = symbolic["xml"]!.ToString();
            check(symbolic["ok"]?.GetValue<bool>() == true && symbolic["analysis"]?["symbolicBindingCount"]?.GetValue<int>() == 2
                  && symbolic["analysis"]?["absoluteBindingCount"]?.GetValue<int>() == 0,
                "[反向哨兵] 符号寻址照旧：2 个符号绑定、0 个绝对");
            check(sx.Contains("<AddressAccessMode>Symbolic</AddressAccessMode>") && sx.Contains("<LogicalAddress></LogicalAddress>") && sx.Contains("DB1_MotorData.SpeedSet"),
                "[反向哨兵] 符号寻址 XML 形状不变（Symbolic + 空 LogicalAddress + ControllerTag）");

            var tableConn = ClassicHmiTagTableXmlBuilder.BuildFromJson(@"{ ""name"": ""T"", ""connectionName"": ""C1"", ""tags"": [ { ""name"": ""A"", ""plcTag"": ""DB1.X"" }, { ""name"": ""Internal_1"" } ] }");
            var tx = XDocument.Parse(tableConn["xml"]!.ToString()).Descendants("Hmi.Tag.Tag").ToArray();
            check(tx[0].Element("LinkList")?.Element("Connection")?.Element("Name")?.Value == "C1", "#57 表级连接名用于只给了 PlcTag 的符号变量");
            check(tx[1].Element("AttributeList")?.Element("AddressAccessMode") == null && tx[1].Element("LinkList")?.Element("Connection") == null,
                "[反向哨兵] 没地址没符号的变量仍是内部变量，表级连接名不会把它变成外部变量");
        }

        private static bool Throws(string json, params string[] mustContain)
        {
            try { ClassicHmiTagTableXmlBuilder.BuildFromJson(json); return false; }
            catch (ArgumentException ex) { return mustContain.All(m => ex.Message.Contains(m)); }
        }

        private static XElement Normalize(XElement e)
            => new XElement(e.Name, e.Attributes(), e.HasElements ? e.Elements().Select(Normalize) : (object)e.Value.Trim());

        private static string? FindSource(string relative)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 10 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "src", "TiaMcpServer", relative);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }
    }
}
