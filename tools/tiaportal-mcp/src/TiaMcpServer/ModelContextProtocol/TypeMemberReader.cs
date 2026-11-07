using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Reads the declared fields of an exported PLC type (SimaticML) into a flat list that keeps the
    /// hierarchy in the path (issue #62): dotted paths such as "status.ready" (array members keep the
    /// array's name; the bounds are in dataType, e.g. "Array[0..3] of Struct"), data type, explicit
    /// start value(s) and comment.
    ///
    /// Two nesting shapes are followed: a Member directly containing Members (Struct), and
    /// Member → Sections → Section → Member. Walking only the first shape silently drops fields of the
    /// second. Completeness is checked independently of the walk: every Member element in the document
    /// must have been visited, otherwise <c>complete=false</c> and the count of unvisited ones is reported.
    /// The raw XML stays the source of truth (ExportType); this is a projection and says when it is partial.
    /// Zero dependencies so the offline suite can feed it synthetic XML.
    /// </summary>
    internal static class TypeMemberReader
    {
        internal static JsonObject Read(string xml)
        {
            var members = new JsonArray();
            var issues = new JsonArray();
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                issues.Add("xml-parse-error: " + ex.Message);
                return Result(members, false, issues);
            }

            var interfaceSections = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sections");
            if (interfaceSections == null)
            {
                issues.Add("no-interface: the export has no Interface/Sections element (not a PLC type export?).");
                return Result(members, false, issues);
            }

            var visited = new HashSet<XElement>();
            foreach (var section in Children(interfaceSections, "Section"))
                foreach (var m in Children(section, "Member"))
                    Walk(m, "", members, visited);

            var total = interfaceSections.Descendants().Count(e => e.Name.LocalName == "Member");
            var complete = visited.Count == total;
            if (!complete)
                issues.Add($"unvisited-members: {total - visited.Count} of {total} Member elements sit in a container this reader does not follow; the field list is INCOMPLETE - read the raw XML (ExportType).");
            return Result(members, complete, issues);
        }

        private static void Walk(XElement member, string parentPath, JsonArray output, HashSet<XElement> visited)
        {
            visited.Add(member);
            var name = (string?)member.Attribute("Name") ?? "?";
            var path = parentPath.Length == 0 ? name : parentPath + "." + name;
            var entry = new JsonObject
            {
                ["path"] = path,
                ["dataType"] = (string?)member.Attribute("Datatype")
            };

            var start = Children(member, "StartValue").FirstOrDefault();
            if (start != null) entry["startValue"] = start.Value;

            var elementStarts = new JsonArray();
            foreach (var sub in Children(member, "Subelement"))
            {
                var sv = Children(sub, "StartValue").FirstOrDefault();
                if (sv != null) elementStarts.Add(new JsonObject { ["index"] = (string?)sub.Attribute("Path"), ["startValue"] = sv.Value });
            }
            if (elementStarts.Count > 0) entry["elementStartValues"] = elementStarts;

            var comment = Children(member, "Comment").SelectMany(c => c.Elements()).FirstOrDefault(t => t.Name.LocalName == "MultiLanguageText")?.Value;
            if (!string.IsNullOrEmpty(comment)) entry["comment"] = comment;

            output.Add(entry);

            foreach (var child in Children(member, "Member"))
                Walk(child, path, output, visited);
            foreach (var sections in Children(member, "Sections"))
                foreach (var section in Children(sections, "Section"))
                    foreach (var child in Children(section, "Member"))
                        Walk(child, path, output, visited);
        }

        private static IEnumerable<XElement> Children(XElement e, string localName)
            => e.Elements().Where(c => c.Name.LocalName == localName);

        private static JsonObject Result(JsonArray members, bool complete, JsonArray issues)
            => new JsonObject { ["members"] = members, ["memberCount"] = members.Count, ["complete"] = complete, ["issues"] = issues };
    }
}
