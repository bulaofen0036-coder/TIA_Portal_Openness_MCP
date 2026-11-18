using System;
using TiaMcpServer.ModelContextProtocol;

// ─────────────────────────────────────────────────────────────────────────────
//  DescribeBlockLogic 的 LAD 输出必须还原完整接线，而不只是「主电流 + 能流表达式」。
//
//  现场缺陷（2.7.8.1）：
//  1) TON 的 IN 脚是大写 "IN"，旧实现只查小写 ":in"，触发条件整段丢失；
//  2) 只渲染线圈和 MOVE/CALL，TON 之外的运算盒（Add/Sub/…）不参与渲染；
//  3) 一个输入脚的多条来源线被最后一条覆盖，并联支路只剩一支。
//
//  下面的用例都用真实 SimaticML 形态（Powerrail / NameCon / IdentCon / OpenCon），
//  断言的是「哪个脚被什么接通」，而不是某段漂亮但会骗人的表达式。
// ─────────────────────────────────────────────────────────────────────────────
internal static class LadWiringTests
{
    private static string Block(string unit) =>
        "<Document><SW.Blocks.FC ID=\"0\"><AttributeList><Name>LadProbe</Name></AttributeList>"
      + "<ObjectList>" + unit + "</ObjectList></SW.Blocks.FC></Document>";

    private static string LadUnit(string parts, string wires) =>
        "<SW.Blocks.CompileUnit ID=\"1\"><AttributeList><NetworkSource><FlgNet><Parts>" + parts
      + "</Parts><Wires>" + wires + "</Wires></FlgNet></NetworkSource>"
      + "<ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>";

