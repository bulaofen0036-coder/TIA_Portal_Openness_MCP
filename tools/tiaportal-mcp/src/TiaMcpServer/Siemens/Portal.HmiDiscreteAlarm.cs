using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial：WinCC Unified 的**离散报警**（Discrete alarm）。
    ///
    /// 补的洞：全仓只有 Export/ImportAlarmClasses / ImportAlarmTextLists，
    /// **没有任何工具能建一条报警**。于是自动生成的工程里，报警画面上那个
    /// HmiAlarmControl 永远是空的 —— 控件在、变量在、报警一条没有，
    /// 而这件事从工具的返回值上完全看不出来（每一步都 Success）。
    ///
    /// ── V21 Openness 形状（离线读 Siemens.Engineering.WinCCUnified.xml 确认）──
    ///   HmiSoftware.DiscreteAlarms : HmiDiscreteAlarmComposition
    ///   HmiDiscreteAlarmComposition.Create(String) / Find(String)
    ///   HmiDiscreteAlarm.TriggerBitAddress          ← 触发变量（HMI 变量名）
    ///   HmiDiscreteAlarm.RaisedStateTagBitNumber    ← 触发变量里的第几位
    ///   HmiDiscreteAlarm.TriggerMode                ← OnRisingEdge / OnFallingEdge
    ///   AlarmBase.EventText / .AlarmClass / .Priority / .Origin / .Area
    ///
    /// 这里仍然走反射而不是强类型，理由和本仓其余 HMI 代码一致：同一份源要出 V20/V21
    /// 两条引擎线。代价是成员名写错不会编译报错 —— 所以**每一项都读回比对**，
    /// 读不回来就报失败（meta.success=false），绝不因为"赋值没抛异常"就算成功。
    /// </summary>
    public partial class Portal
    {
        public ResponseMessage EnsureUnifiedHmiDiscreteAlarm(
            string hmiSoftwarePath, string alarmName, string eventText,
            string triggerTag, int bitNumber, string alarmClass, string triggerMode)
        {
            // ── 预检：一个字节都不写之前把「setter 收、编译才炸」的输入拦下 ──
            // 放在 RunHmiStepTool 外面是有意的：它把 action 里抛的一切都报成失败（工程可能改了一半），
            // 而预检失败时工程确定没被碰过，是 Failure。
            string? canonicalClass = null;
            string? classCheckNote = null; // 预检放行了、但某一项没校验成（读不出来）时的如实说明
            if (!IsProjectNull())
            {
                string? rejection;
                try { rejection = PrecheckDiscreteAlarm(hmiSoftwarePath, alarmName, triggerTag, bitNumber, alarmClass, out canonicalClass, out classCheckNote); }
                catch (Exception ex)
                {
                    // 预检本身读炸了（路径不对 / 代理失效）：不能当「预检通过」往下写。
                    var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    rejection = $"写入前的预检没能完成（{inner.GetType().Name}: {inner.Message}）";
                }
                if (rejection != null)
                {
                    return new ResponseMessage
                    {
                        Message = $"报警 '{alarmName}' 没有写入：{rejection}。工程没被改动。",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now, ["tool"] = "EnsureUnifiedHmiDiscreteAlarm", ["success"] = false,
                            ["rejectedBy"] = "precheck (nothing written)", ["reason"] = rejection,
                        }
                    };
                }
            }

            return RunHmiStepTool("EnsureUnifiedHmiDiscreteAlarm", meta =>
            {
                var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);

                var alarms = TryGetPropertyValue(sw, "DiscreteAlarms");
                if (alarms == null)
                    throw new InvalidOperationException(
                        $"取不到 DiscreteAlarms 集合（hmiType={sw.GetType().FullName}）。" +
                        "经典 HMI（Comfort/KTP）没有这个集合，这个工具只管 WinCC Unified。");

                var existing = TryFindByNameInCollection(sw, new[] { "DiscreteAlarms" }, alarmName);
                string action = "exists";
                object? alarm = existing;
                if (alarm == null)
                {
                    alarm = TryCreateNamedEngineeringObject(alarms, alarmName, out var createError);
                    if (alarm == null)
                        throw new InvalidOperationException(createError ?? $"Create 在 {alarms.GetType().FullName} 上失败。");
                    action = "created";
                }

                // ---- 逐项写，逐项读回 ----
                var writes = new JsonArray();
                var unverified = new List<string>();

                void Apply(string label, object? value, params string[] candidateNames)
                {
                    if (value == null) return;
                    string? hitName = null;
                    var reasons = new List<string>();
                    foreach (var n in candidateNames)
                    {
                        // 带原因地写：共用的 TrySetProperty 把 setter 抛的异常吞成 false，
                        // 真机上 bitNumber=20 写进 Bool 变量被 TIA 拒掉时，报出来的却是
                        // 「没有一个候选属性名写得进去」—— 听起来像属性名错了，实际是值越界。
                        if (!TrySetPropertyWithReason(alarm!, n, value, out var why)) { reasons.Add($"{n}: {why}"); continue; }
                        hitName = n;
                        break;
                    }
                    if (hitName == null)
                    {
                        writes.Add(new JsonObject { ["item"] = label, ["written"] = false, ["reason"] = "写不进去：" + string.Join("；", reasons) });
                        unverified.Add(label);
                        return;
                    }
                    var back = TryGetPropertyValue(alarm!, hitName);
                    string expected = value.ToString() ?? "";
                    string actual = back?.ToString() ?? "";
                    bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
                    writes.Add(new JsonObject
                    {
                        ["item"] = label, ["property"] = hitName, ["written"] = true,
                        ["expected"] = expected, ["readBack"] = actual, ["verified"] = ok,
                    });
                    if (!ok) unverified.Add($"{label}（写 '{expected}'，读回 '{actual}'）");
                }

                // ⚠️ 这两条是真机实测踩出来的，别按文档里的属性名想当然：
                //   · TriggerBitAddress 是**只读**（CanWrite=False）—— 它是 TIA 自己拼出来的
                //     "变量.位号" 复合串。能写的是 RaisedStateTag（String）+ RaisedStateTagBitNumber。
                //   · EventText 是 MultilingualText 而且**属性本身只读**，文本要写进
                //     EventText.Items 里对应语言那一条的 Text。
                // 第一版照着 Openness 文档的成员名直接写这两个，工具当场报「没有一个候选属性名
                // 写得进去」并判失败 —— 那正是它该有的反应，而不是假装成功。
                ApplyMultilingual(alarm!, "EventText", eventText, writes, unverified);
                Apply("triggerTag", string.IsNullOrWhiteSpace(triggerTag) ? null : triggerTag, "RaisedStateTag");
                // bitNumber=-1 = 不动。以前 MCP 层默认值是 0：只想改一句报警文本，
                // 真机上 Word 变量第 15 位的报警被悄悄改回了第 0 位，读回还"一致"。
                Apply("bitNumber", bitNumber >= 0 ? (object)(uint)bitNumber : null, "RaisedStateTagBitNumber");
                Apply("alarmClass", string.IsNullOrWhiteSpace(alarmClass) ? null : (canonicalClass ?? alarmClass), "AlarmClass");
                if (!string.IsNullOrWhiteSpace(triggerMode))
                    ApplyEnum(alarm!, "TriggerMode", triggerMode, writes, unverified);

                // TriggerBitAddress 写不了，但**读得到** —— 它是 TIA 用上面两项拼出来的结果，
                // 正好是一条独立于我们自己赋值的旁证：它对了，说明变量名和位号确实落进去了。
                meta["triggerBitAddress"] = TryGetPropertyValue(alarm!, "TriggerBitAddress")?.ToString() ?? "";
                meta["action"] = action;
                meta["alarmType"] = alarm!.GetType().FullName;
                meta["writes"] = writes;
                meta["allWritesVerified"] = unverified.Count == 0;
                // 预检放行 ≠ 预检校验过。读不出报警类别清单时上面是「不拿猜测拒真活」地放行的，
                // 这件事必须跟着结果一起出去，否则调用方会以为类别名已经被查过了。
                meta["alarmClassVerified"] = classCheckNote == null;
                if (classCheckNote != null) meta["alarmClassPrecheckNote"] = classCheckNote;

                if (unverified.Count > 0)
                {
                    // 这里必须抛：RunHmiStepTool 只在 action 正常返回时才报 Success，
                    // 而「写了但读不回来/读回来不一样」正是最需要被看见的那种半成品。
                    throw new InvalidOperationException(
                        $"报警 '{alarmName}' {action}，但有 {unverified.Count} 项没通过读回核对：" +
                        string.Join("；", unverified) + "。工程可能已经被改了一半。");
                }

                return $"Unified HMI 离散报警 '{alarmName}' {action}，" +
                       $"触发 {meta["triggerBitAddress"]}，每一项都读回核对过。" +
                       (classCheckNote == null ? "" : " 注意：" + classCheckNote);
            });
        }

        /// <summary>
        /// 写入前预检。返回 null = 通过；否则是拒绝理由（此时工程一个字节都没动）。
        /// 规则见 <see cref="HmiDiscreteAlarmSpec"/>，全部是真机上「setter 收下、编译才炸」或「setter 拒但报警已建出来」的输入。
        /// </summary>
        private string? PrecheckDiscreteAlarm(string hmiSoftwarePath, string alarmName, string triggerTag, int bitNumber, string alarmClass, out string? canonicalClass, out string? classCheckNote)
        {
            canonicalClass = null;
            classCheckNote = null;
            var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
            if (TryGetPropertyValue(sw, "DiscreteAlarms") is not IEnumerable alarms)
                return $"取不到 DiscreteAlarms 集合（hmiType={sw.GetType().FullName}）。经典 HMI（Comfort/KTP）没有这个集合，这个工具只管 WinCC Unified";

            // 报警类别：写一个不存在的名字 setter 照收、读回也一致，编译才报 "alarm class property … is invalid"。
            if (!string.IsNullOrWhiteSpace(alarmClass))
            {
                // 原来用 ListNamesInCollection —— 它把「取不到集合」「枚举抛异常」一律吞成空表 → 空表让
                // ResolveAlarmClass 返回 null → 拒掉，消息还是「'X' 不存在。工程里有：」后面一片空白。
                // 「读不出来」不是「不存在」。同仓经典连接守卫（Portal.HmiClassicImportGuard.cs 的
                // GuardClassicTagTableConnectionsBeforeImport）对同一个两难的处置是**相反**的：读不出来就放行，
                // 注释写明"不拿猜测拒真活"。两边必须一致 —— 现在只有真读出一份非空清单、而且要的名字不在里面才拒；
                // 其余情况放行，并把"这一项没校验成"如实带回给调用方。
                var classes = TryListNamesInCollection(sw, "AlarmClasses", out var classesFailure);
                if (classesFailure != null || classes.Count == 0)
                {
                    canonicalClass = alarmClass.Trim(); // 校验不了就原样用调用方给的写法，不做大小写归一
                    classCheckNote = $"报警类别 '{canonicalClass}' 没能校验（{classesFailure ?? "工程里一个报警类别名都没读出来"}）——" +
                                     "类别名写错时 setter 照收、读回也一致，要到编译才报 'alarm class property … is invalid'，请自行编译确认。";
                }
                else
                {
                    canonicalClass = HmiDiscreteAlarmSpec.ResolveAlarmClass(classes, alarmClass.Trim());
                    if (canonicalClass == null)
                        return $"报警类别 '{alarmClass}' 不存在。工程里有：{string.Join(", ", classes)}";
                }
            }

            // 现有报警（含自己）的触发变量 + 位。
            var triggers = new List<HmiDiscreteAlarmSpec.Trigger>();
            object? self = null;
            foreach (var a in alarms)
            {
                if (a == null) continue;
                var name = TryGetPropertyValue(a, "Name") as string ?? "";
                if (string.Equals(name, alarmName, StringComparison.OrdinalIgnoreCase)) self = a;
                triggers.Add(new HmiDiscreteAlarmSpec.Trigger
                {
                    AlarmName = name,
                    Tag = TryGetPropertyValue(a, "RaisedStateTag")?.ToString() ?? "",
                    Bit = Convert.ToInt64(TryGetPropertyValue(a, "RaisedStateTagBitNumber") ?? 0L),
                });
            }

            // 这次调用之后这条报警**实际**会落在哪个变量、哪一位（没传的沿用现有值；新建的位号是 0）。
            var selfTrigger = triggers.FirstOrDefault(t => self != null && string.Equals(t.AlarmName, alarmName, StringComparison.OrdinalIgnoreCase));
            string effectiveTag = !string.IsNullOrWhiteSpace(triggerTag) ? triggerTag.Trim() : selfTrigger?.Tag ?? "";
            long effectiveBit = bitNumber >= 0 ? bitNumber : selfTrigger?.Bit ?? 0;
            if (string.IsNullOrWhiteSpace(effectiveTag) || effectiveTag.StartsWith("<", StringComparison.Ordinal))
                return null; // 还没绑触发变量（读回 "<无变量>"）：没有可查的组合

            // 触发变量必须真的存在。绑一个不存在的名字不会当场报错，
            // 要到编译或运行时才炸，而那时人已经不在这一步了。
            var tag = TryFindByNameInCollection(sw, new[] { "Tags" }, effectiveTag);
            if (tag == null)
            {
                if (string.IsNullOrWhiteSpace(triggerTag)) return null; // 现有绑定本身悬空不是这次调用造成的，不拦
                var known = ListHmiTagNamesForDiagnostics(sw, 40);
                return $"HMI 变量 '{triggerTag}' 不存在，不给你绑一个悬空的名字。" +
                       (known.Count > 0 ? "现有变量（最多列 40 个）：" + string.Join(", ", known) : "");
            }

            var typeProblem = HmiDiscreteAlarmSpec.CheckTrigger(TryGetPropertyValue(tag, "DataType")?.ToString(), (int)effectiveBit);
            if (typeProblem != null) return $"变量 '{effectiveTag}'：{typeProblem}";

            var conflict = HmiDiscreteAlarmSpec.FindTriggerConflict(triggers, alarmName, effectiveTag, effectiveBit);
            if (conflict != null)
                return $"变量 '{effectiveTag}' 的第 {effectiveBit} 位已经被报警 '{conflict.AlarmName}' 占用"
                     + "（TIA 编译会对两条都报 'The tag/bit combination is also used for other HMI alarms'）。换一位或换一个变量";
            return null;
        }

        /// <summary>
        /// 只读：列出 Unified 离散报警（触发变量/位/类别/文本…）和工程里实际有的报警类别。
        /// 每条报警上的标量属性原样读出，读炸的写明读炸了，不当成空值。
        /// </summary>
        public ResponseMessage GetUnifiedHmiDiscreteAlarms(string hmiSoftwarePath)
        {
            if (IsProjectNull())
                return new ResponseMessage { Message = "No project is open (project is null) — nothing was read or changed.", Meta = new JsonObject { ["success"] = false } };

            var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
            if (TryGetPropertyValue(sw, "DiscreteAlarms") is not IEnumerable alarms)
                throw new InvalidOperationException(
                    $"取不到 DiscreteAlarms 集合（hmiType={sw.GetType().FullName}）。这个工具只管 WinCC Unified。");

            // 报警类别读不出来原来吞成空表，工具照样报 Success「类别：」（空）—— 用带病因的版本（2026-09-26 审查 A6）。
            var classes = TryListNamesInCollection(sw, "AlarmClasses", out var classesFailure);
            var list = new JsonArray();
            int unreadable = 0;
            foreach (var a in alarms)
            {
                if (a == null) continue;
                var props = new JsonObject();
                foreach (var p in a.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
                    var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    if (!(t.IsPrimitive || t.IsEnum || t == typeof(string))) continue;
                    try { props[p.Name] = p.GetValue(a)?.ToString(); }
                    catch (Exception ex) { props[p.Name] = "<read threw: " + ex.GetType().Name + ">"; unreadable++; }
                }
                var texts = new JsonObject();
                if (TryGetPropertyValue(a, "EventText") is object mlt && TryGetPropertyValue(mlt, "Items") is IEnumerable items)
                {
                    foreach (var item in items)
                    {
                        if (item == null) continue;
                        string lang = "?";
                        try { lang = TryGetPropertyValue(TryGetPropertyValue(item, "Language") ?? new object(), "Culture")?.ToString() ?? "?"; } catch { }
                        texts[lang] = StripXmlTags(TryGetPropertyValue(item, "Text") as string ?? "");
                    }
                }
                props["EventText"] = texts;
                list.Add(props);
            }

            return new ResponseMessage
            {
                Message = $"'{hmiSoftwarePath}' 有 {list.Count} 条离散报警；工程里的报警类别："
                          + (classesFailure == null ? string.Join(", ", classes) : "<读不出来：" + classesFailure + ">") + "。"
                          + (unreadable == 0 ? "" : $" 有 {unreadable} 个属性读炸了，见 <read threw>。"),
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = unreadable == 0 && classesFailure == null,
                    ["alarmClassesFailure"] = classesFailure,
                    ["alarmClasses"] = new JsonArray(classes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                    ["alarms"] = list,
                }
            };
        }

        /// <summary>
        /// 同上，但把**为什么列不出来**带出去：取不到集合 / 枚举抛异常 / 元素上读不到 Name，
        /// 原来全被吞成"空表"，于是「读不出来」和「真的一个都没有」在调用点长得一模一样 ——
        /// 前者该放行，后者才谈得上拒。failure == null 才表示这份清单是真读出来的。
        /// </summary>
        /// <summary>按名取属性；派生类隐藏基类同名属性时取最派生的那个（Type.GetProperty 会抛 AmbiguousMatchException）。</summary>
        private static System.Reflection.PropertyInfo? FindSinglePropertyByName(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }

        private static List<string> TryListNamesInCollection(object root, string collectionProperty, out string? failure)
        {
            failure = null;
            var names = new List<string>();
            try
            {
                if (TryGetPropertyValue(root, collectionProperty) is not IEnumerable col)
                {
                    failure = $"{root.GetType().Name} 上取不到 {collectionProperty} 集合";
                    return names;
                }
                int unnamed = 0;
                foreach (var o in col)
                {
                    if (o == null) continue;
                    if (TryGetPropertyValue(o, "Name") is string n) names.Add(n);
                    else unnamed++;
                }
                if (names.Count == 0 && unnamed > 0)
                    failure = $"{collectionProperty} 里有 {unnamed} 个对象，但一个都读不出 Name";
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                failure = $"枚举 {collectionProperty} 时抛了 {inner.GetType().Name}: {inner.Message}";
            }
            return names;
        }

        /// <summary>
        /// 写 MultilingualText 型的文本（EventText / InfoText）。
        /// 属性本身只读，真正能改的是 <c>Items</c> 里每种语言那一条的 <c>Text</c>；
        /// <c>Items</c> 只有 <c>Find(Language)</c>、**没有 Create**，所以只能写工程里已有的语言。
        /// 这里把每一种已有语言都写成同一句，省得运行时切语言就空了。
        /// </summary>
        private static void ApplyMultilingual(object target, string propName, string text, JsonArray writes, List<string> unverified)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var mlt = TryGetPropertyValue(target, propName);
            if (mlt == null)
            {
                writes.Add(new JsonObject { ["item"] = propName, ["written"] = false, ["reason"] = $"{target.GetType().Name} 上取不到 {propName}" });
                unverified.Add(propName);
                return;
            }
            if (TryGetPropertyValue(mlt, "Items") is not IEnumerable items)
            {
                writes.Add(new JsonObject { ["item"] = propName, ["written"] = false, ["reason"] = $"{propName}.Items 取不到（类型 {mlt.GetType().FullName}）" });
                unverified.Add(propName);
                return;
            }

            int written = 0, verified = 0;
            var langs = new JsonArray();
            foreach (var item in items)
            {
                if (item == null) continue;
                string lang = "?";
                try { lang = TryGetPropertyValue(TryGetPropertyValue(item, "Language") ?? new object(), "Culture")?.ToString() ?? "?"; } catch { }
                // WinCC Unified 的报警文本是**富文本**：读回来长这样 `<body><p/></body>`，
                // 直接塞一段纯文本进去会被 setter 拒掉（而 TrySetProperty 把异常吞了，
                // 只剩一个 false，看起来像"这个属性不存在"—— 完全是另一回事）。
                // 所以这里先按现有值的形状决定写什么，并且**把失败原因带出来**。
                string current = TryGetPropertyValue(item, "Text") as string ?? "";
                bool rich = current.TrimStart().StartsWith("<body", StringComparison.OrdinalIgnoreCase);
                string payload = rich ? "<body><p>" + EscapeXmlText(text) + "</p></body>" : text;

                bool set = TrySetTextWithReason(item, payload, out var reason);
                if (!set && !rich)
                {
                    // 现有值是空的、看不出是不是富文本时，退一步按富文本再试一次。
                    payload = "<body><p>" + EscapeXmlText(text) + "</p></body>";
                    set = TrySetTextWithReason(item, payload, out reason);
                    rich = true;
                }

                var back = TryGetPropertyValue(item, "Text") as string ?? "";
                // 富文本读回来带标签，逐字比对必然不等 —— 判据改成"纯文本内容一致"。
                bool ok = set && StripXmlTags(back).Trim() == text.Trim();
                if (set) written++;
                if (ok) verified++;
                langs.Add(new JsonObject
                {
                    ["language"] = lang, ["written"] = set, ["verified"] = ok,
                    ["richText"] = rich, ["readBack"] = back,
                    ["reason"] = set ? null : reason,
                });
            }

            writes.Add(new JsonObject
            {
                ["item"] = propName, ["property"] = propName + ".Items[].Text",
                ["written"] = written > 0, ["languages"] = langs,
                ["verified"] = written > 0 && verified == written,
            });

            if (written == 0)
                unverified.Add($"{propName}（工程里一种语言都没写进去 —— Items 是空的，或者 Text 写不进）");
            else if (verified != written)
                unverified.Add($"{propName}（{written} 种语言里只有 {verified} 种读回一致）");
        }

        /// <summary>同 TrySetProperty（含值类型转换），但把失败原因带出来：属性不存在 / 只读 / setter 拒值，三种分开说。</summary>
        private static bool TrySetPropertyWithReason(object target, string propName, object? value, out string reason)
        {
            reason = "";
            try
            {
                var p = FindSinglePropertyByName(target.GetType(), propName);
                if (p == null) { reason = $"{target.GetType().Name} 上没有 {propName}"; return false; }
                if (!p.CanWrite) { reason = $"{target.GetType().Name}.{propName} 是只读的"; return false; }
                p.SetValue(target, CoerceReflectionValue(value, p.PropertyType));
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                reason = $"{inner.GetType().Name}: {inner.Message}";
                return false;
            }
        }

        /// <summary>写 Text 并**把失败原因带出来**。共用的 TrySetProperty 把异常吞成一个 false，
        /// 于是"属性不存在"和"值的格式不对"长得一模一样 —— 这两种要修的地方完全不同。</summary>
        private static bool TrySetTextWithReason(object item, string value, out string? reason)
        {
            reason = null;
            try
            {
                var p = item.GetType().GetProperty("Text");
                if (p == null) { reason = $"{item.GetType().Name} 上没有 Text 属性"; return false; }
                if (!p.CanWrite) { reason = $"{item.GetType().Name}.Text 是只读的"; return false; }
                p.SetValue(item, value);
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                reason = $"{inner.GetType().Name}: {inner.Message}";
                return false;
            }
        }

        private static string EscapeXmlText(string s)
            => (s ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>把富文本读回值剥成纯文本，只为比对用。</summary>
        private static string StripXmlTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var plain = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", string.Empty);
            return plain.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");
        }

        /// <summary>枚举属性单独一条路：名字不合法时要把可用值列出来，不能只说"设置失败"。</summary>
        private static void ApplyEnum(object target, string propName, string value, JsonArray writes, List<string> unverified)
        {
            var prop = target.GetType().GetProperty(propName);
            if (prop == null)
            {
                writes.Add(new JsonObject { ["item"] = propName, ["written"] = false, ["reason"] = $"这个 TIA 版本的 {target.GetType().Name} 上没有 {propName}" });
                unverified.Add(propName);
                return;
            }
            var enumType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!enumType.IsEnum)
            {
                writes.Add(new JsonObject { ["item"] = propName, ["written"] = false, ["reason"] = $"{propName} 不是枚举（{enumType.FullName}）" });
                unverified.Add(propName);
                return;
            }
            // 只按名字匹配：Enum.Parse(..., ignoreCase:true) 会把 "7" 这种数字串
            // 造成一个未定义的枚举值传进 Openness，而那是静默的。
            var names = Enum.GetNames(enumType);
            var hit = names.FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                writes.Add(new JsonObject { ["item"] = propName, ["written"] = false, ["reason"] = $"'{value}' 不是合法值。可用：{string.Join(", ", names)}" });
                unverified.Add($"{propName}（'{value}' 不是合法值，可用：{string.Join(", ", names)}）");
                return;
            }
            var enumValue = Enum.Parse(enumType, hit);
            bool set = TrySetProperty(target, propName, enumValue);
            var back = TryGetPropertyValue(target, propName);
            bool ok = set && string.Equals(back?.ToString(), hit, StringComparison.Ordinal);
            writes.Add(new JsonObject
            {
                ["item"] = propName, ["property"] = propName, ["written"] = set,
                ["expected"] = hit, ["readBack"] = back?.ToString() ?? "", ["verified"] = ok,
            });
            if (!ok) unverified.Add($"{propName}（写 '{hit}'，读回 '{back}'）");
        }

        /// <summary>出错时列几个真实存在的 HMI 变量名，省得调用方去猜。列不出来就返回空表，不抛。</summary>
        private static List<string> ListHmiTagNamesForDiagnostics(object hmiSoftware, int max)
        {
            var names = new List<string>();
            try
            {
                if (TryGetPropertyValue(hmiSoftware, "Tags") is not IEnumerable tags) return names;
                foreach (var t in tags)
                {
                    if (t == null) continue;
                    names.Add(TryGetPropertyValue(t, "Name") as string ?? "?");
                    if (names.Count >= max) break;
                }
            }
            catch { }
            return names;
        }
    }
}
