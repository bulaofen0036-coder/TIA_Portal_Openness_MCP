using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

// ─────────────────────────────────────────────────────────────────────────────
//  「执行 JSON 检查」不许是复述已知事实的同义反复。
//
//  HmiTemplateLayoutAnalyzer 的执行 JSON 检查原来收一个可选委托，委托没传时退化成：
//      executionJsonChecked = items.Length > 0 && errors.Count == 0
//  判据里已经含着 errors.Count == 0 —— 这个「检查」永远不可能新增一条错误，
//  它只是把「前面没报错」再说一遍，然后挂上「已检查」的牌子。
//
//  走这条空转路径的是两个最该被验的调用方：
//   1) MCP 工具 AnalyzeUnifiedHmiTemplateLayout，而它的描述明写承诺检查 "execution JSON shape"；
//   2) OfflineReleaseValidationSuite —— 对外发版的离线验收闸，报 "ok":true 而执行 JSON
//      构建路径一次都没跑过。
//
//  修法是把委托改成必填（编译期堵死），并把真正的检查 ExecutionJsonBuilds 放进分析器自己，
//  让调用方无处可退。所以本文件既盯运行期行为，也盯 API 形状 ——
//  形状那条是唯一能拦住「有人手滑把 `= null` 加回去」的检查。
//
//  全部离线：不连博途、不起引擎，只读临时目录里的模板 JSON。
// ─────────────────────────────────────────────────────────────────────────────
internal static class HmiTemplateLayoutExecutionCheckTests
{
    /// <summary>一份布局上完全干净的模板：尺寸合法、条目不越界、名字不重复。</summary>
    private const string CleanTemplate = @"{
  ""TemplateName"": ""ExecCheckClean"",
  ""DesignSystem"": { ""Name"": ""T"", ""Palette"": { ""a"":""#1"",""b"":""#2"",""c"":""#3"",""d"":""#4"",""e"":""#5"",""f"":""#6"" }, ""Layout"": { ""Grid"": 8 } },
  ""Screen"": { ""Width"": 800, ""Height"": 480 },
  ""Items"": [
    { ""Name"": ""Title"", ""Type"": ""Text"", ""Left"": 16, ""Top"": 16, ""Width"": 300, ""Height"": 40 },
    { ""Name"": ""Start"", ""Type"": ""Button"", ""Left"": 16, ""Top"": 80, ""Width"": 120, ""Height"": 48 }
  ]
}";

    public static void Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tia_hmi_exec_check_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // -- 1. API 形状：委托必须是必填 -------------------------------------
            // 这是唯一能拦住回归的检查 —— 一旦有人把 `Func<string,bool>? … = null` 加回去，
            // 空转路径就复活了，而运行期用例（传了委托）一个都不会红。
            foreach (var name in new[] { "AnalyzeFile", "AnalyzeDirectory" })
            {
                var method = typeof(HmiTemplateLayoutAnalyzer).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
                check(method != null, "能定位 HmiTemplateLayoutAnalyzer." + name + "（定位不到，这条检查本身就是假的）");
                var parameter = method?.GetParameters().FirstOrDefault(x => x.Name == "executionJsonCheck");
                check(parameter != null, name + " 仍有 executionJsonCheck 参数");
                check(parameter != null && !parameter.IsOptional,
                    name + " 的 executionJsonCheck 不许是可选参数（可选＝「不传就当验过了」的空转路径复活）");
            }

            var cleanFile = Path.Combine(dir, "unified_clean.json");
            File.WriteAllText(cleanFile, CleanTemplate, Encoding.UTF8);

            // 传 null 也不许蒙混过关：显式空委托要炸，不能被当成「跳过检查」。
            check(Throws<ArgumentNullException>(() => HmiTemplateLayoutAnalyzer.AnalyzeFile(cleanFile, null!)),
                "显式传 null 委托要抛 ArgumentNullException，不许静默降级成不检查");

            // -- 2. 真检查真的会响：布局干净但执行 JSON 建不出来 -------------------
            // Items 里混进一个非对象条目：布局 QA 用 OfType<JsonObject>() 把它跳过，所以
            // 布局层面一条错误都没有；而执行 JSON 的条目数会比模板条目数少一个。
            // 这正是旧同义反复必然漏掉的形态 —— 旧判据 errors.Count == 0 成立 → 报 pass。
            var mismatchFile = Path.Combine(dir, "unified_mismatch.json");
            var mismatchRoot = JsonNode.Parse(CleanTemplate)!.AsObject();
            (mismatchRoot["Items"] as JsonArray)!.Add(JsonValue.Create("not-an-object"));
            File.WriteAllText(mismatchFile, mismatchRoot.ToJsonString(), Encoding.UTF8);

            check(!HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds(mismatchFile),
                "执行 JSON 条目数对不上模板条目数时，ExecutionJsonBuilds 必须返回 false");

            var mismatchRow = HmiTemplateLayoutAnalyzer.AnalyzeFile(mismatchFile, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
            var mismatchErrors = mismatchRow["errors"] as JsonArray ?? new JsonArray();
            check(!mismatchErrors.Any(x => (x?.ToString() ?? "").StartsWith("json-parse-error")
                                        || (x?.ToString() ?? "").StartsWith("missing-screen")
                                        || (x?.ToString() ?? "").StartsWith("item-out-of-screen")
                                        || (x?.ToString() ?? "").StartsWith("duplicate-item-name")),
                "该模板在布局层面本来是干净的（旧代码正因如此报 pass）");
            check(mismatchErrors.Any(x => (x?.ToString() ?? "").StartsWith("execution-json-build-failed")),
                "执行 JSON 建不出来必须报 execution-json-build-failed");
            check(string.Equals(mismatchRow["status"]?.ToString(), "fail", StringComparison.OrdinalIgnoreCase),
                "执行 JSON 检查没过的模板不许报 pass");
            check(mismatchRow["executionJsonChecked"]?.GetValue<bool>() == false,
                "executionJsonChecked 要如实报 false");

            // 目录级和顶层结论一起变红，发布闸才拦得住。
            var dirResult = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(dir, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
            check(dirResult["ok"]?.GetValue<bool>() == false,
                "只要有一份模板的执行 JSON 建不出来，目录级 ok 就不许是 true");

            // -- 3. 反向哨兵：真委托返回 true 时行为一个字都没变 -------------------
            var cleanRow = HmiTemplateLayoutAnalyzer.AnalyzeFile(cleanFile, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
            check(string.Equals(cleanRow["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase),
                "[哨兵] 干净模板 + 真检查通过，照旧 pass");
            check(cleanRow["executionJsonChecked"]?.GetValue<bool>() == true,
                "[哨兵] 真检查通过时 executionJsonChecked 为 true");
            check(!(cleanRow["errors"] as JsonArray ?? new JsonArray()).Any(),
                "[哨兵] 干净模板不许被新检查误伤出错误");
            check(HmiTemplateLayoutAnalyzer.AnalyzeFile(cleanFile, _ => true)["status"]?.ToString() == "pass",
                "[哨兵] 自带委托返回 true 的调用方（BuildUnifiedHmiTemplateApplyDesignJson 那条路）行为不变");

            // [哨兵] 委托抛异常仍走 execution-json-build-failed，不许把异常吞成通过。
            var throwRow = HmiTemplateLayoutAnalyzer.AnalyzeFile(cleanFile, _ => throw new InvalidOperationException("boom"));
            check(string.Equals(throwRow["status"]?.ToString(), "fail", StringComparison.OrdinalIgnoreCase)
                  && (throwRow["errors"] as JsonArray ?? new JsonArray())
                        .Any(x => (x?.ToString() ?? "").Contains("boom")),
                "[哨兵] 检查自己炸了要报错，不许当成通过");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录清不掉不影响结论 */ }
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }
}
