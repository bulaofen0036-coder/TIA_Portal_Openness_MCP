# V21 机器编译 + 加壳 + 测试指南

## 前置条件

- Windows 机器，已安装 **TIA Portal V21** (含 Openness SDK)
- .NET 8 SDK (验证: `dotnet --list-sdks`)
- 用户属于 `Siemens TIA Openness` Windows 组
- Git 已安装

## 0. 克隆两个仓库

```bash
# ① TIA MCP 主仓库 (含全部 License 代码 + 实施文档)
git clone https://github.com/AhYesZ/TIA_Portal_Openness_MCP.git
cd TIA_Portal_Openness_MCP

# ② 授权服务器仓库 (独立 GitCode，keygen.py 在这)
git clone https://gitcode.com/qq_43301551/main.git license-server
cd license-server && git checkout master && cd ..
```

> 仓库说明：主仓库含 `docs/project-progress/`（Step3/4/5 实施总结 + 授权方案）和 `tools/`（V20 + V21 两份 crproj）。不在仓库里的大文件见下方「额外下载」。

## 额外下载 (不在仓库内)

| 文件 | 说明 | 下载 |
|------|------|------|
| ConfuserEx CLI | .NET 混淆工具 | https://github.com/mkaring/ConfuserEx/releases |
| V20 发布包 | 参考 V20 打包格式 | 从已有机器复制或重新编译 |

## 1. 编译 V21

```bash
cd tools\tiaportal-mcp\src\TiaMcpServer

# 如果 TIA Portal 装在默认路径，直接编译
dotnet build TiaMcpServer.csproj -c Release

# 如果自定义路径：
dotnet build TiaMcpServer.csproj -c Release -p:TiaPortalLocation="你的TIA安装路径"
```

产物: `bin\Release\net48\TiaMcpServer.exe`

## 2. 验证机器指纹

```bash
.\bin\Release\net48\TiaMcpServer.exe --show-machine-id
```

记录输出的 MachineId，用于申请 License Key。

> ⚠ 本例中 `cd license-server` 然后用 `python keygen.py create` 签发 Key。授权服务器 `47.96.87.46` 已在线。

## 3. 加壳

> ⚠ V21 与 V20 不同：V21 的 Siemens 程序集是**拆分 DLL**（17 个 `Siemens.Engineering.*.dll`），全在
> `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\`。
> ConfuserEx 需要解析 exe 的全部依赖，因此**全部复制**到 bin 目录。

```bash
# 3a) 复制所有 V21 拆分 DLL（ConfuserEx 需要解析依赖链）
copy "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.*.dll" bin\Release\net48\

# 3b) 生成临时 crproj（原来的 crproj 用的是 V20 路径，这里创建适配本机的）
set CRPROJ=%TEMP%\obfuscate-v21.crproj
(
echo ^<project outputDir="%CD%\..\..\..\..\..\release\obfuscated-v21" baseDir="%CD%\bin\Release\net48" xmlns="http://confuser.codeplex.com"^>
echo   ^<module path="TiaMcpServer.exe"^>
echo     ^<rule pattern="true" inherit="false"^>
echo       ^<protection id="ctrl flow" action="add" /^>
echo       ^<protection id="anti debug" action="add" /^>
echo     ^</rule^>
echo   ^</module^>
echo ^</project^>
) > %CRPROJ%

# 3c) 运行 ConfuserEx（从 bin\Release\net48 执行）
cd bin\Release\net48
path\to\Confuser.CLI.exe -n %CRPROJ%
```

> 也可以手动编辑 `tools\TiaMcpServer-v21.crproj`，把 `outputDir` 和 `baseDir` 改成本机绝对路径。

产物: `release\obfuscated-v21\TiaMcpServer.exe`

> ⚠ **已知问题**：ConfuserEx 的 hardening 会破坏 .NET 的注册表访问 API。
> 混淆后的 exe **必须**设置 `TiaPortalLocation` 环境变量（或通过 MCP 配置注入），否则无法解析 Siemens 程序集。
> `tia config` 命令会自动把路径写入 MCP 配置，终端用户无感。

## 4. 打包发布

```bash
# 复制所有依赖 DLL
mkdir release\v2.3.0\v21
copy release\obfuscated-v21\TiaMcpServer.exe release\v2.3.0\v21\
copy bin\Release\net48\*.dll release\v2.3.0\v21\
copy bin\Release\net48\TiaMcpServer.exe.config release\v2.3.0\v21\
copy 配置MCP.bat release\v2.3.0\v21\

# 删除调试符号、Siemens DLL、临时文件
del release\v2.3.0\v21\*.pdb
del release\v2.3.0\v21\Siemens.Engineering*.dll
del release\v2.3.0\v21\System.Management.dll

# 打包为 zip
cd release\v2.3.0\v21
powershell Compress-Archive -Path * -DestinationPath ..\..\TIA_MCP_v2.3.0_V21.zip -Force
```

> zip 产物: `release\TIA_MCP_v2.3.0_V21.zip`（约 5-6 MB）

## 5. 注册 MCP 到 Claude（自动）

```bash
# tia config 会自动探测 TIA 版本和路径，写入所有已安装 AI 客户端
bin\Release\net48\TiaMcpServer.exe config

# 或指定宿主
bin\Release\net48\TiaMcpServer.exe config --host claude-code
```

> ⚠ 混淆后的 exe **不要**用于 `tia config`。先用未混淆版本执行 config（它会设置
> `TiaPortalLocation` 环境变量），再手动将 `command` 路径改为混淆版 exe。

## 5. 端到端测试

```bash
# 7a) 不带 license → 应该拒绝 (exit 7)
TiaMcpServer.exe
# 预期: LICENSE: no --license-key provided. LICENSE DENIED

# 7b) 获取机器指纹
TiaMcpServer.exe --show-machine-id
# 记录输出的 MachineId

# 7c) 用 keygen.py 生成 Key（在管理机上操作）
python keygen.py create --to "V21测试机" --machines 1 --expires 2027-07-01

# 7d) 激活
TiaMcpServer.exe --license-key TIA-XXXX-XXXX --license-server-url http://47.96.87.46

# 7e) 验证缓存文件存在
dir %ProgramData%\TiaMcpServer\.license_cache

# 7f) 断网 → 重启 → 确认离线缓存可用
# 然后复网

# 7g) 完整功能测试
# 在 Hermes/Claude 中:
#   Bootstrap → Connect → CreateProject → GetProjectTree
