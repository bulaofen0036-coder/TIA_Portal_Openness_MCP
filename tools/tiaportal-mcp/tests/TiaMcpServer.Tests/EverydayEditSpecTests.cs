using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 日常编辑工具（GetPlcTags / SetPlcTag / DeletePlcTag / 建表 / 删监控表 / CreateInstanceDB / DeleteDevice /
    /// RenamePlcObject / CompileHardware / ArchiveProject）的纯逻辑：参数校验、地址规整、组路径、归档文件名、整台设备守卫。
    /// Openness 那一半（真建、真删、读回）离线测不到，在 V21 隔离实例上真机验。
    /// </summary>
    internal static class EverydayEditSpecTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // ---- RenameKind ----
            check(EverydayEditSpec.RenameKind("FB") == EverydayEditSpec.KindBlock, "kind 'FB' → block");
            check(EverydayEditSpec.RenameKind("监控表") == EverydayEditSpec.KindWatchTable, "kind '监控表' → watchtable");
            check(EverydayEditSpec.RenameKind("tag_table") == EverydayEditSpec.KindTagTable, "kind 'tag_table' → tagtable");
            check(EverydayEditSpec.RenameKind("Station") == EverydayEditSpec.KindDevice, "kind 'Station' → device");
            check(EverydayEditSpec.RenameKind("group") == null, "[哨兵] 认不出的 kind → null（调用方据此报错）");
            check(EverydayEditSpec.RenameKind(null) == null, "[哨兵] kind=null → null");

            // ---- NameProblem ----
            check(EverydayEditSpec.NameProblem("Motor_1") == null, "[反向哨兵] 正常名字不许报问题");
            check(EverydayEditSpec.NameProblem("") != null, "空名字要报");
            check(EverydayEditSpec.NameProblem("A/B") != null, "带 '/' 的名字要报（是路径不是名字）");
            check(EverydayEditSpec.NameProblem(" A") != null, "首尾空白要报");

            // ---- 地址 ----
            check(EverydayEditSpec.NormalizeAddress(" %q 4.2") == "Q4.2", "地址规整：去空白、去 %、大写");
            check(EverydayEditSpec.AddressMatches("%Q4.2", "%Q"), "前缀 %Q 命中 %Q4.2");
            check(EverydayEditSpec.AddressMatches("%Q4.2", "q4"), "筛选忽略 % 与大小写");
            check(!EverydayEditSpec.AddressMatches("%I0.0", "%Q"), "[反向哨兵] %I0.0 不许被 %Q 命中");
            check(EverydayEditSpec.AddressMatches("%I0.0", ""), "空筛选 = 全要");
            check(!EverydayEditSpec.AddressMatches(null, "%Q"), "[反向哨兵] 没地址的变量不许被地址筛选命中");

            check(EverydayEditSpec.DefaultInstanceDbName("FB_Motor") == "FB_Motor_DB", "背景 DB 默认名 = FB 名 + _DB");

            // ---- 组路径：剥掉指代根组自身的首段 ----
            check(string.Join("|", EverydayEditSpec.GroupPath(EverydayEditSpec.KindBlock, "程序块/Drives/Conveyor")) == "Drives|Conveyor", "块组路径剥掉首段「程序块」");
            check(string.Join("|", EverydayEditSpec.GroupPath(EverydayEditSpec.KindTagTable, "PLC tags\\IO")) == "IO", "变量表组路径认反斜杠并剥掉「PLC tags」");
            check(string.Join("|", EverydayEditSpec.GroupPath(EverydayEditSpec.KindBlock, "Drives")) == "Drives", "[反向哨兵] 不是根组名的首段不许剥");
            check(EverydayEditSpec.GroupPath(EverydayEditSpec.KindWatchTable, "").Length == 0, "空路径 = 根组");
            check(string.Join("|", EverydayEditSpec.GroupPath(EverydayEditSpec.KindBlock, "Root/A", "Root")) == "A", "实际根组名（随 TIA 界面语言变）也剥");

            // ---- 归档 ----
            check(EverydayEditSpec.ArchiveFileName("bak", ".ap21") == "bak.zap21", ".ap21 工程补 .zap21（TIA 不补扩展名）");
            check(EverydayEditSpec.ArchiveFileName("bak", ".ap20") == "bak.zap20", ".ap20 工程补 .zap20");
            check(EverydayEditSpec.ArchiveFileName("bak.zap21", ".ap21") == "bak.zap21", "[反向哨兵] 已带 .zap21 不许重复补");
            check(EverydayEditSpec.ArchiveFileName("bak", "") == "bak.zap", "认不出版本补 .zap");
            check(EverydayEditSpec.ArchiveMode("") == "Compressed", "模式默认 Compressed");
            check(EverydayEditSpec.ArchiveMode("discardcompressed") == "DiscardRestorableDataAndCompressed", "discardcompressed 映射正确");
            check(EverydayEditSpec.ArchiveMode("rar") == null, "[哨兵] 认不出的模式 → null");

            // ---- 整台设备守卫：模块名不许把「删一个模块」放大成「删整个站」 ----
            var heads = new List<string> { "PLC_1" };
            check(EverydayEditSpec.WholeDeviceRefusal("DeleteDevice", "S7-1500/ET200MP station_1", "S7-1500/ET200MP station_1", heads) == null,
                "[反向哨兵] 设备名本身（含 '/'）放行");
            check(EverydayEditSpec.WholeDeviceRefusal("DeleteDevice", "plc_1", "S7-1500 station_1", heads) == null,
                "[反向哨兵] CPU（头设备项）名放行，不分大小写");
            var refusal = EverydayEditSpec.WholeDeviceRefusal("DeleteDevice", "DI 16x24VDC_1", "S7-1500 station_1", heads);
            check(refusal != null && refusal.Contains("S7-1500 station_1") && refusal.Contains("什么都没改"), "模块名被拒，并指出该用的设备名、说明没改动");
            check(EverydayEditSpec.WholeDeviceRefusal("DeleteDevice", "PLC_1/PROFINET 接口_1", "S7-1500 station_1", heads) != null,
                "'CPU/接口' 写法被拒（指向的是设备项，不是整台设备）");

            check(EverydayEditSpec.RenamedViaHeadItemNote("S7-1500 station_1", "S7-1500 station_1") == null, "用设备名改名 → 无额外说明");
            check(EverydayEditSpec.RenamedViaHeadItemNote("PLC_1", "S7-1500 station_1")?.Contains("PLC_1") == true, "用 CPU 名改名 → 说明改的是站名");

            // ---- 改动后的提示：按 string 集合算，并且只指向公开版真有的工具 ----
            check(EverydayEditSpec.TagEditWarnings(false, new[] { "address" }).Count == 1, "改地址要提示 HMI 侧");
            check(EverydayEditSpec.TagEditWarnings(true, new[] { "comment" }).Count == 0, "[反向哨兵] 新建变量不提示");
            var rw = EverydayEditSpec.RenameWarnings(EverydayEditSpec.KindBlock, new[] { "name" }, isDataBlock: true);
            check(rw.Count == 2 && rw[1].Contains("DB"), "改 DB 名要多一条 HMI 断链警告");
            check(EverydayEditSpec.RenameWarnings(EverydayEditSpec.KindBlock, new[] { "name" }, isDataBlock: false).Count == 1, "[反向哨兵] 改 FC 名没有 DB 警告");
            check(EverydayEditSpec.RenameWarnings(EverydayEditSpec.KindBlock, new[] { "number" }, false).Any(x => x.Contains("块号")), "改块号要提示按号访问的地方");
            var allText = string.Join(" ", rw.Concat(EverydayEditSpec.TagEditWarnings(false, new[] { "name" })));
            check(!allText.Contains("VerifyUnifiedHmiTagBinding"), "提示里不许指向公开版没有的工具");

            // ---- 写到一半被拒：说清已生效的改动和现在的名字 ----
            var pm = EverydayEditSpec.PartialApplyMessage("SetPlcTag", "tag", "A", "B", false, new[] { "name" }, "bad type");
            check(pm.Contains("'B'") && pm.Contains("name") && pm.Contains("NOT applied"), "改名已生效、改类型失败 → 报出新名字和已生效项");
            check(EverydayEditSpec.PartialApplyMessage("SetPlcTag", "tag", "A", "A", false, Array.Empty<string>(), "x").Contains("Nothing was changed"),
                "一项都没生效 → 明说没改");
            check(EverydayEditSpec.PartialApplyMessage("SetPlcTag", "tag", "A", "A", true, Array.Empty<string>(), "x").Contains("WAS created"),
                "新建后写注释失败 → 明说变量已建出来，别再建");
        }
    }
}
