# Step 6 — V21 编译 + 仓库整理 + 正式发布 实施总结

**日期**: 2026-07-03
**版本**: v2.3.0 (正式发布)
**状态**: 完成

---

## 一、上游确认

| 检查项 | 结果 |
|--------|------|
| 拉取 upstream | 无新提交 |
| 当前上游 HEAD | `38043e9` (2026-07-02 14:50) — 已含在 v2.3.0 合并中 |
| upstream release | v2.2.7 |
| 结论 | 代码已是最新，无需合并 |

## 二、仓库整理

### 2.1 归档实施文档

将仓库外的重要文件迁移入 Git 版本控制：

| 文件 | 迁移后路径 |
|------|-----------|
| Step3-实施总结.md | `docs/project-progress/Step3-实施总结.md` |
| Step4-实施总结.md | `docs/project-progress/Step4-实施总结.md` |
| Step5-实施总结.md | `docs/project-progress/Step5-实施总结.md` |
| 授权方案实施计划.md | `docs/project-progress/授权方案实施计划.md` |
| TiaMcpServer-v20.crproj | `tools/TiaMcpServer-v20.crproj` |

### 2.2 .gitignore 优化

```
排除: release/*/ release/obfuscated/ release/*.exe release/*.dll
排除: test_install/ TmpExport/ tools/ConfuserEx/ license-server/ keys/
保留: release/*.zip (发布包纳入仓库)
```

### 2.3 发布包纳入仓库

| 文件 | 大小 | 来源 |
|------|------|------|
| `release/TIA_MCP_v2.3.0_V20.zip` | 5.4 MB | 本机编译 + ConfuserEx 混淆 |
| `release/TIA_MCP_v2.3.0_V21.zip` | 5.4 MB | V21 机器 (gkbpwn) 编译 + ConfuserEx 混淆 |

## 三、V21 编译 (远程机器完成)

V21 机器从 GitHub clone 后，参照 `V21_BUILD.md` 完成：

```
编译 → 机器指纹 → 签发 Key → 激活 → ConfuserEx 加壳 → 打包 zip → push
```

关键提交:
- `7c01341` — V21_BUILD.md 加壳步骤修正（拆分 DLL + 注册表已知问题）
- `c4e9706` — V21 发布包纳入仓库

## 四、客户安装包

### V20 版本
```
TIA_MCP_v2.3.0_V20.zip
├── TiaMcpServer.exe         (ConfuserEx: ctrl flow + anti debug)
├── TiaMcpServer.exe.config
├── *.dll × 61
├── 配置MCP.bat              (一键注册 Claude Desktop / Cursor / VS Code / Claude Code)
├── 快速开始.md               (3步激活指引)
└── LICENSE.txt
```

### V21 版本
```
TIA_MCP_v2.3.0_V21.zip  (同上结构)
```

## 五、交付流程

```
供应商:
  1. 发送 TIA_MCP_v2.3.0_V20.zip (或 V21) 给客户
  2. 等客户返回机器指纹 (MachineId)

客户:
  3. 解压 → TiaMcpServer.exe --show-machine-id
  4. 把指纹发给供应商

供应商:
  5. cd license-server && python keygen.py create --to "客户" --machines 1
  6. 把 Key 发给客户

客户:
  7. TiaMcpServer.exe --license-key TIA-... --license-server-url http://47.96.87.46
  8. 运行 配置MCP.bat
  9. 重启 AI 客户端 → 调 Bootstrap 验证
```

## 六、运维速查

| 资源 | 地址 |
|------|------|
| TIA MCP 代码 | `https://github.com/AhYesZ/TIA_Portal_Openness_MCP` |
| 授权服务器代码 | `https://gitcode.com/qq_43301551/main` (branch: master) |
| 授权服务器 | `47.96.87.46` (Alibaba Cloud Linux 3) |
| admin 面板 | `http://47.96.87.46/admin` |
| 上游仓库 | `https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP` |
| ConfuserEx | `https://github.com/mkaring/ConfuserEx/releases` |

## 七、Git 记录 (v2.3.0 完整链路)

```
c4e9706 release: v2.3.0 V21 发布包纳入仓库           (V21机器)
0345171 release: v2.3.0 V20 发布包纳入仓库           (本机)
7c01341 docs: 修正 V21_BUILD.md 加壳步骤             (V21机器)
dec81f2 docs: 更新 V21_BUILD.md — 双仓库 clone       (本机)
4097e08 docs: 归档项目进度文件 + V20 crproj 纳入仓库  (本机)
4dca768 docs: Step5 待办更新
8bb5228 docs: Step5 编译+加壳+发布实施总结
89242bc fix: ConfuserEx crproj 去掉 rename 保护
d663ba6 fix: ConfuserEx renameArgs=false
64ec5ca docs: V21 编译+加壳+测试指南
2c8819d fix: SHA256 net48 兼容
776d3cd v2.3.0: 版本号升级
1c58491 Merge upstream v2.2.7
3c68f77 Update RSA public key (server new key pair)
e305c07 Step 3: License 离线授权模式
7793a00 Step 1: MachineId 硬件指纹
```

---

## 附录: 项目文件结构 (最终)

```
TIA_Portal_Openness_MCP/              ← GitHub 仓库根目录
│
├── docs/
│   ├── project-progress/             ← 实施过程归档
│   │   ├── 授权方案实施计划.md         (原始 5 步计划)
│   │   ├── Step3-实施总结.md           (License 离线模式)
│   │   ├── Step4-实施总结.md           (在线授权服务器)
│   │   ├── Step5-实施总结.md           (编译+加壳+发布)
│   │   └── Step6-实施总结.md           (V21+整理+正式发布) ← 本文件
│   ├── 项目总览.md                    ← 统一归纳
│   ├── ... (上游文档)
│
├── tools/
│   ├── TiaMcpServer-v20.crproj       ← V20 ConfuserEx 配置
│   ├── TiaMcpServer-v21.crproj       ← V21 ConfuserEx 配置
│   └── tiaportal-mcp/
│       └── src/TiaMcpServer/
│           ├── License/              ← 授权代码 (3 文件)
│           ├── TiaMcpServer.csproj    (V21)
│           ├── TiaMcpServer.V20.csproj (V20)
│           └── ...
│
├── release/
│   ├── TIA_MCP_v2.3.0_V20.zip        ← V20 客户交付包
│   └── TIA_MCP_v2.3.0_V21.zip        ← V21 客户交付包
│
├── V21_BUILD.md                      ← V21 机器操作手册
├── 配置MCP.bat / 配置MCP-v20.bat     ← 一键注册脚本
├── README.md / CHANGELOG.md          ← 上游文档
└── ...
```
