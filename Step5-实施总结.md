# Step 5 — 编译 + 加壳 + 发布 实施总结

**日期**: 2026-07-02  
**版本**: v2.3.0  
**基础**: upstream v2.2.7 (38043e9)

---

## 一、版本合并

| 操作 | 结果 |
|------|------|
| upstream | v2.2.4 → v2.2.7 (11 new commits) |
| 冲突 | 仅 `.gitignore`（已手动合并） |
| License 代码 | Program.cs + CliOptions.cs + License/ 自动合并完好 |
| 版本号 | 2.2.7 → **2.3.0**（授权作为 major feature） |

## 二、编译修复

### 2.1 SHA256 兼容性（net48）

```
原因: .NET Framework 4.8 不支持 SHA256.HashData()
修复: SHA256.Create().ComputeHash()（MachineId.cs）
```

### 2.2 V20 csproj 路径

```
旧: D:\app\TIA20\Portal V20（上游硬编码）
新: D:\Program Files\Siemens\Automation\Portal V20（本机实际路径）
```

### 2.3 V21 编译

本机无 V21 Openness SDK（仅有 Win32/Setup，无 PublicAPI），无法编译。已准备：
- `tools/TiaMcpServer-v21.crproj`（ConfuserEx 项目文件）
- `V21_BUILD.md`（完整编译 + 加壳 + 测试指南）

## 三、ConfuserEx 加壳

### 3.1 工具

| 项目 | 值 |
|------|-----|
| 工具 | ConfuserEx v1.6.0（mkaring fork） |
| 位置 | `tools/ConfuserEx/` |
| 下载 | https://github.com/mkaring/ConfuserEx/releases |

### 3.2 保护方案迭代

| 轮次 | 保护 | 问题 | 修复 |
|------|------|------|------|
| 1 | rename + ctrl flow + anti debug + anti ildasm + resources | namespace 规则格式错误 | 改为 `full-name('TypeName')` 语法 |
| 2 | 同上 | MCP 启动崩溃：`Parameter is missing a name` | `renameArgs=false`（保留方法参数名） |
| 3 | 同上 | Bootstrap 返回 JSON 全为零宽字符 | 去掉 `anti ildasm` + `resources` |
| **最终** | **ctrl flow + anti debug** | 无 | — |

### 3.3 最终 crproj（V20）

```xml
<project outputDir="..." baseDir="..." xmlns="...">
  <module path="TiaMcpServer.exe">
    <rule pattern="true" inherit="false">
      <protection id="ctrl flow" action="add" />
      <protection id="anti debug" action="add" />
    </rule>
  </module>
</project>
```

> 未使用 rename 保护。原因是 MCP SDK 通过 `[McpServerTool]` Attribute 反射发现工具方法，System.Text.Json 序列化依赖属性名。虽然 `renameArgs=false` 解决了参数名问题，但出于稳定性考虑最终采用最简保护方案。核心安全靠 RSA 私钥（在服务器上）+ JWT 签名。

## 四、发布包

### 4.1 内容

| 文件 | 说明 |
|------|------|
| `TiaMcpServer.exe` | 混淆后主程序（2.1 MB） |
| `TiaMcpServer.exe.config` | .NET 运行时配置 |
| `*.dll` × 61 | 运行时依赖 |
| `LICENSE.txt` | 授权协议 + 激活步骤 |
| `快速开始.md` | 完整用户手册（中文） |
| `配置MCP.bat` | 一键注册到 Claude Desktop / Cursor |

### 4.2 交付物

```
release/TIA_MCP_v2.3.0_V20.zip
  65 文件, 5.4 MB (解压后 13.5 MB)
```

## 五、授权端到端测试

| 测试项 | 结果 |
|--------|------|
| `--show-machine-id`（混淆后） | ✅ 2A99-469A-B303-AE1D |
| 无 Key 启动 | ✅ `LICENSE DENIED — exit 7` |
| 假 Key 启动 | ✅ `server rejected (invalid license key)` |
| 吊销 Key 启动 | ✅ `server rejected (invalid license key)` |
| 在线激活 | ✅ `online validation OK, token cached for 30 days` |
| 缓存文件 | ✅ `C:\ProgramData\TiaMcpServer\.license_cache` |
| 离线模式 | ✅ `cache valid, offline mode` |
| 服务器绑定 | ✅ keygen.py info 显示 Machines: 1/N |

## 六、客户分发流程

```
客户（拿到 TIA_MCP_v2.3.0_V20.zip）
│
├─ 1. 解压到任意目录
│
├─ 2. TiaMcpServer.exe --show-machine-id
│     → 2A99-469A-B303-AE1D  ──→ 发给供应商
│
│                              供应商:
│                              keygen.py create --to "客户" --machines 1
│                              → TIA-XXXX-XXXX-XXXX  ──→ 发给客户
│
├─ 3. TiaMcpServer.exe --license-key TIA-... --license-server-url http://47.96.87.46
│     → LICENSE: online validation OK
│
├─ 4. 配置MCP.bat → 注册到 Claude Desktop / Cursor
│
└─ 5. 重启 AI 客户端，让 Agent 调 Bootstrap 验证
```

## 七、Git 记录

```
89242bc fix: ConfuserEx crproj 去掉 rename 保护
126f1f2 fix: V21 crproj 同步去掉 anti ildasm + resources
d663ba6 fix: ConfuserEx renameArgs=false 修复 MCP SDK 参数名丢失
64ec5ca docs: V21 编译+加壳+测试指南 + crproj 项目文件
2c8819d fix: SHA256.HashData → SHA256.Create().ComputeHash (net48 兼容)
776d3cd v2.3.0: 版本号升级 + V20 csproj 路径修正
1c58491 Merge upstream v2.2.7 — 合并冲突仅 .gitignore
```

已推送至 GitHub: `https://github.com/AhYesZ/TIA_Portal_Openness_MCP`

## 八、待完成

| 任务 | 状态 | 备注 |
|------|------|------|
| 拉取上游功能性更新 | 📋 | 从朋友仓库（bulaofen0036-coder）拉取最新功能，合并到 v2.3.x |
| V21 编译 + 加壳 | ⏳ | 另一台有 V21 Openness 的机器，按 `V21_BUILD.md` 操作 |
| V21 安装测试 | ⏳ | 完整走一遍客户流程：解压 → 激活 → 注册 → 功能验证 |
| V21 发布包 | ⏳ | 编译后打包 `release/v2.3.0/v21/` |
| 代码签名证书 | 📋 | 先用自签名，有客户后再投资 |
| license-server GitHub 仓库 | ✅ 已删 | 保留 GitCode 为唯一源（`gitcode.com/qq_43301551/main.git`） |
| `hermes mcp add --args` 格式 | 📋 | 当前 `hermes config set` 存为字符串而非列表，需手动 YAML 修复 |