    public static void Run(Action<bool, string> check)
    {
        // 1) TON：触发来自 #Trig；ET 未接。旧实现只能读到 TON(PT=…)，看不到 #Trig。
        string ton = LadTextRenderer.Render(Block(LadUnit(
            "<Access Scope=\"LocalVariable\" UId=\"a1\"><Symbol><Component Name=\"Trig\" /></Symbol></Access>"
          + "<Access Scope=\"TypedConstant\" UId=\"a2\"><Constant><ConstantValue>T#500ms</ConstantValue></Constant></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a3\"><Symbol><Component Name=\"OutTonQ\" /></Symbol></Access>"
          + "<Part Name=\"Contact\" UId=\"p1\" />"
          + "<Part Name=\"TON\" UId=\"p2\"><Instance Scope=\"LocalVariable\" UId=\"p3\"><Component Name=\"tonInst\" /></Instance>"
          + "<TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue></Part>"
          + "<Part Name=\"Coil\" UId=\"p4\" />",
            "<Wire><Powerrail /><NameCon UId=\"p1\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a1\" /><NameCon UId=\"p1\" Name=\"operand\" /></Wire>"
          + "<Wire><NameCon UId=\"p1\" Name=\"out\" /><NameCon UId=\"p2\" Name=\"IN\" /></Wire>"
          + "<Wire><IdentCon UId=\"a2\" /><NameCon UId=\"p2\" Name=\"PT\" /></Wire>"
          + "<Wire><NameCon UId=\"p2\" Name=\"Q\" /><NameCon UId=\"p4\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a3\" /><NameCon UId=\"p4\" Name=\"operand\" /></Wire>"
          + "<Wire><NameCon UId=\"p2\" Name=\"ET\" /><OpenCon UId=\"p5\" /></Wire>")));

        check(ton.Contains("#Trig · TON[tonInst]"), "TON 的 Q 表达式要带上触发支路 #Trig");
        check(ton.Contains("IN⇐#Trig"), "TON 的 IN 脚要写明由 #Trig 接通");
        check(ton.Contains("PT=T#500ms"), "TON 的 PT 脚要保留");
        check(ton.Contains("ET=未接"), "TON 的 ET 未接要显式写出");

        // 2) 运算分支：EN 由两条并联支路供电，IN1/IN2/OUT/ENO 各有自己的来源。
        string add = LadTextRenderer.Render(Block(LadUnit(
            "<Access Scope=\"LocalVariable\" UId=\"a1\"><Symbol><Component Name=\"Start\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a2\"><Symbol><Component Name=\"Bypass\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a3\"><Symbol><Component Name=\"A\" /></Symbol></Access>"
          + "<Access Scope=\"LiteralConstant\" UId=\"a4\"><Constant><ConstantValue>10</ConstantValue></Constant></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a5\"><Symbol><Component Name=\"Result\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a6\"><Symbol><Component Name=\"Done\" /></Symbol></Access>"
          + "<Part Name=\"Contact\" UId=\"p1\" />"
          + "<Part Name=\"Contact\" UId=\"p2\" />"
          + "<Part Name=\"Add\" UId=\"p3\"><TemplateValue Name=\"SrcType\" Type=\"Type\">Int</TemplateValue></Part>"
          + "<Part Name=\"Coil\" UId=\"p4\" />",
            "<Wire><Powerrail /><NameCon UId=\"p1\" Name=\"in\" /></Wire>"
          + "<Wire><Powerrail /><NameCon UId=\"p2\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a1\" /><NameCon UId=\"p1\" Name=\"operand\" /></Wire>"
          + "<Wire><IdentCon UId=\"a2\" /><NameCon UId=\"p2\" Name=\"operand\" /></Wire>"
          + "<Wire><NameCon UId=\"p1\" Name=\"out\" /><NameCon UId=\"p3\" Name=\"EN\" /></Wire>"
          + "<Wire><NameCon UId=\"p2\" Name=\"out\" /><NameCon UId=\"p3\" Name=\"EN\" /></Wire>"
          + "<Wire><IdentCon UId=\"a3\" /><NameCon UId=\"p3\" Name=\"IN1\" /></Wire>"
          + "<Wire><IdentCon UId=\"a4\" /><NameCon UId=\"p3\" Name=\"IN2\" /></Wire>"
          + "<Wire><IdentCon UId=\"a5\" /><NameCon UId=\"p3\" Name=\"OUT\" /></Wire>"
          + "<Wire><NameCon UId=\"p3\" Name=\"ENO\" /><NameCon UId=\"p4\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a6\" /><NameCon UId=\"p4\" Name=\"operand\" /></Wire>")));

        check(add.Contains("EN⇐(#Start + #Bypass)"), "Add 的 EN 要展开成并联支路");
        check(add.Contains("IN1=#A"), "Add 的 IN1 要写明来源 #A");
        check(add.Contains("IN2=10"), "Add 的 IN2 要写明常量 10");
        check(add.Contains("OUT⇒#Result"), "Add 的 OUT 要写明写往 #Result");
        check(add.Contains("ENO⇒[p4] Coil #Done.in"), "Add 的 ENO 要写明接到 Done 线圈");

        // 3) 并联触点直接进线圈：两条来源都要保留，不能被最后一条覆盖。
        string parallel = LadTextRenderer.Render(Block(LadUnit(
            "<Access Scope=\"LocalVariable\" UId=\"a1\"><Symbol><Component Name=\"A\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a2\"><Symbol><Component Name=\"B\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a3\"><Symbol><Component Name=\"Out\" /></Symbol></Access>"
          + "<Part Name=\"Contact\" UId=\"p1\" /><Part Name=\"Contact\" UId=\"p2\" /><Part Name=\"Coil\" UId=\"p3\" />",
            "<Wire><Powerrail /><NameCon UId=\"p1\" Name=\"in\" /></Wire>"
          + "<Wire><Powerrail /><NameCon UId=\"p2\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a1\" /><NameCon UId=\"p1\" Name=\"operand\" /></Wire>"
          + "<Wire><IdentCon UId=\"a2\" /><NameCon UId=\"p2\" Name=\"operand\" /></Wire>"
          + "<Wire><NameCon UId=\"p1\" Name=\"out\" /><NameCon UId=\"p3\" Name=\"in\" /></Wire>"
          + "<Wire><NameCon UId=\"p2\" Name=\"out\" /><NameCon UId=\"p3\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a3\" /><NameCon UId=\"p3\" Name=\"operand\" /></Wire>")));

        check(parallel.Contains("#Out ( )  ⇐  (#A + #B)"), "并联触点要还原成 (#A + #B)");

        // 4) O 盒 + TON：两条触发支路经 O 汇总后再进 TON.IN；ET 输出到 #Elapsed。
        string orTon = LadTextRenderer.Render(Block(LadUnit(
            "<Access Scope=\"LocalVariable\" UId=\"a1\"><Symbol><Component Name=\"TrigA\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a2\"><Symbol><Component Name=\"TrigB\" /></Symbol></Access>"
          + "<Access Scope=\"TypedConstant\" UId=\"a3\"><Constant><ConstantValue>T#2s</ConstantValue></Constant></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a4\"><Symbol><Component Name=\"TimerQ\" /></Symbol></Access>"
          + "<Access Scope=\"LocalVariable\" UId=\"a5\"><Symbol><Component Name=\"Elapsed\" /></Symbol></Access>"
          + "<Part Name=\"Contact\" UId=\"p1\" /><Part Name=\"Contact\" UId=\"p2\" /><Part Name=\"O\" UId=\"p3\" />"
          + "<Part Name=\"TON\" UId=\"p4\"><Instance Scope=\"LocalVariable\" UId=\"p5\"><Component Name=\"ton2\" /></Instance>"
          + "<TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue></Part>"
          + "<Part Name=\"Coil\" UId=\"p6\" />",
            "<Wire><Powerrail /><NameCon UId=\"p1\" Name=\"in\" /></Wire>"
          + "<Wire><Powerrail /><NameCon UId=\"p2\" Name=\"in\" /></Wire>"
          + "<Wire><IdentCon UId=\"a1\" /><NameCon UId=\"p1\" Name=\"operand\" /></Wire>"
          + "<Wire><IdentCon UId=\"a2\" /><NameCon UId=\"p2\" Name=\"operand\" /></Wire>"
          + "<Wire><NameCon UId=\"p1\" Name=\"out\" /><NameCon UId=\"p3\" Name=\"in1\" /></Wire>"
          + "<Wire><NameCon UId=\"p2\" Name=\"out\" /><NameCon UId=\"p3\" Name=\"in2\" /></Wire>"
          + "<Wire><NameCon UId=\"p3\" Name=\"out\" /><NameCon UId=\"p4\" Name=\"IN\" /></Wire>"
          + "<Wire><IdentCon UId=\"a3\" /><NameCon UId=\"p4\" Name=\"PT\" /></Wire>"
          + "<Wire><NameCon UId=\"p4\" Name=\"Q\" /><NameCon UId=\"p6\" Name=\"in\" /></Wire>"
          + "<Wire><NameCon UId=\"p4\" Name=\"ET\" /><IdentCon UId=\"a5\" /></Wire>"
          + "<Wire><IdentCon UId=\"a4\" /><NameCon UId=\"p6\" Name=\"operand\" /></Wire>")));

        check(orTon.Contains("IN⇐(#TrigA + #TrigB)"), "O 盒的两条触发支路要汇总到 TON.IN");
        check(orTon.Contains("ET⇒#Elapsed"), "TON 的 ET 要写明输出到 #Elapsed");
    }
}
