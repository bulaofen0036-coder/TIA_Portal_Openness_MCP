using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Zero-dependency reader for the &lt;Interface&gt; section of SimaticML block/type exports.
    ///
    /// Why this exists: in V19 the typed Openness API exposes <c>DataBlock.Interface</c> only;
    /// FB/FC/OB and PlcType (UDT) have no CLR property for their declarations. The XML export
    /// contains the same information for every object kind, so the MCP server exports the
    /// object to a temp file **on the engine machine** and parses it there. That also keeps
    /// the result on the correct side of the VM/host file boundary.
    ///
    /// The parser deliberately matches element names by LocalName: the interface payload uses
    /// the http://www.siemens.com/automation/Openness/SW/Interface/v5 namespace, while the
    /// surrounding SimaticML elements are unprefixed. Namespace-prefix matching would break
    /// on either half.
    /// </summary>
    public sealed class InterfaceStructure
    {
        public string ObjectKind { get; set; } = "Block";
        public string? ObjectType { get; set; }
        public string? ObjectName { get; set; }
        public string? ObjectNamespace { get; set; }
        public int? Number { get; set; }
        public string? ProgrammingLanguage { get; set; }
        public string? MemoryLayout { get; set; }
        public int MemberCount { get; set; }
        public bool Truncated { get; set; }
        public bool HasInterface { get; set; }
        public bool HasSections { get; set; }
        public List<InterfaceSection> Sections { get; set; } = new List<InterfaceSection>();
    }

    public sealed class InterfaceSection
    {
        public string Name { get; set; } = "";
        public List<InterfaceMember> Members { get; set; } = new List<InterfaceMember>();
    }

    public sealed class InterfaceMember
    {
        public string Name { get; set; } = "";
        public string Datatype { get; set; } = "";
        public string? Path { get; set; }
        public string? StartValue { get; set; }
        public string? Comment { get; set; }
        public string? CommentLanguage { get; set; }
        public string? Remanence { get; set; }
        public string? Accessibility { get; set; }
        public string? Version { get; set; }
        public bool? Informative { get; set; }
        public string? ArrayBounds { get; set; }
        public string? ElementType { get; set; }
        public List<InterfaceMember>? Members { get; set; }
    }

    public sealed class InterfaceChildListing
    {
        public List<string> Items { get; } = new List<string>();
        public int Total { get; set; }
        public int EffectiveLimit { get; set; }
        public bool Truncated { get; set; }
        public bool SourceTruncated { get; set; }
        public bool IsSections { get; set; }
    }

    public static class InterfaceXmlReader
    {
        private static readonly Regex ArrayType = new Regex(
            @"^\s*Array\s*\[(?<bounds>.+?)\]\s*of\s+(?<element>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static InterfaceStructure ReadFile(
            string xmlPath,
            string objectKind = "Block",
            int maxMembers = 1000,
            bool includeComments = true)
        {
            if (string.IsNullOrWhiteSpace(xmlPath))
                throw new ArgumentException("xmlPath is required", nameof(xmlPath));
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("SimaticML export not found", xmlPath);

            return Parse(File.ReadAllText(xmlPath), objectKind, maxMembers, includeComments);
        }

        /// <summary>
        /// Build the top-level child list used by the legacy ListObjectChildren(Interface*) route.
        /// Kept pure so the limit/truncation semantics can be pinned by the offline suite without
        /// a TIA instance. The parser's maxMembers cap is separate: this limit applies only to the
        /// items actually emitted, so nested struct members cannot consume the listing budget.
        /// </summary>
        public static InterfaceChildListing BuildChildren(
            IEnumerable<InterfaceSection>? sections,
            bool sourceTruncated,
            string? collectionProperty,
            int limit)
        {
            var list = sections?.ToList() ?? new List<InterfaceSection>();
            var property = collectionProperty ?? string.Empty;
            var isSections = property.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                || property.Equals("Interface.Sections", StringComparison.OrdinalIgnoreCase);
            var take = Math.Max(1, Math.Min(2000, limit));

            var listing = new InterfaceChildListing
            {
                IsSections = isSections,
                SourceTruncated = sourceTruncated,
                EffectiveLimit = take,
                Total = isSections
                    ? list.Count
                    : list.Sum(s => s.Members?.Count ?? 0)
            };

            if (isSections)
            {
                foreach (var section in list)
                {
                    listing.Items.Add(section.Name);
                    if (listing.Items.Count >= take) break;
                }
            }
            else
            {
                foreach (var section in list)
                {
                    foreach (var member in section.Members ?? new List<InterfaceMember>())
                    {
                        listing.Items.Add(section.Name + "/" + member.Name);
                        if (listing.Items.Count >= take) break;
                    }
                    if (listing.Items.Count >= take) break;
                }
            }

            listing.Truncated = listing.SourceTruncated || listing.Total > listing.Items.Count;
            return listing;
        }

        public static InterfaceStructure Parse(
            string xml,
            string objectKind = "Block",
            int maxMembers = 1000,
            bool includeComments = true)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidDataException("SimaticML export is empty.");

            XDocument doc;
            try
            {
                // SimaticML never needs a DTD. Pin the safe settings instead of relying on
                // framework defaults, and cap the document size so a corrupted export cannot
                // exhaust the engine process.
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 50_000_000
                };
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                    doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("SimaticML export is not valid XML: " + ex.Message, ex);
            }

            var obj = doc.Root?
                .DescendantsAndSelf()
                .FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) ||
                    string.Equals(e.Name.LocalName, "SW.Types.PlcStruct", StringComparison.Ordinal));

            if (obj == null)
                throw new InvalidDataException(
                    "No SW.Blocks.* or SW.Types.PlcStruct element found; this is not a block/type export.");

            var attrList = Child(obj, "AttributeList");
            if (attrList == null)
                throw new InvalidDataException($"'{obj.Name.LocalName}' has no AttributeList element.");

            var structure = new InterfaceStructure
            {
                ObjectKind = objectKind,
                ObjectType = obj.Name.LocalName,
                ObjectName = Value(attrList, "Name"),
                ObjectNamespace = Value(attrList, "Namespace"),
                Number = ParseInt(Value(attrList, "Number")),
                ProgrammingLanguage = Value(attrList, "ProgrammingLanguage"),
                MemoryLayout = Value(attrList, "MemoryLayout")
            };

            // A block without an <Interface> is unusual but can happen for an empty OB.
            // Return an explicit empty structure instead of pretending the object has members.
            var iface = Child(attrList, "Interface");
            if (iface == null)
                return structure;
            structure.HasInterface = true;

            var sectionsEl = Child(iface, "Sections");
            if (sectionsEl == null)
                return structure;
            structure.HasSections = true;

            var ctx = new ParseContext
            {
                MaxMembers = Math.Max(1, Math.Min(5000, maxMembers)),
                IncludeComments = includeComments
            };

            foreach (var sectionEl in sectionsEl.Elements().Where(e => e.Name.LocalName == "Section"))
            {
                var section = new InterfaceSection { Name = Attr(sectionEl, "Name") ?? "" };
                foreach (var memberEl in sectionEl.Elements().Where(e => e.Name.LocalName == "Member"))
                {
                    var member = ParseMember(memberEl, "", ctx);
                    if (member != null)
                        section.Members.Add(member);
                }
                structure.Sections.Add(section);
            }

            structure.MemberCount = ctx.Count;
            structure.Truncated = ctx.Truncated;
            return structure;
        }

        private sealed class ParseContext
        {
            public int MaxMembers;
            public int Count;
            public bool Truncated;
            public bool IncludeComments;
        }

        private static InterfaceMember? ParseMember(
            XElement el,
            string parentPath,
            ParseContext ctx)
        {
            if (ctx.Count >= ctx.MaxMembers)
            {
                ctx.Truncated = true;
                return null;
            }

            ctx.Count++;

            var name = Attr(el, "Name") ?? "";
            var datatype = Attr(el, "Datatype") ?? "";
            var member = new InterfaceMember
            {
                Name = name,
                Datatype = datatype,
                Path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "." + name,
                Remanence = Attr(el, "Remanence"),
                Accessibility = Attr(el, "Accessibility"),
                Version = Attr(el, "Version")
            };

            var informative = Attr(el, "Informative");
            if (bool.TryParse(informative, out var inf))
                member.Informative = inf;

            var startValue = Child(el, "StartValue");
            if (startValue != null)
                member.StartValue = startValue.Value.Trim();

            if (ctx.IncludeComments)
                ReadComment(el, member);

            var array = ArrayType.Match(datatype);
            if (array.Success)
            {
                member.ArrayBounds = array.Groups["bounds"].Value.Trim();
                member.ElementType = array.Groups["element"].Value.Trim();
            }

            foreach (var nested in el.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                var child = ParseMember(nested, member.Path ?? name, ctx);
                if (child == null)
                    continue;
                member.Members ??= new List<InterfaceMember>();
                member.Members.Add(child);
            }

            // A struct member can also arrive as <Member><Sections><Section>...</Section></Sections></Member>.
            // Treat those members as children of this member rather than losing them.
            foreach (var nestedSections in el.Elements().Where(e => e.Name.LocalName == "Sections"))
            {
                foreach (var section in nestedSections.Elements().Where(e => e.Name.LocalName == "Section"))
                {
                    foreach (var nested in section.Elements().Where(e => e.Name.LocalName == "Member"))
                    {
                        var child = ParseMember(nested, member.Path ?? name, ctx);
                        if (child == null)
                            continue;
                        member.Members ??= new List<InterfaceMember>();
                        member.Members.Add(child);
                    }
                }
            }

            return member;
        }

        private static void ReadComment(XElement memberEl, InterfaceMember member)
        {
            var comment = Child(memberEl, "Comment");
            if (comment == null)
                return;

            // Shape A (member comments): <MultiLanguageText Lang="zh-CN">text</MultiLanguageText>
            var direct = comment.Descendants()
                .Where(e => e.Name.LocalName == "MultiLanguageText")
                .Select(e => new { Lang = Attr(e, "Lang") ?? "", Text = e.Value.Trim() })
                .Where(x => !string.IsNullOrEmpty(x.Text))
                .ToList();

            var chosen = direct.FirstOrDefault(x => IsLanguage(x.Lang, "zh-CN"))
                      ?? direct.FirstOrDefault(x => IsLanguage(x.Lang, "en-US"))
                      ?? direct.FirstOrDefault();
            if (chosen != null)
            {
                member.Comment = chosen.Text;
                member.CommentLanguage = chosen.Lang;
                return;
            }

            // Shape B (object comments): <MultilingualText><ObjectList><MultilingualTextItem>
            //   <AttributeList><Culture>zh-CN</Culture><Text>...</Text></AttributeList>
            var items = comment.Descendants()
                .Where(e => e.Name.LocalName == "MultilingualTextItem")
                .Select(e =>
                {
                    var attrs = Child(e, "AttributeList");
                    return new
                    {
                        Lang = attrs == null ? "" : Value(attrs, "Culture") ?? "",
                        Text = attrs == null ? "" : (Value(attrs, "Text") ?? "").Trim()
                    };
                })
                .Where(x => !string.IsNullOrEmpty(x.Text))
                .ToList();

            var pick = items.FirstOrDefault(x => IsLanguage(x.Lang, "zh-CN"))
                    ?? items.FirstOrDefault(x => IsLanguage(x.Lang, "en-US"))
                    ?? items.FirstOrDefault();
            if (pick != null)
            {
                member.Comment = pick.Text;
                member.CommentLanguage = pick.Lang;
            }
        }

        private static bool IsLanguage(string actual, string wanted) =>
            string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);

        private static XElement? Child(XElement parent, string localName) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

        private static string? Value(XElement parent, string localName) =>
            Child(parent, localName)?.Value;

        private static string? Attr(XElement element, string localName) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

        private static int? ParseInt(string? text) =>
            int.TryParse(text, out var value) ? value : (int?)null;
    }
}
