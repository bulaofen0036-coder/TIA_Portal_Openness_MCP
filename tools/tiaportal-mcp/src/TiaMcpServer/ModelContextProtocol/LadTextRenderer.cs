using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Decodes a SimaticML block export (FlgNet ladder + StructuredText SCL) into readable text so a
    /// model/human can analyze ladder logic WITHOUT hand-parsing wires. Siemens-free: works purely on
    /// the exported XML string. For each LAD network it reconstructs the power-flow as a boolean-ish
    /// expression (series = ' · ', parallel = ' + '), shows coils/boxes with their operands, and — the
    /// hard-to-spot thing — flags contacts whose operand is a LITERAL CONSTANT (e.g. a normally-open
    /// contact wired to FALSE permanently disables its rung).
    /// </summary>
    public static class LadTextRenderer
    {
        public static string Render(string xml)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex) { return "Could not parse block XML: " + ex.Message; }

            StripNamespaces(doc);

            var sb = new StringBuilder();
            var units = doc.Descendants("SW.Blocks.CompileUnit").ToList();
            if (units.Count == 0)
            {
                return "No LAD/SCL networks found (block may be a DB/UDT, or an empty program).";
            }

            int netNo = 0;
            foreach (var unit in units)
            {
                netNo++;
                var lang = unit.Descendants("ProgrammingLanguage").FirstOrDefault()?.Value?.Trim() ?? "?";
                var title = FirstMultilingual(unit, "Title");
                var comment = FirstMultilingual(unit, "Comment");
                sb.Append($"── 程序段 {netNo}");
                if (!string.IsNullOrWhiteSpace(title)) sb.Append($" · {title}");
                sb.Append($"  [{lang}]\n");
                if (!string.IsNullOrWhiteSpace(comment)) sb.Append($"   注释: {comment}\n");

                var flg = unit.Descendants("FlgNet").FirstOrDefault();
                if (flg != null && (lang.Equals("LAD", StringComparison.OrdinalIgnoreCase) || lang.Equals("FBD", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.Append(RenderLadNetwork(flg));
                }
                else if (lang.Equals("SCL", StringComparison.OrdinalIgnoreCase) || lang.Equals("STL", StringComparison.OrdinalIgnoreCase))
                {
                    var text = RenderStructuredText(unit);
                    sb.Append(string.IsNullOrWhiteSpace(text)
                        ? "   (无代码或纯声明)\n"
                        : IndentBlock(text, "   "));
                }
                else
                {
                    sb.Append("   (无 FlgNet / 不支持的语言)\n");
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd() + "\n";
        }

        // ---- LAD network ----

        private sealed class Part
        {
            public string UId = "";
            public string Name = "";
            public bool Negated;                 // contact/coil operand negated
            public string? Instance;             // timer/counter/FB instance name
            public Dictionary<string, string> Operands = new();  // pin name -> operand text
        }

        private static string RenderLadNetwork(XElement flg)
        {
            var parts = new Dictionary<string, Part>();
            var accessText = new Dictionary<string, (string text, bool literal)>();

            foreach (var acc in flg.Descendants("Access"))
            {
                var uid = acc.Attribute("UId")?.Value;
                if (uid == null) continue;
                accessText[uid] = ReadAccess(acc);
            }
            foreach (var p in flg.Descendants("Part"))
            {
                var uid = p.Attribute("UId")?.Value;
                if (uid == null) continue;
                var part = new Part { UId = uid, Name = p.Attribute("Name")?.Value ?? "?" };
                part.Negated = p.Elements("Negated").Any();
                part.Instance = p.Descendants("Instance").Descendants("Component").FirstOrDefault()?.Attribute("Name")?.Value;
                parts[uid] = part;
            }

            // Wires: bind operands (Access -> part.pin) and build flow edges (srcPart.out -> dstPart.in).
            // dstKey "(uid,pin)" -> source描述 (RAIL or "uid:pin")
            var flowSource = new Dictionary<string, string>();
            foreach (var wire in flg.Descendants("Wires").Elements("Wire"))
            {
                var ends = wire.Elements().ToList();
                var idents = ends.Where(e => e.Name.LocalName == "IdentCon").Select(e => e.Attribute("UId")?.Value).Where(v => v != null).ToList();
                var names = ends.Where(e => e.Name.LocalName == "NameCon")
                                .Select(e => (uid: e.Attribute("UId")?.Value, pin: e.Attribute("Name")?.Value)).Where(t => t.uid != null).ToList();
                bool hasRail = ends.Any(e => e.Name.LocalName == "Powerrail");

                // operand binding: an Access (IdentCon) tied to a part pin (NameCon).
                // A LITERAL bound to a CONTACT operand is the important tell: a normally-open contact
                // wired to 0/FALSE permanently OPENS (disables) its rung; NC or 1/TRUE permanently CLOSES.
                // Literals on compare/move pins are normal, so only annotate contacts.
                if (idents.Count > 0)
                {
                    foreach (var nc in names)
                    {
                        if (parts.TryGetValue(nc.uid!, out var pt) && accessText.TryGetValue(idents[0]!, out var at))
                        {
                            var pin = nc.pin ?? "operand";
                            string text = at.text;
                            if (at.literal && IsContact(pt.Name) && pin == "operand")
                            {
                                bool truthy = at.text is "1" or "TRUE" or "True";
                                bool falsy = at.text is "0" or "FALSE" or "False";
                                // NO contact: passes when operand true; NC (Negated): passes when operand false.
                                bool alwaysOpen = (!pt.Negated && falsy) || (pt.Negated && truthy);
                                bool alwaysClosed = (!pt.Negated && truthy) || (pt.Negated && falsy);
                                text += alwaysOpen ? " ⟨恒断·禁用本行⟩" : alwaysClosed ? " ⟨恒通⟩" : " ⟨常量触点⟩";
                            }
                            pt.Operands[pin] = text;
                        }
                    }
                }

                // flow: split named endpoints into sources (out-like) and destinations (in-like)
                bool IsOut(string? pin) => pin != null && (pin.Equals("out", StringComparison.OrdinalIgnoreCase)
                    || pin.Equals("eno", StringComparison.OrdinalIgnoreCase) || pin == "Q" || pin.StartsWith("out"));
                bool IsIn(string? pin) => pin != null && (pin.Equals("in", StringComparison.OrdinalIgnoreCase)
                    || pin.Equals("en", StringComparison.OrdinalIgnoreCase) || pin.Equals("pre", StringComparison.OrdinalIgnoreCase) || pin.StartsWith("in"));

                var sources = names.Where(t => IsOut(t.pin)).ToList();
                var dests = names.Where(t => IsIn(t.pin)).ToList();
                foreach (var d in dests)
                {
                    string key = d.uid + ":" + d.pin;
                    if (hasRail && sources.Count == 0) flowSource[key] = "RAIL";
                    else if (sources.Count > 0) flowSource[key] = sources[0].uid + ":" + sources[0].pin;
                    else if (hasRail) flowSource[key] = "RAIL";
                }
            }

            // Render every output element: coils and boxes that write (Move/Call/Set...). Trace their EN/in.
            var sb = new StringBuilder();
            var outputs = parts.Values.Where(p => IsCoil(p.Name) || IsWritingBox(p.Name)).ToList();
            if (outputs.Count == 0)
            {
                // Fallback: just list the parts + operands so nothing is opaque.
                foreach (var p in parts.Values)
                    sb.Append($"   · {p.Name}{FormatOperands(p)}\n");
                return sb.Length == 0 ? "   (空网络)\n" : sb.ToString();
            }

            var guard = new HashSet<string>();
            foreach (var outp in outputs)
            {
                if (IsCoil(outp.Name))
                {
                    string inKey = outp.UId + ":in";
                    string expr = flowSource.TryGetValue(inKey, out var src) ? TraceChain(src, parts, flowSource, guard) : "?";
                    string coil = CoilGlyph(outp.Name);
                    string operand = outp.Operands.TryGetValue("operand", out var o) ? o : "?";
                    sb.Append($"   {operand} {coil}  ⇐  {(string.IsNullOrEmpty(expr) ? "RAIL(恒通)" : expr)}\n");
                }
                else // writing box (MOVE etc.) driven by EN
                {
                    string enKey = outp.UId + ":en";
                    string en = flowSource.TryGetValue(enKey, out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    sb.Append($"   当 [{(string.IsNullOrEmpty(en) ? "RAIL(恒通)" : en)}] 时: {DescribeBox(outp)}\n");
                }
            }
            return sb.ToString();
        }

        // Trace power flow backward from a source node "uid:pin" (or RAIL) into a series/parallel expression.
        private static string TraceChain(string node, Dictionary<string, Part> parts, Dictionary<string, string> flowSource, HashSet<string> guard)
        {
            if (node == "RAIL" || string.IsNullOrEmpty(node)) return "";
            if (!guard.Add(node)) return "…";  // cycle guard
            try
            {
                var uid = node.Split(':')[0];
                if (!parts.TryGetValue(uid, out var p)) return "?";

                if (IsContact(p.Name))
                {
                    string upstream = flowSource.TryGetValue(uid + ":in", out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    string lit = (p.Negated ? "/" : "") + (p.Operands.TryGetValue("operand", out var o) ? o : "?");
                    return Series(upstream, lit);
                }
                if (IsCompare(p.Name))
                {
                    string upstream = flowSource.TryGetValue(uid + ":pre", out var src) ? TraceChain(src, parts, flowSource, guard) : "";
                    var a = p.Operands.TryGetValue("in1", out var i1) ? i1 : "?";
                    var b = p.Operands.TryGetValue("in2", out var i2) ? i2 : "?";
                    string cmp = $"({a} {CompareGlyph(p.Name)} {b})";
                    return Series(upstream, cmp);
                }
                if (p.Name == "O")  // OR box: inputs in1,in2,... are parallel branches
                {
                    var branches = new List<string>();
                    foreach (var pin in p.Operands.Keys.Concat(new[] { "in1", "in2", "in3", "in4" }).Distinct())
                    {
                        if (!pin.StartsWith("in")) continue;
                        if (flowSource.TryGetValue(uid + ":" + pin, out var src))
                            branches.Add(TraceChain(src, parts, flowSource, guard));
                    }
                    branches = branches.Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
                    return branches.Count == 0 ? "" : "(" + string.Join(" + ", branches) + ")";
                }
                // timers / edges / other boxes producing power at Q/out
                string en = flowSource.TryGetValue(uid + ":in", out var s2) ? TraceChain(s2, parts, flowSource, guard) : "";
                string box = DescribeBoxInline(p);
                return Series(en, box);
            }
            finally { guard.Remove(node); }
        }

        private static string Series(string upstream, string term)
            => string.IsNullOrEmpty(upstream) ? term : upstream + " · " + term;

        // ---- helpers ----

        private static bool IsContact(string n) => n == "Contact";
        private static bool IsCoil(string n) => n == "Coil" || n == "SCoil" || n == "RCoil" || n == "SetCoil" || n == "ResetCoil";
        private static bool IsCompare(string n) => n is "Eq" or "Ne" or "Gt" or "Lt" or "Ge" or "Le";
        private static bool IsWritingBox(string n) => n == "Move" || n == "Call";
        private static string CoilGlyph(string n) => n switch
        {
            "SCoil" or "SetCoil" => "(S)",
            "RCoil" or "ResetCoil" => "(R)",
            _ => "( )"
        };
        private static string CompareGlyph(string n) => n switch
        {
            "Eq" => "==", "Ne" => "<>", "Gt" => ">", "Lt" => "<", "Ge" => ">=", "Le" => "<=", _ => "?"
        };

        private static string DescribeBox(Part p)
        {
            if (p.Name == "Move")
            {
                var src = p.Operands.TryGetValue("in", out var i) ? i : "?";
                var dst = p.Operands.TryGetValue("out1", out var o) ? o : (p.Operands.TryGetValue("out", out var o2) ? o2 : "?");
                return $"MOVE {src} → {dst}";
            }
            return DescribeBoxInline(p);
        }

        private static string DescribeBoxInline(Part p)
        {
            var name = p.Name;
            if (p.Instance != null) name += $"[{p.Instance}]";
            var ops = FormatOperands(p);
            // timers commonly produce power at Q; note it
            if (p.Name is "TP" or "TON" or "TOF" or "TONR") return $"{name}{ops}.Q";
            if (p.Name is "PBox" or "NBox" or "P_TRIG" or "N_TRIG" or "Coil_P" or "Coil_N") return $"{name}{ops}(边沿)";
            return $"{name}{ops}";
        }

        private static string FormatOperands(Part p)
        {
            if (p.Operands.Count == 0) return "";
            var kv = p.Operands.Where(k => k.Key != "operand" || IsContact(p.Name) || IsCoil(p.Name))
                               .Select(k => p.Operands.Count == 1 && k.Key == "operand" ? k.Value : $"{k.Key}={k.Value}");
            var s = string.Join(", ", kv);
            return string.IsNullOrEmpty(s) ? "" : $"({s})";
        }

        // 操作数与 SCL 正文的读法只有一份实现（SimaticMlText）。原来这里用 Descendants 拼所有后代分量：
        // 数组下标吞掉符号名、调用与形参整体丢失、引号包住整条路径（issue #42）。
        private static (string text, bool literal) ReadAccess(XElement acc) => SimaticMlText.ReadAccess(acc);

        // ---- StructuredText (SCL/STL) ----

        private static string RenderStructuredText(XElement unit) => SimaticMlText.RenderStructuredText(unit);

        private static string FirstMultilingual(XElement unit, string composition)
        {
            var mt = unit.Elements("ObjectList").Elements("MultilingualText")
                         .FirstOrDefault(m => m.Attribute("CompositionName")?.Value == composition);
            var txt = mt?.Descendants("Text").FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Value))?.Value;
            return txt?.Trim() ?? "";
        }

        private static string IndentBlock(string text, string indent)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => indent + l)) + "\n";
        }

        private static void StripNamespaces(XDocument doc)
        {
            foreach (var e in doc.Descendants())
            {
                e.Name = e.Name.LocalName;
                var atts = e.Attributes()
                    .Where(a => !a.IsNamespaceDeclaration)
                    .Select(a => new XAttribute(a.Name.LocalName, a.Value)).ToList();
                e.ReplaceAttributes(atts);
            }
        }
    }
}
