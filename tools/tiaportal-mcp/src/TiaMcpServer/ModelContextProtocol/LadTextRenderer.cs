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
    ///
    /// It also prints the complete pin-level wiring of every part in the network. The boolean
    /// expression alone loses the wiring of boxes that do not drive a coil (TON trigger, arithmetic
    /// EN/IN1/IN2 branches, ET/ENO, open pins), which is exactly where "which pin is energized by
    /// what" matters. The wiring table is the full ladder netlist; the expression is the summary.
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
                else if (flg == null && (lang.Equals("LAD", StringComparison.OrdinalIgnoreCase) || lang.Equals("FBD", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.Append("   (空网络：0 个部件，0 条接线)\n");
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
                    sb.Append($"   (不支持的语言：{lang})\n");
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
            public string? BlockName;            // call target block name
            public string? BlockType;            // FB / FC / ...
            public Dictionary<string, string> Operands = new(StringComparer.OrdinalIgnoreCase); // pin name -> operand text
        }

        /// <summary>
        /// One LAD network reduced to parts + pin graph. A pin key is "uid|pin".
        /// Sources[pin] lists the output pins (or "RAIL") that energize it, so parallel branches
        /// are kept instead of being overwritten by the last wire.
        /// </summary>
        private sealed class NetModel
        {
            public List<Part> Order = new();
            public Dictionary<string, Part> Parts = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, (string text, bool literal)> AccessText = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> Links = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> Sources = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OpenPins = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> RailPins = new(StringComparer.OrdinalIgnoreCase);
            // NameCon <-> NameCon edges and roles inferred from the other end (Call pins have no
            // static input/output metadata in SimaticML). Without this a Call output never reaches
            // a coil and all its pins render as "↔".
            public List<(string from, string to)> Edges = new();
            public Dictionary<string, PinRole> InferredRoles = new(StringComparer.OrdinalIgnoreCase);
            public int WireCount;
        }

        private enum PinRole { Input, Output, Operand, Unknown }

        private static string Key(string uid, string pin) => uid + "|" + pin;

        private static (string uid, string pin) SplitKey(string key)
        {
            var i = key.LastIndexOf('|');
            return i < 0 ? (key, "") : (key.Substring(0, i), key.Substring(i + 1));
        }

        private static string RenderLadNetwork(XElement flg)
        {
            var net = BuildModel(flg);
            if (net.Order.Count == 0)
                return $"   (空网络：0 个部件，{net.WireCount} 条接线)\n";

            var sb = new StringBuilder();

            // Summary: every coil, plus MOVE/CALL boxes (the things that write a destination).
            foreach (var outp in net.Order.Where(p => IsCoil(p.Name) || p.Name == "Move" || p.Name == "Call"))
            {
                if (IsCoil(outp.Name))
                {
                    string expr = TracePin(net, Key(outp.UId, "in"), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    string coil = CoilGlyph(outp.Name);
                    string operand = outp.Operands.TryGetValue("operand", out var o) ? o : "?";
                    sb.Append($"   {operand} {coil}  ⇐  {(string.IsNullOrEmpty(expr) ? "RAIL(恒通)" : expr)}\n");
                }
                else
                {
                    string enPin = EnablePin(net, outp) ?? "en";
                    string en = TracePin(net, Key(outp.UId, enPin), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    sb.Append($"   当 [{(string.IsNullOrEmpty(en) ? "RAIL(恒通)" : en)}] 时: {DescribeBox(outp)}\n");
                }
            }

            // Full ladder netlist: every part and every pin, including boxes that never reach a coil.
            string wiring = RenderWiring(net);
            if (wiring.Length == 0)
            {
                var types = string.Join("/", net.Order.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
                sb.Append($"   (未识别：{net.Order.Count} 个部件，类型={types}，{net.WireCount} 条接线)\n");
                return sb.ToString();
            }
            sb.Append("   接线（逐引脚）:\n");
            sb.Append(wiring);
            return sb.ToString();
        }

        private static NetModel BuildModel(XElement flg)
        {
            var net = new NetModel();

            foreach (var acc in flg.Descendants("Access"))
            {
                var uid = acc.Attribute("UId")?.Value;
                if (uid == null) continue;
                net.AccessText[uid] = ReadAccess(acc);
            }

            var partsContainer = flg.Element("Parts");
            if (partsContainer != null)
            {
                foreach (var p in partsContainer.Elements())
                {
                    var uid = p.Attribute("UId")?.Value;
                    if (uid == null) continue;

                    if (p.Name.LocalName == "Call")
                    {
                        // A block call is <Call UId=...><CallInfo Name="FB284" BlockType="FB">
                        // <Instance .../></CallInfo></Call>. Old code only walked <Part>, so the
                        // call never got its own line and its pin targets rendered as "[82].Pin".
                        var ci = p.Element("CallInfo");
                        var call = new Part
                        {
                            UId = uid,
                            Name = "Call",
                            BlockName = ci?.Attribute("Name")?.Value,
                            BlockType = ci?.Attribute("BlockType")?.Value,
                            Instance = ci?.Element("Instance") is XElement callInst
                                ? SimaticMlText.ReadInstance(callInst)
                                : null
                        };
                        net.Parts[uid] = call;
                        net.Order.Add(call);
                        continue;
                    }

                    if (p.Name.LocalName != "Part") continue;
                    var part = new Part
                    {
                        UId = uid,
                        Name = p.Attribute("Name")?.Value ?? "?",
                        Negated = p.Elements("Negated").Any(),
                        Instance = p.Element("Instance") is XElement inst ? SimaticMlText.ReadInstance(inst) : null
                    };
                    net.Parts[uid] = part;
                    net.Order.Add(part);
                }
            }

            foreach (var wire in flg.Descendants("Wires").Elements("Wire"))
            {
                net.WireCount++;
                var ends = wire.Elements().ToList();
                bool hasRail = ends.Any(e => e.Name.LocalName == "Powerrail");
                bool hasOpen = ends.Any(e => e.Name.LocalName == "OpenCon");
                var idents = ends.Where(e => e.Name.LocalName == "IdentCon")
                                 .Select(e => e.Attribute("UId")?.Value)
                                 .Where(v => v != null).Select(v => v!).ToList();
                var pins = ends.Where(e => e.Name.LocalName == "NameCon")
                               .Select(e => (uid: e.Attribute("UId")?.Value, pin: e.Attribute("Name")?.Value))
                               .Where(t => t.uid != null && t.pin != null)
                               .Select(t => (uid: t.uid!, pin: t.pin!)).ToList();
                if (pins.Count == 0) continue;

                // Operand binding: Access (IdentCon) -> part pin (NameCon).
                // A LITERAL bound to a CONTACT operand is the important tell: a normally-open contact
                // wired to 0/FALSE permanently OPENS (disables) its rung; NC or 1/TRUE permanently CLOSES.
                // Literals on compare/move pins are normal, so only annotate contacts.
                if (idents.Count > 0)
                {
                    var at = net.AccessText.TryGetValue(idents[0], out var av) ? av : (text: "?", literal: false);
                    foreach (var nc in pins)
                    {
                        if (!net.Parts.TryGetValue(nc.uid, out var pt)) continue;
                        string text = at.text;
                        if (at.literal && IsContact(pt.Name) && nc.pin.Equals("operand", StringComparison.OrdinalIgnoreCase))
                            text = AnnotateContactConstant(pt, text);
                        pt.Operands[nc.pin] = text;
                        if (hasOpen) net.OpenPins.Add(Key(nc.uid, nc.pin));
                        if (hasRail) AddRail(net, nc);
                    }
                    continue;
                }

                foreach (var nc in pins)
                {
                    if (hasRail) AddRail(net, nc);
                    if (hasOpen) net.OpenPins.Add(Key(nc.uid, nc.pin));
                }
                if (hasOpen) continue;

                // NameCon <-> NameCon: record the visual link both ways and derive power flow
                // from an output pin into an input pin. Multiple wires into one pin = parallel.
                for (int i = 0; i < pins.Count; i++)
                {
                    for (int j = i + 1; j < pins.Count; j++)
                    {
                        var a = pins[i];
                        var b = pins[j];
                        AddLink(net, Key(a.uid, a.pin), Key(b.uid, b.pin));
                        AddLink(net, Key(b.uid, b.pin), Key(a.uid, a.pin));
                        net.Edges.Add((Key(a.uid, a.pin), Key(b.uid, b.pin)));
                    }
                }
            }

            InferPinRoles(net);
            RebuildFlow(net);
            return net;
        }

        /// <summary>
        /// Call pins have no static input/output direction in SimaticML. Infer it from the other
        /// end of each wire: contact/coil/box pins already have a known role, so a Call pin wired
        /// from an output is an input, and a Call pin wired to an input is an output.
        /// </summary>
        private static void InferPinRoles(NetModel net)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                foreach (var edge in net.Edges)
                {
                    var fromRole = RoleOf(net, edge.from);
                    var toRole = RoleOf(net, edge.to);

                    if (fromRole == PinRole.Unknown && toRole == PinRole.Output)
                        changed |= SetRole(net, edge.from, PinRole.Input);
                    else if (fromRole == PinRole.Unknown && toRole == PinRole.Input)
                        changed |= SetRole(net, edge.from, PinRole.Output);

                    if (toRole == PinRole.Unknown && fromRole == PinRole.Output)
                        changed |= SetRole(net, edge.to, PinRole.Input);
                    else if (toRole == PinRole.Unknown && fromRole == PinRole.Input)
                        changed |= SetRole(net, edge.to, PinRole.Output);
                }
                if (!changed) break;
            }
        }

        private static bool SetRole(NetModel net, string key, PinRole role)
        {
            if (net.InferredRoles.ContainsKey(key)) return false;
            net.InferredRoles[key] = role;
            return true;
        }

        /// <summary>
        /// Rebuild Sources after role inference. RAIL entries are kept; every NameCon edge is then
        /// walked again so a Call output that only became "Output" after inference reaches its
        /// downstream coil.
        /// </summary>
        private static void RebuildFlow(NetModel net)
        {
            var rebuilt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rail in net.RailPins)
                rebuilt[rail] = new List<string> { "RAIL" };

            foreach (var edge in net.Edges)
            {
                if (RoleOf(net, edge.from) != PinRole.Output || RoleOf(net, edge.to) != PinRole.Input)
                    continue;
                if (!rebuilt.TryGetValue(edge.to, out var list))
                    rebuilt[edge.to] = list = new List<string>();
                if (!list.Contains(edge.from)) list.Add(edge.from);
            }

            net.Sources = rebuilt;
        }

        private static void AddRail(NetModel net, (string uid, string pin) nc)
        {
            var key = Key(nc.uid, nc.pin);
            net.RailPins.Add(key);
            if (!net.Sources.TryGetValue(key, out var list)) net.Sources[key] = list = new List<string>();
            if (!list.Contains("RAIL")) list.Add("RAIL");
        }

        private static void AddLink(NetModel net, string from, string to)
        {
            if (!net.Links.TryGetValue(from, out var list)) net.Links[from] = list = new List<string>();
            if (!list.Contains(to)) list.Add(to);
        }

        private static string AnnotateContactConstant(Part pt, string text)
        {
            bool truthy = text is "1" or "TRUE" or "True";
            bool falsy = text is "0" or "FALSE" or "False";
            // NO contact: passes when operand true; NC (Negated): passes when operand false.
            bool alwaysOpen = (!pt.Negated && falsy) || (pt.Negated && truthy);
            bool alwaysClosed = (!pt.Negated && truthy) || (pt.Negated && falsy);
            return text + (alwaysOpen ? " ⟨恒断·禁用本行⟩" : alwaysClosed ? " ⟨恒通⟩" : " ⟨常量触点⟩");
        }

        // ---- full wiring table ----

        private static string RenderWiring(NetModel net)
        {
            var sb = new StringBuilder();
            foreach (var p in net.Order)
            {
                var pins = CollectPins(net, p);
                if (pins.Count == 0) continue;

                var segments = new List<string>();
                foreach (var pin in pins)
                {
                    string key = Key(p.UId, pin);
                    var role = RoleOf(net, p, pin);
                    p.Operands.TryGetValue(pin, out var opText);
                    bool hasOperand = opText != null;
                    string? seg = null;

                    if (role == PinRole.Operand)
                    {
                        if (hasOperand)
                            seg = "operand=" + (p.Negated && IsContact(p.Name) ? "/" + opText : opText);
                    }
                    else if (role == PinRole.Output)
                    {
                        var values = new List<string>();
                        if (hasOperand) values.Add(opText!);
                        if (net.Links.TryGetValue(key, out var links))
                            values.AddRange(links.Select(l => EndpointLabel(net, l)));
                        if (net.OpenPins.Contains(key)) values.Add("未接");
                        if (values.Count > 0)
                        {
                            seg = values.Count == 1 && values[0] == "未接"
                                ? pin + "=未接"
                                : pin + "⇒" + string.Join(", ", values.Distinct());
                        }
                    }
                    else // Input / Unknown
                    {
                        if (net.OpenPins.Contains(key))
                        {
                            seg = pin + "=未接";
                        }
                        else
                        {
                            if (net.Sources.TryGetValue(key, out var srcs) && srcs.Count > 0)
                            {
                                string expr = TracePin(net, key, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                                if (!string.IsNullOrEmpty(expr)) seg = pin + "⇐" + expr;
                                else if (net.RailPins.Contains(key)) seg = pin + "⇐母线";
                            }
                            if (seg == null && hasOperand) seg = pin + "=" + opText;
                            if (seg == null && net.Links.TryGetValue(key, out var links))
                                seg = pin + (role == PinRole.Unknown ? "↔" : "⇐") +
                                      string.Join(", ", links.Select(l => EndpointLabel(net, l)));
                            if (seg == null && net.RailPins.Contains(key)) seg = pin + "⇐母线";
                        }
                    }

                    if (!string.IsNullOrEmpty(seg) && !segments.Contains(seg!)) segments.Add(seg!);
                }

                if (segments.Count == 0) continue;
                sb.Append("     [").Append(p.UId).Append("] ").Append(PartLabel(p));
                sb.Append("  ").Append(string.Join(" | ", segments)).Append('\n');
            }
            return sb.ToString();
        }

        private static List<string> CollectPins(NetModel net, Part p)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in p.Operands.Keys) set.Add(k);

            void AddKey(string key)
            {
                var (uid, pin) = SplitKey(key);
                if (string.Equals(uid, p.UId, StringComparison.OrdinalIgnoreCase)) set.Add(pin);
            }

            foreach (var k in net.Sources.Keys) AddKey(k);
            foreach (var k in net.Links.Keys) AddKey(k);
            foreach (var k in net.OpenPins) AddKey(k);
            foreach (var k in net.RailPins) AddKey(k);

            return set.OrderBy(pin => RoleOrder(RoleOf(net, p, pin)))
                      .ThenBy(PinOrder)
                      .ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int RoleOrder(PinRole role) => role switch
        {
            PinRole.Input => 0,
            PinRole.Operand => 1,
            PinRole.Output => 2,
            _ => 3
        };

        private static string EndpointLabel(NetModel net, string key)
        {
            var (uid, pin) = SplitKey(key);
            return net.Parts.TryGetValue(uid, out var p)
                ? $"[{uid}] {PartLabel(p)}.{pin}"
                : $"[{uid}].{pin}";
        }

        private static string PartLabel(Part p)
        {
            if (p.Name == "Call")
            {
                var call = "Call";
                if (!string.IsNullOrEmpty(p.Instance)) call += " #" + p.Instance;
                if (!string.IsNullOrEmpty(p.BlockName)) call += $" ({p.BlockName})";
                return call;
            }

            var name = p.Name;
            if (!string.IsNullOrEmpty(p.Instance)) name += $"[{p.Instance}]";
            if ((IsContact(p.Name) || IsCoil(p.Name)) && p.Operands.TryGetValue("operand", out var op))
                name += " " + (p.Negated && IsContact(p.Name) ? "/" + op : op);
            return name;
        }

        private static int PinOrder(string pin)
        {
            var n = pin.ToLowerInvariant();
            if (n == "en") return 0;
            if (n == "pre") return 1;
            if (n == "in") return 2;
            if (n.StartsWith("in") && n.Length > 2) return 10 + PinSuffix(n);
            if (n == "pt") return 20;
            if (n == "bit") return 21;
            if (n == "pv") return 22;
            if (n == "operand") return 30;
            if (n == "out") return 40;
            if (n == "eno") return 41;
            if (n == "q") return 42;
            if (n == "et") return 43;
            if (n.StartsWith("out")) return 50 + PinSuffix(n);
            if (n == "cv") return 60;
            return 70;
        }

        private static int PinSuffix(string name)
        {
            int i = 0;
            while (i < name.Length && !char.IsDigit(name[i])) i++;
            return i < name.Length && int.TryParse(name.Substring(i), out var n) ? n : 0;
        }

        private static PinRole RoleOf(NetModel net, Part p, string pin) => RoleOf(net, Key(p.UId, pin));

        private static PinRole RoleOf(NetModel net, string key)
        {
            if (net.InferredRoles.TryGetValue(key, out var inferred)) return inferred;
            var (uid, pin) = SplitKey(key);
            return net.Parts.TryGetValue(uid, out var p) ? RoleOf(p, pin) : PinRole.Unknown;
        }

        private static PinRole RoleOf(Part p, string pin)
        {
            var n = pin.ToLowerInvariant();
            if (n == "operand") return PinRole.Operand;
            if (IsContact(p.Name)) return n == "out" ? PinRole.Output : n == "in" ? PinRole.Input : PinRole.Operand;
            if (IsCoil(p.Name)) return n == "in" ? PinRole.Input : PinRole.Operand;
            if (IsTimer(p.Name) || p.Name is "CTU" or "CTD" or "CTUD")
            {
                if (n is "q" or "et" or "cv") return PinRole.Output;
                return PinRole.Input;
            }
            if (n is "eno" or "q" or "et" or "cv" or "out") return PinRole.Output;
            if (n.StartsWith("out")) return PinRole.Output;
            if (n is "en" or "pre" or "in" or "pt" or "bit" or "pv" or "cu" or "r" or "reset") return PinRole.Input;
            if (n.StartsWith("in")) return PinRole.Input;
            if (p.Name == "Call") return PinRole.Unknown;
            return PinRole.Input;
        }

        // Trace power flow backward from an input pin (or an output pin node, for nested rungs)
        // into a boolean-ish expression. Multiple sources mean parallel branches.
        private static string TracePin(NetModel net, string key, HashSet<string> guard)
        {
            if (string.IsNullOrEmpty(key) || key == "RAIL") return "";
            if (!net.Sources.TryGetValue(key, out var srcs) || srcs.Count == 0) return "";
            if (!guard.Add("P:" + key)) return "…";  // cycle guard
            try
            {
                var exprs = new List<string>();
                foreach (var src in srcs)
                {
                    // A direct rail branch makes the rung always true. Keep it visible when it is
                    // OR-ed with other branches instead of silently dropping it.
                    if (src == "RAIL")
                    {
                        if (srcs.Count > 1) exprs.Add("1");
                        continue;
                    }
                    var e = TraceNode(net, src, guard);
                    if (!string.IsNullOrWhiteSpace(e) && !exprs.Contains(e)) exprs.Add(e);
                }
                if (exprs.Count == 0) return "";
                return exprs.Count == 1 ? exprs[0] : "(" + string.Join(" + ", exprs) + ")";
            }
            finally { guard.Remove("P:" + key); }
        }

        private static string TraceNode(NetModel net, string node, HashSet<string> guard)
        {
            if (node == "RAIL" || string.IsNullOrEmpty(node)) return "";
            var (uid, pin) = SplitKey(node);
            if (!net.Parts.TryGetValue(uid, out var p)) return "?";

            if (IsContact(p.Name))
            {
                string upstream = TracePin(net, Key(uid, "in"), guard);
                string lit = (p.Negated ? "/" : "") + OperandText(p, "operand");
                return Series(upstream, lit);
            }
            if (IsCompare(p.Name))
            {
                string upstream = TracePin(net, Key(uid, "pre"), guard);
                if (string.IsNullOrEmpty(upstream)) upstream = TracePin(net, Key(uid, EnablePin(net, p) ?? "en"), guard);
                var a = OperandText(p, "in1");
                var b = OperandText(p, "in2");
                return Series(upstream, $"({a} {CompareGlyph(p.Name)} {b})");
            }
            if (p.Name == "O")  // OR box: inputs in1,in2,... are parallel branches
            {
                var branches = new List<string>();
                foreach (var brPin in CollectPins(net, p).Where(x => x.StartsWith("in", StringComparison.OrdinalIgnoreCase)))
                {
                    var b = TracePin(net, Key(uid, brPin), guard);
                    if (!string.IsNullOrWhiteSpace(b) && !branches.Contains(b)) branches.Add(b);
                }
                return branches.Count == 0 ? "" : "(" + string.Join(" + ", branches) + ")";
            }
            if (IsCoil(p.Name))
            {
                return TracePin(net, Key(uid, "in"), guard);
            }
            if (p.Name == "Call")
            {
                string callEn = TracePin(net, Key(uid, EnablePin(net, p) ?? "en"), guard);
                return Series(callEn, DescribeBoxInline(p) + "." + pin);
            }
            // timers / edges / arithmetic / other boxes producing power at Q/OUT/ENO
            string en = TracePin(net, Key(uid, EnablePin(net, p) ?? "en"), guard);
            return Series(en, DescribeBoxInline(p));
        }

        private static string EnablePin(NetModel net, Part p)
        {
            foreach (var cand in new[] { "en", "pre", "in" })
                if (HasPin(net, p, cand)) return cand;
            return "";
        }

        private static bool HasPin(NetModel net, Part p, string pin)
        {
            var key = Key(p.UId, pin);
            return p.Operands.ContainsKey(pin) || net.Sources.ContainsKey(key) || net.Links.ContainsKey(key)
                || net.RailPins.Contains(key) || net.OpenPins.Contains(key);
        }

        private static string OperandText(Part p, string pin)
            => p.Operands.TryGetValue(pin, out var v) ? v : "?";

        private static string Series(string upstream, string term)
            => string.IsNullOrEmpty(upstream) ? term : upstream + " · " + term;

        // ---- helpers ----

        private static bool IsContact(string n) => n == "Contact";
        private static bool IsCoil(string n) => n == "Coil" || n == "SCoil" || n == "RCoil" || n == "SetCoil" || n == "ResetCoil";
        private static bool IsCompare(string n) => n is "Eq" or "Ne" or "Gt" or "Lt" or "Ge" or "Le";
        private static bool IsTimer(string n) => n is "TP" or "TON" or "TOF" or "TONR";

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
            if (p.Name == "Call") return PartLabel(p) + FormatOperands(p);

            var name = p.Name;
            if (p.Instance != null) name += $"[{p.Instance}]";
            var ops = IsTimer(p.Name)
                ? FormatOperands(p, "ET", "Q")
                : (p.Name is "CTU" or "CTD" or "CTUD" ? FormatOperands(p, "CV", "Q") : FormatOperands(p));
            // timers commonly produce power at Q; note it
            if (IsTimer(p.Name)) return $"{name}{ops}.Q";
            if (p.Name is "PBox" or "NBox" or "P_TRIG" or "N_TRIG" or "Coil_P" or "Coil_N") return $"{name}{ops}(边沿)";
            return $"{name}{ops}";
        }

        private static string FormatOperands(Part p, params string[] exclude)
        {
            if (p.Operands.Count == 0) return "";
            var ex = new HashSet<string>(exclude ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var kv = p.Operands
                .Where(k => !ex.Contains(k.Key))
                .Where(k => !k.Key.Equals("operand", StringComparison.OrdinalIgnoreCase) || IsContact(p.Name) || IsCoil(p.Name))
                .OrderBy(k => PinOrder(k.Key))
                .Select(k => p.Operands.Count == 1 && k.Key.Equals("operand", StringComparison.OrdinalIgnoreCase) ? k.Value : $"{k.Key}={k.Value}");
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
