# Step 3 实施总结 — 本地离线授权模式

**日期:** 2026-07-02  
**提交:** `e305c07`  
**状态:** 完成（编译通过，待 TIA Portal 机器上集成测试）

---

## 完成内容

### 新增文件

```
tools/tiaportal-mcp/src/TiaMcpServer/License/
├── MachineId.cs          ← Step 1 (已有)
├── LicenseCache.cs       ← Step 3 新增: JWT 缓存读写 + RSA 验签
└── LicenseValidator.cs   ← Step 3 新增: 校验主入口
```

### LicenseCache.cs — 本地缓存

- **存储位置:** `%ProgramData%\TiaMcpServer\.license_cache`
- **格式:** 纯文本 JWT（RSA 签名）
- **验签流程:**
  1. 读取文件 → 拆分 JWT 三段（header.payload.signature）
  2. 用硬编码 RSA 公钥验证 RS256 签名 → 防篡改
  3. 解码 payload，比对 `mid`（machineId） → 防跨机器复制
  4. 检查 `exp`（Unix 时间戳） → 防过期
  5. 全部通过返回 token，任一步失败返回 null（静默）

**设计决策:** 不加密（纯 JWT 明文存储）。RSA 签名保证防篡改，machineId 保证防跨机器。AES-GCM 在 .NET Framework 4.8 上无内置支持且不增加实质安全性。

### LicenseValidator.cs — 校验入口

```
Validate(licenseKey, serverUrl) 流程:
  1. licenseKey 为空 → 拒绝
  2. LicenseCache.TryLoad() 有效 → 通过 (offline)
  3. POST {serverUrl}/api/validate  → 成功则 Save 缓存 → 通过
  4. 全部失败 → 拒绝 + 友好错误信息
```

**HTTP 调用:** 使用 `System.Net.Http.HttpClient`（net48 内置），超时 10 秒。JSON 手动构造/解析，零外部依赖。

**错误信息映射:**

| HTTP 状态码 | 友好信息 |
|------------|---------|
| 401 | invalid license key |
| 403 | license expired or revoked |
| 409 | key already bound to max machines |
| 超时 | server timeout (10s) |
| 网络不通 | cannot reach license server |

### 修改文件

**CliOptions.cs** (+18 行):
```csharp
public string? LicenseKey { get; set; }          // --license-key TIA-XXXX-...
public string? LicenseServerUrl { get; set; }    // --license-server-url https://...
```

**Program.cs** (+13 行):
```csharp
// 插入位置: Openness.IsUserInGroup() 检查之后，RunStdioHost/RunHttpHost 之前
var (licenseOk, licenseMsg) = await LicenseValidator.Validate(
    options.LicenseKey,
    options.LicenseServerUrl ?? "http://localhost:5000");
LogDiag(licenseMsg);
if (!licenseOk) Environment.Exit(7);
```

### JWT 格式

```
Header:   {"alg":"RS256","typ":"JWT"}
Payload:  {"mid":"2A99-469A-B303-AE1D","exp":1750000000,"iat":1748000000,"kid":"TIA-XXXX"}
签名:     RSA-SHA256(base64url(header) + "." + base64url(payload))
```

### RSA 密钥

- **私钥:** `D:\Z_Work\AI\Program\TIA_MCP\keys\license_private.pem`（2048-bit，你保管）
- **公钥:** 硬编码在 `LicenseCache.cs` 的 `RsaModulus` 字节数组中
- **私钥不提交 Git** — 目录在 repo 外部

---

## 编译验证

| 项目 | 结果 |
|------|------|
| License 代码编译 | 0 错误，0 警告 |
| 整体项目编译 | 124 错误（全部为 TIA Portal Siemens DLL 缺失，本机无 TIA，与授权代码无关） |

---

## 设计原则

1. **零外部依赖** — RSA 验签、JWT 解析、JSON 解析、HTTP 请求全部使用 net48 内置 API
2. **最小侵入** — 不改 McpServer.cs（0 行），只在 Program.cs 入口处插入 13 行固定代码块
3. **安全等价** — JWT 明文存储 + RSA 签名 = AES 加密的安全效果，且更简单可审计
4. **离线优先** — 30 天缓存期内完全不需要网络，适合工厂工控机断网场景

---

## 下一步: Step 4

- 建立授权服务器（Flask + SQLite）
- 实现 `POST /api/activate`（首次绑定）和 `POST /api/validate`（续期/校验）
- RSA 私钥签发 JWT token
- 部署到阿里云 ECS
- 客户端联调完整激活 + 续期流程
