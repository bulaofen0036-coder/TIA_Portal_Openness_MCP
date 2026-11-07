using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Unified 离散报警写入前的**本地预检** —— 零 Openness 依赖，能链进离线套件真执行。
    ///
    /// 为什么要预检（2026-09-17 真机，V21 MTP1200，TankStationDemo 副本）：下面三种输入
    /// Openness 的 setter **照单全收、读回也一致**，要到 CompileAndDiagnoseHmi 才报错，
    /// 而那时报警已经建进工程了：
    ///   · 报警类别写一个不存在的名字 → "The alarm class property of alarm X is invalid."
    ///   · 触发变量是 Real          → "Trigger tag: The data type of the tag is not permitted for this type of alarm."
    ///   · 两条报警同一个变量+同一位  → "Trigger tag: The tag/bit combination is also used for other HMI alarms."（两条都报）
    /// 位号越界（Bool 写 1、Word 写 16、DInt 写 32、Byte 写 8）setter 会拒，
    /// 但那时报警已经 Create 出来了 —— 同样是半成品。所以位宽也在这里先判。
    /// </summary>
    public static class HmiDiscreteAlarmSpec
    {
        /// <summary>能当触发变量的类型和位宽：只有 Bool 和位串类型。</summary>
        private static readonly Dictionary<string, int> BitWidth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bool"] = 1, ["Byte"] = 8, ["Word"] = 16, ["DWord"] = 32, ["LWord"] = 64,
        };

        /// <summary>真机确认过不能当离散报警触发变量的类型（V21，内部变量和 PLC 外部变量都一样，2026-10-26 逐类型编译）：
        /// 有符号、无符号整数全部被拒（SInt/USInt/Int/UInt/DInt/UDInt/LInt/ULInt），浮点、字符串也拒。
        /// 只拒这些 —— 没见过的类型放行给 TIA 判，不替它编一条规则。</summary>
        private static readonly string[] NotPermittedPrefixes =
            { "Real", "LReal", "String", "WString", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt" };

        /// <summary>检查位号对这个数据类型合不合法。返回 null = 通过（含「类型没见过，交给 TIA」）。</summary>
        public static string? CheckTrigger(string? dataType, int bitNumber)
        {
            var dt = (dataType ?? "").Trim();
            if (dt.Length == 0) return null;

            if (BitWidth.TryGetValue(dt, out var width))
            {
                if (bitNumber < 0 || bitNumber >= width)
                    return width == 1
                        ? $"触发变量是 {dt}，位号只能是 0（传的是 {bitNumber}）"
                        : $"触发变量是 {dt}（{width} 位），位号必须在 0..{width - 1}（传的是 {bitNumber}）";
                return null;
            }

            if (NotPermittedPrefixes.Any(p => dt.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return $"触发变量是 {dt}，离散报警不收这个类型（TIA 编译报 'The data type of the tag is not permitted for this type of alarm'）。"
                     + "请用 Bool 或位串类型（Byte/Word/DWord/LWord）的变量；整数要当报警位用，HMI 变量的数据类型改成同宽度的 Word/DWord";
            return null;
        }

        /// <summary>按名字找报警类别。TIA 的类名大小写不敏感（写 "warning" 读回 "Warning"），
        /// 返回工程里的规范写法；找不到返回 null。</summary>
        public static string? ResolveAlarmClass(IEnumerable<string> existingClasses, string wanted)
        {
            var list = (existingClasses ?? Enumerable.Empty<string>()).ToList();
            return list.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.Ordinal))
                ?? list.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
        }

        public sealed class Trigger
        {
            public string AlarmName = "";
            public string Tag = "";
            public long Bit;
        }

        /// <summary>找「另一条」占了同一个变量+位的报警（自己不算）。</summary>
        public static Trigger? FindTriggerConflict(IEnumerable<Trigger> existing, string alarmName, string tag, long bit)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            return (existing ?? Enumerable.Empty<Trigger>()).FirstOrDefault(t =>
                t != null
                && !string.Equals(t.AlarmName, alarmName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)
                && t.Bit == bit);
        }
    }
}
