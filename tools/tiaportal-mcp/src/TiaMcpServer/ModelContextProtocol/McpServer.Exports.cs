using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer.ModelContextProtocol
{
    // ───────────────────────────────────────────────────────────────────────────
    //  大响应寄存的接线层 —— 判定与切片在零依赖的 ExportStore.cs（那份能单测）。
    //
    //  为什么包在注册处而不是往每个工具里加限长：
    //  一个个加，漏掉一个就是一次几万字符灌进上下文，而且新增工具必然会忘。包在
    //  注册处是**结构性**的 —— 工具进不了工具表就到不了模型手里，进了就必然过这
    //  一层。
    //
    //  保守原则：结果的形状只要不是「恰好一个文本块」，就**原样放行**不做任何处理。
    //  宁可漏掉一次瘦身，也不能把一个本来能用的响应改坏。
    //
    //  StructuredContent 必须一起换掉。只换文本块的话，带结构化输出的宿主拿到的
    //  仍是整份原文，上下文一点没省 —— 而你看着响应里的 truncated=true 会以为省了。
    //
    //  ⚠ 与闭源线的差异：本线的 ResponseMessage 只有 Message + Meta，没有三态
    //  ResponseOutcome 契约，也没有审计包装层。所以这里的失败一律按本线既有写法
    //  抛 McpException（见 McpServer.Blocks.cs），成功才正常返回 ResponseMessage。
    // ───────────────────────────────────────────────────────────────────────────
    public static partial class McpServer
    {
        /// <summary>超过这个字符数就寄存并只回头部。0 或负数表示不限。</summary>
        public const int DefaultMaxResponseChars = 20000;

        // 分页工具自身不能被这一层处理：它们的输出本来就是按 offset 夹紧过的，
        // 再包一层只会套娃出一个永远翻不到底的句柄。
        //
        // 比较器必须是 OrdinalIgnoreCase，不能是 Ordinal：CallTool 的工具名映射建在
        // OrdinalIgnoreCase 上（McpServer.ToolBridge.cs 的 AllToolMethods），所以
        // CallTool(name="getexport") 会**成功派发**到 GetExport；这里若按大小写敏感匹配
        // 就漏判，正好给分页结果再套一层句柄。
        private static readonly HashSet<string> ExportToolNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports"
            };

        /// <summary>是不是分页工具自身。</summary>
        internal static bool IsExportTool(string? name) =>
            name != null && ExportToolNames.Contains(name);

        /// <summary>解析本次会话的阈值。TIA_MCP_MAX_RESPONSE_CHARS 覆盖默认值；
        /// 写不成数字就用默认值 —— 一个手滑的环境变量不该把限长悄悄关掉。</summary>
        public static int ResolvedMaxResponseChars()
        {
            var raw = Environment.GetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var v)) return v;
            return DefaultMaxResponseChars;
        }

        /// <summary>给每个工具包上大响应寄存层。签名固定：Program.cs / WrapTools 按这个形状调。</summary>
        public static IList<McpServerTool> WrapWithResponseGuard(IList<McpServerTool> tools)
        {
            if (tools == null) return new List<McpServerTool>();
            var outList = new List<McpServerTool>(tools.Count);
            foreach (var t in tools)
            {
                if (t == null) continue;
                var name = t.ProtocolTool?.Name;
                // 分页工具不寄存（见上），但同样要去掉多余的转义。
                outList.Add(new ResponseGuardTool(t, shrink: !(name != null && ExportToolNames.Contains(name))));
            }
            return outList;
        }

        // ── 对模型暴露的分页工具 ────────────────────────────────────────────

        [McpServerTool(Name = "GetExport"), Description(
            "[L1][Exports] Read one page of a large response that was parked under an export handle. "
            + "When a tool's response exceeds the size limit the engine stores the full text and returns "
            + "only its head plus an 'exportId'; call this with that id to read the rest. "
            + "Page forward by passing the 'nextOffset' from the previous page until 'eof' is true. "
            + "Each page is a raw CHARACTER SLICE of the original text — it can cut a line or a JSON value "
            + "in half. Concatenate every page first, THEN parse; never parse a single page on its own. "
            + "Handles live for 24 hours and only inside the current engine session.")]
        public static ResponseMessage GetExport(
            [Description("exportId: the handle from a truncated response, e.g. 'ex_20260902103000_0001'")] string exportId,
            [Description("offset: character index to start at; use the previous page's nextOffset. 0 = beginning")] int offset = 0,
            [Description("length: how many characters to return. 0 or omitted = this session's response limit.")] int length = 0)
        {
            // 默认跟随本会话阈值，别硬编码 20000：把 TIA_MCP_MAX_RESPONSE_CHARS 调小的人，
            // 本意就是每次少给点，硬编码等于把这个配置整个绕过去。
            if (length <= 0)
            {
                int lim = ResolvedMaxResponseChars();
                length = lim > 0 ? lim : ExportStore.MaxSliceChars;
            }
            var slice = ExportStore.Slice(exportId, offset, length);
            if (slice.Error != null)
            {
                // 句柄不存在/已过期/被淘汰 —— 这一页取不到，而且知道为什么。
                // 本线没有三态契约：取不到就抛，让宿主标 IsError，别返回一份空 Message
                // 让模型误以为「这份导出是空的」。
                throw new McpException(
                    slice.Message ?? $"取不到句柄 {exportId}（{slice.Error}）。",
                    McpErrorCode.InvalidParams);
            }

            return new ResponseMessage
            {
                Message = slice.Text,
                Meta = new JsonObject
                {
                    ["ok"] = true,
                    ["exportId"] = slice.Id,
                    ["offset"] = slice.Offset,
                    ["returned"] = slice.Returned,
                    ["totalLength"] = slice.TotalLength,
                    ["nextOffset"] = slice.NextOffset.HasValue ? JsonValue.Create(slice.NextOffset.Value) : null,
                    ["eof"] = slice.Eof
                }
            };
        }

        [McpServerTool(Name = "ListExports"), Description(
            "[L1][Exports] List the export handles currently held by this engine session — id, the tool "
            + "that produced each one, its target, age, and total size. Use it when you have lost an "
            + "exportId, or to check what is still available before paging.")]
        public static ResponseMessage ListExports(
            [Description("tool: optional filter, matches part of the producing tool's name")] string? tool = null,
            [Description("limit: maximum handles to return")] int limit = 20)
        {
            var items = ExportStore.List(tool, limit);
            var arr = new JsonArray();
            foreach (var e in items)
            {
                arr.Add(new JsonObject
                {
                    ["exportId"] = e.Id,
                    ["tool"] = e.Tool,
                    ["target"] = e.Target,
                    ["createdUtc"] = e.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss") + "Z",
                    ["totalLength"] = e.Length
                });
            }
            var (count, chars) = ExportStore.Stats();
            // 列举本身不依赖外部资源，走到这里就是列完了；空表也是一个确定的答案，不该报错。
            return new ResponseMessage
            {
                Message = items.Count == 0
                    ? "当前没有寄存的响应。"
                    : $"寄存中 {count} 份，共 {chars} 字符；此处列出 {items.Count} 份。",
                Meta = new JsonObject { ["ok"] = true, ["count"] = count, ["items"] = arr }
            };
        }

        [McpServerTool(Name = "SaveExport"), Description(
            "[L1][Exports] Write a parked response to a file in one step, instead of paging it through "
            + "the conversation. Prefer this whenever the user wants the whole thing (a full cross-reference "
            + "dump, a whole block list, a whole block export): it costs one call and no context. "
            + "By default the tool's PAYLOAD is written (e.g. the CSV itself), not the JSON envelope around it — "
            + "so saving a truncated table to 'IO.csv' really gives you a CSV you can open in Excel. "
            + "Pass raw=true to write the untouched response text instead. "
            + "Written as UTF-8 with BOM so Chinese opens correctly in Notepad and Excel.")]
        public static ResponseMessage SaveExport(
            [Description("exportId: the handle from a truncated response")] string exportId,
            [Description("outputPath: full file path to write, e.g. 'C:\\\\Temp\\\\IO表.csv'")] string outputPath,
            [Description("raw: true = write the response text verbatim (JSON envelope included). Default false = write just the payload.")] bool raw = false,
            [Description("overwrite: DEFAULT false — if the file already exists the call is REFUSED rather than replacing it. Pass true only after the user agreed to overwrite that specific file.")] bool overwrite = false)
        {
            var entry = ExportStore.Get(exportId);
            if (entry == null)
            {
                // 没这个句柄，文件一个字都没写。借 Slice 拿到「过期 / 被淘汰 / id 记错了」的准确说法。
                var probe = ExportStore.Slice(exportId, 0, 1);
                throw new McpException(
                    probe.Message ?? $"没有句柄 {exportId}。",
                    McpErrorCode.InvalidParams);
            }
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new McpException("outputPath 不能为空。", McpErrorCode.InvalidParams);
            }

            string full;
            try
            {
                full = Path.GetFullPath(outputPath.Trim());
            }
            catch (Exception ex)
            {
                throw new McpException($"outputPath 不是一个合法路径：{ex.Message}", ex, McpErrorCode.InvalidParams);
            }

            // 不覆盖已存在的文件。这个工具不动 TIA 工程，所以看起来「只读」、门槛低；
            // 如果它能无声覆盖任意路径，那就等于开了个写盘后门 —— 路径写成工程目录里
            // 某个已有文件，一次调用就把它盖了。默认拒绝，要覆盖得明说。
            if (File.Exists(full) && !overwrite)
            {
                // 这是按设计主动不干，但对调用方来说文件没写成，必须报错 ——
                // 报成功会让它以为备份已经存下来了。
                throw new McpException(
                    $"{full} 已存在，未覆盖。换个文件名，或者在用户同意后传 overwrite=true。",
                    McpErrorCode.InvalidParams);
            }

            // 先初始化：raw 分支不走 UnwrapPayload，out 参数不会被赋值。
            bool unwrapped = false;
            string content;
            try
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                content = raw
                    ? entry.Content
                    : ResponseGuardTool.UnwrapPayload(entry.Content, out unwrapped);
                File.WriteAllText(full, content, new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is not McpException)
            {
                // 写盘抛了：内容没有完整写出去。注意磁盘上可能留了个半截文件，别当它是好的。
                throw new McpException("写文件失败：" + ex.Message, ex, McpErrorCode.InternalError);
            }

            // WriteAllText 返回即文件已落盘，长度是写进去的那份内容的长度。
            return new ResponseMessage
            {
                Message = $"已写入 {full}（{content.Length} 字符，来自 {entry.Tool}）。"
                        + (unwrapped ? "写的是工具正文本身（已剥掉 JSON 信封）；要原文加 raw=true。" : ""),
                Meta = new JsonObject
                {
                    ["ok"] = true,
                    ["exportId"] = entry.Id,
                    ["path"] = full,
                    ["writtenLength"] = content.Length,
                    ["totalLength"] = entry.Length,
                    ["unwrapped"] = unwrapped
                }
            };
        }

        [McpServerTool(Name = "DeleteExport"), Description(
            "[L1][Exports] Drop one export handle once you are done with it. Optional — handles expire on "
            + "their own after 24 hours and the oldest are evicted automatically when the store fills up.")]
        public static ResponseMessage DeleteExport(
            [Description("exportId: the handle to drop")] string exportId)
        {
            if (!ExportStore.Delete(exportId))
            {
                // 没找到时分不清是「本来就没有/已过期」还是「id 打错了」——
                // 后一种情况下调用方真正的那个句柄还活着，报成功等于骗它。
                throw new McpException(
                    $"没有句柄 {exportId}（可能已过期或已删除，也可能 id 写错了）。用 ListExports 看当前还有哪些。",
                    McpErrorCode.InvalidParams);
            }
            return new ResponseMessage
            {
                Message = $"已删除 {exportId}。",
                Meta = new JsonObject { ["ok"] = true, ["exportId"] = exportId ?? "" }
            };
        }

        [McpServerTool(Name = "ClearExports"), Description(
            "[L1][Exports] Drop parked responses in bulk. NOTE: handles already expire on their own at 24h, "
            + "so the default olderThanHours=24 almost always deletes nothing — pass olderThanHours=0 to "
            + "actually free the store now.")]
        public static ResponseMessage ClearExports(
            [Description("olderThanHours: drop handles at least this old; 0 drops every handle")] int olderThanHours = 24)
        {
            int n = ExportStore.Clear(olderThanHours);
            var (count, chars) = ExportStore.Stats();
            // 24h 那个默认值恒等于空操作（句柄本来就到 24h 自动过期），
            // 不点破的话最自然的一次裸调用永远回「已删除 0 份」，调用方只会以为工具坏了。
            string hint = (n == 0 && olderThanHours >= ExportStore.DefaultTtlHours)
                ? $"（句柄本来就满 {ExportStore.DefaultTtlHours} 小时自动过期，所以这个默认值几乎总是删不掉东西；"
                  + "要立刻清空传 olderThanHours=0。）" : "";
            // 删了几份、还剩几份都是数出来的，纯内存操作，结局是确定的。
            return new ResponseMessage
            {
                Message = $"已删除 {n} 份；剩余 {count} 份、共 {chars} 字符。" + hint,
                Meta = new JsonObject { ["ok"] = true, ["deleted"] = n, ["remaining"] = count }
            };
        }
    }

    internal sealed class ResponseGuardTool : McpServerTool
    {
        // 文本内容是给模型原样读的。SDK 用默认编码器生成它：每个引号、每个非 ASCII 字符
        // 都变成六个字符的 \u 转义序列，而且这些序列就留在文本里 —— 模型读到的正是它们。
        // 一份带中文注释的块信息因此膨胀好几倍，也更难读。文本在传输层还会按 JSON 规则
        // 再编码一次，所以这里不转义是安全的。
        private static readonly JsonSerializerOptions StubJson = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly McpServerTool _inner;
        private readonly bool _shrink;

        public ResponseGuardTool(McpServerTool inner, bool shrink = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _shrink = shrink;
        }

        public override Tool ProtocolTool => _inner.ProtocolTool;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            // 先去转义再量长度：寄存与截断按模型真正读到的文本来算。
            try { result = RelaxJsonText(result); }
            catch { /* 去转义失败就原样返回，绝不能吃掉结果 */ }
            if (!_shrink) return result;
            try
            {
                string name = ProtocolTool?.Name ?? "(unknown)";
                string? forwarded = ForwardedToolName(name, request?.Params?.Arguments);
                // CallTool 转发到分页工具时同样要放行 —— 否则 GetExport 的那一页
                // 会被再寄存一次，模型拿到的是「一个句柄的句柄」，越翻越远。
                if (forwarded != null && McpServer.IsExportTool(forwarded)) return result;

                string target = DescribeTarget(request?.Params?.Arguments);
                if (forwarded != null) target = ("→" + forwarded + " " + target).Trim();
                return Shrink(result, forwarded ?? name, target);
            }
            catch
            {
                // 瘦身失败绝不能吃掉本来正常的结果。
                return result;
            }
        }

        /// <summary>调用目标的一句话简述，只为 ListExports 里能认出「这是哪次调用留下的」。
        /// 闭源线里这个helper长在审计层上，本线没有审计层，就地放一份 —— 只有这里用得着，
        /// 挂到 McpServer 上反而会跟别的回流文件撞名。</summary>
        internal static string DescribeTarget(IReadOnlyDictionary<string, JsonElement>? args)
        {
            if (args == null || args.Count == 0) return "";
            string[] keys = {
                "blockPath", "blockName", "typeName", "path", "softwarePath",
                "softwareName", "deviceName", "tagTableName", "watchTableName", "screenName",
            };
            var parts = new List<string>();
            foreach (var k in keys)
            {
                if (!args.TryGetValue(k, out var v)) continue;
                string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(k + "=" + s);
                if (parts.Count >= 3) break;
            }
            return string.Join(" ", parts);
        }

        /// <summary>寄存的是工具响应的**完整文本**，而这个引擎的每个工具都返回
        /// <c>ResponseMessage{Message, Meta}</c>，所以文本几乎总是一层 JSON 信封。
        /// 落盘时把信封剥掉，写里面的 message —— 落盘的意义就是给人用：
        /// 不剥的话「把截断的表存成 IO.csv」得到的是 <c>{"message":"..."}</c>，
        /// Excel 打开一片乱码，而模型正是照工具描述这么干的。
        ///
        /// 只在**确实是这个形状**时才剥（顶层 JSON 对象 + 字符串 message），
        /// 其它一律原样写 —— 猜错了把内容改坏，比多一层信封糟得多。</summary>
        internal static string UnwrapPayload(string content, out bool unwrapped)
        {
            unwrapped = false;
            if (string.IsNullOrEmpty(content)) return content;
            var t = content.TrimStart();
            if (t.Length == 0 || t[0] != '{') return content;
            try
            {
                var node = JsonNode.Parse(content);
                if (node is not JsonObject obj) return content;
                if (!obj.TryGetPropertyValue("message", out var msg)) return content;
                if (msg is not JsonValue v || !v.TryGetValue<string>(out var s)) return content;
                unwrapped = true;
                return s ?? "";
            }
            catch
            {
                return content;   // 解不出来就当它不是信封
            }
        }

        /// <summary>把文本块里的 JSON 按宽松编码重写一遍：内容逐字不变，只去掉多余的转义。
        /// 只动「整块就是一个 JSON 值、且里面确实有转义」的文本；解析不了（包括重复键）就原样放行。</summary>
        internal static CallToolResult RelaxJsonText(CallToolResult result)
        {
            var blocks = result?.Content;
            if (blocks == null || blocks.Count == 0) return result!;

            List<ContentBlock>? rewritten = null;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!(blocks[i] is TextContentBlock tb)) continue;
                string text = tb.Text ?? "";
                if (text.IndexOf("\\u", StringComparison.Ordinal) < 0) continue;
                var head = text.TrimStart();
                if (head.Length == 0 || (head[0] != '{' && head[0] != '[')) continue;

                string relaxed;
                try { relaxed = JsonNode.Parse(text)?.ToJsonString(StubJson) ?? text; }
                catch { continue; }
                if (relaxed.Length >= text.Length) continue;

                rewritten ??= new List<ContentBlock>(blocks);
                rewritten[i] = new TextContentBlock { Text = relaxed, Annotations = tb.Annotations, Meta = tb.Meta };
            }

            if (rewritten == null) return result!;
            return new CallToolResult
            {
                Content = rewritten,
                IsError = result!.IsError,
                StructuredContent = result.StructuredContent,
                Meta = result.Meta,
            };
        }

        /// <summary>CallTool 转发的目标工具名；本次调用不是转发则返回 null。</summary>
        internal static string? ForwardedToolName(
            string toolName, IReadOnlyDictionary<string, JsonElement>? args)
        {
            if (!string.Equals(toolName, "CallTool", StringComparison.Ordinal)) return null;
            if (args == null || !args.TryGetValue("name", out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }

        /// <summary>超阈值就寄存并只回头部；其余情况原样返回同一个对象。</summary>
        internal static CallToolResult Shrink(CallToolResult? result, string toolName, string target)
        {
            if (result == null) return result!;

            // 错误结果不动：它们本来就短，而且是模型自我纠正最需要看全的东西。
            if (result.IsError ?? false) return result;

            int limit = McpServer.ResolvedMaxResponseChars();
            if (limit <= 0) return result;

            // 只处理「恰好一个文本块」这一种形状。多块、图片、资源链接一律放行 ——
            // 看不懂的形状去改它，改坏的概率比省下来的上下文值钱。
            var blocks = result.Content;
            if (blocks == null || blocks.Count != 1) return result;
            if (!(blocks[0] is TextContentBlock text)) return result;

            string full = text.Text ?? "";
            // 反向哨兵：没超阈值就在这里原样返回**同一个对象引用**，
            // 未超阈值的响应因此一字不变（含 StructuredContent、Meta、块类型）。
            if (full.Length <= limit) return result;

            // 原子：寄存 + 取头部在同一把锁里完成。分成两步的话，中间别的线程 Put 时
            // 的淘汰可能把我们刚存的挤掉，Slice 就拿到 Error != null、Text=""，
            // 于是模型收到一份「成功但空」的响应 —— 原文既不在上下文也不在句柄里。
            var (id, head) = ExportStore.PutAndSlice(toolName, target, full, limit);
            // fail-open：头部都取不到就原样放行完整响应。多花上下文可以接受，丢内容不行。
            if (head.Error != null) return result;

            var meta = new JsonObject
            {
                ["truncated"] = true,
                ["exportId"] = id,
                ["offset"] = 0,
                ["returned"] = head.Returned,
                ["totalLength"] = head.TotalLength,
                ["nextOffset"] = head.NextOffset.HasValue ? JsonValue.Create(head.NextOffset.Value) : null,
                ["eof"] = head.Eof,
                ["hint"] = $"这是 {toolName} 响应的前 {head.Returned} 个字符，共 {head.TotalLength} 个。"
                         + $"**你自己要读全文**：GetExport(exportId=\"{id}\", offset={head.NextOffset}) 往后翻，"
                         + "直到 eof=true；每页是**字符切片**，会从行或 JSON 中间断开，"
                         + "要解析必须先把所有页拼完整再解析，别拿单页去 parse。"
                         + $"**用户要的是文件**：SaveExport(exportId=\"{id}\", outputPath=...) 一次落盘"
                         + "（它只回路径，不回内容，所以你自己要看的话别用它）。"
            };

            var stub = new JsonObject
            {
                ["message"] = head.Text,
                ["meta"] = meta.DeepClone()
            };

            return new CallToolResult
            {
                IsError = false,
                // 结构化输出也必须换掉，否则宿主照样把整份原文发给模型。
                StructuredContent = stub,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = stub.ToJsonString(StubJson) }
                }
            };
        }
    }
}
