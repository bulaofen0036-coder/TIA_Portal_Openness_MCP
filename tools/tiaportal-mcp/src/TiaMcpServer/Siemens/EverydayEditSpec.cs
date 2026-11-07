using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// 日常对象编辑（PLC 变量增删改、建变量表 / 监控表、背景 DB、删设备、改名改号、硬件编译、归档）的纯逻辑：
    /// 参数校验与名字 / 地址 / 路径规整。零依赖（不碰 Openness），离线测试工程按文件链接进来。
    /// 用到的 Openness 成员 V20 / V21 PublicAPI 都有。
    /// </summary>
    public static class EverydayEditSpec
    {
        public const string KindBlock = "block";
        public const string KindType = "type";
        public const string KindTagTable = "tagtable";
        public const string KindWatchTable = "watchtable";
        public const string KindDevice = "device";

        /// <summary>RenamePlcObject 的 kind：认几个常见别名，认不出回 null。</summary>
        public static string? RenameKind(string? kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", ""))
            {
                case "block": case "fb": case "fc": case "ob": case "db": case "块": case "程序块": return KindBlock;
                case "type": case "udt": case "plctype": case "datatype": case "数据类型": return KindType;
                case "tagtable": case "plctagtable": case "变量表": return KindTagTable;
                case "watchtable": case "plcwatchtable": case "监控表": return KindWatchTable;
                case "device": case "station": case "设备": case "站": return KindDevice;
                default: return null;
            }
        }

        /// <summary>
        /// 新名字的问题：空、含路径分隔符、首尾空白。null = 没问题。
        /// TIA 自己还会拒绝更多（非法字符、重名），那些照实转述，不在这里猜。
        /// </summary>
        public static string? NameProblem(string? name, string what = "name")
        {
            if (string.IsNullOrWhiteSpace(name)) return $"{what} is empty";
            if (name!.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return $"{what} '{name}' contains '/' or '\\' — give the bare name, not a path";
            if (name != name.Trim()) return $"{what} '{name}' has leading/trailing spaces";
            return null;
        }

        /// <summary>
        /// 比较两个 PLC 地址是否「同一个」：去空白、去 %、大写。只用于重叠提示，不做位/字节区间推算
        /// （%IW0 与 %I0.0 的重叠不在这里判 —— 那要按数据宽度算，判错比不判更糟）。
        /// </summary>
        public static string NormalizeAddress(string? address)
        {
            var s = new string((address ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            return s.StartsWith("%") ? s.Substring(1) : s;
        }

        /// <summary>地址筛选：前缀匹配（"%Q" / "Q" / "%IW64" 都行），忽略 % 与大小写。空筛选 = 全要。</summary>
        public static bool AddressMatches(string? address, string? filter)
        {
            var f = NormalizeAddress(filter);
            if (f.Length == 0) return true;
            return NormalizeAddress(address).StartsWith(f, StringComparison.Ordinal);
        }

        /// <summary>背景 DB 默认名：FB 名 + "_DB"（TIA 界面「调用时自动建」的默认也是这个形状）。</summary>
        public static string DefaultInstanceDbName(string fbName) => (fbName ?? "").Trim() + "_DB";

        /// <summary>各类根组在界面 / GetSoftwareTree 里的叫法（随 TIA 界面语言变），调用方照抄路径时常把它带在最前面。</summary>
        private static readonly Dictionary<string, string[]> RootAliases = new Dictionary<string, string[]>
        {
            [KindBlock] = new[] { "Program blocks", "程序块" },
            [KindTagTable] = new[] { "PLC tags", "PLC 变量", "PLC变量" },
            [KindWatchTable] = new[] { "Watch and force tables", "监控与强制表" },
        };

        /// <summary>组路径分段（'/' 和 '\' 都算分隔符），丢空段，剥掉指代根组自身的首段。</summary>
        public static string[] GroupPath(string kind, string? groupPath, string? actualRootName = null)
        {
            var names = (groupPath ?? string.Empty).Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToArray();
            if (names.Length == 0) return names;

            var aliases = new List<string>(RootAliases.TryGetValue(kind, out var a) ? a : Array.Empty<string>());
            if (!string.IsNullOrWhiteSpace(actualRootName)) aliases.Add(actualRootName!.Trim());
            return aliases.Any(x => names[0].Equals(x, StringComparison.OrdinalIgnoreCase))
                ? names.Skip(1).ToArray()
                : names;
        }

        /// <summary>
        /// 归档文件名：Project.Archive 的 targetName 就是最终文件名，TIA 不补扩展名（V21 真机实测生成了无扩展名文件）。
        /// 已带 .zapNN 就原样；否则按工程文件扩展名补（.ap21 → .zap21，.ap20 → .zap20），认不出版本补 .zap。
        /// </summary>
        public static string ArchiveFileName(string name, string? projectExtension)
        {
            var n = (name ?? "").Trim();
            var lower = n.ToLowerInvariant();
            int i = lower.LastIndexOf(".zap", StringComparison.Ordinal);
            if (i >= 0 && lower.Substring(i + 4).All(char.IsDigit)) return n;
            var ext = (projectExtension ?? "").Trim().ToLowerInvariant();
            var digits = ext.StartsWith(".ap") ? ext.Substring(3) : "";
            return n + ".zap" + (digits.Length > 0 && digits.All(char.IsDigit) ? digits : "");
        }

        /// <summary>ArchiveProject 的模式 → ProjectArchivationMode 枚举名；认不出回 null。</summary>
        public static string? ArchiveMode(string? mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant().Replace("_", "").Replace("-", ""))
            {
                case "": case "compressed": case "zip": case "压缩": return "Compressed";
                case "none": case "uncompressed": return "None";
                case "discard": case "discardrestorabledata": case "small": return "DiscardRestorableData";
                case "discardcompressed": case "discardrestorabledataandcompressed": case "smallest": return "DiscardRestorableDataAndCompressed";
                default: return null;
            }
        }

        /// <summary>
        /// 「对整台设备动手」的工具（DeleteDevice / RenamePlcObject(kind=device)）只认两种写法：设备名本身，
        /// 或这台设备的「头」设备项名（CPU / 接口模块 / 带软件的设备项 —— IDE 项目树里看到的那个名字）。
        /// 设备解析会按任意深度的设备项名找所属设备（GetDeviceByPath），这对查询是方便，对删设备就是把
        /// 「删一个模块」放大成「连程序删掉整个站」，所以这里把其余写法一律拒绝。
        /// 返回 null = 可以；否则是给调用方的拒绝原因（什么都没改）。
        /// </summary>
        public static string? WholeDeviceRefusal(string tool, string requested, string deviceName, IReadOnlyCollection<string> headItemNames)
        {
            var r = (requested ?? string.Empty).Trim();
            if (string.Equals(r, deviceName, StringComparison.OrdinalIgnoreCase)) return null;
            if (r.IndexOf('/') < 0 && headItemNames.Any(h => string.Equals(h, r, StringComparison.OrdinalIgnoreCase))) return null;
            var others = headItemNames.Where(h => !string.Equals(h, deviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            var heads = others.Count > 0 ? "（或它的 " + string.Join(" / ", others.Select(h => "'" + h + "'")) + "）" : "";
            return $"{tool}: '{requested}' 不是设备名，而是设备 '{deviceName}' 里的一个模块 / 设备项。{tool} 作用于整台设备（包括它的 PLC 程序和画面），"
                   + $"不会只动这个模块，所以没有执行，什么都没改。确实要对整台设备操作，就用设备名 '{deviceName}'{heads}。";
        }

        /// <summary>
        /// RenamePlcObject(kind=device) 用 CPU 名点名时，改的是设备（站）名，CPU 这个设备项的名字不变。用设备名点名时返回 null。
        /// </summary>
        public static string? RenamedViaHeadItemNote(string requested, string oldDeviceName)
        {
            var r = (requested ?? string.Empty).Trim();
            if (string.Equals(r, oldDeviceName, StringComparison.OrdinalIgnoreCase)) return null;
            return $"'{r}' 是这台设备里 CPU / 头模块的名字：改的是设备（站）名 '{oldDeviceName}'，'{r}' 这个设备项本身的名字没变。";
        }

        /// <summary>SetPlcTag 改了已有变量之后的提示。</summary>
        public static List<string> TagEditWarnings(bool created, IReadOnlyCollection<string> changes)
        {
            var w = new List<string>();
            if (!created && (changes.Contains("name") || changes.Contains("address")))
                w.Add("程序里按符号引用的地方会跟着变；HMI 按符号名绑定 PLC 变量，改名后 HMI 侧要重新对一遍（GetHmiTagDetails 看 PlcTag 绑定）。");
            return w;
        }

        /// <summary>RenamePlcObject 之后的提示。<paramref name="isDataBlock"/> = 改的是 DB（全局 / 背景）。</summary>
        public static List<string> RenameWarnings(string kind, IReadOnlyCollection<string> changes, bool isDataBlock)
        {
            var w = new List<string>();
            if (kind == KindBlock && changes.Contains("name"))
            {
                w.Add("程序里调用这个块的地方 TIA 会跟着改；按名字引用它的外部文件（导出的 XML / SCL 源、HMI 脚本）不会跟着变。改完 CompileAndDiagnosePlc。");
                if (isDataBlock)
                    w.Add("⚠️ 改的是 DB 名：HMI 变量按「DB名.成员」符号绑定的会断链，而 PLC 编译 0 错也看不出来 —— 要逐个核对 HMI 变量（GetHmiTagDetails）并编译 HMI；按 DB 号绝对寻址的（PUT/GET、上位机）不受名字影响。没有必要就别改 DB 名。");
            }
            if (kind == KindBlock && changes.Contains("number"))
                w.Add("块号改了：按块号访问的地方（如 HMI 里写死的 DB 号、上位机按 DB 号读、PUT/GET）要跟着改；下载后 PLC 里旧号的块不再存在。");
            if (kind == KindDevice && changes.Contains("name"))
                w.Add("设备名改了：以后工具里的设备路径用新名字；PROFINET 设备名（设备上的站名）不受影响。");
            return w;
        }

        /// <summary>
        /// 写到一半 TIA 拒了：把已生效的改动和现在的名字说清楚 —— 否则调用方用旧名重试报找不到，
        /// 再补上表 / 类型 / 地址就新建出一个重复变量。
        /// </summary>
        public static string PartialApplyMessage(string tool, string what, string requestedName, string currentName, bool created, IReadOnlyCollection<string> applied, string error)
        {
            var head = $"{tool}: TIA refused part of the change ({error}).";
            if (created)
                return head + $" The {what} '{currentName}' WAS created" + (applied.Count > 0 ? " and these were applied: " + string.Join(", ", applied) : "")
                       + $" — it exists now. Read it back (GetPlcTags) before retrying; do not create it again.";
            if (applied.Count == 0) return head + " Nothing was changed.";
            var rename = string.Equals(requestedName, currentName, StringComparison.Ordinal) ? "" : $" Its name is now '{currentName}' (not '{requestedName}') — use that name when retrying.";
            return head + $" Already applied to the {what}: {string.Join(", ", applied)}.{rename} The rest was NOT applied.";
        }
    }
}
