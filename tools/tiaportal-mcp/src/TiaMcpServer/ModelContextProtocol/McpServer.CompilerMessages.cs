using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace TiaMcpServer.ModelContextProtocol
{
    // ========================================================================
    //  编译诊断消息的收集与分类。
    //
    //  这簇原来埋在 McpServer.PlcSoftware.cs 里，全是 private static。它其实
    //  **一点 Siemens.Engineering 都不碰** —— 对 CompilerResult.Messages 完全靠
    //  反射鸭子类型地走。搬出来单独成文件之后，测试工程可以像 BlockHealthAnalyzer /
    //  McpServer.SafetyTables 那样直接 link 进去测，不必拖起整个 Openness 依赖。
    //  搬出来，才有地方给它写故障注入用例。
    //
    //  纯搬运：逻辑与原文件逐行一致，只把测试要够到的两处 private 放开成 internal。
    // ========================================================================
    public static partial class McpServer
    {
        internal sealed class CompilerMessageCollectResult
        {
            public List<string> Raw { get; } = new List<string>();
            public List<string> Errors { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public List<string> Info { get; } = new List<string>();

            /// <summary>遍历途中出的岔子（每条一句）。空 = 全程顺利。
            ///
            /// 必须有这么个东西：诊断消息是 Openness 代理对象，代理随时可能失效
            /// （切工程、TIA UI 抢了句柄），一失效属性访问就抛。以前整个遍历套在
            /// 一个 catch{} 里，抛了就**一条诊断都不返回**，而 TIA 给的
            /// ErrorCount 照样是真值 —— 调用方拿到的是「有 5 个错、errors: []」，
            /// 而且没有任何迹象表明是收集炸了而不是本来就没有明细。</summary>
            public List<string> CollectFailures { get; } = new List<string>();
        }

        internal static CompilerMessageCollectResult CollectCompilerMessages(object? messagesRoot)
        {
            var collected = new CompilerMessageCollectResult();
            if (messagesRoot is System.Collections.IEnumerable enumerable && messagesRoot is not string)
            {
                // 逐条兜：一条消息炸了不该把已经收到的其他条一起丢掉。
                // 枚举器本身也可能在 MoveNext 上炸，所以连 foreach 一起兜在外面。
                try
                {
                    foreach (var message in enumerable)
                    {
                        try
                        {
                            WalkCompilerMessageNode(message, collected);
                        }
                        catch (Exception ex)
                        {
                            collected.CollectFailures.Add(
                                $"读一条编译消息时出错（其余照常收集）：{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    collected.CollectFailures.Add(
                        $"遍历编译消息列表时中断，返回的是已收到的部分：{ex.GetType().Name}: {ex.Message}");
                }
            }
            return collected;
        }

        private static void WalkCompilerMessageNode(object? message, CompilerMessageCollectResult collected)
        {
            if (message == null) return;

            var formatted = FormatCompilerMessage(message);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                collected.Raw.Add(formatted!);
                ClassifyCompilerMessage(message, formatted!, collected);
            }

            if (!TryGetCompilerMessageChildren(message, out var children)) return;
            foreach (var child in children)
                WalkCompilerMessageNode(child, collected);
        }

        private static void ClassifyCompilerMessage(object message, string formatted, CompilerMessageCollectResult collected)
        {
            var state = ReadCompilerMessageState(message);
            var description = ReadCompilerMessageProperty(message, "Description") ?? string.Empty;
            var hasChildren = HasCompilerMessageChildren(message);

            if (IsCompilerSummaryDescription(description))
                return;

            if (IsCompilerErrorState(state))
            {
                if (!hasChildren || !string.IsNullOrWhiteSpace(description))
                    AddUniqueCompilerLine(collected.Errors, formatted);
                return;
            }

            if (IsCompilerWarningState(state))
            {
                if (!hasChildren || !string.IsNullOrWhiteSpace(description))
                    AddUniqueCompilerLine(collected.Warnings, formatted);
                return;
            }

            if (!string.IsNullOrWhiteSpace(description))
                AddUniqueCompilerLine(collected.Info, formatted);
        }

        private static bool HasCompilerMessageChildren(object message)
        {
            return TryGetCompilerMessageChildren(message, out var children) && children.Count > 0;
        }

        private static bool TryGetCompilerMessageChildren(object message, out List<object> children)
        {
            children = new List<object>();
            try
            {
                var messagesValue = message.GetType().GetProperty("Messages")?.GetValue(message);
                if (messagesValue is System.Collections.IEnumerable enumerable && messagesValue is not string)
                {
                    foreach (var child in enumerable)
                    {
                        if (child != null)
                            children.Add(child);
                    }
                }
            }
            catch
            {
                // best effort only
            }

            return children.Count > 0;
        }

        private static string? ReadCompilerMessageProperty(object message, string propertyName)
        {
            try
            {
                var value = message.GetType().GetProperty(propertyName)?.GetValue(message);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadCompilerMessageState(object message)
        {
            return ReadCompilerMessageProperty(message, "State") ?? string.Empty;
        }

        private static bool IsCompilerErrorState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            return state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || state.IndexOf("fehler", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompilerWarningState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            return state.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0
                || state.IndexOf("warnung", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompilerSummaryDescription(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return false;
            var text = description!.Trim();
            return text.StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Compilation finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Kompilierung beendet", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddUniqueCompilerLine(List<string> target, string line)
        {
            if (!target.Contains(line))
                target.Add(line);
        }

        private static void AppendCompilerEngineeringAttributes(object message, List<string> parts)
        {
            var getAttribute = message.GetType().GetMethod(
                "GetAttribute",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            if (getAttribute == null) return;

            foreach (var attrName in new[]
            {
                "Line", "Column", "BlockName", "Severity", "ErrorCode", "Message", "Text", "ObjectPath"
            })
            {
                if (parts.Any(p => p.StartsWith(attrName + "=", StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var value = getAttribute.Invoke(message, new object[] { attrName });
                    if (value == null) continue;
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add($"{attrName}={text}");
                }
                catch
                {
                    // attribute not supported on this message type
                }
            }
        }

        private static string? FormatCompilerMessage(object? message)
        {
            if (message == null) return null;

            try
            {
                var t = message.GetType();
                var parts = new List<string>();

                foreach (var name in new[]
                {
                    "State", "Severity", "ErrorCode", "Message", "Description", "Text",
                    "Path", "ObjectPath", "BlockName", "Line", "Column", "DateTime"
                })
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || p.GetIndexParameters().Length != 0) continue;

                    object? value = null;
                    try { value = p.GetValue(message); } catch { }
                    if (value == null) continue;

                    var s = value.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add($"{name}={s}");
                }

                AppendCompilerEngineeringAttributes(message, parts);

                if (parts.Count == 0)
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length != 0) continue;
                        if (string.Equals(p.Name, "Messages", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Name, "Parent", StringComparison.OrdinalIgnoreCase))
                            continue;

                        object? value = null;
                        try { value = p.GetValue(message); } catch { }
                        if (value == null) continue;
                        var s = value.ToString();
                        if (!string.IsNullOrWhiteSpace(s) && s != t.FullName)
                            parts.Add($"{p.Name}={s}");
                    }
                }

                return parts.Count > 0 ? string.Join("; ", parts) : message.ToString();
            }
            catch
            {
                return message.ToString();
            }
        }
    }
}
