using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;


namespace TiaMcpServer
{
    // Partial: hmi template validation. Extracted from Program.cs (god-file split); behavior unchanged.
    public partial class Program
    {
        private static string MakeSafeReportFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }
            return sb.ToString();
        }

        private static void AppendJsonArrayPreview(StringBuilder md, JsonArray? array, string title, int limit)
        {
            md.AppendLine("## " + title);
            if (array == null || array.Count == 0)
            {
                md.AppendLine("- <none>");
                md.AppendLine();
                return;
            }

            foreach (var item in array.Take(limit))
            {
                md.AppendLine("- " + item);
            }
            if (array.Count > limit)
            {
                md.AppendLine("- ... " + (array.Count - limit) + " more");
            }
            md.AppendLine();
        }

        private static void AppendSectionSummary(StringBuilder md, JsonObject? parent, string key, string title)
        {
            var sections = parent?["sections"] as JsonObject;
            var section = sections?[key] as JsonObject;
            if (section == null) return;

            md.AppendLine();
            md.AppendLine("### " + title);
            md.AppendLine("- Exists: " + section["exists"]);
            md.AppendLine("- File count: " + section["fileCount"]);
            md.AppendLine("- Total bytes: " + section["totalBytes"]);
            if (section["samples"] is JsonArray samples && samples.Count > 0)
            {
                md.AppendLine("- Largest samples:");
                foreach (var item in samples.Take(5))
                {
                    var obj = item as JsonObject;
                    md.AppendLine("  - " + obj?["relativePath"] + " (" + obj?["bytes"] + " bytes)");
                }
            }
        }

        private static void SyncMotorMinimalArtifacts(string projectName, string projectDirectory, string importDir)
        {
            try
            {
                var packRoot = Path.Combine(Directory.GetCurrentDirectory(), "TIA_MCP_AI_PACK");
                var refRoot = Path.Combine(packRoot, "references", "motor_minimal_latest_fixed");
                var reportsRoot = Path.Combine(packRoot, "references", "reports");
                Directory.CreateDirectory(refRoot);
                Directory.CreateDirectory(reportsRoot);

                foreach (var file in Directory.GetFiles(importDir))
                {
                    var dest = Path.Combine(refRoot, Path.GetFileName(file));
                    File.Copy(file, dest, true);
                }

                var report = Path.Combine(projectDirectory, projectName + "_REPORT.txt");
                if (File.Exists(report))
                {
                    File.Copy(report, Path.Combine(reportsRoot, projectName + "_REPORT.txt"), true);
                    File.Copy(report, Path.Combine(refRoot, projectName + "_REPORT.txt"), true);
                }

                var notes = new StringBuilder();
                notes.AppendLine("# Latest Motor Minimal Fixed Run");
                notes.AppendLine();
                notes.AppendLine("Project: " + projectName);
                notes.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                notes.AppendLine("Source project directory: " + projectDirectory);
                notes.AppendLine("Generator: tools\\tiaportal-mcp\\src\\TiaMcpServer\\Program.cs --run-motor-minimal-test");
                notes.AppendLine();
                notes.AppendLine("Verification focus:");
                notes.AppendLine("- PLC/HMI Profinet node connection probe is recorded in the report.");
                notes.AppendLine("- HMI connection CommunicationDriver/Partner/Station/Node readback is recorded.");
                notes.AppendLine("- HMI tags try symbolic PLC binding first; if TIA V21 does not read back PlcTag, the generator applies and verifies real absolute PLC addresses.");
                notes.AppendLine("- Buttons include pressed-state binding; STOP also attempts a click event script.");
                TryWriteText(Path.Combine(refRoot, "README.md"), notes.ToString());
            }
            catch (Exception ex)
            {
                LogDiag("SyncMotorMinimalArtifacts failed: " + ex.Message);
            }
        }

        private static void WritePlcSyntaxValidationXml(string dir)
        {
            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Local(string name) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" /></Symbol></Access>";
            string Const(string value) => $"<Access Scope=\"LiteralConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";
            string Call(string name, params string[] args)
            {
                var b = new StringBuilder();
                b.Append($"<Access Scope=\"Call\" UId=\"{U()}\"><Instruction Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\">");
                b.Append(Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(Tok(","));
                    b.Append($"<NamelessParameter UId=\"{U()}\">");
                    b.Append(args[i]);
                    b.Append("</NamelessParameter>");
                }
                b.Append(Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }
            string CallNamed(string name, params (string Name, string Value)[] args)
            {
                var b = new StringBuilder();
                b.Append($"<Access Scope=\"Call\" UId=\"{U()}\"><Instruction Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\">");
                b.Append(Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(Tok(","));
                    b.Append($"<Parameter Name=\"{System.Security.SecurityElement.Escape(args[i].Name)}\" UId=\"{U()}\">");
                    b.Append(Blank());
                    b.Append(Tok(":="));
                    b.Append(Blank());
                    b.Append(args[i].Value);
                    b.Append("</Parameter>");
                }
                b.Append(Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }
            var st = new StringBuilder();
            void Line(params string[] parts) { foreach (var p in parts) st.AppendLine(p); st.AppendLine(NL()); }

            Line(Local("OutBool"), Blank(), Tok(":="), Blank(), Local("Enable"), Blank(), Tok("AND"), Blank(), Tok("("), Local("InInt"), Blank(), Tok(">="), Blank(), Const("0"), Tok(")"), Blank(), Tok("OR"), Blank(), Tok("("), Local("Mode"), Blank(), Tok("="), Blank(), Const("2"), Tok(")"), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), Local("InInt"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("INT_TO_DINT", Local("OutInt")), Tok(";"));
            Line(Local("OutReal"), Blank(), Tok(":="), Blank(), Call("DINT_TO_REAL", Local("OutDInt")), Blank(), Tok("/"), Blank(), Const("10.0"), Tok(";"));
            Line(Local("OutWord"), Blank(), Tok(":="), Blank(), Const("16#1234"), Tok(";"));
            Line(Local("OutReal"), Blank(), Tok(":="), Blank(), Call("ABS", Local("InReal")), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("MIN", ("IN1", Local("OutInt")), ("IN2", Const("5")), ("IN3", Const("10"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("MAX", ("IN1", Local("OutInt")), ("IN2", Const("5")), ("IN3", Const("10"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("LIMIT", ("MN", Const("-100")), ("IN", Local("OutInt")), ("MX", Const("100"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("SEL", ("G", Local("Enable")), ("IN0", Const("0")), ("IN1", Local("OutInt"))), Tok(";"));
            Line(Local("OutInt"), Blank(), Tok(":="), Blank(), CallNamed("MUX", ("K", Const("1")), ("IN0", Const("0")), ("IN1", Const("10")), ("IN2", Const("20"))), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("ROUND", Local("InReal")), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("TRUNC", Local("InReal")), Tok(";"));
            Line(Local("OutDInt"), Blank(), Tok(":="), Blank(), Call("REAL_TO_DINT", Local("InReal")), Tok(";"));
            Line(Local("OutWord"), Blank(), Tok(":="), Blank(), CallNamed("SHL", ("IN", Local("OutWord")), ("N", Const("1"))), Tok(";"));
            Line(Local("OutWord"), Blank(), Tok(":="), Blank(), CallNamed("SHR", ("IN", Local("OutWord")), ("N", Const("1"))), Tok(";"));

            Line(Tok("IF"), Blank(), Tok("NOT"), Blank(), Local("Enable"), Blank(), Tok("THEN"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("ELSIF"), Blank(), Local("Mode"), Blank(), Tok("="), Blank(), Const("1"), Blank(), Tok("THEN"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("10"), Tok(";"));
            Line(Tok("ELSE"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("20"), Tok(";"));
            Line(Tok("END_IF"), Tok(";"));

            Line(Tok("CASE"), Blank(), Local("Mode"), Blank(), Tok("OF"));
            Line(Blank(2), Const("0"), Tok(":"), Blank(), Local("OutInt"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Blank(2), Const("1"), Tok(","), Blank(), Const("2"), Tok(":"), Blank(), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Blank(2), Const("3"), Tok(".."), Const("5"), Tok(":"), Blank(), Local("OutInt"), Blank(), Tok(":="), Blank(), Local("OutInt"), Blank(), Tok("+"), Blank(), Const("3"), Tok(";"));
            Line(Tok("ELSE"));
            Line(Blank(2), Local("OutInt"), Blank(), Tok(":="), Blank(), Const("-1"), Tok(";"));
            Line(Tok("END_CASE"), Tok(";"));

            Line(Local("acc"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("FOR"), Blank(), Local("i"), Blank(), Tok(":="), Blank(), Const("0"), Blank(), Tok("TO"), Blank(), Const("9"), Blank(), Tok("DO"));
            Line(Blank(2), Local("acc"), Blank(), Tok(":="), Blank(), Local("acc"), Blank(), Tok("+"), Blank(), Local("i"), Tok(";"));
            Line(Tok("END_FOR"), Tok(";"));

            Line(Local("i"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("WHILE"), Blank(), Local("i"), Blank(), Tok("<"), Blank(), Const("3"), Blank(), Tok("DO"));
            Line(Blank(2), Local("acc"), Blank(), Tok(":="), Blank(), Local("acc"), Blank(), Tok("+"), Blank(), Local("i"), Tok(";"));
            Line(Blank(2), Local("i"), Blank(), Tok(":="), Blank(), Local("i"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Tok("END_WHILE"), Tok(";"));

            Line(Tok("REPEAT"));
            Line(Blank(2), Local("acc"), Blank(), Tok(":="), Blank(), Local("acc"), Blank(), Tok("-"), Blank(), Const("1"), Tok(";"));
            Line(Tok("UNTIL"), Blank(), Local("acc"), Blank(), Tok("<="), Blank(), Const("0"));
            Line(Tok("END_REPEAT"), Tok(";"));

            File.WriteAllText(Path.Combine(dir, "MCP_Syntax_FC.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FC ID=""0"">
    <AttributeList>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""Enable"" Datatype=""Bool"" /><Member Name=""Mode"" Datatype=""Int"" /><Member Name=""InInt"" Datatype=""Int"" /><Member Name=""InReal"" Datatype=""Real"" /></Section>
          <Section Name=""Output""><Member Name=""OutBool"" Datatype=""Bool"" /><Member Name=""OutInt"" Datatype=""Int"" /><Member Name=""OutDInt"" Datatype=""DInt"" /><Member Name=""OutReal"" Datatype=""Real"" /><Member Name=""OutWord"" Datatype=""Word"" /></Section>
          <Section Name=""InOut"" />
          <Section Name=""Temp""><Member Name=""i"" Datatype=""Int"" /><Member Name=""acc"" Datatype=""Int"" /></Section>
          <Section Name=""Constant"" />
          <Section Name=""Return""><Member Name=""Ret_Val"" Datatype=""Void"" /></Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>MCP_Syntax_FC</Name><Namespace /><Number>12001</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FC>
</Document>", Encoding.UTF8);
        }

        private static void WritePlcSyntaxIecFbXml(string dir)
        {
            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Local(string name) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" /></Symbol></Access>";
            string LocalField(string name, string field) => $"<Access Scope=\"LocalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\" />{Tok(".")}<Component Name=\"{System.Security.SecurityElement.Escape(field)}\" UId=\"{U()}\" /></Symbol></Access>";
            string TypedConst(string value) => $"<Access Scope=\"TypedConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";
            string Param(string name, string value) => $"<Parameter Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\">{Tok(":=")}{value}</Parameter>";
            string InstanceCall(string instance, params (string Name, string Value)[] args)
            {
                var b = new StringBuilder();
                b.Append(Local(instance));
                b.Append($"<Access Scope=\"Call\" UId=\"{U()}\"><Instruction UId=\"{U()}\">");
                b.Append(Tok("("));
                for (var i = 0; i < args.Length; i++)
                {
                    if (i > 0) b.Append(Tok(","));
                    b.Append(Param(args[i].Name, args[i].Value));
                }
                b.Append(Tok(")"));
                b.Append("</Instruction></Access>");
                return b.ToString();
            }

            var st = new StringBuilder();
            void Line(params string[] parts) { foreach (var p in parts) st.AppendLine(p); st.AppendLine(NL()); }

            Line(InstanceCall("rEdge", ("CLK", Local("Pulse"))), Tok(";"));
            Line(InstanceCall("fEdge", ("CLK", Local("Pulse"))), Tok(";"));
            Line(Local("Rising"), Blank(), Tok(":="), Blank(), LocalField("rEdge", "Q"), Tok(";"));
            Line(Local("Falling"), Blank(), Tok(":="), Blank(), LocalField("fEdge", "Q"), Tok(";"));
            Line(InstanceCall("tOn", ("IN", Local("Enable")), ("PT", TypedConst("T#2S"))), Tok(";"));
            Line(Local("TimerDone"), Blank(), Tok(":="), Blank(), LocalField("tOn", "Q"), Tok(";"));
            Line(Local("Elapsed"), Blank(), Tok(":="), Blank(), LocalField("tOn", "ET"), Tok(";"));

            File.WriteAllText(Path.Combine(dir, "MCP_Syntax_Iec_FB.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.FB ID=""0"">
    <AttributeList>
      <HeaderVersion>1.0</HeaderVersion>
      <Interface>
        <Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5"">
          <Section Name=""Input""><Member Name=""Enable"" Datatype=""Bool"" /><Member Name=""Pulse"" Datatype=""Bool"" /></Section>
          <Section Name=""Output""><Member Name=""Rising"" Datatype=""Bool"" /><Member Name=""Falling"" Datatype=""Bool"" /><Member Name=""TimerDone"" Datatype=""Bool"" /><Member Name=""Elapsed"" Datatype=""Time"" /></Section>
          <Section Name=""InOut"" />
          <Section Name=""Static"">
            <Member Name=""rEdge"" Datatype=""R_TRIG"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member>
            <Member Name=""fEdge"" Datatype=""F_TRIG"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member>
            <Member Name=""tOn"" Datatype=""TON_TIME"" Version=""1.0""><AttributeList><BooleanAttribute Name=""SetPoint"" SystemDefined=""true"">true</BooleanAttribute></AttributeList></Member>
          </Section>
          <Section Name=""Temp"" />
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>MCP_Syntax_Iec_FB</Name><Namespace /><Number>12002</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>", Encoding.UTF8);
        }

        private static void WritePlcSyntaxValidationScl(string path)
        {
            File.WriteAllText(path, @"TYPE ""MCP_Syntax_UDT""
VERSION : 0.1
   STRUCT
      Flag : Bool;
      Count : Int;
      Level : Real;
      Stamp : Time;
   END_STRUCT;
END_TYPE

FUNCTION ""MCP_Syntax_CalcInt"" : Int
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   A : Int;
   B : Int;
END_VAR
BEGIN
   ""MCP_Syntax_CalcInt"" := #A + #B;
END_FUNCTION

FUNCTION ""MCP_Syntax_Basics"" : Void
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   Enable : Bool;
   Mode : Int;
   InInt : Int;
   InReal : Real;
END_VAR
VAR_OUTPUT
   OutBool : Bool;
   OutInt : Int;
   OutDInt : DInt;
   OutReal : Real;
   OutWord : Word;
END_VAR
VAR_TEMP
   i : Int;
   acc : Int;
   localArray : Array[0..9] of Int;
   data : ""MCP_Syntax_UDT"";
END_VAR
BEGIN
   // Assignment, boolean logic, comparison, arithmetic, and type conversion.
   #OutBool := #Enable AND (#InInt >= 0) OR (#Mode = 2);
   #OutInt := #InInt + 1;
   #OutDInt := INT_TO_DINT(#OutInt);
   #OutReal := DINT_TO_REAL(#OutDInt) / 10.0;
   #OutWord := INT_TO_WORD(#OutInt);

   // Common scalar functions.
   #OutInt := LIMIT(MN := -100, IN := #OutInt, MX := 100);
   #OutReal := ABS(#InReal);

   // IF / ELSIF / ELSE.
   IF NOT #Enable THEN
      #OutInt := 0;
   ELSIF #Mode = 1 THEN
      #OutInt := #OutInt + 10;
   ELSE
      #OutInt := #OutInt + 20;
   END_IF;

   // CASE with single values, value list, and range.
   CASE #Mode OF
      0:
         #OutInt := 0;
      1, 2:
         #OutInt := #OutInt + 1;
      3..5:
         #OutInt := #OutInt + 3;
   ELSE
      #OutInt := -1;
   END_CASE;

   // FOR loop and array indexing.
   #acc := 0;
   FOR #i := 0 TO 9 DO
      #localArray[#i] := #i;
      #acc := #acc + #localArray[#i];
   END_FOR;

   // WHILE loop.
   #i := 0;
   WHILE #i < 3 DO
      #acc := #acc + #i;
      #i := #i + 1;
   END_WHILE;

   // REPEAT loop.
   REPEAT
      #acc := #acc - 1;
   UNTIL #acc <= 0
   END_REPEAT;

   // UDT/STRUCT field access.
   #data.Flag := #OutBool;
   #data.Count := #OutInt;
   #data.Level := #OutReal;
   #data.Stamp := T#1s;
END_FUNCTION

FUNCTION_BLOCK ""MCP_Syntax_Iec""
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   Enable : Bool;
   Reset : Bool;
   Pulse : Bool;
END_VAR
VAR_OUTPUT
   Rising : Bool;
   Falling : Bool;
   TimerDone : Bool;
   Elapsed : Time;
   CountValue : Int;
END_VAR
VAR
   tOn : TON;
   cUp : CTU;
   rEdge : R_TRIG;
   fEdge : F_TRIG;
END_VAR
BEGIN
   // IEC multi-instance calls. Inputs use :=, outputs are read from instance members.
   #rEdge(CLK := #Pulse);
   #fEdge(CLK := #Pulse);
   #Rising := #rEdge.Q;
   #Falling := #fEdge.Q;

   #tOn(IN := #Enable, PT := T#2s);
   #TimerDone := #tOn.Q;
   #Elapsed := #tOn.ET;

   #cUp(CU := #rEdge.Q, R := #Reset, PV := 10);
   #CountValue := #cUp.CV;
END_FUNCTION_BLOCK

FUNCTION_BLOCK ""MCP_Syntax_Caller""
{ S7_Optimized_Access := 'TRUE' }
VAR_INPUT
   Enable : Bool;
   Reset : Bool;
   Pulse : Bool;
   A : Int;
   B : Int;
END_VAR
VAR_OUTPUT
   Done : Bool;
   Sum : Int;
   CountValue : Int;
END_VAR
VAR
   iec : ""MCP_Syntax_Iec"";
END_VAR
BEGIN
   // Function call with named parameters and return value.
   #Sum := ""MCP_Syntax_CalcInt""(A := #A, B := #B);

   // FB multi-instance call with output parameter assignment.
   #iec(Enable := #Enable,
        Reset := #Reset,
        Pulse := #Pulse,
        TimerDone => #Done,
        CountValue => #CountValue);
END_FUNCTION_BLOCK

DATA_BLOCK ""MCP_Syntax_DB""
{ S7_Optimized_Access := 'TRUE' }
VAR
   Enable : Bool := TRUE;
   Value : Int := 10;
   Data : ""MCP_Syntax_UDT"";
   Caller : ""MCP_Syntax_Caller"";
END_VAR
BEGIN
END_DATA_BLOCK
", Encoding.UTF8);
        }

        private static void WriteFlowLightPlcXml(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "FlowLightTags.xml"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Tags.PlcTagTable ID=""0"">
    <AttributeList><Name>FlowLightTags</Name></AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID=""1"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Flow_Enable</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""2"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.5</LogicalAddress><Name>Clock_1Hz</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""3"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M1.0</LogicalAddress><Name>Clock_Last</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""4"" CompositionName=""Tags""><AttributeList><DataTypeName>Int</DataTypeName><LogicalAddress>%MW2</LogicalAddress><Name>Flow_Step</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""5"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.0</LogicalAddress><Name>Light_1</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""6"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.1</LogicalAddress><Name>Light_2</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""7"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.2</LogicalAddress><Name>Light_3</Name></AttributeList></SW.Tags.PlcTag>
      <SW.Tags.PlcTag ID=""8"" CompositionName=""Tags""><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.3</LogicalAddress><Name>Light_4</Name></AttributeList></SW.Tags.PlcTag>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>");

            var uid = 21;
            string U() => (uid++).ToString();
            string Tok(string text) => $"<Token Text=\"{System.Security.SecurityElement.Escape(text)}\" UId=\"{U()}\" />";
            string Blank(int n = 1) => n == 1 ? $"<Blank UId=\"{U()}\" />" : $"<Blank Num=\"{n}\" UId=\"{U()}\" />";
            string NL() => $"<NewLine UId=\"{U()}\" />";
            string Global(string name) => $"<Access Scope=\"GlobalVariable\" UId=\"{U()}\"><Symbol UId=\"{U()}\"><Component Name=\"{System.Security.SecurityElement.Escape(name)}\" UId=\"{U()}\"><BooleanAttribute Name=\"HasQuotes\" UId=\"{U()}\">true</BooleanAttribute></Component></Symbol></Access>";
            string Const(string value) => $"<Access Scope=\"LiteralConstant\" UId=\"{U()}\"><Constant UId=\"{U()}\"><ConstantValue UId=\"{U()}\">{System.Security.SecurityElement.Escape(value)}</ConstantValue></Constant></Access>";

            var st = new System.Text.StringBuilder();
            void Line(params string[] parts) { foreach (var p in parts) st.AppendLine(p); st.AppendLine(NL()); }

            Line(Tok("IF"), Blank(), Tok("NOT"), Blank(), Global("Flow_Enable"), Blank(), Tok("THEN"));
            Line(Blank(2), Global("Flow_Step"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Tok("ELSIF"), Blank(), Global("Clock_1Hz"), Blank(), Tok("AND"), Blank(), Tok("NOT"), Blank(), Global("Clock_Last"), Blank(), Tok("THEN"));
            Line(Blank(2), Tok("IF"), Blank(), Global("Flow_Step"), Blank(), Tok(">="), Blank(), Const("3"), Blank(), Tok("THEN"));
            Line(Blank(4), Global("Flow_Step"), Blank(), Tok(":="), Blank(), Const("0"), Tok(";"));
            Line(Blank(2), Tok("ELSE"));
            Line(Blank(4), Global("Flow_Step"), Blank(), Tok(":="), Blank(), Global("Flow_Step"), Blank(), Tok("+"), Blank(), Const("1"), Tok(";"));
            Line(Blank(2), Tok("END_IF"), Tok(";"));
            Line(Tok("END_IF"), Tok(";"));
            Line(Global("Clock_Last"), Blank(), Tok(":="), Blank(), Global("Clock_1Hz"), Tok(";"));
            for (var i = 1; i <= 4; i++)
            {
                Line(Global($"Light_{i}"), Blank(), Tok(":="), Blank(), Global("Flow_Enable"), Blank(), Tok("AND"), Blank(), Tok("("), Global("Flow_Step"), Blank(), Tok("="), Blank(), Const((i - 1).ToString()), Tok(")"), Tok(";"));
            }

            File.WriteAllText(Path.Combine(dir, "Main.xml"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo><Created>2000-01-01T00:00:00.0000000Z</Created><ExportSetting>None</ExportSetting><InstalledProducts /></DocumentInfo>
  <SW.Blocks.OB ID=""0"">
    <AttributeList>
      <Interface><Sections xmlns=""http://www.siemens.com/automation/Openness/SW/Interface/v5""><Section Name=""Input""><Member Name=""Initial_Call"" Datatype=""Bool"" Informative=""true"" /><Member Name=""Remanence"" Datatype=""Bool"" Informative=""true"" /></Section><Section Name=""Temp"" /><Section Name=""Constant"" /></Sections></Interface>
      <MemoryLayout>Optimized</MemoryLayout><Name>Main</Name><Namespace /><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><SecondaryType>ProgramCycle</SecondaryType><SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""1"" CompositionName=""CompileUnits"">
        <AttributeList><NetworkSource><StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
{st}
        </StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.OB>
</Document>");
        }

        private static void WriteClassicHmiSymbolicTagTableProbeXml(string path, string tableName, string connectionName)
        {
            File.WriteAllText(path, $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <DocumentInfo>
    <Created>2000-01-01T00:00:00.0000000Z</Created>
    <ExportSetting>None</ExportSetting>
    <InstalledProducts />
  </DocumentInfo>
  <Hmi.Tag.TagTable ID=""0"">
    <AttributeList>
      <Name>{SecurityElement.Escape(tableName)}</Name>
    </AttributeList>
    <ObjectList>
{ClassicHmiSymbolicTagXml("1", "Motor_Start", "Bool", "1", connectionName, "DB1_MotorData.Motor.Start")}
{ClassicHmiSymbolicTagXml("2", "Motor_Stop", "Bool", "1", connectionName, "DB1_MotorData.Motor.Stop")}
{ClassicHmiSymbolicTagXml("3", "Motor_Run", "Bool", "1", connectionName, "DB1_MotorData.Motor.Run")}
{ClassicHmiSymbolicTagXml("4", "Motor_Fault", "Bool", "1", connectionName, "DB1_MotorData.Motor.Fault")}
{ClassicHmiSymbolicTagXml("5", "Counter", "Int", "2", connectionName, "DB1_MotorData.Counter")}
    </ObjectList>
  </Hmi.Tag.TagTable>
</Document>
", Encoding.UTF8);
        }

        private static string ClassicHmiSymbolicTagXml(string id, string name, string dataType, string length, string connectionName, string controllerTag)
        {
            return $@"      <Hmi.Tag.Tag ID=""{SecurityElement.Escape(id)}"" CompositionName=""Tags"">
        <AttributeList>
          <AcquisitionTriggerMode>Visible</AcquisitionTriggerMode>
          <AddressAccessMode>Symbolic</AddressAccessMode>
          <Length>{SecurityElement.Escape(length)}</Length>
          <LogicalAddress />
          <Name>{SecurityElement.Escape(name)}</Name>
        </AttributeList>
        <LinkList>
          <AcquisitionCycle TargetID=""@OpenLink"">
            <Name>1 s</Name>
          </AcquisitionCycle>
          <Connection TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(connectionName)}</Name>
          </Connection>
          <ControllerTag TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(controllerTag)}</Name>
          </ControllerTag>
          <DataType TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(dataType)}</Name>
          </DataType>
          <HmiDataType TargetID=""@OpenLink"">
            <Name>{SecurityElement.Escape(dataType)}</Name>
          </HmiDataType>
        </LinkList>
      </Hmi.Tag.Tag>";
        }

        private static void RunValidateUnifiedHmiTemplates(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_HMI_Template_Validation_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var templateDirectory = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "hmi_templates")
                : options.HmiTemplateDirectory!;

            Directory.CreateDirectory(projectDirectory);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "unified_hmi_template_validation.md");
            var jsonReportPath = Path.Combine(reportDir, "unified_hmi_template_validation.json");

            LogDiag($"Unified HMI template validation: directory={projectDirectory}, project={projectName}, templates={templateDirectory}");

            var connect = McpServer.Connect();
            LogDiag(connect.Message ?? "Connect completed");
            var create = McpServer.CreateProject(projectDirectory, projectName);
            LogDiag(create.Message ?? "Project created");

            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            LogDiag($"HMI add for template validation: ok={hmi.Ok}, used={hmi.MlfbUsed}/{hmi.VersionUsed}, error={hmi.Error}");
            if (hmi.Ok != true)
            {
                throw new InvalidOperationException("Failed to add Unified HMI for template validation. Last error: " + hmi.Error);
            }

            var templateFiles = Directory.Exists(templateDirectory)
                ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(path).Equals("MotorUnifiedDesign_DemoSlate.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            if (templateFiles.Length == 0)
            {
                throw new InvalidOperationException("No Unified HMI template JSON files found in: " + templateDirectory);
            }

            var results = new List<Dictionary<string, object?>>();
            foreach (var templateFile in templateFiles)
            {
                var screenName = "Tpl_" + Regex.Replace(Path.GetFileNameWithoutExtension(templateFile), @"[^A-Za-z0-9_]+", "_");
                if (screenName.StartsWith("Tpl_unified_", StringComparison.OrdinalIgnoreCase))
                {
                    screenName = "Tpl_" + screenName.Substring("Tpl_unified_".Length);
                }
                if (screenName.Length > 44) screenName = screenName.Substring(0, 44);

                var result = new Dictionary<string, object?>
                {
                    ["template"] = templateFile,
                    ["screen"] = screenName,
                    ["applied"] = false,
                    ["screenReadback"] = false,
                    ["error"] = ""
                };

                try
                {
                    var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(templateFile, 800, 480);
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480);
                    var apply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", screenName, designJson);
                    var applyFailureCount = CountHmiApplyFailures(apply.Meta);
                    result["applied"] = true;
                    result["message"] = apply.Message ?? "";
                    result["meta"] = apply.Meta?.ToJsonString() ?? "";
                    result["applyFailures"] = applyFailureCount;
                    if (applyFailureCount > 0)
                    {
                        result["error"] = "ApplyUnifiedHmiScreenDesignJson reported failed writes: " + applyFailureCount;
                    }

                    var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                    result["screenReadback"] = screens.Any(s => string.Equals(s, screenName, StringComparison.OrdinalIgnoreCase));
                    result["screenCount"] = screens.Length;
                    LogDiag($"Template {Path.GetFileName(templateFile)}: readback={result["screenReadback"]}, screen={screenName}");
                }
                catch (Exception ex)
                {
                    result["error"] = ex.InnerException?.Message ?? ex.Message;
                    LogDiag($"Template {Path.GetFileName(templateFile)} failed: {result["error"]}");
                }

                results.Add(result);
            }

            var save = McpServer.SaveProject();
            LogDiag(save.Message ?? "Project saved");

            var failed = results.Where(r => !Equals(r["applied"], true) || !Equals(r["screenReadback"], true) || (r.TryGetValue("applyFailures", out var af) && Convert.ToInt32(af ?? 0) > 0) || !string.IsNullOrWhiteSpace(r["error"]?.ToString())).ToList();
            var markdown = new StringBuilder();
            markdown.AppendLine("# Unified HMI Template Validation");
            markdown.AppendLine();
            markdown.AppendLine("- Project: `" + projectName + "`");
            markdown.AppendLine("- ProjectDirectory: `" + projectDirectory + "`");
            markdown.AppendLine("- TemplateDirectory: `" + templateDirectory + "`");
            markdown.AppendLine("- Result: `" + (failed.Count == 0 ? "PASS" : "FAIL") + "`");
            markdown.AppendLine();
            markdown.AppendLine("| Template | Screen | Applied | Readback | Apply failures | Error |");
            markdown.AppendLine("|---|---|---|---|---:|---|");
            foreach (var r in results)
            {
                markdown.AppendLine("| " + Path.GetFileName(r["template"]?.ToString() ?? "") + " | " + r["screen"] + " | " + r["applied"] + " | " + r["screenReadback"] + " | " + (r.TryGetValue("applyFailures", out var applyFailures) ? applyFailures : 0) + " | " + (r["error"]?.ToString() ?? "").Replace("|", "\\|") + " |");
            }
            File.WriteAllText(reportPath, markdown.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonReportPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                projectName,
                projectDirectory,
                templateDirectory,
                passed = failed.Count == 0,
                results
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            LogDiag("Unified HMI template validation report: " + reportPath);
            if (failed.Count > 0)
            {
                throw new InvalidOperationException("Unified HMI template validation failed. Report: " + reportPath);
            }
        }

        private static void RunValidateUnifiedHmiTemplateBindings(CliOptions options)
        {
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_HMI_Template_Binding_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var templateDirectory = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "hmi_templates")
                : options.HmiTemplateDirectory!;
            Directory.CreateDirectory(projectDirectory);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "unified_hmi_template_binding_validation.md");
            var jsonReportPath = Path.Combine(reportDir, "unified_hmi_template_binding_validation.json");

            LogDiag($"Unified HMI template binding validation: directory={projectDirectory}, project={projectName}, templates={templateDirectory}");
            McpServer.Connect();
            McpServer.CreateProject(projectDirectory, projectName);
            var plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
            if (plc.Ok != true) throw new InvalidOperationException("Failed to add PLC for HMI binding validation: " + plc.Error);
            var hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
            if (hmi.Ok != true) throw new InvalidOperationException("Failed to add Unified HMI for binding validation: " + hmi.Error);

            var connectionName = "HMI_Connection_1";
            McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1");
            McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "Template_Binding_Tags");

            var templates = Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = new List<Dictionary<string, object?>>();

            foreach (var template in templates)
            {
                var screenName = "Bind_" + Regex.Replace(Path.GetFileNameWithoutExtension(template).Replace("unified_", ""), @"[^A-Za-z0-9_]+", "_");
                if (screenName.Length > 44) screenName = screenName.Substring(0, 44);
                var currentTags = Array.Empty<HmiTemplateTagSpec>();
                var result = new Dictionary<string, object?>
                {
                    ["template"] = template,
                    ["screen"] = screenName,
                    ["screenReadback"] = false,
                    ["hmiTagsCreated"] = 0,
                    ["bindingsAttempted"] = 0,
                    ["bindingsSucceeded"] = 0,
                    ["eventsAttempted"] = 0,
                    ["eventsSucceeded"] = 0,
                    ["eventReadbacks"] = 0,
                    ["errors"] = new List<string>()
                };

                try
                {
                    var json = File.ReadAllText(template, Encoding.UTF8);
                    var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
                    var requiredTags = ReadHmiTemplateTags(root);
                    currentTags = requiredTags;
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480);
                    var apply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", screenName, HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(template, 800, 480));
                    var applyFailures = CountHmiApplyFailures(apply.Meta);
                    result["applyFailures"] = applyFailures;
                    if (applyFailures > 0)
                    {
                        ((List<string>)result["errors"]!).Add("ApplyUnifiedHmiScreenDesignJson reported failed writes: " + applyFailures);
                        results.Add(result);
                        continue;
                    }
                    var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                    result["screenReadback"] = screens.Any(s => string.Equals(s, screenName, StringComparison.OrdinalIgnoreCase));

                    foreach (var tag in requiredTags)
                    {
                        McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Template_Binding_Tags", tag.Name, tag.DataType, "PLC_1", tag.PlcTag, connectionName, tag.Address);
                        result["hmiTagsCreated"] = (int)result["hmiTagsCreated"]! + 1;
                    }

                    var tagNames = requiredTags.Select(x => x.Name).ToArray();
                    var items = (root["Items"] as JsonArray ?? root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
                    foreach (var item in items)
                    {
                        var type = item["Type"]?.ToString() ?? item["type"]?.ToString() ?? "";
                        var name = item["Name"]?.ToString() ?? item["name"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var dyn in ReadTemplateDynamizations(item))
                        {
                            if (tagNames.Contains(dyn.Tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryBind(name, dyn.PropertyName, dyn.Tag);
                            }
                        }

                        foreach (var action in ReadTemplateItemActions(item))
                        {
                            var tag = action.TargetTag;
                            if (string.IsNullOrWhiteSpace(tag))
                            {
                                tag = ExtractFirstRuntimeTag(action.Script);
                            }
                            if (tagNames.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryButtonEvent(name, action.Event, tag, action.Script);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add(ex.InnerException?.Message ?? ex.Message);
                }

                results.Add(result);

                void TryBind(string itemName, string propertyName, string tagName)
                {
                    result["bindingsAttempted"] = (int)result["bindingsAttempted"]! + 1;
                    try
                    {
                        var tag = currentTags.FirstOrDefault(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase));
                        McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", screenName, itemName, propertyName, tagName, tag?.DataType ?? "Bool", tag?.PlcTag ?? "", "");
                        McpServer.DescribeHmiScreenItem("HMI_RT_1", screenName, itemName, 80);
                        result["bindingsSucceeded"] = (int)result["bindingsSucceeded"]! + 1;
                    }
                    catch (Exception ex)
                    {
                        ((List<string>)result["errors"]!).Add($"{itemName}.{propertyName}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                void TryButtonEvent(string buttonName, string eventType, string tagName, string scriptCode)
                {
                    result["eventsAttempted"] = (int)result["eventsAttempted"]! + 1;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(eventType)) eventType = "Tapped";
                        if (string.IsNullOrWhiteSpace(scriptCode))
                        {
                            scriptCode = $"HMIRuntime.Tags.SysFct.SetBitInTag(\"{tagName}\", 0);";
                        }
                        McpServer.EnsureUnifiedHmiButtonEventHandler("HMI_RT_1", screenName, buttonName, eventType);
                        McpServer.SetUnifiedHmiButtonEventScriptCode("HMI_RT_1", screenName, buttonName, eventType, scriptCode, "", false);
                        var readback = McpServer.DescribeUnifiedHmiButtonEventScript("HMI_RT_1", screenName, buttonName, eventType, 80);
                        if ((readback.Members?.Any() ?? false) || !string.IsNullOrWhiteSpace(readback.Message))
                        {
                            result["eventReadbacks"] = (int)result["eventReadbacks"]! + 1;
                        }
                        result["eventsSucceeded"] = (int)result["eventsSucceeded"]! + 1;
                    }
                    catch (Exception ex)
                    {
                        ((List<string>)result["errors"]!).Add($"{buttonName}.{eventType}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }

            McpServer.SaveProject();
            var failed = results.Where(r => !(bool)r["screenReadback"]! || (r.TryGetValue("applyFailures", out var af) && Convert.ToInt32(af ?? 0) > 0) || (int)r["hmiTagsCreated"]! == 0 || (int)r["bindingsAttempted"]! != (int)r["bindingsSucceeded"]! || (int)r["eventsAttempted"]! != (int)r["eventsSucceeded"]!).ToList();
            WriteBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, failed.Count == 0, results);
            if (failed.Count > 0)
            {
                throw new InvalidOperationException("Unified HMI template binding validation failed. Report: " + reportPath);
            }

            void WriteBindingReport(string mdPath, string jsonPath, string projName, string projDir, string tplDir, bool passed, List<Dictionary<string, object?>> rows)
            {
                var md = new StringBuilder();
                md.AppendLine("# Unified HMI Template Binding Validation");
                md.AppendLine();
                md.AppendLine("- Project: `" + projName + "`");
                md.AppendLine("- ProjectDirectory: `" + projDir + "`");
                md.AppendLine("- TemplateDirectory: `" + tplDir + "`");
                md.AppendLine("- Result: `" + (passed ? "PASS" : "FAIL") + "`");
                md.AppendLine();
                md.AppendLine("| Template | Screen | Apply failures | Tags | Bindings | Events | Event Readback | Errors |");
                md.AppendLine("|---|---|---:|---:|---:|---:|---:|---|");
                foreach (var r in rows)
                {
                    var errs = string.Join("<br>", ((List<string>)r["errors"]!).Select(e => e.Replace("|", "\\|")));
                    md.AppendLine("| " + Path.GetFileName(r["template"]?.ToString() ?? "") + " | " + r["screen"] + " | " + (r.TryGetValue("applyFailures", out var applyFailures) ? applyFailures : 0) + " | " + r["hmiTagsCreated"] + " | " + r["bindingsSucceeded"] + "/" + r["bindingsAttempted"] + " | " + r["eventsSucceeded"] + "/" + r["eventsAttempted"] + " | " + r["eventReadbacks"] + " | " + errs + " |");
                }
                File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
                File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new { projectName = projName, projectDirectory = projDir, templateDirectory = tplDir, passed, results = rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }

        private sealed class HmiTemplateTagSpec
        {
            public string Name { get; set; } = "";
            public string DataType { get; set; } = "Bool";
            public string PlcTag { get; set; } = "";
            public string Address { get; set; } = "";
        }

        private sealed class HmiTemplateDynamizationSpec
        {
            public string PropertyName { get; set; } = "";
            public string Tag { get; set; } = "";
        }

        private sealed class HmiTemplateActionSpec
        {
            public string Event { get; set; } = "Tapped";
            public string TargetTag { get; set; } = "";
            public string Script { get; set; } = "";
        }

        private static HmiTemplateTagSpec[] ReadHmiTemplateTags(JsonObject root)
        {
            var required = root["RequiredTags"] as JsonArray ?? root["requiredTags"] as JsonArray ?? new JsonArray();
            return required.OfType<JsonObject>()
                .Select(tag => new HmiTemplateTagSpec
                {
                    Name = tag["Name"]?.ToString() ?? tag["name"]?.ToString() ?? "",
                    DataType = tag["DataType"]?.ToString() ?? tag["dataType"]?.ToString() ?? "Bool",
                    PlcTag = tag["PlcTag"]?.ToString() ?? tag["plcTag"]?.ToString() ?? "",
                    Address = tag["Address"]?.ToString() ?? tag["address"]?.ToString() ?? ""
                })
                .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
                .ToArray();
        }

        private static HmiTemplateDynamizationSpec[] ReadTemplateDynamizations(JsonObject item)
        {
            var result = new List<HmiTemplateDynamizationSpec>();
            var dyns = item["Dynamizations"] as JsonObject ?? item["dynamizations"] as JsonObject;
            if (dyns == null) return result.ToArray();

            foreach (var dyn in dyns)
            {
                if (dyn.Value is not JsonObject cfg) continue;
                var property = dyn.Key;
                var suffix = ".TagDynamization";
                if (property.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    property = property.Substring(0, property.Length - suffix.Length);
                }

                var tag = cfg["Tag"]?.ToString() ?? cfg["tag"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(property) || string.IsNullOrWhiteSpace(tag)) continue;
                result.Add(new HmiTemplateDynamizationSpec { PropertyName = property, Tag = tag });
            }

            return result.ToArray();
        }

        private static HmiTemplateActionSpec[] ReadTemplateItemActions(JsonObject item)
        {
            var actions = item["Actions"] as JsonArray ?? item["actions"] as JsonArray ?? new JsonArray();
            return actions.OfType<JsonObject>()
                .Select(action => new HmiTemplateActionSpec
                {
                    Event = action["Event"]?.ToString() ?? action["event"]?.ToString() ?? "Tapped",
                    TargetTag = action["TargetTag"]?.ToString() ?? action["targetTag"]?.ToString() ?? "",
                    Script = action["Script"]?.ToString() ?? action["script"]?.ToString() ?? ""
                })
                .ToArray();
        }

        private static string ExtractFirstRuntimeTag(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return "";
            var match = Regex.Match(script, @"Tags\.SysFct\.\w+\(\s*""([^""]+)""", RegexOptions.IgnoreCase);
            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : "";
        }

        private static int CountHmiApplyFailures(JsonObject? meta)
        {
            if (meta == null) return 0;
            if (meta["success"] is JsonValue successValue && successValue.TryGetValue<bool>(out var success) && !success) return 1;
            if (meta["failed"] is JsonArray failed) return failed.Count;
            return 0;
        }

        private static void RunValidateMappedHmiTemplateBindings(CliOptions options)
        {
            var workspaceRoot = Directory.GetCurrentDirectory();
            var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation")
                : options.ProjectDirectory!;
            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? "MCP_Mapped_HMI_Template_Binding_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : options.ProjectName!;
            var templateDirectory = string.IsNullOrWhiteSpace(options.HmiTemplateDirectory)
                ? Path.Combine(workspaceRoot, "docs", "hmi_templates")
                : options.HmiTemplateDirectory!;
            var plcExportDirectory = options.PlcExportDirectory ?? "";
            var mappingPath = options.HmiTemplateMappingPath ?? "";
            var tiaStepTimeoutSeconds = Math.Max(15, options.TiaStepTimeoutSeconds ?? 90);

            if (string.IsNullOrWhiteSpace(mappingPath))
            {
                throw new InvalidOperationException("--hmi-template-mapping-path is required for mapped HMI template binding validation.");
            }

            Directory.CreateDirectory(projectDirectory);
            var reportDir = Path.Combine(projectDirectory, projectName + "_reports");
            Directory.CreateDirectory(reportDir);
            var mappedTemplateDir = Path.Combine(reportDir, "mapped_templates");
            Directory.CreateDirectory(mappedTemplateDir);
            var reportPath = Path.Combine(reportDir, "mapped_hmi_template_binding_validation.md");
            var jsonReportPath = Path.Combine(reportDir, "mapped_hmi_template_binding_validation.json");

            var templates = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, "", "");
            var templateArray = templates["templates"] as JsonArray ?? new JsonArray();
            var mappingFile = LoadHmiTemplateMappingFile(mappingPath);
            var effectiveTemplates = ApplyHmiTemplateMapping(templateArray, mappingFile);
            var plcExportCatalog = AnalyzePlcExportDirectory(plcExportDirectory);
            var plcSymbolCatalog = BuildPlcSymbolCatalog(plcExportCatalog);
            var plcSymbols = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in (plcExportCatalog["symbols"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                plcSymbols.Add(symbol);
            }
            var precheck = BuildHmiTemplateSyncPrecheck(effectiveTemplates, plcSymbols, plcSymbolCatalog);
            var readyTemplateNames = new HashSet<string>(
                precheck
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["status"]?.ToString(), "plc-symbols-present", StringComparison.OrdinalIgnoreCase)
                                && TemplateAllRequiredTagsMapped(effectiveTemplates, x["templateName"]?.ToString() ?? ""))
                    .Select(x => x["templateName"]?.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase);

            var templateFiles = Directory.Exists(templateDirectory)
                ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            var mappedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<Dictionary<string, object?>>();

            foreach (var templateNode in effectiveTemplates.OfType<JsonObject>())
            {
                var templateName = templateNode["templateName"]?.ToString() ?? "";
                var templateFile = templateNode["file"]?.ToString() ?? "";
                var precheckRow = precheck.OfType<JsonObject>().FirstOrDefault(x => string.Equals(x["templateName"]?.ToString(), templateName, StringComparison.OrdinalIgnoreCase));
                if (!readyTemplateNames.Contains(templateName))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["templateName"] = templateName,
                        ["template"] = templateFile,
                        ["screen"] = "",
                        ["status"] = "skipped",
                        ["reason"] = TemplateAllRequiredTagsMapped(effectiveTemplates, templateName)
                            ? "PLC symbol precheck failed."
                            : "RequiredTags are not fully mapped by explicit mapping file.",
                        ["precheckStatus"] = precheckRow?["status"]?.ToString() ?? "",
                        ["missingPlcSymbols"] = (precheckRow?["missingRootSymbols"] as JsonArray)?.Count ?? 0,
                        ["hmiTagsCreated"] = 0,
                        ["bindingsAttempted"] = 0,
                        ["bindingsSucceeded"] = 0,
                        ["eventsAttempted"] = 0,
                        ["eventsSucceeded"] = 0,
                        ["eventReadbacks"] = 0,
                        ["errors"] = new List<string>()
                    });
                    continue;
                }

                var originalPath = templateFiles.FirstOrDefault(path => string.Equals(path, templateFile, StringComparison.OrdinalIgnoreCase))
                                   ?? templateFiles.FirstOrDefault(path => string.Equals(ReadTemplateNameFromFile(path), templateName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(originalPath))
                {
                    results.Add(new Dictionary<string, object?>
                    {
                        ["templateName"] = templateName,
                        ["template"] = templateFile,
                        ["screen"] = "",
                        ["status"] = "failed",
                        ["reason"] = "Template file not found.",
                        ["precheckStatus"] = precheckRow?["status"]?.ToString() ?? "",
                        ["missingPlcSymbols"] = 0,
                        ["hmiTagsCreated"] = 0,
                        ["bindingsAttempted"] = 0,
                        ["bindingsSucceeded"] = 0,
                        ["eventsAttempted"] = 0,
                        ["eventsSucceeded"] = 0,
                        ["eventReadbacks"] = 0,
                        ["errors"] = new List<string> { "Template file not found for " + templateName }
                    });
                    continue;
                }

                var mappedPath = Path.Combine(mappedTemplateDir, Path.GetFileNameWithoutExtension(originalPath) + ".mapped.json");
                WriteMappedTemplateFile(originalPath, templateNode, mappedPath);
                mappedFiles[templateName] = mappedPath;
                results.Add(new Dictionary<string, object?>
                {
                    ["templateName"] = templateName,
                    ["template"] = mappedPath,
                    ["screen"] = "",
                    ["status"] = options.MappedHmiTemplateOfflineOnly ? "offline-gate-pass" : "ready-for-tia-validation",
                    ["reason"] = "Explicit mapping and PLC export full-symbol precheck passed.",
                    ["precheckStatus"] = precheckRow?["status"]?.ToString() ?? "",
                    ["missingPlcSymbols"] = 0,
                    ["hmiTagsCreated"] = 0,
                    ["bindingsAttempted"] = 0,
                    ["bindingsSucceeded"] = 0,
                    ["eventsAttempted"] = 0,
                    ["eventsSucceeded"] = 0,
                    ["eventReadbacks"] = 0,
                    ["errors"] = new List<string>()
                });
            }

            if (mappedFiles.Count == 0)
            {
                WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, "No fully mapped template passed PLC symbol precheck.", mappingFile, plcExportCatalog, precheck, results);
                throw new InvalidOperationException("No fully mapped template passed PLC symbol precheck. Report: " + reportPath);
            }

            if (options.MappedHmiTemplateOfflineOnly)
            {
                WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, true, "Offline mapped-template gate passed. TIA temporary-project validation was intentionally skipped.", mappingFile, plcExportCatalog, precheck, results);
                return;
            }

            WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, "PLC export precheck passed for mapped templates; TIA temporary-project validation is about to start.", mappingFile, plcExportCatalog, precheck, results);
            LogDiag($"Mapped HMI template binding validation: project={projectName}, mappedTemplates={mappedFiles.Count}");
            if (!TryRunMappedTiaStep("Connect", tiaStepTimeoutSeconds, () => McpServer.Connect(), results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at TIA Connect. Report: " + reportPath);
            }
            if (!TryRunMappedTiaStep("CreateProject", tiaStepTimeoutSeconds, () => McpServer.CreateProject(projectDirectory, projectName), results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at CreateProject. Report: " + reportPath);
            }
            ResponseDeviceProbe plc = null!;
            if (!TryRunMappedTiaStep("Add PLC", tiaStepTimeoutSeconds, () =>
                {
                    plc = McpServer.AddDeviceWithFallback("6ES7211-1AE40-0XB0", "V4.7", "PLC_1", "S7-1200");
                    if (plc.Ok != true) throw new InvalidOperationException("Failed to add PLC for mapped HMI binding validation: " + plc.Error);
                    return plc;
                }, results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at Add PLC. Report: " + reportPath);
            }
            LogDiag($"Mapped HMI template binding validation: Add PLC completed ok={plc.Ok}, used={plc.MlfbUsed}/{plc.VersionUsed}");
            ResponseDeviceProbe hmi = null!;
            if (!TryRunMappedTiaStep("Add HMI", tiaStepTimeoutSeconds, () =>
                {
                    hmi = McpServer.AddDeviceWithFallback("OrderNumber:6AV2 123-3GB32-0AW0/21.0.0.0", "", "HMI_RT_1", "WinCCUnifiedPC");
                    if (hmi.Ok != true) throw new InvalidOperationException("Failed to add Unified HMI for mapped HMI binding validation: " + hmi.Error);
                    return hmi;
                }, results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at Add HMI. Report: " + reportPath);
            }
            LogDiag($"Mapped HMI template binding validation: Add HMI completed ok={hmi.Ok}, used={hmi.MlfbUsed}/{hmi.VersionUsed}");

            var connectionName = "Mapped_HMI_Connection_1";
            if (!TryRunMappedTiaStep("Ensure HMI connection/tag table", tiaStepTimeoutSeconds, () =>
                {
                    McpServer.EnsureUnifiedHmiConnection("HMI_RT_1", connectionName, "PLC_1");
                    McpServer.EnsureUnifiedHmiTagTable("HMI_RT_1", "Mapped_Template_Tags");
                    return "ok";
                }, results, reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, mappingFile, plcExportCatalog, precheck))
            {
                throw new TimeoutException("Mapped HMI template binding validation timed out or failed at HMI connection/tag table setup. Report: " + reportPath);
            }

            foreach (var kv in mappedFiles)
            {
                ValidateOneMappedTemplate(kv.Key, kv.Value, connectionName, results);
            }

            McpServer.SaveProject();
            var failed = results.Where(r => string.Equals(r["status"]?.ToString(), "failed", StringComparison.OrdinalIgnoreCase)).ToList();
            var passed = failed.Count == 0 && results.Any(r => string.Equals(r["status"]?.ToString(), "validated", StringComparison.OrdinalIgnoreCase));
            WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, passed, "", mappingFile, plcExportCatalog, precheck, results);
            if (!passed)
            {
                throw new InvalidOperationException("Mapped HMI template binding validation failed. Report: " + reportPath);
            }

            void ValidateOneMappedTemplate(string templateName, string templateFile, string connectionNameLocal, List<Dictionary<string, object?>> rows)
            {
                rows.RemoveAll(r => string.Equals(r["templateName"]?.ToString(), templateName, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(r["status"]?.ToString(), "ready-for-tia-validation", StringComparison.OrdinalIgnoreCase));
                var screenName = "Map_" + Regex.Replace(templateName, @"[^A-Za-z0-9_]+", "_");
                if (screenName.Length > 44) screenName = screenName.Substring(0, 44);
                var currentTags = Array.Empty<HmiTemplateTagSpec>();
                var result = new Dictionary<string, object?>
                {
                    ["templateName"] = templateName,
                    ["template"] = templateFile,
                    ["screen"] = screenName,
                    ["status"] = "validated",
                    ["reason"] = "Explicit mapping and PLC export precheck passed.",
                    ["precheckStatus"] = "plc-symbols-present",
                    ["missingPlcSymbols"] = 0,
                    ["screenReadback"] = false,
                    ["applyFailures"] = 0,
                    ["hmiTagsCreated"] = 0,
                    ["bindingsAttempted"] = 0,
                    ["bindingsSucceeded"] = 0,
                    ["eventsAttempted"] = 0,
                    ["eventsSucceeded"] = 0,
                    ["eventReadbacks"] = 0,
                    ["errors"] = new List<string>()
                };

                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(templateFile, Encoding.UTF8))?.AsObject() ?? new JsonObject();
                    currentTags = ReadHmiTemplateTags(root);
                    LogDiag($"Mapped HMI template binding validation: Ensure screen start template={templateName}, screen={screenName}");
                    McpServer.EnsureUnifiedHmiScreen("HMI_RT_1", screenName, 800, 480);
                    LogDiag($"Mapped HMI template binding validation: Apply screen start template={templateName}, screen={screenName}");
                    var apply = McpServer.ApplyUnifiedHmiScreenDesignJson("HMI_RT_1", screenName, HmiTemplateDesignJsonBuilder.BuildApplyDesignJson(templateFile, 800, 480));
                    var applyFailures = CountHmiApplyFailures(apply.Meta);
                    result["applyFailures"] = applyFailures;
                    if (applyFailures > 0)
                    {
                        ((List<string>)result["errors"]!).Add("ApplyUnifiedHmiScreenDesignJson reported failed writes: " + applyFailures);
                    }

                    var screens = McpServer.GetHmiScreens("HMI_RT_1").Items?.ToArray() ?? Array.Empty<string>();
                    result["screenReadback"] = screens.Any(s => string.Equals(s, screenName, StringComparison.OrdinalIgnoreCase));
                    LogDiag($"Mapped HMI template binding validation: Screen readback template={templateName}, readback={result["screenReadback"]}, applyFailures={applyFailures}");

                    foreach (var tag in currentTags)
                    {
                        LogDiag($"Mapped HMI template binding validation: Ensure HMI tag {tag.Name}->{tag.PlcTag}");
                        McpServer.EnsureUnifiedHmiTag("HMI_RT_1", "Mapped_Template_Tags", tag.Name, tag.DataType, "PLC_1", tag.PlcTag, connectionNameLocal, tag.Address);
                        result["hmiTagsCreated"] = (int)result["hmiTagsCreated"]! + 1;
                    }

                    var tagNames = currentTags.Select(x => x.Name).ToArray();
                    var items = (root["Items"] as JsonArray ?? root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
                    foreach (var item in items)
                    {
                        var name = item["Name"]?.ToString() ?? item["name"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        foreach (var dyn in ReadTemplateDynamizations(item))
                        {
                            if (tagNames.Contains(dyn.Tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryBindMapped(result, currentTags, screenName, name, dyn.PropertyName, dyn.Tag);
                            }
                        }

                        foreach (var action in ReadTemplateItemActions(item))
                        {
                            var tag = action.TargetTag;
                            if (string.IsNullOrWhiteSpace(tag))
                            {
                                tag = ExtractFirstRuntimeTag(action.Script);
                            }
                            if (tagNames.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            {
                                TryButtonEventMapped(result, screenName, name, action.Event, tag, action.Script);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add(ex.InnerException?.Message ?? ex.Message);
                }

                var errors = (List<string>)result["errors"]!;
                if (!Equals(result["screenReadback"], true)
                    || Convert.ToInt32(result["applyFailures"] ?? 0) > 0
                    || Convert.ToInt32(result["hmiTagsCreated"] ?? 0) == 0
                    || Convert.ToInt32(result["bindingsAttempted"] ?? 0) != Convert.ToInt32(result["bindingsSucceeded"] ?? 0)
                    || Convert.ToInt32(result["eventsAttempted"] ?? 0) != Convert.ToInt32(result["eventsSucceeded"] ?? 0)
                    || errors.Count > 0)
                {
                    result["status"] = "failed";
                }
                rows.Add(result);
            }

            void TryBindMapped(Dictionary<string, object?> result, HmiTemplateTagSpec[] currentTags, string screenName, string itemName, string propertyName, string tagName)
            {
                result["bindingsAttempted"] = (int)result["bindingsAttempted"]! + 1;
                try
                {
                    var tag = currentTags.FirstOrDefault(x => string.Equals(x.Name, tagName, StringComparison.OrdinalIgnoreCase));
                    McpServer.BindUnifiedHmiTagDynamization("HMI_RT_1", screenName, itemName, propertyName, tagName, tag?.DataType ?? "Bool", tag?.PlcTag ?? "", "");
                    McpServer.DescribeHmiScreenItem("HMI_RT_1", screenName, itemName, 80);
                    result["bindingsSucceeded"] = (int)result["bindingsSucceeded"]! + 1;
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add($"{itemName}.{propertyName}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            void TryButtonEventMapped(Dictionary<string, object?> result, string screenName, string buttonName, string eventType, string tagName, string scriptCode)
            {
                result["eventsAttempted"] = (int)result["eventsAttempted"]! + 1;
                try
                {
                    if (string.IsNullOrWhiteSpace(eventType)) eventType = "Tapped";
                    if (string.IsNullOrWhiteSpace(scriptCode))
                    {
                        scriptCode = $"HMIRuntime.Tags.SysFct.SetBitInTag(\"{tagName}\", 0);";
                    }
                    McpServer.EnsureUnifiedHmiButtonEventHandler("HMI_RT_1", screenName, buttonName, eventType);
                    McpServer.SetUnifiedHmiButtonEventScriptCode("HMI_RT_1", screenName, buttonName, eventType, scriptCode, "", false);
                    var readback = McpServer.DescribeUnifiedHmiButtonEventScript("HMI_RT_1", screenName, buttonName, eventType, 80);
                    if ((readback.Members?.Any() ?? false) || !string.IsNullOrWhiteSpace(readback.Message))
                    {
                        result["eventReadbacks"] = (int)result["eventReadbacks"]! + 1;
                    }
                    result["eventsSucceeded"] = (int)result["eventsSucceeded"]! + 1;
                }
                catch (Exception ex)
                {
                    ((List<string>)result["errors"]!).Add($"{buttonName}.{eventType}->{tagName}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        private static bool TemplateAllRequiredTagsMapped(JsonArray effectiveTemplates, string templateName)
        {
            var template = effectiveTemplates
                .OfType<JsonObject>()
                .FirstOrDefault(x => string.Equals(x["templateName"]?.ToString(), templateName, StringComparison.OrdinalIgnoreCase));
            if (template == null) return false;
            var required = template["requiredTags"] as JsonArray ?? new JsonArray();
            if (required.Count == 0) return false;
            return required
                .OfType<JsonObject>()
                .All(tag => string.Equals(tag["MappedBy"]?.ToString(), "hmi-template-mapping-file", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(tag["PlcTag"]?.ToString()));
        }

        private static string ReadTemplateNameFromFile(string path)
        {
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                return json?["TemplateName"]?.ToString() ?? Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(path);
            }
        }

        private static void WriteMappedTemplateFile(string originalPath, JsonObject effectiveTemplate, string outputPath)
        {
            var root = JsonNode.Parse(File.ReadAllText(originalPath, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidOperationException("HMI template JSON root must be an object: " + originalPath);
            var mappedByTag = (effectiveTemplate["requiredTags"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToDictionary(x => x["Name"]?.ToString() ?? "", x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var tagNode in root["RequiredTags"] as JsonArray ?? new JsonArray())
            {
                if (tagNode is not JsonObject tag) continue;
                var name = tag["Name"]?.ToString() ?? "";
                if (!mappedByTag.TryGetValue(name, out var mappedTag)) continue;
                var mapped = mappedTag["PlcTag"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(mapped)) continue;
                tag["OriginalPlcTag"] = tag["PlcTag"]?.ToString() ?? "";
                tag["PlcTag"] = mapped;
                tag["MappedBy"] = "hmi-template-mapping-file";
                tag["MappingStatus"] = mappedTag["MappingStatus"]?.ToString() ?? "";
                tag["MappingSource"] = mappedTag["MappingSource"]?.ToString() ?? "";
                tag["DeterministicRule"] = mappedTag["DeterministicRule"]?.ToString() ?? "";
                tag["RuleEvidence"] = mappedTag["RuleEvidence"]?.ToString() ?? "";
                tag["Comment"] = "中文说明：此变量来自显式映射文件，已先通过离线 PLC 导出符号预检。";
            }
            File.WriteAllText(outputPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
        }

        private static bool TryRunMappedTiaStep<T>(
            string stepName,
            int timeoutSeconds,
            Func<T> action,
            List<Dictionary<string, object?>> rows,
            string reportPath,
            string jsonReportPath,
            string projectName,
            string projectDirectory,
            string templateDirectory,
            string mappingPath,
            string plcExportDirectory,
            string mappedTemplateDir,
            JsonObject mappingFile,
            JsonObject plcExportCatalog,
            JsonArray precheck)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(15, timeoutSeconds));
            LogDiag($"Mapped HMI template binding validation: {stepName} start, timeoutSeconds={timeout.TotalSeconds:0}");
            try
            {
                var task = Task.Run(action);
                if (!task.Wait(timeout))
                {
                    AddMappedTiaStepFailure(rows, stepName, "timeout", $"TIA step timed out after {timeout.TotalSeconds:0} seconds.");
                    WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, $"TIA temporary-project validation stopped at `{stepName}` because the step timed out.", mappingFile, plcExportCatalog, precheck, rows);
                    LogDiag($"Mapped HMI template binding validation: {stepName} timeout after {timeout.TotalSeconds:0}s");
                    return false;
                }

                if (task.IsFaulted)
                {
                    var message = task.Exception?.GetBaseException().Message ?? "Unknown TIA step failure.";
                    AddMappedTiaStepFailure(rows, stepName, "failed", message);
                    WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, $"TIA temporary-project validation failed at `{stepName}`.", mappingFile, plcExportCatalog, precheck, rows);
                    LogDiag($"Mapped HMI template binding validation: {stepName} failed: {message}");
                    return false;
                }

                LogDiag($"Mapped HMI template binding validation: {stepName} completed");
                return true;
            }
            catch (Exception ex)
            {
                var message = ex.InnerException?.Message ?? ex.Message;
                AddMappedTiaStepFailure(rows, stepName, "failed", message);
                WriteMappedBindingReport(reportPath, jsonReportPath, projectName, projectDirectory, templateDirectory, mappingPath, plcExportDirectory, mappedTemplateDir, false, $"TIA temporary-project validation failed at `{stepName}`.", mappingFile, plcExportCatalog, precheck, rows);
                LogDiag($"Mapped HMI template binding validation: {stepName} failed: {message}");
                return false;
            }
        }

        private static void AddMappedTiaStepFailure(List<Dictionary<string, object?>> rows, string stepName, string status, string detail)
        {
            rows.RemoveAll(r => string.Equals(r["templateName"]?.ToString(), "__tia_step__", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(r["screen"]?.ToString(), stepName, StringComparison.OrdinalIgnoreCase));
            rows.Add(new Dictionary<string, object?>
            {
                ["templateName"] = "__tia_step__",
                ["template"] = "",
                ["screen"] = stepName,
                ["status"] = status,
                ["reason"] = detail,
                ["precheckStatus"] = "",
                ["missingPlcSymbols"] = 0,
                ["hmiTagsCreated"] = 0,
                ["bindingsAttempted"] = 0,
                ["bindingsSucceeded"] = 0,
                ["eventsAttempted"] = 0,
                ["eventsSucceeded"] = 0,
                ["eventReadbacks"] = 0,
                ["errors"] = new List<string> { detail }
            });
        }

        private static void WriteMappedBindingReport(
            string mdPath,
            string jsonPath,
            string projectName,
            string projectDirectory,
            string templateDirectory,
            string mappingPath,
            string plcExportDirectory,
            string mappedTemplateDirectory,
            bool passed,
            string failureReason,
            JsonObject mappingFile,
            JsonObject plcExportCatalog,
            JsonArray precheck,
            List<Dictionary<string, object?>> rows)
        {
            var md = new StringBuilder();
            md.AppendLine("# Mapped HMI Template Binding Validation");
            md.AppendLine();
            md.AppendLine("- Project: `" + projectName + "`");
            md.AppendLine("- ProjectDirectory: `" + projectDirectory + "`");
            md.AppendLine("- TemplateDirectory: `" + templateDirectory + "`");
            md.AppendLine("- MappingFile: `" + mappingPath + "`");
            md.AppendLine("- PlcExportDirectory: `" + plcExportDirectory + "`");
            md.AppendLine("- MappedTemplateDirectory: `" + mappedTemplateDirectory + "`");
            md.AppendLine("- Result: `" + (passed ? "PASS" : "FAIL") + "`");
            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                md.AppendLine("- " + (passed ? "Note" : "FailureReason") + ": " + failureReason);
            }
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- Only explicit `MappedPlcTag` entries are used; candidate suggestions are ignored.");
            md.AppendLine("- PLC symbols must pass offline full-symbol precheck before HMI tags/events are created.");
            md.AppendLine("- This validation writes only a temporary validation project and does not modify the delivery package.");
            md.AppendLine();
            md.AppendLine("## Inputs");
            md.AppendLine("- Mapping loaded: " + mappingFile["loaded"]);
            md.AppendLine("- Effective mappings: " + ((mappingFile["entries"] as JsonArray)?.Count ?? 0));
            md.AppendLine("- PLC export files scanned: " + plcExportCatalog["filesScanned"]);
            md.AppendLine("- PLC symbols: " + plcExportCatalog["symbolCount"]);
            md.AppendLine();
            md.AppendLine("## PLC Precheck");
            foreach (var node in precheck.OfType<JsonObject>())
            {
                md.AppendLine("- " + node["templateName"] + ": " + node["status"] + ", missing=" + ((node["missingRootSymbols"] as JsonArray)?.Count ?? 0));
            }
            md.AppendLine();
            md.AppendLine("## HMI Validation");
            md.AppendLine("| Template | Status | Screen | Tags | Bindings | Events | Event Readback | Reason | Errors |");
            md.AppendLine("|---|---|---|---:|---:|---:|---:|---|---|");
            foreach (var r in rows)
            {
                var errs = r.TryGetValue("errors", out var errObj) && errObj is List<string> errList
                    ? string.Join("<br>", errList.Select(e => e.Replace("|", "\\|")))
                    : "";
                md.AppendLine("| " + EscapeMarkdownCell(r["templateName"]?.ToString() ?? "") +
                              " | " + EscapeMarkdownCell(r["status"]?.ToString() ?? "") +
                              " | " + EscapeMarkdownCell(r["screen"]?.ToString() ?? "") +
                              " | " + r["hmiTagsCreated"] +
                              " | " + r["bindingsSucceeded"] + "/" + r["bindingsAttempted"] +
                              " | " + r["eventsSucceeded"] + "/" + r["eventsAttempted"] +
                              " | " + r["eventReadbacks"] +
                              " | " + EscapeMarkdownCell(r["reason"]?.ToString() ?? "") +
                              " | " + errs + " |");
            }
            md.AppendLine();
            md.AppendLine("## Release Readiness Gate");
            md.AppendLine("- A template is reusable only after it has explicit mapping, PLC symbol precheck, HMI tag creation, screen apply, dynamization binding, event binding, and readback evidence.");
            md.AppendLine("- Templates listed as skipped still need mapping rules or manual mapping before delivery-package synchronization.");

            File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                projectName,
                projectDirectory,
                templateDirectory,
                mappingPath,
                plcExportDirectory,
                mappedTemplateDirectory,
                passed,
                failureReason,
                mappingFile,
                plcExportCatalog,
                precheck,
                results = rows
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }

    }
}
