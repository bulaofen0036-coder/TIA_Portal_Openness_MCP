using System;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// CreateS7Connection（issue #29）的离线部分。编译结果树按 V21 Upd2 两台 S7-1200 的真实硬件编译输出复刻：
    /// 与连接无关的错误（机密组态数据密码）挂在 CPU 下，连接自己的错误挂在以连接名为 Path 的节点下。
    /// </summary>
    internal static class S7ConnectionArgsTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // ---- 连接 ID：十六进制，与 TIA 连接表同一写法 ----
            check(S7ConnectionArgs.NormalizeConnectionId("101") == "101", "[反向哨兵] '101' stays 101 (hex)");
            check(S7ConnectionArgs.NormalizeConnectionId("W#16#102") == "102", "W#16# prefix stripped");
            check(S7ConnectionArgs.NormalizeConnectionId("16#1a0") == "1A0", "16# prefix stripped, upper-cased like TIA");
            check(S7ConnectionArgs.NormalizeConnectionId("0x0101") == "101", "0x prefix and leading zeros normalised");
            check(S7ConnectionArgs.NormalizeConnectionId("") == "" && S7ConnectionArgs.NormalizeConnectionId(null) == "", "empty = let TIA assign");
            check(Throws(() => S7ConnectionArgs.NormalizeConnectionId("G1"), "hexadecimal"), "non-hex refused");
            check(Throws(() => S7ConnectionArgs.NormalizeConnectionId("0"), "hexadecimal"), "0 refused");
            check(Throws(() => S7ConnectionArgs.NormalizeConnectionId("12345"), "hexadecimal"), "more than 4 hex digits refused");
            check(S7ConnectionArgs.IdWarning("5") != null && S7ConnectionArgs.IdWarning("100") == null && S7ConnectionArgs.IdWarning("") == null,
                "IDs below 16#100 get a warning (TIA rejected them live), 16#100 and up do not");

            // ---- 伙伴二选一 ----
            check(S7ConnectionArgs.ValidatePartner("PLC_2", "") == "", "[反向哨兵] partner PLC in this project");
            check(S7ConnectionArgs.ValidatePartner("", " 192.168.000.050 ") == "192.168.0.50", "partner IP normalised");
            check(Throws(() => S7ConnectionArgs.ValidatePartner("PLC_2", "192.168.0.2"), "not both"), "both given refused");
            check(Throws(() => S7ConnectionArgs.ValidatePartner("", ""), "partner is required"), "neither given refused");
            check(Throws(() => S7ConnectionArgs.ValidatePartner("", "300.1.1.1"), "IPv4"), "octet > 255 refused");
            check(Throws(() => S7ConnectionArgs.ValidatePartner("", "192.168.0"), "IPv4"), "three octets refused");

            // ---- 连接名唯一（与 TIA 编译规则一致，大小写不敏感）----
            check(S7ConnectionArgs.ValidateConnectionName("S7_to_PLC2", new[] { "Other" }) == "S7_to_PLC2", "[反向哨兵] unique name accepted");
            check(Throws(() => S7ConnectionArgs.ValidateConnectionName("s7_to_plc2", new[] { "S7_to_PLC2" }), "already exists"), "case-insensitive clash refused");
            check(S7ConnectionArgs.ValidateConnectionName("", new[] { "A" }) == "", "empty = let TIA name it");

            // ---- 从硬件编译结果里挑出这条连接的消息 ----
            var tree = new[]
            {
                N("Error", "PLC_1", "",
                    N("Error", "Hardware configuration", "",
                        N("Error", "PLC_1", "",
                            N("Error", "Rack_0", "",
                                N("Error", "PLC_1", "",
                                    N("Error", "PLC_1", "",
                                        N("Error", "", "Password for confidential PLC configuration data is not configured in the PLC."),
                                        N("Error", "", "The PLC communication certificate cannot be configured without the password for confidential PLC configuration data. Enter the password.")),
                                    N("Error", "S7_LowId", "",
                                        N("Error", "", "Local ID 5. The local ID 5 (hex) / 5 (dec) is either outside the range or is already assigned to another connection."))))))),
                N("Error", "", "Compiling finished (errors: 3; warnings: 0)")
            };
            var bad = S7ConnectionArgs.MessagesForConnection(tree, "S7_LowId", out var others);
            check(bad.Count == 1 && bad[0].Contains("Local ID 5") && S7ConnectionArgs.HasError(bad), "the connection's own error is picked out");
            check(others == 2, "unrelated device errors are counted separately (the 'Compiling finished' summary is not an error)");

            var clean = S7ConnectionArgs.MessagesForConnection(tree, "S7_to_PLC2", out var others2);
            check(clean.Count == 0 && !S7ConnectionArgs.HasError(clean) && others2 == 3,
                "[反向哨兵] a connection with no messages is clean even when the device has other errors");
        }

        private static CompileNode N(string state, string path, string description, params CompileNode[] children) =>
            new CompileNode(state, path, description, children);

        private static bool Throws(Action action, string messageFragment)
        {
            try { action(); return false; }
            catch (ArgumentException ex) { return ex.Message.IndexOf(messageFragment, StringComparison.Ordinal) >= 0; }
        }
    }
}
