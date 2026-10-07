using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    // ───────────────────────────────────────────────────────────────────────────
    //  参数诊断的接线层 —— 判定逻辑在零依赖的 ArgDiagnostics.cs（那份能单测）。
    //
    //  实测的问题：直接调用一个已注册工具时少传/写错参数名，调用方拿到的全部信息就是一句
    //        An error occurred invoking 'GetSoftwareTree'.
    //  没说少了什么，也没说正确的参数叫什么。SDK 在参数绑定阶段就失败，工具方法体
    //  根本没执行，所以方法内部的任何校验都来不及。对 AI 客户端等于什么都没说，
    //  只能靠反复试错猜参数名。
    //
    //  为什么包在注册处而不是往每个工具里加校验：
    //  一个个加，漏掉一个就是一次零信息失败，而且新增工具必然会忘。包在注册处是
    //  **结构性**的 —— 工具进不了工具表就到不了模型手里，进了就必然过这一层。
    //
    //  保守原则：InputSchema 读不出来就**不做任何判断**直接放行。
    //  宁可漏掉一次诊断，也不能把一个本来能跑的调用拦下来。
    // ───────────────────────────────────────────────────────────────────────────
    public static partial class McpServer
    {
        /// <summary>注册工具的**唯一**入口：参数诊断在最外层，其次是 Openness 串行闸，大响应护栏在里层。
        /// 每一处把工具交给 MCP 服务器的地方都必须走这里 —— 只接一半的层，
        /// 就是后加的注册点静默丢掉另一半的由来。
        /// 诊断放最外层是因为它必须在参数绑定之前拦住调用（副作用一点都不许发生），
        /// 也不该为了报一句「参数写错了」去排队等闸。</summary>
        public static IList<McpServerTool> WrapTools(IList<McpServerTool> tools) =>
            WrapWithArgDiagnostics(WrapWithPortalGate(WrapWithResponseGuard(Annotate(tools))));

        /// <summary>把 ToolSafety 的判定写进协议层的 annotations。不写的话，按 MCP 规范的默认值
        /// 每个工具都是「可能破坏、会碰外部世界」，宿主没法区分 GetBlocks 和 DownloadToPlc。
        /// 外面几层包装都原样转发 ProtocolTool，所以写在最里层的对象上就够了。</summary>
        public static IList<McpServerTool> Annotate(IList<McpServerTool> tools)
        {
            if (tools == null) return new List<McpServerTool>();
            foreach (var t in tools)
            {
                var pt = t?.ProtocolTool;
                if (pt == null) continue;
                var s = ToolSafety.Classify(pt.Name);
                pt.Annotations ??= new ToolAnnotations();
                pt.Annotations.ReadOnlyHint = s.ReadOnly;
                pt.Annotations.DestructiveHint = s.Destructive;
                pt.Annotations.IdempotentHint = s.Idempotent;
                pt.Annotations.OpenWorldHint = s.OpenWorld;
            }
            return tools;
        }

        /// <summary>需要 TIA Portal 句柄的工具统一过同一把闸，见 <see cref="PortalGateTool"/>。</summary>
        public static IList<McpServerTool> WrapWithPortalGate(IList<McpServerTool> tools)
        {
            if (tools == null) return new List<McpServerTool>();
            var outList = new List<McpServerTool>(tools.Count);
            foreach (var t in tools)
            {
                if (t == null) continue;
                outList.Add(ToolSafety.Classify(t.ProtocolTool?.Name).TouchesPortal ? new PortalGateTool(t) : t);
            }
            return outList;
        }

        /// <summary>逐个包装，让参数错误能自己把话说清楚。</summary>
        public static IList<McpServerTool> WrapWithArgDiagnostics(IList<McpServerTool> tools)
        {
            if (tools == null) return new List<McpServerTool>();
            var outList = new List<McpServerTool>(tools.Count);
            foreach (var t in tools)
            {
                if (t == null) continue;
                outList.Add(new ArgDiagnosticTool(t));
            }
            return outList;
        }
    }

    internal sealed class ArgDiagnosticTool : McpServerTool
    {
        private readonly McpServerTool _inner;

        public ArgDiagnosticTool(McpServerTool inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        // 协议层看到的仍是原工具的完整描述：这层只在出错时说话，不改工具的对外形状。
        public override Tool ProtocolTool => _inner.ProtocolTool;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var tool = ProtocolTool;
            var known = new List<string>();
            var required = new List<string>();
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (ReadSchema(tool, known, required, types))
            {
                var supplied = new List<string>();
                var args = request.Params?.Arguments;
                if (args != null)
                    foreach (var kv in args)
                        if (!IsProtocolField(kv.Key))
                            supplied.Add(kv.Key);

                // 无参工具要单独说一句。ArgDiagnostics.Check 把「known 为空」定义成
                // 「schema 读不出来 → 不做判断」（那条哨兵是对的：判不了就别拦）。
                // 但**无参工具的 schema 是读得出来的**，它只是没有参数 —— 两件事被同一个
                // 空集合表示，于是无参工具（GetState / Connect / Disconnect / SaveProject /
                // CloseProject …）身上这层保护会整批失效：喂一个不存在的参数不但不拦，
                // 还照常执行（SaveProject 会落盘）。
                // 判断留在这里而不是改 Check：Check 是零依赖单测件，它那条语义有哨兵钉着。
                if (known.Count == 0)
                {
                    if (supplied.Count > 0)
                        return TextError((tool?.Name ?? "(tool)")
                            + " takes no arguments, but got: " + string.Join(", ", supplied)
                            + ". They would have been SILENTLY IGNORED (nothing was executed).");
                }
                else
                {
                    string problem = ArgDiagnostics.Check(tool?.Name ?? "(tool)", known, required, supplied, types);
                    if (problem.Length > 0)
                        return TextError(problem);
                }
            }

            // 参数没问题 → 原样转交，返回内部工具的结果本身（不改写、不重新包装）。
            return await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>协议自己可能塞进来的字段，不算工具参数。</summary>
        private static bool IsProtocolField(string key) =>
            string.Equals(key, "_meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>从工具自带的 JSON schema 里取出属性名、required 列表和类型。
        /// false = schema 读不出来，意思是「这次调用不作判断」。</summary>
        private static bool ReadSchema(Tool? tool, List<string> known, List<string> required,
                                      Dictionary<string, string> types)
        {
            if (tool == null) return false;
            try
            {
                JsonElement schema = tool.InputSchema;
                if (schema.ValueKind != JsonValueKind.Object) return false;
                // properties 缺失 = **无参工具**，不是「schema 读不出来」。SDK 对有参数的
                // 工具一定会写 properties，所以这里的空是权威的空，可以照它判。
                if (!schema.TryGetProperty("properties", out var props)) return true;
                if (props.ValueKind != JsonValueKind.Object) return false;

                foreach (var p in props.EnumerateObject())
                {
                    known.Add(p.Name);
                    if (p.Value.ValueKind == JsonValueKind.Object
                        && p.Value.TryGetProperty("type", out var ty)
                        && ty.ValueKind == JsonValueKind.String)
                    {
                        var s = ty.GetString();
                        if (!string.IsNullOrEmpty(s)) types[p.Name] = s!;
                    }
                }

                if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                    foreach (var r in req.EnumerateArray())
                        if (r.ValueKind == JsonValueKind.String)
                        {
                            var s = r.GetString();
                            if (!string.IsNullOrEmpty(s)) required.Add(s!);
                        }

                return true;
            }
            catch
            {
                return false;   // schema 读不动，绝不能因此把调用拦下来
            }
        }

        private static CallToolResult TextError(string message) =>
            new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = message } }
            };
    }

    /// <summary>
    /// 同一时刻只让一个工具调用碰 TIA Portal。
    ///
    /// SDK 对每条进来的请求各起一个任务处理，宿主一次发出的几个并行 tools/call
    /// 会在不同线程上同时进入 Portal —— 而 Portal 是进程级单例，字段（当前工程、
    /// 会话、SoftwareContainer 缓存、LastConnectError…）全都没有同步。两个调用交错时，
    /// 一个的 OpenProject 可以换掉另一个正在遍历的工程，结果是张冠李戴而不是报错。
    /// 串行化放在注册层而不是 Portal 里：Portal 的方法彼此嵌套调用，在那里加锁要么漏、要么自锁。
    /// </summary>
    internal sealed class PortalGateTool : McpServerTool
    {
        internal static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private readonly McpServerTool _inner;

        public PortalGateTool(McpServerTool inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override Tool ProtocolTool => _inner.ProtocolTool;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            // 排队期间被取消的调用直接放弃，不能在客户端已经不要结果之后才去动工程。
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
