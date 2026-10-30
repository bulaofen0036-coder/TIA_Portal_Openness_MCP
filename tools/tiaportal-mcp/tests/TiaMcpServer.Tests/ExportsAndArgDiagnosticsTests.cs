using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TiaMcpServer.ModelContextProtocol;

// ─────────────────────────────────────────────────────────────────────────────
//  两块「必要能力」的离线回归：参数诊断 与 大响应寄存分页。
//
//  为什么它们值得有用例盯着：
//   · 参数诊断错了不会崩，只会**少说一句话** —— 调用方（尤其是 AI 客户端）
//     退回到「An error occurred」那种什么也没说的状态，而这在日志里看不出来。
//   · 分页切片切错了同样不会崩：切在代理对中间只会让某一页的文本报废，
//     肉眼看是「后面几个字乱了」，而不是一个异常。
//  两类都属于「坏了也悄无声息」，所以每组都带反向哨兵：
//  正常输入必须照旧安静通过，否则说明这次是把功能改坏了而不是把检查修好了。
//
//  全部离线：不连博途、不碰注册表、不写工程。
// ─────────────────────────────────────────────────────────────────────────────
internal static class ExportsAndArgDiagnosticsTests
{
    public static void Run(Action<bool, string> check)
    {
        void Check(bool ok, string what) => check(ok, what);

        // ---- 参数诊断（4 条正例 + 5 条防误报哨兵）------------------------------
        

            var known = new[] { "directoryPath", "projectName", "closeForeignProject" };
            var required = new[] { "directoryPath", "projectName" };

            // A correct call must stay silent.
            Check(ArgDiagnostics.Check("CreateProject", known, required,
                      new[] { "directoryPath", "projectName" }) == "",
                "argdiag: a correct call produces no complaint");

            // Missing required -> name it and show the signature.
            var missMsg = ArgDiagnostics.Check("CreateProject", known, required, new[] { "directoryPath" });
            Check(missMsg.Contains("projectName") && missMsg.Contains("Expected signature"),
                "argdiag: missing required argument is named with a signature");

            // Unknown name -> must say it would be silently ignored.
            var unkMsg = ArgDiagnostics.Check("CreateProject", known, required,
                             new[] { "directoryPath", "projectName", "overwrite" });
            Check(unkMsg.Contains("overwrite") && unkMsg.Contains("SILENTLY IGNORED"),
                "argdiag: an unknown argument is reported, not dropped");

            // THE REAL CASE that started this: CreateProject(name, path).
            var realMsg = ArgDiagnostics.Check("CreateProject", known, required, new[] { "name", "path" });
            Check(realMsg.Contains("directoryPath") && realMsg.Contains("projectName")
                  && realMsg.Contains("name") && realMsg.Contains("path"),
                "argdiag: the measured CreateProject(name, path) failure is fully explained");

            // Typo close to a real name -> offer it.
            var typoMsg = ArgDiagnostics.Check("CreateProject", known, required,
                              new[] { "directoryPath", "projectNam" });
            Check(typoMsg.Contains("Did you mean") && typoMsg.Contains("projectName"),
                "argdiag: a near-miss name gets a suggestion");

            // SENTINEL 1 (most important): schema unreadable -> never block the call.
            Check(ArgDiagnostics.Check("Whatever", new string[0], new string[0],
                      new[] { "anything", "at", "all" }) == "",
                "argdiag sentinel: unknown schema never blocks a call");
            Check(ArgDiagnostics.Check("Whatever", null, null, new[] { "x" }) == "",
                "argdiag sentinel: null schema never blocks a call");

            // Case differences ARE errors on the direct path. This used to be a sentinel asserting
            // the opposite, on the belief that the SDK binds names case-insensitively. Measured on
            // 0.3.0-preview.4 it does not: FindTools({"Query":"watch table"}) ran with the query
            // silently dropped and listed the whole roster. So a PascalCase name must be reported,
            // with the exact spelling to use.
            var caseMsg = ArgDiagnostics.Check("CreateProject", known, required,
                              new[] { "DirectoryPath", "ProjectName" });
            Check(caseMsg.Contains("SILENTLY IGNORED") && caseMsg.Contains("DirectoryPath -> directoryPath")
                  && caseMsg.Contains("case-sensitive"),
                "argdiag: a wrong-case name is reported with the exact spelling (SDK binding is case-sensitive)");
            Check(ArgDiagnostics.Check("CreateProject", known, required,
                      new[] { "directoryPath", "projectName", "CloseForeignProject" }).Contains("CloseForeignProject -> closeForeignProject"),
                "argdiag: a wrong-case OPTIONAL name is reported too - that is the silently-ignored case");

            // SENTINEL 3: omitting an OPTIONAL argument is not an error.
            Check(ArgDiagnostics.Check("CreateProject", known, required,
                      new[] { "directoryPath", "projectName" }) == "",
                "argdiag sentinel: omitting an optional argument is fine");

            // SENTINEL 4: no suggestion when nothing is actually close - a confidently wrong
            // suggestion sends the caller down another dead end.
            Check(ArgDiagnostics.NearestName("zzzzzzzzzz", known) == null,
                "argdiag sentinel: no suggestion when nothing is close");
            Check(ArgDiagnostics.NearestName("projectNam", known) == "projectName",
                "argdiag: a one-edit typo is matched");

        // ---- 大响应寄存与分页（截断即丢 → 寄存可翻）----------------------------
        RunExportStore(Check);
    }

