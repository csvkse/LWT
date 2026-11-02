# Go 动态增量编译包路径缺陷分析与 SOCKS5 代理三维安全防御方案

## 一、 问题现象与根因深度复盘

### 1. 故障现象
在插件控制台修改 `socks5-proxy` 配置并保存后，重启插件失败，微服务进入崩溃状态并提示：
```text
构造启动参数异常: Go 动态增量编译失败 (ExitCode 1): 
# command-line-arguments 
cmd/socks5-proxy/main.go:85:11: undefined: AclEngine 
cmd/socks5-proxy/main.go:146:20: undefined: NewAclEngine
```

### 2. 根因深度剖析
查阅 `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/GoRuntimeAdapter.cs` 第 101 行：
```csharp
var buildTarget = entry.EndsWith(".go", StringComparison.OrdinalIgnoreCase) ? entry : "./...";
```
- 插件清单 `plugin.json` 中配置的入口为 `"entrypoint": "cmd/socks5-proxy/main.go"`。
- `entry` 判定以 `.go` 结尾，直接将该单文件绝对路径传递给了 `go build`：
  ```bash
  go build -ldflags "-s -w" -o "/app/data/plugins/socks5-proxy/.cache/bin/app" /app/data/plugins/socks5-proxy/cmd/socks5-proxy/main.go
  ```
- **Go 语言编译器的底层规范**：
  - 当给 `go build` 传入单个 `.go` 文件路径时，`cmd/go` 会将其视为特殊的即席单文件包（`# command-line-arguments`）；
  - 编译器**严格只编译该独立文件**，刻意不加载同目录下的兄弟文件（如 `cmd/socks5-proxy/acl.go`）；
  - 导致 `main.go` 中引用的结构体 `AclEngine` 与函数 `NewAclEngine` 无法被解析，抛出 `undefined` 致命编译错误。

---

## 二、 现有库、业界主流方案与系统基础设施复用

### 1. Go 官方构建标准与主流构建工具调研
- **Go 官方手册**：
  > "A package path that begins with ./ or ../ is called a relative path. The tool compiles the package found in that directory."
  > "If the arguments are a list of .go files, build treats them as a list of source files specifying a single package."
- **行业流行工具（Air、Ko、Goreleaser、Pack 等）**：
  - 均将入口文件所在目录提取为包路径：`dir = filepath.Dir(entrypoint)`；
  - 统一构造为相对包路径格式：`./` + `filepath.ToSlash(dir)`（例如 `./cmd/socks5-proxy` 或 `./.`）；
  - 绝不将 `.go` 单源文件绝对路径作为构建命令的目标。

### 2. 复用当前系统已有基础设施
- **`SingleFileHeaderParser` 与 `GoRuntimeAdapter`**：
  - 可以利用 `Path.GetRelativePath(pluginDir, entry)` 结合 `Path.GetDirectoryName` 计算相对包路径；
  - 若入口在根目录（如 `main.go`），计算结果为 `./.`；
  - 若入口在子目录（如 `cmd/socks5-proxy/main.go`），计算结果为 `./cmd/socks5-proxy`；
  - 若入口直接为目录，则计算结果为 `./<dir>`；
  - 彻底规避单文件孤立编译问题，同时原生支持同包内多个源文件（`main.go`, `acl.go`, `config.go` 等）的联动编译。

---

## 三、 SOCKS5 代理“三维安全防御”升级方案

结合用户对 **标准 CIDR**、**入站 IP 访问限制** 以及 **用户名密码认证 (RFC 1929)** 的需求，形成三维纵深防御闭环：

```
                    ┌─────────────────────────────────────────────────────────────┐
                    │               SOCKS5 Proxy (Go 微服务)                      │
                    │                                                             │
[客户端连接] ------>│  1. 入站源 IP 限制 (Client Ingress CIDR)                     │
                    │     • 检查来源客户端 IP 是否属于允许网段 (如 10.0.0.0/8)     │
                    │     • 未通过：TCP 握手前立即断开连接 (零协议栈消耗)           │
                    │                                                             │
                    │  2. 用户名/密码认证 (RFC 1929 Authentication)               │
                    │     • 校验 Client 提供的 Username / Password                │
                    │     • 密码错误：返回 0x01 认证拒绝，计入爆破探测计数        │
                    │                                                             │
                    │  3. 出站目标限制 (Target Egress CIDR & 域名双检)            │ ------> [内网目标主机]
                    │     • 严格标准 CIDR (10.0.0.0/8, 172.16.0.0/12, 192.168/16) │
                    │     • 域名通配符/正则 + Anti-DNS-Rebinding 双检             │
                    └─────────────────────────────────────────────────────────────┘
```

### 1. 维度一：出站目标网段全量采用标准 CIDR
- **消除前缀歧义**：
  - 废除可能导致公网误放行（如 `172.16.` 误放行 `172.169.x.x`）的纯文本前缀；
  - 默认采用 RFC 1918 标准私网网段与回环网段：
    ```
    10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8, ::1/128
    ```
- **输入容错自动归一化**：
  - 若运维输入 `10.` 自动补齐为 `10.0.0.0/8`；输入 `192.168.` 自动补齐为 `192.168.0.0/16`；
  - 若输入单 IP `1.2.3.4` 自动规整为 `1.2.3.4/32`。

### 2. 维度二：入站客户端 IP 访问限制 (Standard CIDR)
- **前置短路拦截**：
  - 在 `net.Accept()` 收到 TCP 连接后，先通过 `conn.RemoteAddr()` 提取客户端 IP 并判定 `IsClientAllowed(ip)`；
  - 违规连接直接 `conn.Close()` 截断，不分配任何 SOCKS5 协程与内存缓冲区；
  - 实时递增指标 `blockedClients`，并在管理端提供爆破防范监控。

### 3. 维度三：用户名与密码访问认证 (RFC 1929)
- **协议握手支持**：
  - 支持 `none`（无认证）与 `password`（RFC 1929 用户名/密码）两种模式；
  - 密码比对采用 `crypto/subtle.ConstantTimeCompare`，杜绝时序攻击；
- **控制台与热重载**：
  - Webview 控制台支持自由切换认证模式、设置用户名、设置密码、一键生成安全强密码；
  - 通过 `/api/acl`（扩展为 `/api/config`）接口实现运行时凭据热重载，无需中断既有连接。

---

## 四、 具体执行步骤与架构门禁设计

### 1. 执行清单
1. **修复 `GoRuntimeAdapter.cs`**：
   - 提取规范相对包路径 `./<dir>`，彻底解决 Go 编译器 `undefined: AclEngine` 问题；
2. **升级 Go 端 `acl.go` 与 `main.go`**：
   - 目标网段解析强化为标准 CIDR；
   - 增加入站客户端 CIDR 校验与实时指标；
   - 接入用户名密码热重载机制与失败审计计数；
3. **升级 Webview 控制台 `ui/index.html`**：
   - 增加入站客户端 IP 白名单卡片；
   - 增加用户名密码认证配置卡片与一键密码生成；
   - 动态更新客户端使用指引（自动携带 `user:pass@` 凭据）；
4. **架构门禁 `LinuxArch047`**：
   - 在 `ArchitectureTests.cs` 中新增针对 Go 运行引擎相对包路径编译规范及 SOCKS5 三维安全模型的自动化防回归门禁；
5. **部署与端到端实机验证**：
   - 远程容器热部署、全量单元测试与内置浏览器截屏验收。
