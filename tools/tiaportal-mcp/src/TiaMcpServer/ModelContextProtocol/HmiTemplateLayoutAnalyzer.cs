using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static class HmiTemplateLayoutAnalyzer
    {
        /// <summary>
        /// 真实的执行 JSON 构建检查：读模板条目数 → 用本仓的执行 JSON 构建器生成 → 比对 items 数是否一致。
        /// 之所以要有这个默认实现，是因为过去调用方不传委托时会退化成「前面没报错就算验过」，
        /// 而那个判据里已经含着 errors.Count == 0，永远不可能新增错误 —— 等于没检查却报 pass。
        /// </summary>
        public static bool ExecutionJsonBuilds(string templateFile)
        {
            var templateRoot = JsonNode.Parse(File.ReadAllText(templateFile)) as JsonObject;
            var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
            var screen = templateRoot?["Screen"] as JsonObject ?? templateRoot?["screen"] as JsonObject ?? new JsonObject();
            // 回退宽高与 Program.ReportBuilders 的 TemplateExecutionJsonBuilds 保持一致，避免同一模板两处判定不同。
            var width = GetJsonInt(screen["Width"] ?? screen["width"], 800);
            var height = GetJsonInt(screen["Height"] ?? screen["height"], 480);
            var execution = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, width, height);
            return execution["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
        }

        public static JsonObject AnalyzeDirectory(string templateDirectory, Func<string, bool> executionJsonCheck)
        {
            // 必填：不传委托就没有真检查可做，这条路必须在编译期/入口处堵死，不允许悄悄退化成恒真。
            if (executionJsonCheck == null) throw new ArgumentNullException(nameof(executionJsonCheck));

            var files = Directory.Exists(templateDirectory)
                ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            var results = new JsonArray(files.Select(path => AnalyzeFile(path, executionJsonCheck)).ToArray());
            var failed = results.OfType<JsonObject>().Count(x => !string.Equals(x["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase));
            var warningCount = results.OfType<JsonObject>().Sum(x => (x["warnings"] as JsonArray)?.Count ?? 0);

            return new JsonObject
            {
                ["format"] = "tia-unified-hmi-template-layout-qa-v1",
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["templateDirectory"] = templateDirectory,
                ["templateCount"] = files.Length,
                ["failed"] = failed,
                ["warnings"] = warningCount,
                ["ok"] = failed == 0,
                ["policy"] = new JsonObject
                {
                    ["offlineOnly"] = true,
                    ["tiaConnection"] = "not-used",
                    ["blockingErrors"] = new JsonArray("json-parse-error", "missing-screen", "invalid-size", "duplicate-item-name", "item-out-of-screen", "execution-json-build-failed"),
                    ["nonBlockingWarnings"] = new JsonArray("layout-overlap", "dense-screen", "text-may-overflow", "missing-design-system-field")
                },
                ["results"] = results
            };
        }

        public static JsonObject AnalyzeFile(string templateFile, Func<string, bool> executionJsonCheck)
        {
            if (executionJsonCheck == null) throw new ArgumentNullException(nameof(executionJsonCheck));

            var errors = new JsonArray();
            var warnings = new JsonArray();
            var row = new JsonObject
            {
                ["file"] = templateFile,
                ["templateName"] = Path.GetFileNameWithoutExtension(templateFile),
                ["status"] = "pass",
                ["width"] = 0,
                ["height"] = 0,
                ["itemCount"] = 0,
                ["designSystemName"] = "",
                ["paletteColorCount"] = 0,
                ["layoutGrid"] = 0,
                ["executionJsonChecked"] = false,
                ["errors"] = errors,
                ["warnings"] = warnings
            };

            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(templateFile)) as JsonObject
                    ?? throw new InvalidOperationException("root is not an object");
            }
            catch (Exception ex)
            {
                errors.Add("json-parse-error: " + ex.Message);
                row["status"] = "fail";
                return row;
            }

            row["templateName"] = root["TemplateName"]?.ToString() ?? root["templateName"]?.ToString() ?? row["templateName"]?.ToString();
            var designSystem = root["DesignSystem"] as JsonObject ?? root["designSystem"] as JsonObject;
            if (designSystem == null)
            {
                warnings.Add("missing-design-system-field: DesignSystem");
            }
            else
            {
                row["designSystemName"] = designSystem["Name"]?.ToString() ?? "";
                var palette = designSystem["Palette"] as JsonObject ?? designSystem["palette"] as JsonObject;
                var layout = designSystem["Layout"] as JsonObject ?? designSystem["layout"] as JsonObject;
                row["paletteColorCount"] = palette?.Count ?? 0;
                row["layoutGrid"] = GetJsonInt(layout?["Grid"] ?? layout?["grid"], 0);
                if ((palette?.Count ?? 0) < 6) warnings.Add("missing-design-system-field: Palette should contain at least 6 named colors.");
                if (GetJsonInt(layout?["Grid"] ?? layout?["grid"], 0) <= 0) warnings.Add("missing-design-system-field: Layout.Grid should be a positive number.");
            }

            var screen = root["Screen"] as JsonObject ?? root["screen"] as JsonObject;
            if (screen == null)
            {
                errors.Add("missing-screen: Screen object is required.");
                row["status"] = "fail";
                return row;
            }

            var screenWidth = GetJsonInt(screen["Width"] ?? screen["width"], 0);
            var screenHeight = GetJsonInt(screen["Height"] ?? screen["height"], 0);
            row["width"] = screenWidth;
            row["height"] = screenHeight;
            if (screenWidth < 320 || screenHeight < 240)
            {
                errors.Add("invalid-size: screen must be at least 320x240.");
            }

            var items = (root["Items"] as JsonArray ?? root["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
            row["itemCount"] = items.Length;
            if (items.Length == 0)
            {
                warnings.Add("empty-screen: no screen items found.");
            }

            var boxes = new List<Dictionary<string, object>>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var name = item["Name"]?.ToString() ?? item["name"]?.ToString() ?? "";
                var type = NormalizeUnifiedHmiItemType(item["Type"]?.ToString() ?? item["type"]?.ToString() ?? "");
                var left = GetJsonInt(item["Left"] ?? item["left"], 0);
                var top = GetJsonInt(item["Top"] ?? item["top"], 0);
                var width = GetJsonInt(item["Width"] ?? item["width"], 0);
                var height = GetJsonInt(item["Height"] ?? item["height"], 0);
                if (string.IsNullOrWhiteSpace(name)) warnings.Add("unnamed-item: " + type + " at " + left + "," + top);
                else if (!names.Add(name)) errors.Add("duplicate-item-name: " + name);

                if (width <= 0 || height <= 0)
                {
                    errors.Add("invalid-size: " + name + " has non-positive width/height.");
                    continue;
                }

                if (left < 0 || top < 0 || left + width > screenWidth || top + height > screenHeight)
                {
                    errors.Add("item-out-of-screen: " + name + " bounds=" + left + "," + top + "," + width + "," + height);
                }

                if (type.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 && (width < 72 || height < 40)) warnings.Add("small-button: " + name + " is smaller than 72x40.");
                if (type.IndexOf("IOField", StringComparison.OrdinalIgnoreCase) >= 0 && height < 28) warnings.Add("small-iofield: " + name + " height is smaller than 28.");

                var props = item["Properties"] as JsonObject ?? item["properties"] as JsonObject ?? new JsonObject();
                var text = ExtractTemplateText(item["Text"] ?? item["text"]);
                if (!string.IsNullOrWhiteSpace(text) && LooksTextTooWide(text, width, GetJsonInt(props["FontSize"] ?? props["fontSize"], 16)))
                {
                    warnings.Add("text-may-overflow: " + name + " text length may exceed width.");
                }

                boxes.Add(new Dictionary<string, object>
                {
                    ["name"] = string.IsNullOrWhiteSpace(name) ? type + "@" + left + "," + top : name,
                    ["type"] = type,
                    ["left"] = left,
                    ["top"] = top,
                    ["width"] = width,
                    ["height"] = height
                });
            }

            var severeOverlapCount = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    if (IsIntentionalHmiLayer(boxes[i], boxes[j], screenWidth, screenHeight)) continue;
                    var overlap = GetOverlapArea(boxes[i], boxes[j]);
                    if (overlap <= 0) continue;
                    var smaller = Math.Min((int)boxes[i]["width"] * (int)boxes[i]["height"], (int)boxes[j]["width"] * (int)boxes[j]["height"]);
                    if (smaller > 0 && overlap >= smaller * 0.55)
                    {
                        severeOverlapCount++;
                        if (severeOverlapCount <= 20) warnings.Add("layout-overlap: " + boxes[i]["name"] + " overlaps " + boxes[j]["name"]);
                    }
                }
            }
            row["severeOverlapCount"] = severeOverlapCount;

            var totalItemArea = boxes.Sum(box => (int)box["width"] * (int)box["height"]);
            var screenArea = Math.Max(1, screenWidth * screenHeight);
            row["layoutDensity"] = Math.Round(totalItemArea / (double)screenArea, 3);
            if (totalItemArea / (double)screenArea > 1.35) warnings.Add("dense-screen: total item area is more than 135% of screen area.");

            try
            {
                row["executionJsonChecked"] = executionJsonCheck(templateFile);
                if (!string.Equals(row["executionJsonChecked"]?.ToString(), "True", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("execution-json-build-failed: generated execution JSON item count mismatch.");
                }
            }
            catch (Exception ex)
            {
                errors.Add("execution-json-build-failed: " + ex.Message);
            }

            row["status"] = errors.Count == 0 ? "pass" : "fail";
            return row;
        }

        private static int GetJsonInt(JsonNode? node, int fallback)
        {
            if (node == null) return fallback;
            if (int.TryParse(node.ToString(), out var value)) return value;
            if (double.TryParse(node.ToString(), out var doubleValue)) return (int)Math.Round(doubleValue);
            return fallback;
        }

        private static string NormalizeUnifiedHmiItemType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return "Rectangle";
            if (type.StartsWith("Hmi", StringComparison.OrdinalIgnoreCase)) return type.Substring(3);
            return type;
        }

        private static string ExtractTemplateText(JsonNode? node)
        {
            if (node == null) return "";
            if (node is JsonValue value) return StripHtmlText(value.ToString());
            if (node is JsonObject obj) return StripHtmlText(obj["zh-CN"]?.ToString() ?? obj["zh"]?.ToString() ?? obj.FirstOrDefault().Value?.ToString() ?? "");
            return StripHtmlText(node.ToString());
        }

        private static string StripHtmlText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return Regex.Replace(text, "<[^>]+>", "").Replace("\\n", Environment.NewLine);
        }

        private static bool LooksTextTooWide(string text, int width, int fontSize)
        {
            if (width <= 0) return false;
            var compact = Regex.Replace(text, @"\s+", "");
            var estimated = compact.Length * Math.Max(8, fontSize) * 0.62;
            return estimated > width * 1.08;
        }

        private static int GetOverlapArea(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            var left = Math.Max((int)a["left"], (int)b["left"]);
            var top = Math.Max((int)a["top"], (int)b["top"]);
            var right = Math.Min((int)a["left"] + (int)a["width"], (int)b["left"] + (int)b["width"]);
            var bottom = Math.Min((int)a["top"] + (int)a["height"], (int)b["top"] + (int)b["height"]);
            return Math.Max(0, right - left) * Math.Max(0, bottom - top);
        }

        private static bool IsIntentionalHmiLayer(Dictionary<string, object> a, Dictionary<string, object> b, int screenWidth, int screenHeight)
        {
            var aLarge = (int)a["width"] >= screenWidth * 0.75 || (int)a["height"] >= screenHeight * 0.35;
            var bLarge = (int)b["width"] >= screenWidth * 0.75 || (int)b["height"] >= screenHeight * 0.35;
            var aName = a["name"]?.ToString() ?? "";
            var bName = b["name"]?.ToString() ?? "";
            var aLooksContainer = aLarge || Regex.IsMatch(aName, "Header|Panel|Card|Module|Background|Group", RegexOptions.IgnoreCase);
            var bLooksContainer = bLarge || Regex.IsMatch(bName, "Header|Panel|Card|Module|Background|Group", RegexOptions.IgnoreCase);
            return aLooksContainer || bLooksContainer;
        }
    }
}
