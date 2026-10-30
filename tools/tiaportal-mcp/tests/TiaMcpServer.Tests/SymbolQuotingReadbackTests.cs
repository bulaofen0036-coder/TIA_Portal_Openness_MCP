using System;
using TiaMcpServer.ModelContextProtocol;

// ─────────────────────────────────────────────────────────────────────────────
//  回读里的引号只包单个分量，不包整条路径（公开仓 issue #42）。
//
//  缺陷：全局变量回读成 "HMI.Layer_Distance"（SCL 里这是另一个名为 HMI.Layer_Distance 的全局符号），
//  含特殊字符的成员回读成 "HMI.M/A" / #my var（M/A 会被读成除法，my var 读成两个词）。
//
//  下面的 StructuredText 是 TIA V21 真实导出（2026-09-17 在无头隔离实例里用 SCL 源生成、
//  编译 0 错 0 警、ExportBlock 导出），只去掉了 UId 和命名空间。关键事实：
//  V21 只给全局根分量挂 HasQuotes=true；M/A 与 my var **都不带** HasQuotes，
//  所以判断要不要引号不能只看这个属性。
//  断言用的每一行就是当初喂给 TIA 的 SCL 源 —— 回读必须逐行还原。
// ─────────────────────────────────────────────────────────────────────────────
internal static class SymbolQuotingReadbackTests
{
    private static string Block(string units) =>
        "<Document><SW.Blocks.FB ID=\"0\"><AttributeList><Name>Probe42</Name></AttributeList>"
      + "<ObjectList>" + units + "</ObjectList></SW.Blocks.FB></Document>";

    private static string SclUnit(string structuredText) =>
        "<SW.Blocks.CompileUnit ID=\"1\"><AttributeList><NetworkSource>" + structuredText
      + "</NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>";

    private const string RealV21Export = """
<StructuredText><NewLine />
<Blank /><Access Scope="LocalVariable"><Symbol><Component Name="Postion"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="LiteralConstant"><Constant><ConstantValue>0</ConstantValue></Constant></Access><Blank /><Token Text="+" /><Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="Layer_Distance" /></Symbol></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="Postion"><Token Text="[" /><Access Scope="LocalVariable"><Symbol><Component Name="i" /></Symbol></Access><Token Text="]" /></Component></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="LocalVariable"><Symbol><Component Name="Postion"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="M/A" /></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="LiteralConstant"><Constant><ConstantValue>TRUE</ConstantValue></Constant></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="Axis" /><Token Text="." /><Component Name="Ctrl" /><Token Text="." /><Component Name="A_Speed_Set" /></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="LiteralConstant"><Constant><ConstantValue>30</ConstantValue></Constant></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="Grid"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="," /><Blank /><Access Scope="LocalVariable"><Symbol><Component Name="i" /></Symbol></Access><Token Text="]" /></Component></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="LiteralConstant"><Constant><ConstantValue>2</ConstantValue></Constant></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="Call"><CallInfo BlockType="FC"><Instance Scope="GlobalVariable"><Component Name="DeviceStatus" /></Instance><Token Text="(" /><Parameter Name="DeviceStatus"><Blank /><Token Text="=&gt;" /><Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="DeviceStatus" /></Symbol></Access></Parameter><Token Text=")" /></CallInfo></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="LocalVariable"><Symbol><Component Name="Delay_Time"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access><Access Scope="Call"><Instruction><Token Text="(" /><Parameter Name="IN"><Blank /><Token Text=":=" /><Blank /><Access Scope="LocalVariable"><Symbol><Component Name="TakeTemp" /></Symbol></Access></Parameter><Token Text="," /><Blank /><Parameter Name="PT"><Blank /><Token Text=":=" /><Blank /><Access Scope="LocalConstant"><Constant Name="DelayTime" /></Access></Parameter><Token Text="," /><Blank /><Parameter Name="Q"><Blank /><Token Text="=&gt;" /><Blank /><Access Scope="LocalVariable"><Symbol><Component Name="Dealy_Q"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access></Parameter><Token Text=")" /></Instruction></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="LocalVariable"><Symbol><Component Name="my var" /></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="M/A" /></Symbol></Access><Blank /><Token Text="AND" /><Blank /><Access Scope="LocalVariable"><Symbol><Component Name="Dealy_Q"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access><Token Text=";" /><NewLine />
<Blank /><Access Scope="LocalVariable"><Symbol><Component Name="Postion"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>2</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access><Blank /><Token Text=":=" /><Blank /><Access Scope="Call"><Instruction Name="ABS"><Token Text="(" /><NamelessParameter><Access Scope="LocalVariable"><Symbol><Component Name="Postion"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>1</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access><Blank /><Token Text="-" /><Blank /><Access Scope="GlobalVariable"><Symbol><Component Name="HMI"><BooleanAttribute Name="HasQuotes">true</BooleanAttribute></Component><Token Text="." /><Component Name="Postion"><Token Text="[" /><Access Scope="LiteralConstant"><Constant><ConstantValue>2</ConstantValue></Constant></Access><Token Text="]" /></Component></Symbol></Access></NamelessParameter><Token Text=")" /></Instruction></Access><Token Text=";" /><NewLine />
</StructuredText>
""";

