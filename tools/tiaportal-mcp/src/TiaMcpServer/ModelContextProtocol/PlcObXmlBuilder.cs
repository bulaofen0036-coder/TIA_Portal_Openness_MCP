using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// PLC OB（组织块）XML 构造器，issue #30。
    ///
    /// 为什么必须走 XML：.s7dcl 的 ORGANIZATION_BLOCK 头里既没有块号也没有事件类别，导入后一律
    /// 变成「程序循环」OB（V21 实测：Startup 内容导进去是 Program cycle、编号 1），而且不报错。
    /// SimaticML 里 OB 的 SecondaryType 与块号绑定，手册：「导入时检查这一对应关系，不对则抛
    /// Recoverable 异常」—— 所以这里在生成 XML 之前就按同一规则校验，错在构建期而不是导入期。
    ///
    /// 第一版支持三类（V21 实测可导入、可编译、可读回）：
    ///   ProgramCycle     OB1 或 OB123..32767
    ///   Startup          OB100 或 OB123..32767
    ///   CyclicInterrupt  OB30..38 或 OB123..32767，必须给 CyclicTime（µs）
    /// 硬件中断、时间中断、错误 OB 等需要和硬件事件/时间组态配合，块 XML 表达不了，明确拒绝。
    ///
    /// Input 段刻意留空：OB 的启动信息（Initial_Call / Remanence / LostRetentive / Event_Count …）
    /// 由 TIA 按类型自动补上（实测导出里都有），自己写反而可能和类型对不上。
    /// </summary>
    public static class PlcObXmlBuilder
    {
        private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
        private static readonly XNamespace StructuredTextNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";

        public const string ProgramCycle = "ProgramCycle";
        public const string Startup = "Startup";
        public const string CyclicInterrupt = "CyclicInterrupt";

        public static IReadOnlyList<string> SupportedEventClasses { get; } = new[] { ProgramCycle, Startup, CyclicInterrupt };

        /// <summary>循环中断的周期上限：60 s（以 µs 计）。更细的 CPU 相关下限交给 TIA，导入后读回兜底。</summary>
        public const int MaxCyclicTimeUs = 60_000_000;

        private const int FirstFreeNumber = 123;
        private const int MaxObNumber = 32767;

        /// <summary>
        /// 把用户给的事件类别规范成 SimaticML 的 SecondaryType 写法。
        /// 忽略大小写、空格、下划线、连字符（"Program cycle" / "program_cycle" 都行）；
        /// 没给时按块号推断（1 / 100 / 30..38），123 以上的自由编号必须显式给出。
        /// </summary>
        public static string NormalizeEventClass(string? eventClass, int obNumber)
        {
            var key = new string((eventClass ?? "").Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-').ToArray()).ToLowerInvariant();
            if (key.Length == 0)
            {
                if (obNumber == 1) return ProgramCycle;
                if (obNumber == 100) return Startup;
                if (obNumber >= 30 && obNumber <= 38) return CyclicInterrupt;
                throw new ArgumentException(
                    $"eventClass is required for OB{obNumber}: only OB1 / OB100 / OB30..38 imply their class. "
                    + $"Supported: {string.Join(", ", SupportedEventClasses)}.");
            }

            switch (key)
            {
                case "programcycle": return ProgramCycle;
                case "startup": return Startup;
                case "cyclicinterrupt": return CyclicInterrupt;
            }

            throw new ArgumentException(
                $"eventClass '{eventClass}' is not supported by kind=ob yet. Supported: {string.Join(", ", SupportedEventClasses)}. "
                + "Hardware/time-of-day/time-delay interrupts and error OBs need hardware or schedule configuration that block XML "
                + "cannot carry: export an existing OB of that type with ExportBlock and import it with ImportBlock.");
        }

        /// <summary>块号与类别是否匹配 —— 与 TIA 导入时的检查同一口径（实测 OB101 配 CyclicInterrupt 被拒）。</summary>
        public static void ValidateNumberForClass(string eventClass, int obNumber)
        {
            if (obNumber >= FirstFreeNumber && obNumber <= MaxObNumber) return;

            bool ok = eventClass switch
            {
                ProgramCycle => obNumber == 1,
                Startup => obNumber == 100,
                CyclicInterrupt => obNumber >= 30 && obNumber <= 38,
                _ => false
            };
            if (ok) return;

            var allowed = eventClass switch
            {
                ProgramCycle => "1 or 123..32767",
                Startup => "100 or 123..32767",
                CyclicInterrupt => "30..38 or 123..32767",
                _ => "123..32767"
            };
            throw new ArgumentException(
                $"OB{obNumber} cannot be a {eventClass} OB: TIA only accepts {allowed} for that class "
                + "(the import would fail with \"is not a valid OB number\").");
        }

        public static XDocument BuildDocument(
            string obName,
            int obNumber,
            string eventClass,
            int? cyclicTimeUs,
            int? phaseOffsetUs,
            IEnumerable<PlcBlockMemberDefinition> temps,
            string structuredTextInnerXml,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string networkCommentZhCn = "",
            string networkTitleZhCn = "")
        {
            if (string.IsNullOrWhiteSpace(obName))
                throw new ArgumentException("OB name must not be empty.", nameof(obName));
            if (obNumber <= 0 || obNumber > MaxObNumber)
                throw new ArgumentException($"OB number must be 1..{MaxObNumber}.", nameof(obNumber));

            var cls = NormalizeEventClass(eventClass, obNumber);
            ValidateNumberForClass(cls, obNumber);

            if (cls == CyclicInterrupt)
            {
                if (!cyclicTimeUs.HasValue)
                    throw new ArgumentException("A CyclicInterrupt OB needs cyclicTimeUs (the interval in microseconds, e.g. 100000 = 100 ms).", nameof(cyclicTimeUs));
                if (cyclicTimeUs.Value <= 0 || cyclicTimeUs.Value > MaxCyclicTimeUs)
                    throw new ArgumentException($"cyclicTimeUs must be 1..{MaxCyclicTimeUs} µs (got {cyclicTimeUs.Value}).", nameof(cyclicTimeUs));
                if (phaseOffsetUs.HasValue && (phaseOffsetUs.Value < 0 || phaseOffsetUs.Value >= cyclicTimeUs.Value))
                    throw new ArgumentException($"phaseOffsetUs must be 0..cyclicTimeUs-1 (got {phaseOffsetUs.Value}).", nameof(phaseOffsetUs));
            }
            else if (cyclicTimeUs.HasValue || phaseOffsetUs.HasValue)
            {
                throw new ArgumentException($"cyclicTimeUs / phaseOffsetUs only apply to CyclicInterrupt OBs, not {cls}.");
            }

            var tempMembers = temps?.ToArray() ?? Array.Empty<PlcBlockMemberDefinition>();
            ValidateMembers(tempMembers);

            var st = XElement.Parse("<StructuredText xmlns=\"" + StructuredTextNs + "\">" + (structuredTextInnerXml ?? "") + "</StructuredText>");

            // 与 FC/FB 组合器同一套 ObjectList 编号：块 Comment 1/2、CompileUnit 3（Comment 4/5、Title 6/7）、块 Title 8/9。
            // 但标题/注释**只在给了文字时才写**：zh-CN 文本导进一个没有中文项目语言的工程会整块失败
            // （V21 实测："culture 'zh-CN' does not exist within the current project"），空文本毫无用处却会触发它。
            var compileUnit = new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", "3"),
                new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    new XElement("NetworkSource", st),
                    new XElement("ProgrammingLanguage", "SCL")));
            var networkTexts = new XElement("ObjectList",
                OptionalText("4", "5", "Comment", networkCommentZhCn),
                OptionalText("6", "7", "Title", networkTitleZhCn));
            if (networkTexts.HasElements)
                compileUnit.Add(networkTexts);

            // AttributeList 按 TIA 导出的字母序：CyclicTime 在 Interface 之前，PhaseOffset 在 Number 之后。
            var attributes = new XElement("AttributeList");
            if (cyclicTimeUs.HasValue)
                attributes.Add(new XElement("CyclicTime", cyclicTimeUs.Value));
            attributes.Add(
                new XElement("Interface",
                    new XElement(InterfaceNs + "Sections",
                        new XElement(InterfaceNs + "Section", new XAttribute("Name", "Input")),
                        BuildSection("Temp", tempMembers),
                        new XElement(InterfaceNs + "Section", new XAttribute("Name", "Constant")))),
                new XElement("MemoryLayout", "Optimized"),
                new XElement("Name", obName),
                new XElement("Namespace"),
                new XElement("Number", obNumber));
            if (phaseOffsetUs.HasValue)
                attributes.Add(new XElement("PhaseOffset", phaseOffsetUs.Value));
            attributes.Add(
                new XElement("ProgrammingLanguage", "SCL"),
                new XElement("SecondaryType", cls),
                new XElement("SetENOAutomatically", "false"));

            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("Document",
                    new XElement("Engineering", new XAttribute("version", "V21")),
                    new XElement("DocumentInfo",
                        new XElement("Created", "2000-01-01T00:00:00.0000000Z"),
                        new XElement("ExportSetting", "None"),
                        new XElement("InstalledProducts")),
                    new XElement("SW.Blocks.OB",
                        new XAttribute("ID", "0"),
                        attributes,
                        new XElement("ObjectList",
                            OptionalText("1", "2", "Comment", blockCommentZhCn),
                            compileUnit,
                            OptionalText("8", "9", "Title", blockTitleZhCn)))));
        }

        private static XElement? OptionalText(string id, string itemId, string compositionName, string? textZhCn) =>
            string.IsNullOrWhiteSpace(textZhCn) ? null : PlcBlockXmlHelpers.BuildMultilingualText(id, itemId, compositionName, textZhCn!);

        public static string BuildXml(
            string obName,
            int obNumber,
            string eventClass,
            int? cyclicTimeUs,
            int? phaseOffsetUs,
            IEnumerable<PlcBlockMemberDefinition> temps,
            string structuredTextInnerXml,
            string blockCommentZhCn = "",
            string blockTitleZhCn = "",
            string networkCommentZhCn = "",
            string networkTitleZhCn = "")
        {
            using var writer = new Utf8StringWriter();
            BuildDocument(obName, obNumber, eventClass, cyclicTimeUs, phaseOffsetUs, temps, structuredTextInnerXml,
                blockCommentZhCn, blockTitleZhCn, networkCommentZhCn, networkTitleZhCn).Save(writer, SaveOptions.None);
            return writer.ToString();
        }

        private static XElement BuildSection(string name, IReadOnlyCollection<PlcBlockMemberDefinition> members)
        {
            var section = new XElement(InterfaceNs + "Section", new XAttribute("Name", name));
            foreach (var m in members)
            {
                var memEl = new XElement(InterfaceNs + "Member",
                    new XAttribute("Name", m.Name),
                    new XAttribute("Datatype", m.Datatype));
                PlcBlockXmlHelpers.AppendMemberCommentIfAny(memEl, m.CommentZhCn);
                section.Add(memEl);
            }
            return section;
        }

        private static void ValidateMembers(IReadOnlyCollection<PlcBlockMemberDefinition> members)
        {
            var duplicates = members.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToArray();
            if (duplicates.Length > 0)
                throw new ArgumentException("OB temp member names are duplicated: " + string.Join(", ", duplicates));

            foreach (var member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Name))
                    throw new ArgumentException("OB temp member name must not be empty.");
                if (string.IsNullOrWhiteSpace(member.Datatype))
                    throw new ArgumentException("OB temp member datatype must not be empty: " + member.Name);
            }
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
