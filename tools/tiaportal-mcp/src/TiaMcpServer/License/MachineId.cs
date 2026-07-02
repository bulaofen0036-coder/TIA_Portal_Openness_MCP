using System;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcpServer.License
{
    /// <summary>
    /// 机器硬件指纹。组合 CPU ID + 主板序列号 + 主网卡 MAC，
    /// SHA256 后取前 16 字符，格式化为 XXXX-XXXX-XXXX-XXXX。
    /// 用于授权绑定，防止 license 文件跨机器复制。
    /// </summary>
    public static class MachineId
    {
        /// <summary>
        /// 计算当前机器的指纹。如果某个硬件信息无法获取，
        /// 使用降级策略：跳过该项，用前一项加倍权重。
        /// 三项全失败时抛出 InvalidOperationException。
        /// </summary>
        public static string Get()
        {
            var cpu = GetWmiValue("Win32_Processor", "ProcessorId");
            var board = GetWmiValue("Win32_BaseBoard", "SerialNumber");
            var mac = GetPrimaryMac();

            // 降级策略：缺一项时前一项加倍
            var raw = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(cpu))
            {
                raw.Append(cpu.Trim());
                raw.Append('|');
                if (!string.IsNullOrWhiteSpace(board))
                {
                    raw.Append(board.Trim());
                    raw.Append('|');
                }
                else
                {
                    // CPU 加倍补偿缺失的主板
                    raw.Append(cpu.Trim());
                    raw.Append('|');
                }
            }
            else if (!string.IsNullOrWhiteSpace(board))
            {
                raw.Append(board.Trim());
                raw.Append('|');
                raw.Append(board.Trim());
                raw.Append('|');
            }

            if (!string.IsNullOrWhiteSpace(mac))
            {
                raw.Append(mac);
            }
            else if (raw.Length > 0)
            {
                // MAC 缺失：复用已收集的硬件
                raw.Append(raw.ToString());
            }

            if (raw.Length == 0)
            {
                throw new InvalidOperationException(
                    "MachineId: all hardware probes failed (CPU, motherboard, MAC). " +
                    "Cannot generate machine fingerprint.");
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()));
                var hex = BitConverter.ToString(hash).Replace("-", "");
                var id = hex.Substring(0, 16);

                return $"{id.Substring(0, 4)}-{id.Substring(4, 4)}-{id.Substring(8, 4)}-{id.Substring(12, 4)}";
            }
        }

        /// <summary>
        /// 从 WMI 查询一个字符串值。失败时返回 null。
        /// </summary>
        private static string? GetWmiValue(string wmiClass, string property)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT {property} FROM {wmiClass}");
                using var results = searcher.Get();
                foreach (var obj in results.Cast<ManagementObject>())
                {
                    var value = obj[property];
                    if (value != null)
                    {
                        var s = value.ToString() ?? "";
                        // 过滤无效值：全0、空、"To be filled by O.E.M."
                        if (!string.IsNullOrWhiteSpace(s)
                            && !s.All(c => c == '0')
                            && s.IndexOf("O.E.M.", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            return s;
                        }
                    }
                    break; // 只取第一个
                }
            }
            catch
            {
                // WMI 查询失败在受限环境/虚拟机中是正常的
            }
            return null;
        }

        /// <summary>
        /// 获取第一个已启用的物理网卡 MAC 地址。
        /// 通过 NetEnabled=true 过滤虚拟网卡和 VPN 适配器。
        /// 去大写、去冒号。
        /// </summary>
        private static string? GetPrimaryMac()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT MACAddress FROM Win32_NetworkAdapter " +
                    "WHERE NetEnabled=true AND MACAddress IS NOT NULL");
                using var results = searcher.Get();
                foreach (var obj in results.Cast<ManagementObject>())
                {
                    var mac = obj["MACAddress"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(mac))
                    {
                        return mac.Replace(":", "").ToUpperInvariant();
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
