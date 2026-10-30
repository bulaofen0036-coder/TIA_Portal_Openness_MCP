using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    // ───────────────────────────────────────────────────────────────────────────
    //  大响应寄存 —— 判定与切片逻辑，零依赖，可单测。接线层在 McpServer.Exports.cs。
    //
    //  在此之前引擎对超大响应什么都没有：宿主按上限一刀截断，截断即丢，模型拿不到
    //  剩下的部分，只能换个更窄的参数重跑一遍整个工具调用 —— 一次 GetBlocks 或
    //  ExportBlocksAsDocuments 的重跑要几十秒，而且它凭什么知道该窄多少。真机实测过
    //  一台天车 PLC 的 GetBlocks：payload 在 2 万字符处没了，后面的内容**没有任何
    //  办法**拿到。
    //
    //  这里把「截断」换成「寄存 + 分页」：超阈值的响应整份存进这个库，返回头部
    //  切片加一个 exportId，模型用 GetExport(exportId, offset) 往后翻，或者
    //  SaveExport 一次落盘。上下文占用被阈值钉死，内容一点不丢。
    //
    //  为什么只存在内存里、不落盘：引擎是随会话起停的进程，句柄的寿命本来就不该
    //  超过会话。落盘要管清理、管并发、管跨会话的陈旧句柄，都是这条需求没有的。
    //
    //  Id 里带着建立时刻，是为了让「过期了」和「压根没这个 id」能分开报。对模型
    //  这是两件事：前者该重跑原工具，后者是它把 id 记错了。
    // ───────────────────────────────────────────────────────────────────────────

    /// <summary>寄存的一份完整响应。</summary>
    public sealed class ExportEntry
    {
        public string Id { get; set; } = "";
        /// <summary>产生它的工具名。</summary>
        public string Tool { get; set; } = "";
        /// <summary>调用目标的简述（块路径/设备名等），只为 ListExports 好认。</summary>
        public string Target { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        /// <summary>最后一次被读到的时刻。淘汰按它排（LRU），**不能按 CreatedUtc**：
        /// 这套机制的典型用法就是「寄存一份大的，一边干活一边分页读」，
        /// 正在被翻的那份必然创建最早 —— 按创建时间淘汰等于优先删掉正在用的那个。</summary>
        public DateTime LastTouchUtc { get; set; }
        public string Content { get; set; } = "";
        public int Length => Content.Length;
    }

    /// <summary>一次分页读取的结果。Error 非空时其余字段无意义。</summary>
    public sealed class ExportSlice
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public int Offset { get; set; }
        public int Returned { get; set; }
        public int TotalLength { get; set; }
        /// <summary>还有后续时给出下一段的 offset；读到末尾为 null。</summary>
        public int? NextOffset { get; set; }
        public bool Eof { get; set; }
        /// <summary>"expired"（超过 TTL）/ "evicted"（寄存区满被挤掉，内容没过期但已丢）/
        /// "unknown"（本引擎没发过这个 id）/ null（正常）。三种对模型是三种不同的下一步：
        /// expired 和 evicted 都该重跑原工具，unknown 是它把 id 记错了 —— 别混成一句话。</summary>
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    public static class ExportStore
    {
        public const int DefaultTtlHours = 24;

        /// <summary>一次 GetExport 最多给多少字符。再大就失去分页的意义了。</summary>
        public const int MaxSliceChars = 20000;

        // 总量上限：句柄是内存里的整份响应，不设顶的话一个长会话能把进程撑爆。
        // 超了就淘汰最老的 —— 最老的那个模型早就翻完或者放弃了。
        private const int MaxEntries = 32;
        private const int MaxTotalChars = 8_000_000;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, ExportEntry> _entries =
            new Dictionary<string, ExportEntry>(StringComparer.Ordinal);
        private static int _counter;

        // 墓碑：被淘汰/被删的 id 记一笔，好让 Slice 能报 "evicted" 而不是 "unknown"。
        // 「你的句柄被挤掉了，重跑原工具」和「没这个 id，你记错了」给出的下一步完全不同，
        // 后者会把模型引到 ListExports 去找一个注定不在那儿的东西。
        private const int MaxTombstones = 512;
        private static readonly HashSet<string> _tombstones = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<string> _tombstoneOrder = new Queue<string>();

        /// <summary>可注入的时钟，只为单测能造过期。生产恒为 UtcNow。</summary>
        internal static Func<DateTime> NowUtc = () => DateTime.UtcNow;

        // ── 写入 ────────────────────────────────────────────────────────────

        /// <summary>寄存一份内容，返回句柄 id。content 为 null 按空串处理。</summary>
        public static string Put(string tool, string target, string? content)
        {
            lock (_lock) { return PutLocked(tool, target, content); }
        }

        private static string PutLocked(string tool, string target, string? content)
        {
            var now = NowUtc();
            PurgeExpiredLocked(now);
            var id = "ex_" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                   + "_" + (++_counter).ToString("D4", CultureInfo.InvariantCulture);
            _entries[id] = new ExportEntry
            {
                Id = id,
                Tool = tool ?? "",
                Target = target ?? "",
                CreatedUtc = now,
                LastTouchUtc = now,
                Content = content ?? ""
            };
            // 淘汰在插入之后做，且永远不动刚插进来的这条 —— 否则一份超大响应
            // 会把自己挤掉，返回一个当场就查不到的 id。
            EvictLocked(id);
            return id;
        }

        /// <summary>寄存并**在同一把锁里**取出头部切片。
        ///
        /// 为什么要有这个原子操作：Put 之后再 Slice 是两次独立加锁，中间那一瞬别的线程
        /// 也在 Put，它的淘汰只保证不删自己刚插的那条，完全可能把我们刚插的挤掉 ——
        /// 于是 Slice 拿到 Error!=null、Text=""，调用方若不检查就把一份**成功但空**的响应
        /// 交给模型，原文既不在上下文也不在句柄里，彻底丢失且无任何报错。
        /// 合成一个原子操作，这个窗口从根上不存在。</summary>
        public static (string id, ExportSlice head) PutAndSlice(
            string tool, string target, string? content, int headLength)
        {
            lock (_lock)
            {
                var id = PutLocked(tool, target, content);
                return (id, SliceLocked(id, 0, headLength, NowUtc()));
            }
        }

        // ── 读取 ────────────────────────────────────────────────────────────

        /// <summary>取一段。offset/length 越界一律夹紧，不抛异常 —— 分页的调用者
        /// 就是会算错边界，为这个抛异常等于逼它再猜一次。</summary>
        public static ExportSlice Slice(string? id, int offset, int length)
        {
            lock (_lock) { return SliceLocked(id, offset, length, NowUtc()); }
        }

        private static ExportSlice SliceLocked(string? id, int offset, int length, DateTime now)
        {
            PurgeExpiredLocked(now);
            _entries.TryGetValue(id ?? "", out ExportEntry? entry);

            if (entry == null)
            {
                // 三种状态给三种下一步，别混成一句话：
                //   evicted 寄存区满被挤掉（内容没过期，但没了）→ 重跑原工具
                //   expired 超过 TTL                            → 重跑原工具
                //   unknown 本引擎没发过这个 id                  → 是你把 id 记错了
                // 早先只有后两种，被淘汰的一律落进 unknown，等于告诉模型「你记错了」，
                // 于是它去 ListExports 找一个注定不在那儿的东西，白烧两三个来回。
                string err = _tombstones.Contains(id ?? "") ? "evicted"
                           : LooksLikeIssuedId(id, now) ? "expired" : "unknown";
                return new ExportSlice
                {
                    Id = id ?? "",
                    Error = err,
                    Message = err == "evicted"
                        ? $"句柄 {id} 已被淘汰：寄存区放满了，最久没被读到的先出局。内容没过期但已丢弃，"
                          + "请重跑产生它的那个工具；要整份就直接 SaveExport 落盘，别一页页翻。"
                        : err == "expired"
                        ? $"句柄 {id} 已过期（寄存只保留 {DefaultTtlHours} 小时）。内容已经丢弃，"
                          + "要拿全量请重跑产生它的那个工具。"
                        : $"没有句柄 {id}。用 ListExports 看当前还有哪些，"
                          + "或者重跑产生它的工具拿一个新的。"
                };
            }

            entry.LastTouchUtc = now;      // LRU：读过就不算「最久没用」
            int total = entry.Content.Length;
            if (offset < 0) offset = 0;
            if (offset > total) offset = total;
            if (length <= 0) length = MaxSliceChars;
            if (length > MaxSliceChars) length = MaxSliceChars;

            // 代理对：offset 落在低位代理上就退一格，切片末尾落在高位代理上就缩一格。
            // 留下半个代理对，序列化成 UTF-8 时就是一个坏字节序列 —— 整个响应报废，
            // 而且报的错跟分页毫无关系，排查起来根本想不到这儿。
            if (offset > 0 && offset < total
                && char.IsLowSurrogate(entry.Content[offset])
                && char.IsHighSurrogate(entry.Content[offset - 1]))
            {
                offset--;
            }
            int end = offset + length;
            if (end > total) end = total;
            if (end > offset && end < total
                && char.IsHighSurrogate(entry.Content[end - 1])
                && char.IsLowSurrogate(entry.Content[end]))
            {
                end--;
            }

            // 零进展保护：length=1 且切点正好落在代理对上时，上面的「末尾缩一格」会把 end
            // 缩回 offset —— Returned=0 而 NextOffset==Offset，调用方照着 NextOffset 翻页
            // 就在同一个位置**无限打转**。这种时候宁可多给一个 char（整个代理对一起给），
            // 也绝不能返回一个不前进的 NextOffset。
            if (end == offset && offset < total)
            {
                end = Math.Min(total, offset + 2);   // 代理对是 2 个 char，一起给
            }

            int returned = end - offset;
            bool eof = end >= total;
            return new ExportSlice
            {
                Id = entry.Id,
                Text = entry.Content.Substring(offset, returned),
                Offset = offset,
                Returned = returned,
                TotalLength = total,
                NextOffset = eof ? (int?)null : end,
                Eof = eof
            };
        }

        /// <summary>整份内容；不存在/已过期返回 null。给 SaveExport 用。</summary>
        public static ExportEntry? Get(string? id)
        {
            var now = NowUtc();
            lock (_lock)
            {
                PurgeExpiredLocked(now);
                _entries.TryGetValue(id ?? "", out var e);
                if (e != null) e.LastTouchUtc = now;   // LRU：SaveExport 读过也算用过
                return e;
            }
        }

        /// <summary>当前还在的句柄，新的在前。</summary>
        public static IList<ExportEntry> List(string? toolFilter, int limit)
        {
            var now = NowUtc();
            if (limit <= 0) limit = 20;
            lock (_lock)
            {
                PurgeExpiredLocked(now);
                IEnumerable<ExportEntry> q = _entries.Values;
                if (!string.IsNullOrWhiteSpace(toolFilter))
                    q = q.Where(e => e.Tool.IndexOf(toolFilter!.Trim(),
                                     StringComparison.OrdinalIgnoreCase) >= 0);
                return q.OrderByDescending(e => e.CreatedUtc).ThenByDescending(e => e.Id)
                        .Take(limit).ToList();
            }
        }

        // ── 清理 ────────────────────────────────────────────────────────────

        /// <summary>删一个，返回是否真的删掉了。</summary>
        public static bool Delete(string? id)
        {
            lock (_lock)
            {
                if (!_entries.Remove(id ?? "")) return false;
                TombstoneLocked(id ?? "");
                return true;
            }
        }

        /// <summary>删掉早于 olderThanHours 的；传 0 或负数表示全清。返回删了几个。</summary>
        public static int Clear(int olderThanHours)
        {
            var now = NowUtc();
            lock (_lock)
            {
                var doomed = olderThanHours <= 0
                    ? _entries.Keys.ToList()
                    : _entries.Where(kv => (now - kv.Value.CreatedUtc).TotalHours >= olderThanHours)
                              .Select(kv => kv.Key).ToList();
                foreach (var k in doomed) { _entries.Remove(k); TombstoneLocked(k); }
                return doomed.Count;
            }
        }

        /// <summary>当前句柄数和总字符数，给 admin 类工具报状态。</summary>
        public static (int count, long chars) Stats()
        {
            lock (_lock)
            {
                PurgeExpiredLocked(NowUtc());
                return (_entries.Count, _entries.Values.Sum(e => (long)e.Length));
            }
        }

        /// <summary>只给单测用：清空并复位计数。</summary>
        internal static void ResetForTests()
        {
            lock (_lock) { _entries.Clear(); _tombstones.Clear(); _tombstoneOrder.Clear(); _counter = 0; }
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        private static void PurgeExpiredLocked(DateTime now)
        {
            var doomed = _entries.Where(kv => (now - kv.Value.CreatedUtc).TotalHours >= DefaultTtlHours)
                                 .Select(kv => kv.Key).ToList();
            foreach (var k in doomed) _entries.Remove(k);   // 过期不留墓碑：LooksLikeIssuedId 按时间就能判出 expired
        }

        /// <summary>记一笔墓碑，好让后来的 Slice 能报 "evicted" 而不是 "unknown"。
        /// 上限之内先进先出 —— 这只是为了给出更准的提示，不必无限保留。</summary>
        private static void TombstoneLocked(string id)
        {
            if (string.IsNullOrEmpty(id) || !_tombstones.Add(id)) return;
            _tombstoneOrder.Enqueue(id);
            while (_tombstoneOrder.Count > MaxTombstones)
                _tombstones.Remove(_tombstoneOrder.Dequeue());
        }

        private static void EvictLocked(string keepId)
        {
            while (_entries.Count > MaxEntries
                   || (_entries.Count > 1 && _entries.Values.Sum(e => (long)e.Length) > MaxTotalChars))
            {
                // 按**最后一次被读到**排，不是按创建时间：正在被分页翻的那份必然创建最早，
                // 按创建时间淘汰等于优先干掉正在用的那个（模型翻到一半句柄就没了）。
                var oldest = _entries.Values.Where(e => e.Id != keepId)
                                     .OrderBy(e => e.LastTouchUtc).ThenBy(e => e.Id)
                                     .FirstOrDefault();
                if (oldest == null) break;
                _entries.Remove(oldest.Id);
                TombstoneLocked(oldest.Id);
            }
        }

        /// <summary>这个 id 是不是本引擎发过的格式，且时间上已经该过期了。
        /// 用来把「过期」和「记错了」分开报。</summary>
        private static bool LooksLikeIssuedId(string? id, DateTime now)
        {
            if (string.IsNullOrEmpty(id) || !id!.StartsWith("ex_", StringComparison.Ordinal)) return false;
            var parts = id.Split('_');
            if (parts.Length != 3) return false;
            if (!DateTime.TryParseExact(parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var created)) return false;
            return (now - created).TotalHours >= DefaultTtlHours;
        }
    }
}
