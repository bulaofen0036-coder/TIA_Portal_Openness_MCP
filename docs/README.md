# 文档导航（新手从这里开始）

> 只想跑起来？回到仓库根 [`README.zh-CN.md`](../README.zh-CN.md) 照「⚡ 最快上手（3 步）」做即可，不用读本目录任何文件。
> 本页给「想多懂一点」的人指路：**按你的角色挑一节看，不必通读。**

## 我是工程师，想零编程生成/修改工程（CLI 路线）

1. [`CLI_quickstart.md`](CLI_quickstart.md) —— `tia gen / patch / compile / doctor` 全部子命令与退出码
2. [`AI_spec_prompt.md`](AI_spec_prompt.md) —— 复制给任意 AI，让它产出一份可直接 `tia gen` 的 spec
3. 模板直接用：`../templates/project-blueprints/`（启停 / 电机两个现成 spec）

## 我要接 AI 客户端（Cursor / Claude / VS Code，MCP 路线）

1. 根 README「上手步骤」——双击 `配置MCP.bat` 一键注册四宿主
2. [`使用说明与介绍.md`](使用说明与介绍.md) —— 各客户端配置文件位置、手动配置、常见问题
3. [`mcp-ide-and-tool-visibility.md`](mcp-ide-and-tool-visibility.md) —— 「为什么 IDE 里少工具」的解释（客户端缓存/上限，非包裁剪）

## 我是驱动本 MCP 的 AI / 想看写码规范

1. `../tools/tiaportal-mcp/skill/SKILL.md` —— **主规范**（工具分层、参数陷阱、LAD/SCL 边界）
2. [`scl-instruction-library.md`](scl-instruction-library.md) / [`lad-instruction-library.md`](lad-instruction-library.md) —— 指令模板库
3. [`full-project-generation-runbook.md`](full-project-generation-runbook.md) —— 手工多步流程（一把梭优先用 `ScaffoldProject`，本文是分步排障用的降级路径）
4. [`hmi-plc-tag-binding-and-addressing.md`](hmi-plc-tag-binding-and-addressing.md) / [`hmi-connection-driver-matrix.md`](hmi-connection-driver-matrix.md) / [`HMI_Unified_画面生成规范与模板.md`](HMI_Unified_画面生成规范与模板.md) —— HMI 三件套
5. [`在线实时读值_使用指南.md`](在线实时读值_使用指南.md) —— 在线只读监控

## 我在排障

1. 先跑 `tia.cmd doctor`（`--fix` 自动补 Openness 用户组）
2. [`../手册/error-model.md`](../手册/error-model.md) —— 错误形态说明
3. [`../手册/openness-limitations.md`](../手册/openness-limitations.md) —— Openness **做不到**的事（别在这些上头硬试）

## 参考与索引（检索用，不必通读）

- [`tool-capability-matrix.md`](tool-capability-matrix.md) —— 全部工具能力矩阵（静态快照；运行时以 `tools/list` 为准）
- [`../manifest/tools-list.json`](../manifest/tools-list.json) —— 工具清单快照
- [`basic-plc-template-library.md`](basic-plc-template-library.md) / [`plc-network-patterns-expanded.md`](plc-network-patterns-expanded.md) / [`optional-reference-materials.md`](optional-reference-materials.md)
- [`../手册/TIA_NL_INTENT_RECIPES.md`](../手册/TIA_NL_INTENT_RECIPES.md) —— 自然语言 → 工具序列索引
- [`server-maturity-roadmap.md`](server-maturity-roadmap.md) / [`verify-low-barrier-features.md`](verify-low-barrier-features.md) —— 路线图与验证记录

> 历史提示：`../手册/quickstart.md` 与根 README 内容重叠，以**根 README 为准**。
