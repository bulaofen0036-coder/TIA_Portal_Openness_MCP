using System;
using System.IO;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// issue #38：画面事件名的规范化 + 脚本模块导入目录的预检。
    /// 枚举值取自 V21 实测的 HmiScreenEventType：None/Tapped/ContextTapped/Loaded/Unloaded。
    /// 模块目录的写法取自 TIA 的 VCI 导出：Name.hmi.yml（ScriptModules → Name → ScriptFile）+ Name.hmi.js。
    /// </summary>
    internal static class UnifiedHmiScriptArgsTests
    {
        private static readonly string[] ScreenEvents = { "None", "Tapped", "ContextTapped", "Loaded", "Unloaded" };

        internal static void Run(Action<bool, string> check)
        {
            // ---- 事件名 ----
            check(UnifiedHmiScriptArgs.NormalizeEventName("Loaded", ScreenEvents) == "Loaded", "[反向哨兵] exact name accepted");
            check(UnifiedHmiScriptArgs.NormalizeEventName(" loaded ", ScreenEvents) == "Loaded", "case/whitespace-insensitive → enum spelling");
            check(UnifiedHmiScriptArgs.NormalizeEventName("Cleared", ScreenEvents) == "Unloaded", "runtime name 'Cleared' → Unloaded");
            check(UnifiedHmiScriptArgs.NormalizeEventName("OnLoaded", ScreenEvents) == "Loaded", "'OnLoaded' (official sample wording) → Loaded");
            check(UnifiedHmiScriptArgs.NormalizeEventName("onunloaded", ScreenEvents) == "Unloaded", "'onunloaded' → Unloaded");
            check(Throws(() => UnifiedHmiScriptArgs.NormalizeEventName("Foo", ScreenEvents), "Valid events: Tapped, ContextTapped, Loaded, Unloaded"),
                "unknown name → error listing the valid events (None excluded)");
            check(Throws(() => UnifiedHmiScriptArgs.NormalizeEventName("3", ScreenEvents), "single event name"),
                "numeric value refused (Enum.Parse would accept it)");
            check(Throws(() => UnifiedHmiScriptArgs.NormalizeEventName("Loaded,Unloaded", ScreenEvents), "single event name"),
                "comma list refused (Enum.Parse would build a combined value)");
            check(Throws(() => UnifiedHmiScriptArgs.NormalizeEventName("None", ScreenEvents), "not an event"),
                "'None' is not an event you can script");
            check(Throws(() => UnifiedHmiScriptArgs.NormalizeEventName("", ScreenEvents), "required"), "empty → required");
            check(Throws(() => UnifiedHmiScriptArgs.NormalizeEventName("Cleared", new[] { "None", "Tapped" }), "not an event"),
                "an alias only applies when its target exists in the enum");

            // ---- 模块名 ----
            check(UnifiedHmiScriptArgs.NormalizeModuleName("McpMathB.hmi.yml") == "McpMathB", "file name McpMathB.hmi.yml → base name (TIA returns false for the suffixed form)");
            check(UnifiedHmiScriptArgs.NormalizeModuleName("McpMathB.hmi") == "McpMathB", ".hmi stripped");
            check(UnifiedHmiScriptArgs.NormalizeModuleName(" McpMathB ") == "McpMathB", "[反向哨兵] plain base name unchanged (trimmed)");
            check(UnifiedHmiScriptArgs.NormalizeModuleName(null) == "", "null → empty (import the whole folder)");

            // ---- 导入前后的名字差 ----
            check(UnifiedHmiScriptArgs.NewNames(new[] { "A" }, new[] { "a", "B" }).SequenceEqual(new[] { "B" }), "new names, case-insensitive");
            check(UnifiedHmiScriptArgs.NewNames(new[] { "A" }, new[] { "A" }).Length == 0, "re-import of an existing module → no new name");

            // ---- ScriptFile 行 ----
            var refs = UnifiedHmiScriptArgs.ReadScriptFiles(new[] { "#Version: 2.0", "", "ScriptModules:", "  M:", "    ScriptFile: M.hmi.js", "    ScriptFile: \"N.hmi.js\"" }).ToArray();
            check(refs.SequenceEqual(new[] { "M.hmi.js", "N.hmi.js" }), "ScriptFile values read, quotes removed");

            // ---- 目录预检（真实文件）----
            var root = Path.Combine(Path.GetTempPath(), "unified_module_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                var good = Dir(root, "good");
                WriteModule(good, "McpMathA");
                check(UnifiedHmiScriptArgs.ValidateModuleFolder(good, "").Length == 2, "[反向哨兵] a folder in TIA's export layout passes");
                check(UnifiedHmiScriptArgs.ValidateModuleFolder(good, "McpMathA.hmi.yml").Length == 2, "named import by file name passes");

                check(Throws(() => UnifiedHmiScriptArgs.ValidateModuleFolder(Path.Combine(root, "missing"), ""), "does not exist"), "missing folder");
                check(Throws(() => UnifiedHmiScriptArgs.ValidateModuleFolder(Dir(root, "empty"), ""), "No .yml file"), "empty folder");

                var jsOnly = Dir(root, "jsonly");
                File.WriteAllText(Path.Combine(jsOnly, "Lone.hmi.js"), "export function F() {}\n");
                check(Throws(() => UnifiedHmiScriptArgs.ValidateModuleFolder(jsOnly, ""), "ScriptFile: <Name>.hmi.js"),
                    ".js without .yml → refused with the layout (TIA only says 'no YAML files')");

                var dangling = Dir(root, "dangling");
                File.WriteAllText(Path.Combine(dangling, "M.hmi.yml"), "#Version: 2.0\n\nScriptModules:\n  M:\n    ScriptFile: M.hmi.js\n");
                check(Throws(() => UnifiedHmiScriptArgs.ValidateModuleFolder(dangling, ""), "references ScriptFile 'M.hmi.js'"),
                    ".yml whose ScriptFile is missing → refused");

                check(Throws(() => UnifiedHmiScriptArgs.ValidateModuleFolder(good, "Other"), "has no Other.hmi.yml"),
                    "named module not in the folder → refused, listing what is there");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static string Dir(string root, string name)
        {
            var d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            return d;
        }

        private static void WriteModule(string dir, string name)
        {
            File.WriteAllText(Path.Combine(dir, name + ".hmi.yml"), $"#Version: 2.0\n\nScriptModules:\n  {name}:\n    ScriptFile: {name}.hmi.js\n");
            File.WriteAllText(Path.Combine(dir, name + ".hmi.js"), "export function Twice(x) {\n  return x * 2;\n}\n");
        }

        private static bool Throws(Action action, string messageFragment)
        {
            try { action(); return false; }
            catch (ArgumentException ex) { return ex.Message.IndexOf(messageFragment, StringComparison.Ordinal) >= 0; }
        }
    }
}
