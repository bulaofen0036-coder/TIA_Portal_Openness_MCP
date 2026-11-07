using System;
using System.Collections.Generic;
using System.IO;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Scanner for SIMATIC SD resource files (.s7res).
    ///
    /// A .s7res is **YAML**, not XML:
    ///
    ///     MultiLingualTexts:
    ///       - id: MLC_jr
    ///         zh-CN: 起升
    ///         en-US: Hoist
    ///
    /// The previous implementation fed it to <c>XDocument.Load</c> and looked for
    /// SimaticML &lt;Comment&gt;/&lt;MultiLanguageText&gt; elements, so it threw
    /// <c>XmlException</c> on every real file and the "missing en-US" pre-check
    /// never once produced a warning. This replacement is a dependency-free
    /// line scanner over the YAML shape actually emitted by ExportAsDocuments.
    /// </summary>
    internal static class S7ResScanner
    {
        private const string EnUsKey = "en-US";

        /// <summary>
        /// Returns the MultiLingualText ids that have no non-empty <c>en-US</c> entry.
        /// Empty list when the file does not exist or is not a recognizable
        /// MultiLingualTexts document (unknown shape is reported as "nothing to warn
        /// about" rather than "everything is missing").
        /// </summary>
        public static List<string> GetMissingEnUsIds(string directory, string baseName)
        {
            var path = Path.Combine(directory, baseName + ".s7res");
            if (!File.Exists(path))
            {
                return new List<string>();
            }

            return GetMissingEnUsIdsFromLines(File.ReadAllLines(path));
        }

        /// <summary>Line-level scan; separated out so it can be exercised without a file.</summary>
        public static List<string> GetMissingEnUsIdsFromLines(IReadOnlyList<string> lines)
        {
            var missing = new List<string>();
            var sawContainer = false;
            string? currentId = null;
            var currentHasEnUs = false;

            void Flush()
            {
                if (currentId != null && !currentHasEnUs)
                {
                    missing.Add(currentId);
                }
                currentId = null;
                currentHasEnUs = false;
            }

            foreach (var raw in lines)
            {
                // Strip a UTF-8 BOM on the first line and any trailing whitespace.
                var line = raw.TrimStart('﻿').TrimEnd();
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                if (trimmed.StartsWith("MultiLingualTexts:", StringComparison.OrdinalIgnoreCase))
                {
                    sawContainer = true;
                    continue;
                }

                // New list item: "- id: MLC_xxx"
                if (trimmed.StartsWith("-", StringComparison.Ordinal))
                {
                    var item = trimmed.Substring(1).TrimStart();
                    if (TryReadValue(item, "id", out var id))
                    {
                        Flush();
                        currentId = id.Length == 0 ? "<unnamed>" : id;
                        continue;
                    }
                }

                if (currentId != null && TryReadValue(trimmed, EnUsKey, out var enUs) && enUs.Length > 0)
                {
                    currentHasEnUs = true;
                }
            }

            Flush();

            return sawContainer ? missing : new List<string>();
        }

        /// <summary>Matches "key: value" case-insensitively on the key; unquotes the value.</summary>
        private static bool TryReadValue(string text, string key, out string value)
        {
            value = "";
            if (!text.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = text.Substring(key.Length).TrimStart();
            if (rest.Length == 0 || rest[0] != ':')
            {
                return false;
            }

            value = rest.Substring(1).Trim().Trim('"', '\'');
            return true;
        }
    }
}
