using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Classic/Basic HMI 最小离线包构建器。
    /// 组合画面 XML 与变量表 XML，并在离线阶段检查控件引用的 HMI tag 是否已声明。
    /// </summary>
    public static class ClassicHmiMinimalPackageBuilder
    {
        public static JsonObject BuildFromJson(string packageJson)
        {
            var root = JsonNode.Parse(packageJson) as JsonObject
                ?? throw new ArgumentException("Classic HMI package JSON root must be an object.", nameof(packageJson));
            var screenDesign = root["ScreenDesign"] as JsonObject ?? root["screenDesign"] as JsonObject ?? root["Screen"] as JsonObject ?? root["screen"] as JsonObject
                ?? throw new ArgumentException("Classic HMI package requires ScreenDesign.");
            var tagTable = root["TagTable"] as JsonObject ?? root["tagTable"] as JsonObject
                ?? throw new ArgumentException("Classic HMI package requires TagTable.");

            var screenResult = ClassicHmiScreenXmlBuilder.BuildFromJson(screenDesign.ToJsonString());
            var tagResult = ClassicHmiTagTableXmlBuilder.BuildFromJson(tagTable.ToJsonString());
            var readiness = BuildReadiness(screenDesign, tagTable, screenResult, tagResult);
            var ok = screenResult["ok"]?.GetValue<bool>() == true &&
                     tagResult["ok"]?.GetValue<bool>() == true &&
                     readiness["ok"]?.GetValue<bool>() == true;

            return new JsonObject
            {
                ["format"] = "tia-classic-hmi-minimal-package-offline-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["packageName"] = root["Name"]?.ToString() ?? root["name"]?.ToString() ?? "Classic_HMI_Minimal_Package",
                ["ok"] = ok,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "Offline package generation only; TIA Portal is not connected.",
                    ["write"] = "No project, reference project, or delivery package content is modified.",
                    ["binding"] = "PLC-side symbols and Classic HMI imports are not verified until a temporary TIA project import/readback succeeds.",
                    ["apply"] = "Import tag table first, then screen XML in a temporary Classic/Basic HMI project. Read back HMI tags, screen items, connections, controller tags, and compile/diagnose before using a real project."
                },
                ["importOrder"] = new JsonArray("Build/import HMI tag table XML", "Build/import HMI screen XML", "Read back HMI tags and screen items", "Compile/diagnose Classic HMI", "Save only after successful readback and diagnostics"),
                ["readiness"] = readiness,
                ["tagTable"] = tagResult,
                ["screen"] = screenResult
            };
        }

        public static JsonObject WriteFiles(string packageJson, string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

            Directory.CreateDirectory(outputDirectory);
            var package = BuildFromJson(packageJson);
            var packageName = SanitizeFileName(package["packageName"]?.ToString() ?? "Classic_HMI_Minimal_Package");
            var tagTablePath = Path.Combine(outputDirectory, packageName + "_TagTable.xml");
            var screenPath = Path.Combine(outputDirectory, packageName + "_Screen.xml");
            var manifestPath = Path.Combine(outputDirectory, packageName + "_manifest.json");

            var tagXml = package["tagTable"]?["xml"]?.ToString() ?? "";
            var screenXml = package["screen"]?["xml"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(tagXml) || string.IsNullOrWhiteSpace(screenXml))
                throw new InvalidOperationException("Classic HMI package did not produce both screen XML and tag table XML.");

            File.WriteAllText(tagTablePath, tagXml, Encoding.UTF8);
            File.WriteAllText(screenPath, screenXml, Encoding.UTF8);

            var manifest = new JsonObject
            {
                ["format"] = "tia-classic-hmi-minimal-package-files-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["packageName"] = package["packageName"]?.ToString() ?? "",
                ["ok"] = package["ok"]?.GetValue<bool>() == true,
                ["outputDirectory"] = outputDirectory,
                ["tagTableXmlPath"] = tagTablePath,
                ["screenXmlPath"] = screenPath,
                ["importOrder"] = package["importOrder"]?.DeepClone(),
                ["readiness"] = package["readiness"]?.DeepClone(),
                ["screenAnalysis"] = package["screen"]?["analysis"]?.DeepClone(),
                ["tagTableAnalysis"] = package["tagTable"]?["analysis"]?.DeepClone(),
                ["nextValidation"] = new JsonArray(
                    "Import tagTableXmlPath into a temporary Classic/Basic HMI project first.",
                    "Import screenXmlPath after the tag table import succeeds.",
                    "Read back HMI tags, controller bindings, screen items, dynamic bindings, and button events.",
                    "Compile/diagnose the HMI and use the files on a real project only after temporary validation succeeds.")
            };

            File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);

            manifest["manifestPath"] = manifestPath;
            manifest["files"] = new JsonArray(tagTablePath, screenPath, manifestPath);
            manifest["fileCount"] = 3;
            manifest["tagTableXmlBytes"] = new FileInfo(tagTablePath).Length;
            manifest["screenXmlBytes"] = new FileInfo(screenPath).Length;
            manifest["manifestBytes"] = new FileInfo(manifestPath).Length;
            return manifest;
        }

        /// <summary>
        /// 离线校验已写出的 Classic/Basic HMI 最小文件包。
        /// 这个步骤用于交付前自检：只读取 manifest/XML，不连接 TIA，也不修改工程。
        /// </summary>
        public static JsonObject ValidateFiles(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Classic HMI package path is required.", nameof(path));

            var resolved = Path.GetFullPath(path);
            var manifestPath = Directory.Exists(resolved)
                ? Directory.GetFiles(resolved, "*_manifest.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : resolved;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return new JsonObject
                {
                    ["format"] = "tia-classic-hmi-minimal-package-file-validation-v1",
                    ["timestamp"] = DateTime.Now.ToString("O"),
                    ["offlineOnly"] = true,
                    ["ok"] = false,
                    ["inputPath"] = path,
                    ["manifestPath"] = manifestPath ?? "",
                    ["errors"] = new JsonArray("manifest-not-found: " + path),
                    ["warnings"] = new JsonArray()
                };
            }

            var errors = new JsonArray();
            var warnings = new JsonArray();
            JsonObject manifest;
            try
            {
                manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject
                    ?? throw new InvalidOperationException("manifest root is not a JSON object.");
            }
            catch (Exception ex)
            {
                errors.Add("manifest-parse-error: " + ex.Message);
                return BuildValidationResult(path, manifestPath, "", "", errors, warnings, null, null, Array.Empty<string>(), Array.Empty<string>());
            }

            var baseDir = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
            var tagPath = ResolveManifestPath(baseDir, manifest["tagTableXmlPath"]?.ToString() ?? "");
            var screenPath = ResolveManifestPath(baseDir, manifest["screenXmlPath"]?.ToString() ?? "");
            if (string.IsNullOrWhiteSpace(tagPath)) errors.Add("manifest-missing-tagTableXmlPath");
            else if (!File.Exists(tagPath)) errors.Add("tag-table-file-not-found: " + tagPath);
            if (string.IsNullOrWhiteSpace(screenPath)) errors.Add("manifest-missing-screenXmlPath");
            else if (!File.Exists(screenPath)) errors.Add("screen-file-not-found: " + screenPath);

            JsonObject? tagAnalysis = null;
            JsonObject? screenAnalysis = null;
            string[] declaredTags = Array.Empty<string>();
            string[] referencedTags = Array.Empty<string>();
            if (File.Exists(tagPath))
            {
                var xml = File.ReadAllText(tagPath, Encoding.UTF8);
                tagAnalysis = ClassicHmiTagTableXmlBuilder.AnalyzeXml(xml);
                declaredTags = ExtractDeclaredTags(xml);
                if (tagAnalysis["ok"]?.GetValue<bool>() != true)
                    errors.Add("tag-table-analysis-failed");
            }

            if (File.Exists(screenPath))
            {
                var xml = File.ReadAllText(screenPath, Encoding.UTF8);
                screenAnalysis = ClassicHmiScreenXmlBuilder.AnalyzeXml(xml);
                referencedTags = ExtractReferencedTagsFromScreenXml(xml);
                if (screenAnalysis["ok"]?.GetValue<bool>() != true)
                    errors.Add("screen-analysis-failed");
            }

            var declaredSet = declaredTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingTags = referencedTags.Where(x => !declaredSet.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var tag in missingTags)
                errors.Add("missing-referenced-hmi-tag: " + tag);

            var referencedSet = referencedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unusedTags = declaredTags.Where(x => !referencedSet.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var tag in unusedTags)
                warnings.Add("unused-hmi-tag: " + tag);

            return BuildValidationResult(path, manifestPath, tagPath, screenPath, errors, warnings, tagAnalysis, screenAnalysis, declaredTags, referencedTags);
        }

        /// <summary>
        /// 离线校验 Classic/Basic HMI 文件包与 PLC 符号清单是否同步。
        /// PLC 符号清单可来自临时工程读回、导出报告或调用方整理的白名单；这里不猜 PLC 变量。
        /// </summary>
        public static JsonObject ValidateFilesWithPlcSymbols(string path, string plcSymbolsJson)
        {
            var fileValidation = ValidateFiles(path);
            var errors = CloneJsonArray(fileValidation["errors"] as JsonArray);
            var warnings = CloneJsonArray(fileValidation["warnings"] as JsonArray);
            var tagPath = fileValidation["tagTableXmlPath"]?.ToString() ?? "";
            var plcSymbols = ParsePlcSymbols(plcSymbolsJson);
            if (plcSymbols.Count == 0)
                errors.Add("plc-symbol-list-empty: provide exact PLC symbols exported or read back from the target PLC.");

            var controllerTags = File.Exists(tagPath)
                ? ExtractControllerTagsFromTagTableXml(File.ReadAllText(tagPath, Encoding.UTF8))
                : Array.Empty<string>();
            var plcSet = plcSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingPlcSymbols = controllerTags.Where(x => !plcSet.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var symbol in missingPlcSymbols)
                errors.Add("missing-plc-symbol: " + symbol);

            // Absolute-address tags (#57) carry no ControllerTag, so the symbol check above never sees them:
            // a package of absolute tags used to pass with nothing checked. I/Q/M addresses are checked against the
            // addresses in the PLC symbol list; DB addresses cannot be checked offline and are listed as unchecked.
            var absoluteTags = File.Exists(tagPath)
                ? ExtractAbsoluteTagsFromTagTableXml(File.ReadAllText(tagPath, Encoding.UTF8))
                : Array.Empty<(string Name, string Address)>();
            var plcAddresses = ParsePlcAddresses(plcSymbolsJson);
            var missingPlcAddresses = new List<string>();
            var uncheckedAbsolute = new List<string>();
            var usedSet = controllerTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, address) in absoluteTags)
            {
                var key = NormalizeAddress(address);
                if (!IsTagTableArea(key))
                {
                    uncheckedAbsolute.Add(name + " = " + address);
                    continue;
                }
                if (plcAddresses.Count == 0) continue; // reported once below
                if (plcAddresses.TryGetValue(key, out var symbol)) usedSet.Add(symbol);
                else missingPlcAddresses.Add(name + " = " + address);
            }
            var checkableAbsolute = absoluteTags.Length - uncheckedAbsolute.Count;
            // Absolute addressing does not need a PLC symbol, so these are findings to look at, not blockers.
            // What they must never be again is silent.
            if (checkableAbsolute > 0 && plcAddresses.Count == 0)
                warnings.Add($"absolute-bindings-unchecked: {checkableAbsolute} HMI tag(s) use absolute I/Q/M addresses, but the PLC symbol list carries no addresses, so none of them was checked. Pass objects with name + address (the GetPlcTags result can be passed as-is; raise its limit so it is not truncated).");
            foreach (var item in missingPlcAddresses)
                warnings.Add("no-plc-tag-at-address: " + item + " (no PLC tag with exactly this address; legal for absolute addressing, but check for a typo or an overlapping address)");
            if (PlcSymbolListTruncated(plcSymbolsJson))
                warnings.Add("plc-symbol-list-truncated: the PLC symbol list says it is truncated; missing symbols/addresses may just be beyond the cut.");
            foreach (var item in uncheckedAbsolute)
                warnings.Add("absolute-address-unchecked: " + item + " (DB / non tag-table address; verify after a temporary import)");

            var unusedPlcSymbols = plcSymbols.Where(x => !usedSet.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var symbol in unusedPlcSymbols)
                warnings.Add("unused-plc-symbol: " + symbol);

            return new JsonObject
            {
                ["format"] = "tia-classic-hmi-plc-symbol-sync-validation-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["ok"] = errors.Count == 0,
                ["inputPath"] = path,
                ["baseFileValidationOk"] = fileValidation["ok"]?.GetValue<bool>() == true,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线 PLC-HMI 同步校验：不连接 TIA Portal，不打开工程，不导入 HMI 文件。",
                    ["write"] = "只读取 HMI 文件包和调用方提供的 PLC 符号清单，不修改工程、reference 或交付包。",
                    ["binding"] = "要求 HMI tag table 中的 ControllerTag/PlcTag 精确存在于 PLC 符号清单；缺失即阻止继续导入真实工程。"
                },
                ["declaredHmiTagCount"] = fileValidation["declaredTagCount"]?.GetValue<int>() ?? 0,
                ["referencedHmiTagCount"] = fileValidation["referencedTagCount"]?.GetValue<int>() ?? 0,
                ["missingHmiTagCount"] = fileValidation["missingTagCount"]?.GetValue<int>() ?? 0,
                ["controllerTagCount"] = controllerTags.Length,
                ["absoluteTagCount"] = absoluteTags.Length,
                ["absoluteCheckedCount"] = plcAddresses.Count == 0 ? 0 : checkableAbsolute,
                ["noPlcTagAtAddressCount"] = missingPlcAddresses.Count,
                ["uncheckedAbsoluteCount"] = uncheckedAbsolute.Count,
                ["plcSymbolCount"] = plcSymbols.Count,
                ["missingPlcSymbolCount"] = missingPlcSymbols.Length,
                ["unusedPlcSymbolCount"] = unusedPlcSymbols.Length,
                ["controllerTags"] = new JsonArray(controllerTags.Select(x => JsonValue.Create(x)).ToArray()),
                ["plcSymbols"] = new JsonArray(plcSymbols.Select(x => JsonValue.Create(x)).ToArray()),
                ["missingPlcSymbols"] = new JsonArray(missingPlcSymbols.Select(x => JsonValue.Create(x)).ToArray()),
                ["unusedPlcSymbols"] = new JsonArray(unusedPlcSymbols.Select(x => JsonValue.Create(x)).ToArray()),
                ["fileValidation"] = fileValidation,
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["nextValidation"] = errors.Count == 0
                    ? "PLC-HMI 离线同步自检通过。下一步仍需导入临时工程，读回 HMI tags 的 ControllerTag，并编译诊断。"
                    : "先补齐 PLC 符号或修正 HMI 变量表绑定，不要导入真实工程。"
            };
        }

        private static JsonObject BuildReadiness(JsonObject screenDesign, JsonObject tagTable, JsonObject screenResult, JsonObject tagResult)
        {
            var declaredTags = GetDeclaredTags(tagTable);
            var referencedTags = GetReferencedTags(screenDesign);
            var missing = referencedTags.Where(x => !declaredTags.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var unused = declaredTags.Where(x => !referencedTags.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var screenOk = screenResult["ok"]?.GetValue<bool>() == true;
            var tagsOk = tagResult["ok"]?.GetValue<bool>() == true;
            var ok = screenOk && tagsOk && missing.Length == 0;

            return new JsonObject
            {
                ["ok"] = ok,
                ["screenOk"] = screenOk,
                ["tagTableOk"] = tagsOk,
                ["declaredTagCount"] = declaredTags.Count,
                ["referencedTagCount"] = referencedTags.Count,
                ["missingTagCount"] = missing.Length,
                ["unusedTagCount"] = unused.Length,
                ["missingTags"] = new JsonArray(missing.Select(x => JsonValue.Create(x)).ToArray()),
                ["unusedTags"] = new JsonArray(unused.Select(x => JsonValue.Create(x)).ToArray()),
                ["recommendedNextAction"] = ok
                    ? "Import tag table and screen XML into a temporary Classic/Basic HMI project, then read back tags/items and compile/diagnose."
                    : "Fix screen XML, tag table XML, or missing HMI tag declarations before attempting a temporary-project import."
            };
        }

        private static HashSet<string> GetDeclaredTags(JsonObject tagTable)
        {
            return (tagTable["Tags"] as JsonArray ?? tagTable["tags"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(x => x["Name"]?.ToString() ?? x["name"]?.ToString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetReferencedTags(JsonObject screenDesign)
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in screenDesign["Items"] as JsonArray ?? screenDesign["items"] as JsonArray ?? new JsonArray())
            {
                if (item is not JsonObject obj) continue;
                AddTag(tags, obj["Tag"] ?? obj["tag"] ?? obj["HmiTag"] ?? obj["hmiTag"] ?? obj["ProcessValueTag"] ?? obj["processValueTag"]);
                var props = obj["Properties"] as JsonObject ?? obj["properties"] as JsonObject;
                if (props != null)
                {
                    AddTag(tags, props["Tag"] ?? props["tag"] ?? props["HmiTag"] ?? props["hmiTag"] ?? props["ProcessValueTag"] ?? props["processValueTag"]);
                }

                foreach (var action in obj["Actions"] as JsonArray ?? obj["actions"] as JsonArray ?? new JsonArray())
                {
                    if (action is not JsonObject actionObj) continue;
                    AddTag(tags, actionObj["TargetTag"] ?? actionObj["targetTag"] ?? actionObj["Tag"] ?? actionObj["tag"]);
                }
            }
            return tags;
        }

        private static void AddTag(HashSet<string> tags, JsonNode? node)
        {
            var value = node?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(value)) tags.Add(value.Trim());
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var chars = (name ?? "Classic_HMI_Minimal_Package")
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();
            var value = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(value) ? "Classic_HMI_Minimal_Package" : value;
        }

        private static JsonObject BuildValidationResult(
            string inputPath,
            string manifestPath,
            string tagPath,
            string screenPath,
            JsonArray errors,
            JsonArray warnings,
            JsonObject? tagAnalysis,
            JsonObject? screenAnalysis,
            string[] declaredTags,
            string[] referencedTags)
        {
            var declaredSet = declaredTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingTags = referencedTags.Where(x => !declaredSet.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var referencedSet = referencedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unusedTags = declaredTags.Where(x => !referencedSet.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            return new JsonObject
            {
                ["format"] = "tia-classic-hmi-minimal-package-file-validation-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["offlineOnly"] = true,
                ["inputPath"] = inputPath,
                ["manifestPath"] = manifestPath,
                ["tagTableXmlPath"] = tagPath,
                ["screenXmlPath"] = screenPath,
                ["ok"] = errors.Count == 0,
                ["safetyPolicy"] = new JsonObject
                {
                    ["tia"] = "离线文件包校验：不连接 TIA Portal，不打开工程，不执行导入。",
                    ["write"] = "只读取 manifest/XML 文件，不修改工程、reference 或交付包。",
                    ["binding"] = "校验 HMI 控件/事件引用的 HMI tag 是否在变量表 XML 中声明；PLC 侧真实符号仍需临时工程导入读回确认。"
                },
                ["fileChecks"] = new JsonObject
                {
                    ["manifestExists"] = File.Exists(manifestPath),
                    ["tagTableXmlExists"] = !string.IsNullOrWhiteSpace(tagPath) && File.Exists(tagPath),
                    ["screenXmlExists"] = !string.IsNullOrWhiteSpace(screenPath) && File.Exists(screenPath)
                },
                ["declaredTagCount"] = declaredTags.Length,
                ["referencedTagCount"] = referencedTags.Length,
                ["missingTagCount"] = missingTags.Length,
                ["unusedTagCount"] = unusedTags.Length,
                ["declaredTags"] = new JsonArray(declaredTags.Select(x => JsonValue.Create(x)).ToArray()),
                ["referencedTags"] = new JsonArray(referencedTags.Select(x => JsonValue.Create(x)).ToArray()),
                ["missingTags"] = new JsonArray(missingTags.Select(x => JsonValue.Create(x)).ToArray()),
                ["unusedTags"] = new JsonArray(unusedTags.Select(x => JsonValue.Create(x)).ToArray()),
                ["tagTableAnalysis"] = tagAnalysis?.DeepClone(),
                ["screenAnalysis"] = screenAnalysis?.DeepClone(),
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["nextValidation"] = errors.Count == 0
                    ? "文件包离线自检通过。下一步仍需导入临时 Classic/Basic HMI 工程，读回 tags/items/bindings/events 并编译诊断。"
                    : "先修复 manifest、XML 或 HMI tag 引用问题，不要导入真实工程。"
            };
        }

        private static string ResolveManifestPath(string baseDir, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(baseDir, value));
        }

        private static string[] ExtractDeclaredTags(string tagXml)
        {
            var doc = XDocument.Parse(tagXml, LoadOptions.PreserveWhitespace);
            return doc.Descendants("Hmi.Tag.Tag")
                .Select(x => x.Element("AttributeList")?.Element("Name")?.Value ?? "")
                .Select(CleanClassicTagName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static (string Name, string Address)[] ExtractAbsoluteTagsFromTagTableXml(string tagXml)
        {
            var doc = XDocument.Parse(tagXml, LoadOptions.PreserveWhitespace);
            return doc.Descendants("Hmi.Tag.Tag")
                .Where(x => string.Equals(x.Element("AttributeList")?.Element("AddressAccessMode")?.Value, "Absolute", StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Element("AttributeList")?.Element("Name")?.Value ?? "", x.Element("AttributeList")?.Element("LogicalAddress")?.Value ?? ""))
                .Where(x => !string.IsNullOrWhiteSpace(x.Item2))
                .ToArray();
        }

        /// <summary>Address -> symbol from symbol objects that carry Address / LogicalAddress (e.g. GetPlcTags output).</summary>
        private static Dictionary<string, string> ParsePlcAddresses(string plcSymbolsJson)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in SymbolItems(plcSymbolsJson).OfType<JsonObject>())
            {
                var address = item["Address"]?.ToString() ?? item["address"]?.ToString() ?? item["LogicalAddress"]?.ToString() ?? item["logicalAddress"]?.ToString();
                var name = item["Symbol"]?.ToString() ?? item["symbol"]?.ToString() ?? item["Name"]?.ToString() ?? item["name"]?.ToString()
                    ?? item["Path"]?.ToString() ?? item["path"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(address)) result[NormalizeAddress(address!)] = CleanClassicTagName(name);
            }
            return result;
        }

        /// <summary>Uppercase, no blanks, German mnemonics mapped to IEC (%E -> %I, %A -> %Q).</summary>
        private static string NormalizeAddress(string address)
        {
            var s = new string(address.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            if (s.StartsWith("%E")) s = "%I" + s.Substring(2);
            else if (s.StartsWith("%A")) s = "%Q" + s.Substring(2);
            return s;
        }

        /// <summary>I / Q / M areas live in PLC tag tables; DB addresses do not.</summary>
        private static bool IsTagTableArea(string normalized)
            => normalized.StartsWith("%I") || normalized.StartsWith("%Q") || normalized.StartsWith("%M");

        private static string[] ExtractControllerTagsFromTagTableXml(string tagXml)
        {
            var doc = XDocument.Parse(tagXml, LoadOptions.PreserveWhitespace);
            return doc.Descendants("Hmi.Tag.Tag")
                .Select(x => x.Element("LinkList")?.Element("ControllerTag")?.Element("Name")?.Value ?? "")
                .Select(CleanClassicTagName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string[] ExtractReferencedTagsFromScreenXml(string screenXml)
        {
            var doc = XDocument.Parse(screenXml, LoadOptions.PreserveWhitespace);
            var names = doc.Descendants()
                .Where(x => x.Name.LocalName == "Name" &&
                    (x.Parent?.Name.LocalName == "Tag" || x.Parent?.Name.LocalName == "Value"))
                .Select(x => CleanClassicTagName(x.Value))
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string CleanClassicTagName(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                value = value.Substring(1, value.Length - 2);
            return value.Trim();
        }

        private static HashSet<string> ParsePlcSymbols(string plcSymbolsJson)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in SymbolItems(plcSymbolsJson))
                AddPlcSymbol(result, item);
            return result;
        }

        /// <summary>
        /// The entries of a PLC symbol list, whatever the wrapper: a plain array; an object with
        /// Symbols / PlcSymbols / tags; the GetPlcTags result as-is ({data:{tags:[...]}}); or one symbol object.
        /// </summary>
        private static IEnumerable<JsonNode?> SymbolItems(string plcSymbolsJson)
        {
            if (string.IsNullOrWhiteSpace(plcSymbolsJson)) return Array.Empty<JsonNode?>();
            var node = JsonNode.Parse(plcSymbolsJson);
            if (node is JsonArray array) return array;
            if (node is not JsonObject obj) return Array.Empty<JsonNode?>();
            foreach (var holder in new[] { obj, obj["data"] as JsonObject, obj["Data"] as JsonObject })
            {
                if (holder == null) continue;
                foreach (var key in new[] { "Symbols", "symbols", "PlcSymbols", "plcSymbols", "Tags", "tags" })
                    if (holder[key] is JsonArray list) return list;
            }
            return new JsonNode?[] { obj };
        }

        private static bool PlcSymbolListTruncated(string plcSymbolsJson)
        {
            try
            {
                var obj = string.IsNullOrWhiteSpace(plcSymbolsJson) ? null : JsonNode.Parse(plcSymbolsJson) as JsonObject;
                var holder = obj?["data"] as JsonObject ?? obj?["Data"] as JsonObject ?? obj;
                return holder?["truncated"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            }
            catch { return false; }
        }

        private static void AddPlcSymbol(HashSet<string> result, JsonNode? node)
        {
            var value = "";
            if (node is JsonObject obj)
            {
                value = obj["Symbol"]?.ToString()
                    ?? obj["symbol"]?.ToString()
                    ?? obj["Name"]?.ToString()
                    ?? obj["name"]?.ToString()
                    ?? obj["Path"]?.ToString()
                    ?? obj["path"]?.ToString()
                    ?? "";
            }
            else
            {
                value = node?.ToString() ?? "";
            }

            value = CleanClassicTagName(value);
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }

        private static JsonArray CloneJsonArray(JsonArray? source)
        {
            if (source == null) return new JsonArray();
            return new JsonArray(source.Select(x => x?.DeepClone()).ToArray());
        }
    }
}
