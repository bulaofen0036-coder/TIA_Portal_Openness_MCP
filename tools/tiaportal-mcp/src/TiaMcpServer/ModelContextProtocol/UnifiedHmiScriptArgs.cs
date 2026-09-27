using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Unified HMI 画面事件脚本 / 脚本模块导入（issue #38）里不碰 Openness 的那部分判定，
    /// 单独成文件是为了能在离线测试里喂真输入。
    /// </summary>
    internal static class UnifiedHmiScriptArgs
    {
        /// <summary>
        /// 运行系统与官方写法里的叫法 → Openness 枚举名。
        /// 画面的 "Cleared"（运行系统事件列表里的叫法）和 "OnLoaded"/"OnUnloaded"（官方示例里的叫法）
        /// 都指 HmiScreenEventType.Loaded / Unloaded。只有目标名确实在枚举里时才生效。
        /// </summary>
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cleared"] = "Unloaded",
            ["OnLoaded"] = "Loaded",
            ["OnUnloaded"] = "Unloaded",
            ["OnCleared"] = "Unloaded",
        };

        /// <summary>
        /// 把用户给的事件名规范成枚举里的准确名字。
        /// 不直接用 Enum.Parse：它接受数字（"3"）和逗号列表（"Loaded,Unloaded"），
        /// 前者绕开名字、后者拼出一个不存在的组合值，出错时也不告诉你合法取值是什么。
        /// "None" 不是一个能挂脚本的事件，同样拒绝。
        /// </summary>
        public static string NormalizeEventName(string? input, IEnumerable<string> enumNames)
        {
            var names = (enumNames ?? Array.Empty<string>())
                .Where(n => !string.Equals(n, "None", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var valid = string.Join(", ", names);

            var value = (input ?? "").Trim();
            if (value.Length == 0)
                throw new ArgumentException($"eventType is required. Valid events: {valid}.");
            if (value.IndexOf(',') >= 0 || value.All(c => char.IsDigit(c) || c == '-' || c == '+'))
                throw new ArgumentException($"eventType '{input}' must be a single event name. Valid events: {valid}.");

            var hit = names.FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            if (Aliases.TryGetValue(value, out var target))
            {
                hit = names.FirstOrDefault(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            throw new ArgumentException($"eventType '{input}' is not an event of this object. Valid events: {valid}.");
        }

        /// <summary>导入后新出现的模块名（大小写不敏感，保持导入后的原写法与顺序）。</summary>
        public static string[] NewNames(IEnumerable<string> before, IEnumerable<string> after)
        {
            var old = new HashSet<string>(before ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return (after ?? Array.Empty<string>()).Where(n => !old.Contains(n)).ToArray();
        }

        /// <summary>模块文件夹的写法说明，出现在每一条目录校验错误里。</summary>
        public const string LayoutHint =
            "A script module is two files in the folder: <Name>.hmi.yml containing \"#Version: 2.0\", \"ScriptModules:\", "
            + "\"  <Name>:\", \"    ScriptFile: <Name>.hmi.js\", and <Name>.hmi.js with the JavaScript "
            + "(this is how TIA exports global scripts; both files must be present).";

        /// <summary>
        /// Import(DirectoryInfo, name) 的 name 是模块的基本名（V21 实测："McpMathB" 成功，
        /// "McpMathB.hmi" / "McpMathB.hmi.yml" 返回 false）。调用方很自然会把文件名传进来，这里剥掉后缀。
        /// </summary>
        public static string NormalizeModuleName(string? moduleName)
        {
            var name = (moduleName ?? "").Trim();
            foreach (var suffix in new[] { ".yml", ".yaml", ".js", ".hmi" })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - suffix.Length);
            }
            return name;
        }

        /// <summary>
        /// 碰 TIA 之前先把导入目录查一遍。TIA 对不合格的目录要么只回一个 false，要么报一句
        /// "Couldn't find any YAML files"，都不说该放什么。这里把规矩讲清楚：
        /// 目录存在、至少一个 .yml、每个 .yml 里的 ScriptFile 都在；指定了模块名时它的 .yml 必须在。
        /// </summary>
        public static string[] ValidateModuleFolder(string? importDirectory, string? moduleName)
        {
            if (string.IsNullOrWhiteSpace(importDirectory))
                throw new ArgumentException("importDirectory is required: the folder that holds the script module files. " + LayoutHint);
            if (!Directory.Exists(importDirectory))
                throw new ArgumentException($"importDirectory does not exist: {importDirectory}");

            var files = Directory.GetFiles(importDirectory!).Select(p => Path.GetFileName(p) ?? "").Where(n => n.Length > 0).ToArray();
            var ymls = files.Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (ymls.Length == 0)
                throw new ArgumentException($"No .yml file in {importDirectory} (found: {(files.Length == 0 ? "nothing" : string.Join(", ", files))}). " + LayoutHint);

            foreach (var yml in ymls)
            {
                foreach (var script in ReadScriptFiles(File.ReadAllLines(Path.Combine(importDirectory!, yml))))
                {
                    if (!files.Contains(script, StringComparer.OrdinalIgnoreCase))
                        throw new ArgumentException($"{yml} references ScriptFile '{script}', which is not in {importDirectory}. " + LayoutHint);
                }
            }

            var name = NormalizeModuleName(moduleName);
            if (name.Length > 0 && !ymls.Any(y => string.Equals(StripYaml(y), name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"moduleName '{moduleName}' has no {name}.hmi.yml in {importDirectory} (module files: {string.Join(", ", ymls)}). "
                    + "moduleName is the module's base name, e.g. 'MyModule' for MyModule.hmi.yml.");

            return files;
        }

        /// <summary>从模块 .yml 里取出所有 "ScriptFile: xxx" 的值（去引号）。</summary>
        public static IEnumerable<string> ReadScriptFiles(IEnumerable<string> ymlLines)
        {
            foreach (var raw in ymlLines ?? Array.Empty<string>())
            {
                var line = raw.Trim();
                if (!line.StartsWith("ScriptFile:", StringComparison.Ordinal)) continue;
                var value = line.Substring("ScriptFile:".Length).Trim().Trim('"', '\'');
                if (value.Length > 0) yield return value;
            }
        }

        private static string StripYaml(string fileName) => NormalizeModuleName(fileName);
    }
}
