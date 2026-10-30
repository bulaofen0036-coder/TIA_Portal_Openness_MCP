using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.Cli;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// issue #51：CLI 全局参数写在动词前面被静默当成 MCP 服务器；连接失败只报新起实例那一句。
    /// issue #50：Unified 连接的 InitialAddress 改写与两端 IP 挑选（DisabledAtStartup / 伙伴读回在 Openness 侧，离线测不到）。
    /// </summary>
    internal static class Issue50And51Tests
    {
        private static readonly string[] Verbs =
            { "gen", "patch", "compile", "export", "import", "describe", "prewarm", "config", "doctor", "schema", "version", "help", "--help", "-h" };

        private static bool IsVerb(string s) => Array.IndexOf(Verbs, s.ToLowerInvariant()) >= 0;

        private static string Show(string[]? a) => a == null ? "<null>" : string.Join(" ", a);

        internal static void Run(Action<bool, string> check)
        {
            // ---- #51 CLI：动词前面的全局参数 ----
            var a = CliVerbArgs.ExtractVerbArgs(new[] { "--tia-major-version", "21", "prewarm" }, IsVerb);
            check(Show(a) == "prewarm --tia-major-version 21", "前置 --tia-major-version 21 后的 prewarm 要进 CLI，实得 " + Show(a));

            a = CliVerbArgs.ExtractVerbArgs(new[] { "--with-ui", "gen", "x.json" }, IsVerb);
            check(Show(a) == "gen x.json --with-ui", "前置 --with-ui 后的 gen 要进 CLI，实得 " + Show(a));

            a = CliVerbArgs.ExtractVerbArgs(new[] { "--tia-portal-location", "D:\\TIA\\Portal V21", "describe", "p.ap21" }, IsVerb);
            check(Show(a) == "describe p.ap21 --tia-portal-location D:\\TIA\\Portal V21", "带路径值的全局参数要连值跳过，实得 " + Show(a));

            var plain = new[] { "version", "--tia-major-version", "21" };
            check(ReferenceEquals(CliVerbArgs.ExtractVerbArgs(plain, IsVerb), plain), "动词本来就在最前时原样返回");

            // [反向哨兵] MCP 宿主的启动参数里没有动词：必须照旧走 MCP 服务器，不许被当成 CLI。
            check(CliVerbArgs.ExtractVerbArgs(new[] { "--tia-major-version", "21", "--profile", "full" }, IsVerb) == null,
                "[反向哨兵] 只有全局参数、没有动词 → null（走 MCP 服务器）");
            check(CliVerbArgs.ExtractVerbArgs(new string[0], IsVerb) == null, "[反向哨兵] 无参数 → null");
            check(CliVerbArgs.ExtractVerbArgs(new[] { "--tia-major-version", "21", "foo", "version" }, IsVerb) == null,
                "[反向哨兵] 先碰到不认识的词就不是 CLI 调用 → null");

            // 位置参数要跳过参数的取值：原来 `gen --tia-major-version 21 x.json` 会把 21 当 spec 路径。
            check(CliVerbArgs.Positional(new[] { "gen", "--tia-major-version", "21", "x.json" }) == "x.json", "Positional 跳过 --tia-major-version 的值");
            check(CliVerbArgs.Positional(new[] { "compile", "--plc", "PLC_1", "p.ap21" }) == "p.ap21", "Positional 跳过 --plc 的值");
            check(CliVerbArgs.Positional(new[] { "gen", "x.json", "--dry-run" }) == "x.json", "Positional 普通情况照旧");
            check(CliVerbArgs.Positional(new[] { "gen", "--dry-run" }) == null, "[哨兵] 没有位置参数 → null（调用方据此报缺参）");

            // ---- #51 连接失败文案：附加经过不许丢 ----
            var notes = new List<string>
            {
                ConnectFailureText.AttachTimedOut(1234, 30000),
                ConnectFailureText.AttachFailed(5678, new InvalidOperationException("Security error.\r\nsecond line")),
            };
            var msg = ConnectFailureText.AttachThenLaunchFailed(notes, "WithoutUserInterface", "NonRecoverableException: Connection to TiaPortal failed.");
            check(msg.Contains("PID=1234") && msg.Contains("30s"), "超时的进程要点名并写明等了多久");
            check(msg.Contains("PID=5678") && msg.Contains("InvalidOperationException: Security error."), "附加异常要带类型和首行");
            check(!msg.Contains("second line"), "附加异常只取首行，不把整段栈塞进一行");
            check(msg.Contains("Connection to TiaPortal failed."), "新起实例那句原因也要在");
            check(msg.Contains("2 running TIA Portal process(es)"), "要说清找到了几个运行中的博途");
            check(ConnectFailureText.AttachFailed(1, new Exception("")).Contains("(no message)"), "[哨兵] 空消息不许拼出空串");

            // ---- #50 InitialAddress：默认串（末尾带分号）改 IP ----
            const string def = "Version=16.0.0.0;CommunicationInterface=Industrial Ethernet;HostAccessPoint=S7ONLINE;HostAddress=192.168.0.2;PlcAddress=192.168.0.1;";
            var composed = HmiConnectionSpec.ComposeInitialAddress(def, "10.0.0.10", "10.0.0.20");
            check(composed == "Version=16.0.0.0;CommunicationInterface=Industrial Ethernet;HostAccessPoint=S7ONLINE;HostAddress=10.0.0.20;PlcAddress=10.0.0.10",
                "只换两个 IP、其它键原样且顺序不动、不带末尾分号，实得 " + composed);
            check(!composed.EndsWith(";"), "结果不许带末尾分号（V21 会以 Unsupported format 拒收）");
            check(HmiConnectionSpec.ComposeInitialAddress(def, "10.0.0.10", null).Contains("HostAddress=192.168.0.2"), "传 null 的键不动");
            var kvs = HmiConnectionSpec.ParseInitialAddress(def);
            check(HmiConnectionSpec.GetValue(kvs, "plcaddress") == "192.168.0.1", "按键名取值不分大小写");

            // ---- #50 两端 IP 挑选 ----
            var plcNodes = new[] { new HmiConnectionSpec.NodeInfo { Address = "10.0.0.10", Subnet = "PN/IE_1" }, new HmiConnectionSpec.NodeInfo { Address = "10.0.0.10", Subnet = "" } };
            var hmiNodes = new[] { new HmiConnectionSpec.NodeInfo { Address = "10.0.0.20", Subnet = "PN/IE_1" } };
            var pick = HmiConnectionSpec.PickPeerAddresses(plcNodes, hmiNodes);
            check(pick.PlcIp == "10.0.0.10" && pick.HmiIp == "10.0.0.20" && pick.Reason == null, "同一子网上的一对：确定挑出");

            var twoPlc = new[] { new HmiConnectionSpec.NodeInfo { Address = "10.0.0.10" }, new HmiConnectionSpec.NodeInfo { Address = "10.0.1.10" } };
            pick = HmiConnectionSpec.PickPeerAddresses(twoPlc, hmiNodes);
            check(pick.PlcIp == null && pick.Reason != null, "[哨兵] PLC 两个 IP 又无共同子网：答不上来，不许瞎挑");
            pick = HmiConnectionSpec.PickPeerAddresses(Enumerable.Empty<HmiConnectionSpec.NodeInfo>(), hmiNodes);
            check(pick.PlcIp == null && pick.Reason != null, "[哨兵] PLC 没读到节点：答不上来并说明原因");

            // ---- #13 V21 硬件层建连接：两端节点要挂在同一子网 ----
            NodeInfoList N(params (string a, string s)[] xs) => new NodeInfoList(xs.Select(x => new HmiConnectionSpec.NodeInfo { Address = x.a, Subnet = x.s }));
            var pair = HmiConnectionSpec.PickSharedSubnetPair(N(("10.0.1.1", "PN_B"), ("10.0.0.1", "PN_A")), N(("10.0.0.3", "PN_A")));
            check(pair.Plc == 1 && pair.Hmi == 0 && pair.Reason == null, "同一子网上的那一对：挑第二个 PLC 口，实得 " + pair);
            pair = HmiConnectionSpec.PickSharedSubnetPair(N(("10.0.0.1", "PN_A")), N(("10.0.0.3", "")));
            check(pair.Plc < 0 && (pair.Reason ?? "").Contains("HMI") && (pair.Reason ?? "").Contains("ConnectDeviceNodesToProfinetSubnet"), "[哨兵] HMI 没组网：拒绝并指明下一步，实得 " + pair.Reason);
            pair = HmiConnectionSpec.PickSharedSubnetPair(N(("10.0.0.1", "")), N(("10.0.0.3", "PN_A")));
            check(pair.Plc < 0 && (pair.Reason ?? "").Contains("PLC"), "[哨兵] PLC 没组网：拒绝，实得 " + pair.Reason);
            pair = HmiConnectionSpec.PickSharedSubnetPair(N(("10.0.0.1", "PN_A")), N(("10.0.0.3", "PN_B")));
            check(pair.Plc < 0 && (pair.Reason ?? "").Contains("PN_A") && (pair.Reason ?? "").Contains("PN_B"), "[哨兵] 两条不同子网：拒绝并列出两边子网，实得 " + pair.Reason);
            pair = HmiConnectionSpec.PickSharedSubnetPair(N(("10.0.0.1", "")), N(("10.0.0.3", "")));
            check(pair.Plc < 0 && pair.Reason != null, "[哨兵] 两端都没组网：空子网名不算同一子网，实得 " + pair);
            pair = HmiConnectionSpec.PickSharedSubnetPair(N(), N(("10.0.0.3", "PN_A")));
            check(pair.Plc < 0 && pair.Reason != null, "[哨兵] PLC 无以太网口：拒绝");

            var hwMsg = HmiConnectionSpec.HardwareCreateFailed("HMI_RT_1", "PLC_1", "Error when calling method 'Create'", new[] { "HMI_Connection_1" });
            check(hwMsg.Contains("HMI_Connection_1") && hwMsg.Contains("reuse"), "HMI 上已有连接时点明它挡住了第二条，实得 " + hwMsg);
            hwMsg = HmiConnectionSpec.HardwareCreateFailed("HMI_RT_1", "PLC_1", "x", new[] { "", " " });
            check(!hwMsg.Contains("already has"), "没有现成连接时不提「已有连接」");
            check(HmiConnectionSpec.ClassicPanelOnV20("HMI_RT_2").Contains("V21") && !HmiConnectionSpec.ClassicPanelOnV20("HMI_RT_2").Contains("cannot create classic HMI connections (the classic"),
                "V20 经典屏报错指向 V21，不再说「Openness 建不了经典连接」");
        }

        private sealed class NodeInfoList : List<HmiConnectionSpec.NodeInfo>
        {
            public NodeInfoList(IEnumerable<HmiConnectionSpec.NodeInfo> xs) : base(xs) { }
        }
    }
}
