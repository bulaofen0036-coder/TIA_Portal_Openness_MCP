using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>硬件编译结果树的一个节点（与 Openness CompilerResultMessage 同形，便于离线测试）。</summary>
    internal sealed class CompileNode
    {
        public CompileNode(string state, string path, string description, IReadOnlyList<CompileNode>? children = null)
        {
            State = state ?? "";
            Path = path ?? "";
            Description = description ?? "";
            Children = children ?? Array.Empty<CompileNode>();
        }

        public string State { get; }
        public string Path { get; }
        public string Description { get; }
        public IReadOnlyList<CompileNode> Children { get; }
    }

    /// <summary>
    /// CreateS7Connection（issue #29）里不碰 Openness 的判定：连接 ID、伙伴地址、连接名、
    /// 以及从硬件编译结果里挑出"属于这条连接"的消息。V21 Upd2 两台 S7-1200 实测：
    /// - LocalConnectionId 是十六进制字符串，TIA 默认 "100"；设 1/2/3 硬件编译报
    ///   "outside the range or already assigned"，101/102/103 通过。
    /// - 连接名重复时硬件编译报 "already a connection with this name"。
    /// - 伙伴不在本工程时（未指定伙伴）不给 PartnerAddress，编译报 "Invalid address information"。
    /// </summary>
    internal static class S7ConnectionArgs
    {
        /// <summary>V21 实测：低于它的本地 ID 被硬件编译拒绝（S7-1200 FW V4.7）。只作提示，判据是编译结果。</summary>
        public const int ObservedMinimumId = 0x100;

        /// <summary>
        /// 连接 ID 规范成 TIA 的写法（大写十六进制、无前缀）。接受 "101"、"16#101"、"W#16#101"、"0x101"。
        /// 空串表示让 TIA 分配。注意：ID 按十六进制理解 —— "101" 是 16#101（257），不是十进制 101。
        /// </summary>
        public static string NormalizeConnectionId(string? input, string what = "connection ID")
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0) return "";
            if (s.StartsWith("W#16#", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            else if (s.StartsWith("16#", StringComparison.Ordinal)) s = s.Substring(3);
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

            if (s.Length == 0 || s.Length > 4 || !int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw new ArgumentException($"{what} '{input}' is not a hexadecimal ID (1..FFFF), e.g. \"101\" or \"16#101\". IDs are hexadecimal, as in TIA's connection table.");
            return value.ToString("X", CultureInfo.InvariantCulture);
        }

        /// <summary>低于实测下限时给一句提醒（不拒绝：不同 CPU 的范围可能不同，硬件编译是判据）。</summary>
        public static string? IdWarning(string normalizedId)
        {
            if (string.IsNullOrEmpty(normalizedId)) return null;
            var value = int.Parse(normalizedId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return value < ObservedMinimumId
                ? $"Local ID 16#{normalizedId} is below 16#100; on S7-1200 (FW V4.7, TIA V21) such IDs were rejected by the hardware compile. TIA's own default is 16#100."
                : null;
        }

        /// <summary>
        /// 伙伴二选一：partnerPlc（同一工程里的 PLC，建"已指定"连接）或 partnerIp（伙伴在别的工程，建"未指定"连接）。
        /// 返回规范化后的 IPv4（没给时为 ""）。
        /// </summary>
        public static string ValidatePartner(string? partnerPlc, string? partnerIp)
        {
            var hasPlc = !string.IsNullOrWhiteSpace(partnerPlc);
            var hasIp = !string.IsNullOrWhiteSpace(partnerIp);
            if (hasPlc == hasIp)
                throw new ArgumentException(hasPlc
                    ? "Give either partnerPlc (partner PLC in this project) or partnerIp (partner in another project), not both."
                    : "A partner is required: partnerPlc for a PLC in this project, or partnerIp for a partner PLC in another project.");
            if (!hasIp) return "";

            var ip = partnerIp!.Trim();
            var parts = ip.Split('.');
            if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.Length <= 3 && p.All(char.IsDigit) && int.Parse(p, CultureInfo.InvariantCulture) <= 255))
                throw new ArgumentException($"partnerIp '{partnerIp}' is not an IPv4 address like 192.168.0.2.");
            return string.Join(".", parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>连接名在本 PLC 的连接表里必须唯一（大小写不敏感，与 TIA 编译规则一致）。空名表示让 TIA 命名。</summary>
        public static string ValidateConnectionName(string? name, IEnumerable<string> existingNames)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "";
            var clash = (existingNames ?? Array.Empty<string>()).FirstOrDefault(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                throw new ArgumentException($"A connection named '{clash}' already exists on this PLC. Connection names must be unique; list them with GetPlcConnections.");
            return n;
        }

        /// <summary>
        /// 从 CPU 的硬件编译结果里挑出挂在这条连接名下的错误与警告。
        /// 同一次编译里常有与连接无关的错误（例如 S7-1200 的"机密组态数据密码未设置"），不能算到连接头上。
        /// </summary>
        public static List<string> MessagesForConnection(IEnumerable<CompileNode> roots, string connectionName, out int otherErrorCount)
        {
            var mine = new List<string>();
            int others = 0;

            void Walk(CompileNode node, bool underConnection)
            {
                var here = underConnection || string.Equals(node.Path.Trim(), connectionName, StringComparison.OrdinalIgnoreCase);
                // 末尾那行 "Compiling finished (errors: N; warnings: M)" 是汇总，不是一条独立的错误。
                if (IsProblem(node.State) && node.Description.Trim().Length > 0 && node.Children.Count == 0
                    && !node.Description.TrimStart().StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase))
                {
                    if (here) mine.Add($"{node.State}: {node.Description.Trim()}");
                    else if (IsError(node.State)) others++;
                }
                foreach (var child in node.Children) Walk(child, here);
            }

            foreach (var root in roots ?? Array.Empty<CompileNode>()) Walk(root, false);
            otherErrorCount = others;
            return mine;
        }

        public static bool HasError(IEnumerable<string> messages) =>
            (messages ?? Array.Empty<string>()).Any(m => m.StartsWith("Error", StringComparison.OrdinalIgnoreCase));

        private static bool IsProblem(string state) => IsError(state) || state.Equals("Warning", StringComparison.OrdinalIgnoreCase);
        private static bool IsError(string state) => state.Equals("Error", StringComparison.OrdinalIgnoreCase);
    }
}
