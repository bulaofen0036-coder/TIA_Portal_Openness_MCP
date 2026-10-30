using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// issue #54：Unified 离散报警写入前的本地预检。类型表来自 V21 真机逐类型编译（2026-10-26）：
    /// 只有 Bool 和位串类型能当触发变量；整数（有无符号都一样）、浮点、字符串编译都报
    /// 'The data type of the tag is not permitted for this type of alarm'。
    /// </summary>
    internal static class Issue54Tests
    {
        internal static void Run(Action<bool, string> check)
        {
            foreach (var t in new[] { "Bool", "Byte", "Word", "DWord", "LWord", "word" })
                check(HmiDiscreteAlarmSpec.CheckTrigger(t, 0) == null, "[反向哨兵] " + t + " 第 0 位要放行");

            foreach (var t in new[] { "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal", "String", "WString" })
            {
                var r = HmiDiscreteAlarmSpec.CheckTrigger(t, 0);
                check(r != null && r.Contains("Byte/Word/DWord/LWord"), t + " 要在写入前拒掉并给出可用类型，实得 " + (r ?? "<放行>"));
            }

            check(HmiDiscreteAlarmSpec.CheckTrigger("Word", 15) == null, "Word 第 15 位合法");
            check(HmiDiscreteAlarmSpec.CheckTrigger("Word", 16) != null, "Word 第 16 位越界");
            check(HmiDiscreteAlarmSpec.CheckTrigger("Bool", 1) != null, "Bool 只能第 0 位");
            check(HmiDiscreteAlarmSpec.CheckTrigger("LWord", 63) == null, "LWord 第 63 位合法");
            check(HmiDiscreteAlarmSpec.CheckTrigger("SomeFutureType", 3) == null, "[反向哨兵] 没见过的类型交给 TIA 判，不编规则");
            check(HmiDiscreteAlarmSpec.CheckTrigger("", 0) == null, "[反向哨兵] 类型读不出来不拦");

            var existing = new List<HmiDiscreteAlarmSpec.Trigger>
            {
                new HmiDiscreteAlarmSpec.Trigger { AlarmName = "A1", Tag = "AlmWord", Bit = 0 },
            };
            check(HmiDiscreteAlarmSpec.FindTriggerConflict(existing, "A2", "almword", 0)?.AlarmName == "A1", "同变量同位冲突（变量名大小写不敏感）");
            check(HmiDiscreteAlarmSpec.FindTriggerConflict(existing, "A1", "AlmWord", 0) == null, "[反向哨兵] 更新自己不算冲突");
            check(HmiDiscreteAlarmSpec.FindTriggerConflict(existing, "A2", "AlmWord", 1) == null, "[反向哨兵] 同变量换一位不冲突");

            var classes = new[] { "Alarm", "Warning" };
            check(HmiDiscreteAlarmSpec.ResolveAlarmClass(classes, "warning") == "Warning", "报警类别大小写归一到工程写法");
            check(HmiDiscreteAlarmSpec.ResolveAlarmClass(classes, "NoSuchClass") == null, "不存在的类别返回 null");
        }
    }
}