    private static void RunExportStore(Action<bool, string> Check)
    {

        ExportStore.ResetForTests();
        var fixedNow = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        ExportStore.NowUtc = () => fixedNow;

        // ── 分页往返 ───────────────────────────────────────────────────────
        var big = string.Concat(Enumerable.Range(0, 5000).Select(i => "行" + i + "\n"));
        var id = ExportStore.Put("GetCrossReferences", "blockPath=A3_4_Hoist", big);

        var sb = new StringBuilder();
        int off = 0, pages = 0;
        while (true)
        {
            var s = ExportStore.Slice(id, off, 1000);
            Check(s.Error == null, "分页读不该报错");
            sb.Append(s.Text);
            pages++;
            if (s.Eof) { Check(s.NextOffset == null, "读到末尾 NextOffset 必须为 null"); break; }
            Check(s.NextOffset.HasValue && s.NextOffset.Value > off, "NextOffset 必须前进（否则死循环）");
            off = s.NextOffset!.Value;
            if (pages > 200) break;   // 死循环保护，下面用页数判它没触发
        }
        Check(sb.ToString() == big, "逐页拼回来与原文完全一致");
        Check(pages <= 200, "分页没有死循环");
        Check(ExportStore.Slice(id, 0, 10).TotalLength == big.Length, "TotalLength 报的是全长");

        // ── 边界：越界、负数、零长 ─────────────────────────────────────────
        var past = ExportStore.Slice(id, big.Length + 999, 100);
        Check(past.Error == null && past.Returned == 0 && past.Eof,
              "offset 越界夹紧成空片而不是抛异常");
        var neg = ExportStore.Slice(id, -50, -1);
        Check(neg.Error == null && neg.Offset == 0 && neg.Returned > 0,
              "负 offset/负 length 夹紧到有效范围");
        Check(ExportStore.Slice(id, 0, 999999).Returned <= ExportStore.MaxSliceChars,
              "单页长度被 MaxSliceChars 钉住");

        // ── 代理对不许被切断（切断=整个响应序列化报废）────────────────────
        // "𝄞" 是一个代理对（U+1D11E），两个 char。切在正中间就会留下半个。
        var musical = "𝄞";
        var pairText = "AB" + musical + "CD";          // char 下标：A0 B1 hi2 lo3 C4 D5
        var pid = ExportStore.Put("t", "", pairText);
        var cut = ExportStore.Slice(pid, 0, 3);         // 本来会切在 hi 和 lo 中间
        // 先判非空再索引：坏实现下 Text 可能是空串，直接索引会抛 IndexOutOfRange
        // **中止整轮**（后面几百个用例一个都不跑），而不是记一条 FAIL。
        Check(cut.Returned == 2 && cut.Text.Length > 0
              && !char.IsHighSurrogate(cut.Text[cut.Text.Length - 1]),
              "切片末尾不留半个代理对（末尾缩一格）");
        var mid = ExportStore.Slice(pid, 3, 10);        // offset 落在低位代理上
        Check(mid.Offset == 2 && mid.Text.StartsWith(musical, StringComparison.Ordinal),
              "offset 落在低位代理上时退一格，代理对完整");
        // 反向哨兵：纯 BMP 文本一格都不许动，否则上面两条用「永远退一格」也能过
        var bmpId = ExportStore.Put("t", "", "起升大车小车主钩副钩");
        var bmp = ExportStore.Slice(bmpId, 3, 4);
        Check(bmp.Offset == 3 && bmp.Returned == 4,
              "[哨兵] 纯中文文本的边界一格都不动");

        // ── 零进展保护：分页绝不能原地打转 ────────────────────────────────
        // length=1 且切点正好落在代理对上时，"末尾缩一格"会把 end 缩回 offset：
        // Returned=0 而 NextOffset==Offset，调用方照着翻页就是死循环。
        var spin = ExportStore.Slice(pid, 2, 1);   // pid = "AB𝄞CD"，下标 2 是高位代理
        Check(spin.Returned > 0, "切不出东西时必须多给一个字符，不能返回 0 长度");
        Check(spin.NextOffset == null || spin.NextOffset.Value > spin.Offset,
              "[哨兵] NextOffset 必须严格前进（否则分页死循环）");
        // 拿这个最坏参数真跑一遍分页，跑不完就是死循环
        int spinOff = 0, spinRounds = 0;
        while (spinRounds++ < 50)
        {
            var sp = ExportStore.Slice(pid, spinOff, 1);
            if (sp.Eof) break;
            spinOff = sp.NextOffset!.Value;
        }
        Check(spinRounds < 50, "length=1 逐字符翻页能翻到底（不死循环）");

        // ── 过期 vs 记错了：对模型是两件事 ────────────────────────────────
        Check(ExportStore.Slice("ex_20260101000000_0001", 0, 10).Error == "expired",
              "本引擎发过、时间已过 24h 的句柄报 expired");
        Check(ExportStore.Slice("我瞎编的", 0, 10).Error == "unknown",
              "格式对不上的句柄报 unknown");
        Check(ExportStore.Slice(null, 0, 10).Error == "unknown", "null 句柄报 unknown 不抛异常");
        // 反向哨兵：格式对但**还没到期**的，不能报成 expired（那会让模型白白重跑）
        Check(ExportStore.Slice("ex_20260902095900_0009", 0, 10).Error == "unknown",
              "[哨兵] 格式对但未到期的未知句柄报 unknown，不是 expired");

        // ── 过期清理真的会发生 ────────────────────────────────────────────
        Check(ExportStore.Get(id) != null, "未过期时句柄还在");
        ExportStore.NowUtc = () => fixedNow.AddHours(ExportStore.DefaultTtlHours + 1);
        Check(ExportStore.Get(id) == null, "超过 TTL 后句柄被清掉");
        Check(ExportStore.Slice(id, 0, 10).Error == "expired", "清掉之后按 expired 报");
        ExportStore.NowUtc = () => fixedNow;

        // ── 淘汰：条数上限 ────────────────────────────────────────────────
        // 断言必须是**精确值 + 淘汰掉的是哪一条**。只写 `count <= 32` 和 `Get(last)!=null`
        // 是单向的：一个「每次 Put 都清空其余全部」的坏实现照样两条全过（count=1≤32）。
        ExportStore.ResetForTests();
        var ids = new List<string>();
        for (int i = 0; i < 60; i++) ids.Add(ExportStore.Put("t" + i, "", new string('x', 1000)));
        Check(ExportStore.Get(ids[59]) != null, "撑爆上限后，最新存进去的句柄仍然取得到");
        Check(ExportStore.Stats().count == 32, "淘汰后正好保留 32 条（不是越少越好）");
        Check(ExportStore.Get(ids[0]) == null, "最老的那条确实被淘汰了");
        // 60 存 32 留 → 淘汰 ids[0..27]，存活 ids[28..59]。边界两边都断言，
        // 只断言一边的话「多淘汰一条」或「少淘汰一条」都能溜过去。
        Check(ExportStore.Get(ids[27]) == null && ExportStore.Get(ids[28]) != null,
              "淘汰边界正好落在 ids[27]/ids[28] 之间");
        Check(ExportStore.Slice(ids[0], 0, 10).Error == "evicted",
              "被淘汰的句柄报 evicted（不是 unknown —— 那会让模型以为自己记错了 id）");

        // ── 淘汰：字符总量上限（早先零覆盖，而并发丢内容那条 bug 正走这条路）──
        ExportStore.ResetForTests();
        var big1 = ExportStore.Put("big1", "", new string('x', 3_000_000));
        var big2 = ExportStore.Put("big2", "", new string('y', 3_000_000));
        var big3 = ExportStore.Put("big3", "", new string('z', 3_000_000));
        Check(ExportStore.Stats().chars <= 8_000_000, "总字符数被上限钉住");
        Check(ExportStore.Get(big3) != null && ExportStore.Get(big2) != null, "最新两份还在");
        Check(ExportStore.Get(big1) == null, "最老那份因总量超限被淘汰");

        // ── LRU：正在被翻页的句柄不能因为「创建得早」就先出局 ──────────────
        // 必须用**会走的时钟**：固定时钟下所有 LastTouchUtc 相等，排序退化成按 id 比，
        // 于是「读过」这件事根本体现不出来，这条用例就测了个寂寞。
        ExportStore.ResetForTests();
        var tick = fixedNow;
        ExportStore.NowUtc = () => { tick = tick.AddSeconds(1); return tick; };
        var lruA = ExportStore.Put("A", "", new string('a', 3_000_000));
        var lruB = ExportStore.Put("B", "", new string('b', 3_000_000));
        ExportStore.Slice(lruA, 0, 10);                  // A 刚被读过 → 它不该是下一个牺牲品
        ExportStore.Put("C", "", new string('c', 3_000_000));
        Check(ExportStore.Get(lruA) != null, "刚被翻过的 A 还在（按最后访问时间淘汰）");
        Check(ExportStore.Get(lruB) == null, "被淘汰的是最久没读的 B");

        // ── PutAndSlice：寄存和取头部必须原子 ─────────────────────────────
        ExportStore.ResetForTests();
        var (atomId, atomHead) = ExportStore.PutAndSlice("t", "", new string('q', 5000), 1000);
        Check(atomHead.Error == null && atomHead.Returned == 1000 && atomHead.TotalLength == 5000,
              "PutAndSlice 一把锁里拿到头部");
        Check(ExportStore.Get(atomId) != null, "PutAndSlice 存进去的句柄查得到");

        // ── List / Delete / Clear ─────────────────────────────────────────
        ExportStore.ResetForTests();
        var a = ExportStore.Put("GetCrossReferences", "x", "aaa");
        ExportStore.Put("ExportBlocksAsDocuments", "y", "bbb");
        Check(ExportStore.List(null, 20).Count == 2, "List 列出全部句柄");
        Check(ExportStore.List("CrossRef", 20).Single().Id == a, "List 按工具名过滤");
        Check(ExportStore.Delete(a) && !ExportStore.Delete(a), "Delete 只在真删掉时报 true");
        Check(ExportStore.Clear(0) == 1 && ExportStore.Stats().count == 0, "Clear(0) 清空全部");

        ExportStore.ResetForTests();
        ExportStore.NowUtc = () => DateTime.UtcNow;
    
    }
}
