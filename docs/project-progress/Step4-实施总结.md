# Step 4 实施总结 — 在线授权服务器

**日期:** 2026-07-02  
**状态:** 已完成 + 已部署上线  
**服务器:** 阿里云 ECS `47.96.87.46` (Alibaba Cloud Linux 3)

---

## 完成内容

### 代码结构

```
license-server/                  ← 独立仓库 (GitCode)
├── app.py               Flask 主程序 (230行)
├── models.py            SQLite 数据模型 (license_keys + machines)
├── jwt_util.py          RSA 私钥签发 JWT (RS256)
├── keygen.py            CLI Key 管理工具
├── admin.html           管理界面（含 Token 到期推算）
├── setup.sh             阿里云 Linux 3 一键部署脚本
├── requirements.txt
├── .gitignore
└── deploy/
    ├── nginx.conf       反向代理 + 限流
    └── mcp-license.service  systemd 服务配置
```

### API 端点

| 端点 | 方法 | 认证 | 功能 |
|------|------|------|------|
| `/health` | GET | 无 | `{"status":"ok"}` |
| `/api/activate` | POST | 靠 Key | 首次激活，绑机器，发 JWT |
| `/api/validate` | POST | 靠 Key | 续期，发新 JWT（已绑机器直接续，超限拒绝） |
| `/admin` | GET | Basic Auth | 管理界面 |
| `/admin/api/keys` | GET | Basic Auth | 列出所有 Key（含 Token 到期推算） |
| `/admin/api/keys` | POST | Basic Auth | 创建 Key |
| `/admin/api/keys/<id>/revoke` | POST | Basic Auth | 吊销 Key |

### 核心设计

```
Key (合同)            Token (门禁卡)
 有效期: 2027-07-03     有效期: 最后活跃 + 30天
 过期→拒绝签发          过期→自动续期(调/api/validate)
 由服务端 enforce        由客户端本地 RSA 验签
```

### 部署架构

```
Internet → :80 → Nginx(限流) → :5000 → Gunicorn(2 workers) → Flask
                                         ↓
                                    SQLite (WAL mode)
```

### Key 格式

`TIA-XXXX-XXXX-XXXX`（13 字符，随机生成）

### admin 面板

- **Key Expires** — Key 本身的合同截止日期
- **Token Expires** — 推算的最晚门禁卡过期日（last_seen + 30 天），`-` 表示无机器激活
- 支持创建、吊销、列表查看

---

## 测试覆盖

| 测试项 | 结果 |
|--------|------|
| /health | ✓ |
| /api/activate 正常激活 | ✓ |
| /api/validate 续期 | ✓ |
| 超 max_machines 拒绝 | ✓ `{"error":"key already bound to max machines"}` |
| 无效 Key → 401 | ✓ |
| Revoke 吊销后拒绝 | ✓ |
| admin 面板创建/查看/吊销 | ✓ |
| Token Expires 列正确推算 | ✓ |
| JWT 兼容 C# LicenseCache.cs (RS256, PKCS#1 v1.5) | ✓ |
| systemd 服务自启动/重启 | ✓ |
| nginx 反向代理 | ✓ |

---

## 密钥管理

| 位置 | 内容 | 用途 |
|------|------|------|
| 服务器 `/opt/mcp-license/keys/` | RSA 私钥 | 签发 JWT |
| C# 客户端 `LicenseCache.cs` | RSA 公钥 (硬编码) | 验签 JWT |
| 本地 `keys/` (旧) | 已废弃 | 服务器重新生成 |

### 安全边界

三样绝对不能泄露的资产：

| 资产 | 所在位置 | 泄露后果 |
|------|---------|---------|
| **RSA 私钥** | 服务器 `/opt/mcp-license/keys/license_private.pem` | 可伪造任意 token，全盘失控 |
| **admin 密码** | systemd 环境变量 `ADMIN_PASS` | 可随意增删 Key、吊销授权 |
| **Git 仓库** | GitCode `qq_43301551/main` | 暴露全部源码和部署配置 |

修改 admin 密码：
```bash
sudo sed -i 's/ADMIN_PASS=旧密码/ADMIN_PASS=新密码/' /etc/systemd/system/mcp-license.service
sudo systemctl daemon-reload
sudo systemctl restart mcp-license
```

服务器是唯一的 Token 签发中心。私钥、密码、Git 仓库三者只在你手里。

---

## 踩坑记录

1. **Python 3.6 语法兼容** — `dict | None` / `list[dict]` 不支持 → 改用 `typing.Optional`
2. **Gunicorn 版本** — 23.0 不支持 Python 3.6 → 降级到 `<21.0`
3. **Nginx limit_req_zone** — 必须放在 `http` 块，不能在 `server` 块
4. **init_db() 调用时机** — gunicorn 不执行 `__main__` → 改为模块级调用
5. **私钥传输** — base64 解码损坏 → 服务器直接 openssl 生成新密钥对，更新 C# 公钥
6. **GitCode 默认分支** — 默认 `main`，代码在 `master` → `git checkout master`

---

## 下一步: Step 5

- 在 TIA Portal 机器上编译 TiaMcpServer.exe
- 加壳 (ConfuserEx / .NET Reactor)
- 制作发布包
- 端到端测试（exe → 服务器激活 → 缓存 → 离线验证）

---

## 运维速查

```bash
# 服务器管理
ssh admin@47.96.87.46
systemctl status mcp-license    # 查看状态
systemctl restart mcp-license   # 重启
journalctl -u mcp-license -f    # 实时日志

# 更新代码
cd /opt/mcp-license && git pull && sudo systemctl restart mcp-license

# 本地 keygen
cd license-server
python keygen.py create --to "客户名" --machines 3 --expires 2027-06-01
python keygen.py list
python keygen.py revoke TIA-XXXX-XXXX-XXXX
```

**访问地址:**
- 管理面板: `http://47.96.87.46/admin`（admin / ynT9zR8gLmL5V1je）
- 健康检查: `http://47.96.87.46/health`

**Git 仓库:**
- 代码: `https://gitcode.com/qq_43301551/main`（分支 `master`）
- C# 客户端: `https://github.com/AhYesZ/TIA_Portal_Openness_MCP`
