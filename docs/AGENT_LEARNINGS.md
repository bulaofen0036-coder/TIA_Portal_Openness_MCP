# Agent 学习记录（append-only）

> 规则见 `docs/AGENT_COOKBOOK.md` 第 8 节。
> 会话开始只读最后 40 行；每次任务结束追加一行；同一结论出现 3 次升级为规则。
> 不写项目名、IP、客户名、密钥。

## 已固化规则（promoted）

- [2026-10-25] 判断一个版本分支是否真的适配过：和相邻版本分支比 commit，相同 SHA = 占位快照（`compare` 返回 `identical`）。
- [2026-10-25] 移植新版本时，`?? 21`、`= 21`、`if (version >= 20)` 三类硬编码必须全局搜一遍；初始化门槛不改，旧版本会静默不初始化 Openness。
- [2026-10-25] Openness 的 NuGet 包不含 DLL，编译必须指向 `PublicAPI\V<nn>`；满屏 CS0246 先查这个，不要怀疑包还原。
- [2026-10-25] Windows `.cmd` 一律 CRLF + 纯 ASCII；LF/UTF-8 中文注释会让 cmd 打印「拒绝访问」「'al' 不是内部或外部命令」。
- [2026-10-25] `Siemens TIA Openness` 加组后必须注销重登，`whoami /groups` 里看得到才算生效。
- [2026-10-25] 跨机 MCP 用 HTTP 时，`--http-prefix` 绑具体网卡 IP（不是 127.0.0.1），并配 URL ACL + 防火墙。
- [2026-10-25] VM 的唯一网卡专用于 PLC 调试时，不要动它的桥接/网关；加第二块 host-only 网卡专供 MCP，网关和 DNS 留空。
- [2026-10-26] 全量合并 master 2.7.8 到 v19：真实 merge 只有 `Program.cs` / `Portal.cs` 两个冲突；关键是 V19 运行时版本假设（Openness 门槛 `>=19`、`EngineRouter=19`、`bin-v19`），编译 0 错误不代表运行正确。
- [2026-10-26] `ExportBlock` 只把 XML 写到引擎机器并返回路径；跨 VM/HTTP 时宿主机读不到。读程序逻辑用 `DescribeBlockLogic`（引擎内导出+解析，只回文本），原始 XML 才需要共享目录。
- [2026-10-26] 大块逻辑用 `GetExport`/`SaveExport`（master 2.7.x 的 ExportStore）分页；全量合并后不要再自己加 `maxChars` 分页。

## 待观察（newest first）

- [2026-10-25] TIA V19 无 SIMATIC SD 文档 API：`ExportAsDocuments` / `ImportFromDocuments` / `ImportDocumentOptions` 均不存在（V20 才加入）→ 4 个工具返回 `NotSupportedOnVersion`（证据：CS1061 + 真机调用）。
- [2026-10-25] V19 的 `CommunicationConnections`（V21+）不存在，复用 V20 的反射降级路径可编译可运行（证据：CS0234 → 改为 `#if TIA_V20 || TIA_V19`）。
- [2026-10-25] 移植后第一次真机冒烟：`tia gen` 最小 spec 7 步全 ok，compile 0 errors / 0 warnings（V19 + S7-1200 1211C，`6ES7211-1BE40-0XB0`）。
- [2026-10-25] `describe` 不是列工具，需要 `.apXX` 路径参数（证据：真机 `ERROR: requires a path argument`）。
- [2026-10-25] 离线可用的 CLI 只有 `version` / `help` / `schema`（`schema` 打印 spec 字段说明，不连 TIA）。
- [2026-10-25] `PlcBuildAndImport` 的 `json` 参数是字符串，内部再套一层 JSON；大 spec 用 CLI 文件更省 token（证据：方法签名）。
