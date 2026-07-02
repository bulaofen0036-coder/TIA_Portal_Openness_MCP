# V21 机器编译 + 加壳 + 测试指南

## 前置条件

- Windows 机器，已安装 **TIA Portal V21** (含 Openness SDK)
- .NET 8 SDK
- 用户属于 `Siemens TIA Openness` Windows 组
- Git 已安装

## 1. Clone 仓库

```bash
git clone https://github.com/AhYesZ/TIA_Portal_Openness_MCP.git
cd TIA_Portal_Openness_MCP
```

## 2. 编译 V21

```bash
cd tools\tiaportal-mcp\src\TiaMcpServer

# 如果 TIA Portal 装在默认路径，直接编译
dotnet build TiaMcpServer.csproj -c Release

# 如果自定义路径：
dotnet build TiaMcpServer.csproj -c Release -p:TiaPortalLocation="你的TIA安装路径"
```

产物: `bin\Release\net48\TiaMcpServer.exe`

## 3. 验证机器指纹

```bash
.\bin\Release\net48\TiaMcpServer.exe --show-machine-id
```

记录输出的 MachineId，用于申请 License Key。

## 4. 下载 ConfuserEx

从 https://github.com/mkaring/ConfuserEx/releases 下载 `ConfuserEx-CLI.zip`，解压到任意目录。

## 5. 加壳

```bash
# 先把 Siemens.Engineering.dll 复制到 bin 目录（ConfuserEx 需要解析依赖）
copy "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.dll" bin\Release\net48\
copy "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Hmi.dll" bin\Release\net48\

# 运行 ConfuserEx
cd bin\Release\net48
path\to\Confuser.CLI.exe -n "..\..\..\..\..\..\tools\TiaMcpServer-v21.crproj"
```

产物: `release\obfuscated-v21\TiaMcpServer.exe`

## 6. 打包发布

```bash
# 复制所有依赖 DLL
mkdir release\v2.3.0\v21
copy release\obfuscated-v21\TiaMcpServer.exe release\v2.3.0\v21\
copy bin\Release\net48\*.dll release\v2.3.0\v21\
copy bin\Release\net48\TiaMcpServer.exe.config release\v2.3.0\v21\
copy 配置MCP.bat release\v2.3.0\v21\

# 删除调试符号和 Siemens DLL
del release\v2.3.0\v21\*.pdb
del release\v2.3.0\v21\Siemens.Engineering*.dll
```

## 7. 端到端测试

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
