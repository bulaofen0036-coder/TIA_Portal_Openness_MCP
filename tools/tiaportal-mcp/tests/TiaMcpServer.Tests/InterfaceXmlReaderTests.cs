using System;
using System.IO;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// The interface reader is the answer to three gaps that used to force callers to export XML
    /// and parse it on the host: block interface, DB/UDT structure, and enumerating interface
    /// members. A parser that silently drops sections or members is worse than no tool at all —
    /// the caller then believes the block has fewer variables than it does. These cases pin the
    /// shapes the real V19 exports use.
    /// </summary>
    internal static class InterfaceXmlReaderTests
    {
        private const string FbXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input"">
            <Member Name=""Trig"" Datatype=""Bool"" />
            <Member Name=""PulseBit"" Datatype=""Bool"" />
            <Member Name=""NegIn"" Datatype=""Bool"" />
            <Member Name=""CmpA"" Datatype=""Int"" />
            <Member Name=""CmpB"" Datatype=""Int"" />
          </Section>
          <Section Name=""Output"">
            <Member Name=""OutTonQ"" Datatype=""Bool"" />
            <Member Name=""OutPulse"" Datatype=""Bool"" />
            <Member Name=""OutNot"" Datatype=""Bool"" />
            <Member Name=""OutLt"" Datatype=""Bool"" />
          </Section>
          <Section Name=""InOut"" />
          <Section Name=""Static"">
            <Member Name=""tonInst"" Datatype=""TON_TIME"" Version=""1.0"">
              <AttributeList>
                <BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute>
              </AttributeList>
            </Member>
          </Section>
          <Section Name=""Temp"">
            <Member Name=""PulseWork"" Datatype=""Bool"" />
          </Section>
          <Section Name=""Constant"" />
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>MCPVerify_FB</Name>
      <Namespace />
      <Number>59989</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
    </AttributeList>
  </SW.Blocks.FB>
</Document>";

        private const string DbXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.GlobalDB ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Static"">
            <Member Name=""CmdEnable"" Datatype=""Bool""><StartValue>false</StartValue></Member>
            <Member Name=""StepNo"" Datatype=""Int""><StartValue>0</StartValue></Member>
            <Member Name=""OutputMax"" Datatype=""Real""><StartValue>100.0</StartValue></Member>
          </Section>
        </Sections>
      </Interface>
      <MemoryLayout>Standard</MemoryLayout>
      <Name>DB_Test</Name>
      <Number>200</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>";

        private const string UdtXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Types.PlcStruct ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Static"">
            <Member Name=""st"" Datatype=""MyStruct"">
              <Comment><MultiLanguageText Lang=""zh-CN"">中文注释</MultiLanguageText></Comment>
              <Member Name=""x"" Datatype=""Bool""><StartValue>true</StartValue></Member>
            </Member>
            <Member Name=""arr"" Datatype=""Array[0..9] of Int"" Remanence=""Retain"" Accessibility=""Public"" />
            <Member Name=""m"" Datatype=""Real"">
              <Comment><MultilingualText><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>English comment</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></Comment>
            </Member>
          </Section>
        </Sections>
      </Interface>
      <Name>UDT_Test</Name>
      <Namespace>MyNs</Namespace>
    </AttributeList>
  </SW.Types.PlcStruct>
</Document>";

        private const string NestedSectionsXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Types.PlcStruct>
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Static"">
            <Member Name=""nested"" Datatype=""Inner"">
              <Sections>
                <Section Name=""Static"">
                  <Member Name=""y"" Datatype=""Bool"" />
                </Section>
              </Sections>
            </Member>
          </Section>
        </Sections>
      </Interface>
      <Name>UDT_Nested</Name>
    </AttributeList>
  </SW.Types.PlcStruct>
</Document>";

        public static void Run(Action<bool, string> check, Action<string, string>? skip = null)
        {
            // 1) FB: every section must survive, including empty ones.
            var fb = InterfaceXmlReader.Parse(FbXml, "Block");
            check(fb.ObjectType == "SW.Blocks.FB", "FB: object type read");
            check(fb.ObjectName == "MCPVerify_FB", "FB: name read");
            check(fb.Number == 59989, "FB: block number read");
            check(fb.ProgrammingLanguage == "LAD", "FB: programming language read");
            check(fb.MemoryLayout == "Optimized", "FB: memory layout read");
            check(fb.Sections.Count == 6, "FB: all six sections returned");
            check(fb.Sections.Select(s => s.Name).SequenceEqual(new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" }),
                "FB: section order preserved");
            check(fb.Sections.First(s => s.Name == "Input").Members.Count == 5, "FB: Input member count");
            check(fb.Sections.First(s => s.Name == "Output").Members.Count == 4, "FB: Output member count");
            check(fb.Sections.First(s => s.Name == "InOut").Members.Count == 0, "FB: empty section survives");
            var ton = fb.Sections.First(s => s.Name == "Static").Members.Single();
            check(ton.Datatype == "TON_TIME" && ton.Version == "1.0", "FB: datatype and library version read");
            check(fb.MemberCount == 11, "FB: total member count");
            check(!fb.Truncated, "FB: not truncated");

            // 2) DB: structure is the Static section; start values must not be dropped.
            var db = InterfaceXmlReader.Parse(DbXml, "Block");
            check(db.ObjectType == "SW.Blocks.GlobalDB", "DB: object type read");
            check(db.Sections.Count == 1 && db.Sections[0].Name == "Static", "DB: one Static section");
            check(db.Sections[0].Members.Count == 3, "DB: member count");
            check(db.Sections[0].Members.First(m => m.Name == "OutputMax").StartValue == "100.0",
                "DB: StartValue preserved");

            // 3) UDT: nested struct, array, and both comment shapes.
            var udt = InterfaceXmlReader.Parse(UdtXml, "Type");
            check(udt.ObjectType == "SW.Types.PlcStruct", "UDT: object type read");
            check(udt.ObjectName == "UDT_Test" && udt.ObjectNamespace == "MyNs", "UDT: name and namespace");
            var st = udt.Sections[0].Members.First(m => m.Name == "st");
            check(st.Members != null && st.Members.Count == 1, "UDT: nested struct member keeps its child");
            check(st.Members![0].Name == "x" && st.Members[0].Path == "st.x", "UDT: nested member path is dotted");
            check(st.Comment == "中文注释" && st.CommentLanguage == "zh-CN", "UDT: zh-CN comment preferred");
            var arr = udt.Sections[0].Members.First(m => m.Name == "arr");
            check(arr.ArrayBounds == "0..9" && arr.ElementType == "Int", "UDT: array bounds and element type");
            check(arr.Remanence == "Retain" && arr.Accessibility == "Public", "UDT: member attributes preserved");
            var m = udt.Sections[0].Members.First(x => x.Name == "m");
            check(m.Comment == "English comment" && m.CommentLanguage == "en-US", "UDT: object-style comment read");

            // 3b) A struct member expressed as a nested <Sections> block must keep its children.
            var nested = InterfaceXmlReader.Parse(NestedSectionsXml, "Type");
            var nestedMember = nested.Sections[0].Members.Single();
            check(nestedMember.Members != null && nestedMember.Members.Count == 1,
                "UDT: nested <Sections> member keeps its child");
            check(nestedMember.Members![0].Path == "nested.y", "UDT: nested <Sections> path is dotted");

            // 4) Truncation must be visible, never silent.
            var capped = InterfaceXmlReader.Parse(UdtXml, "Type", maxMembers: 2);
            check(capped.Truncated, "cap: Truncated=true when maxMembers is hit");
            check(capped.MemberCount == 2, "cap: MemberCount reports what was returned");

            // 5) A block without an <Interface> is an explicit empty structure, not a crash.
            var empty = InterfaceXmlReader.Parse(
                @"<Document><SW.Blocks.OB><AttributeList><Name>OB1</Name><Number>1</Number></AttributeList></SW.Blocks.OB></Document>",
                "Block");
            check(empty.Sections.Count == 0 && empty.MemberCount == 0, "empty: no interface returns zero sections");

            // 6) Wrong/empty input must fail loudly.
            var threw = false;
            try { InterfaceXmlReader.Parse("not xml", "Block"); }
            catch (InvalidDataException) { threw = true; }
            check(threw, "invalid: non-XML throws InvalidDataException");

            threw = false;
            try { InterfaceXmlReader.Parse("<Document><Something /></Document>", "Block"); }
            catch (InvalidDataException) { threw = true; }
            check(threw, "invalid: non-block XML throws InvalidDataException");

            // 7) Real V19-shaped export from the repo (skipped only when the test runs outside the repo).
            var realPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../../templates/mcp-full-e2e-verify/plc/blocks/MCPVerify_FB_LAD_v3.xml"));
            if (File.Exists(realPath))
            {
                var real = InterfaceXmlReader.ReadFile(realPath);
                check(real.Sections.Count == 6, "real FB export: six sections");
                check(real.Sections.First(s => s.Name == "Static").Members.Any(m => m.Name == "tonInst"),
                    "real FB export: Static tonInst found");
                check(real.Sections.First(s => s.Name == "Input").Members.Count == 5,
                    "real FB export: Input member count");
            }
            else
            {
                skip?.Invoke("real V19 FB export fixture", "not running from the repo tree");
            }
        }
    }
}