    // 喂给 TIA 的 SCL 源（编译 0 错 0 警），回读必须逐行一致。
    private static readonly string[] SourceLines =
    {
        "#Postion[1] := 0 + \"HMI\".Layer_Distance;",
        "\"HMI\".Postion[#i] := #Postion[1];",
        "\"HMI\".\"M/A\" := TRUE;",
        "\"HMI\".Axis.Ctrl.A_Speed_Set := 30;",
        "\"HMI\".Grid[1, #i] := 2;",
        "\"DeviceStatus\"(DeviceStatus => \"HMI\".DeviceStatus);",
        "#Delay_Time[1](IN := #TakeTemp, PT := #DelayTime, Q => #Dealy_Q[1]);",
        "#\"my var\" := \"HMI\".\"M/A\" AND #Dealy_Q[1];",
        "#Postion[2] := ABS(#Postion[1] - \"HMI\".Postion[2]);",
    };

    public static void Run(Action<bool, string> check)
    {
        string real = LadTextRenderer.Render(Block(SclUnit(RealV21Export)));
        foreach (var line in SourceLines)
            check(real.Contains(line), "V21 真实导出逐行回读: " + line);
        check(!real.Contains("\"HMI.Layer_Distance\""), "[哨兵] 引号不许包住整条路径 \"HMI.Layer_Distance\"");
        check(!real.Contains("\"HMI.M/A\""), "[哨兵] 含 / 的成员不许被并进根分量的引号里");
        check(!real.Contains("#my var"), "[哨兵] 含空格的局部名不许裸写成 #my var");

        // LAD 操作数走的是同一个 ReadAccess：全局路径同样只引根分量。
        string lad = LadTextRenderer.Render(Block(
            "<SW.Blocks.CompileUnit ID=\"2\"><AttributeList><NetworkSource><FlgNet><Parts>"
          + "<Access Scope=\"GlobalVariable\" UId=\"21\"><Symbol><Component Name=\"ModbusTCP_DB\" /><Component Name=\"ModbusTcpConfig\" /><Component Name=\"M/A\" /></Symbol></Access>"
          + "<Access Scope=\"GlobalVariable\" UId=\"24\"><Symbol><Component Name=\"Run\" /></Symbol></Access>"
          + "<Part Name=\"Contact\" UId=\"30\" /><Part Name=\"Coil\" UId=\"31\" />"
          + "</Parts><Wires>"
          + "<Wire><Powerrail /><NameCon UId=\"30\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"21\" /><NameCon UId=\"30\" Name=\"operand\" /></Wire>"
          + "<Wire><NameCon UId=\"30\" Name=\"out\" /><NameCon UId=\"31\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"24\" /><NameCon UId=\"31\" Name=\"operand\" /></Wire>"
          + "</Wires></FlgNet></NetworkSource><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>"
          + "</SW.Blocks.CompileUnit>"));
        check(lad.Contains("\"ModbusTCP_DB\".ModbusTcpConfig.\"M/A\""), "LAD 全局操作数只引根分量与特殊成员");
        check(lad.Contains("\"Run\" ( )"), "[反向哨兵] LAD 单分量全局线圈仍是 \"Run\"");

        // 调用实例与具名常量同一条规则：局部名要引号时写 #"名"。
        string inst = LadTextRenderer.Render(Block(SclUnit(
            "<StructuredText><Access Scope=\"Call\"><CallInfo BlockType=\"FB\" Name=\"MotorFb\">"
          + "<Instance Scope=\"LocalVariable\"><Component Name=\"motor 1\" /></Instance>"
          + "<Token Text=\"(\" /><Token Text=\")\" /></CallInfo></Access><Token Text=\";\" /></StructuredText>")));
        check(inst.Contains("#\"motor 1\"();"), "含空格的多重实例名回读成 #\"motor 1\"()");

        // 反向哨兵：纯标识符不许被多加引号（修过头的形态）。
        string plain = LadTextRenderer.Render(Block(SclUnit(
            "<StructuredText><Access Scope=\"LocalVariable\"><Symbol><Component Name=\"Motor\" /><Token Text=\".\" /><Component Name=\"Speed\" /></Symbol></Access>"
          + "<Blank /><Token Text=\":=\" /><Blank />"
          + "<Access Scope=\"LocalVariable\"><Symbol><Component Name=\"电机速度\" /></Symbol></Access><Token Text=\";\" /></StructuredText>")));
        check(plain.Contains("#Motor.Speed := #电机速度;"), "[反向哨兵] 纯标识符（含中文）不加引号");
    }
}
