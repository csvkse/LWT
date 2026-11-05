# SOCKS5 安全代理微服务 (socks5-proxy)

基于 Go 原生运行引擎（`runtime: go`）构建的轻量极速 SOCKS5 网络代理微服务，完全遵循 RFC 1928 与 RFC 1929 规范。

## 功能特性

- **RFC 1928 完整支持**：支持 No-Auth (`0x00`)、用户名密码认证 (`0x02`)；
- **多地址族支持**：支持 IPv4、IPv6 与 FQDN 域名解析（防本地 DNS 污染与泄漏）；
- **零外部依赖**：纯 Go 标准库实现，启动零网络开销，离线毫秒级编译与调度；
- **JIT 增量哈希构建缓存**：源码未变更时启动耗时 < 5ms，杜绝 `go run` 孤儿进程隐患；
- **平台生命周期监管**：集成端口租约自动分配与回收、崩溃重试退避与 Web 实时日志。

## 启动参数

- `--listen <host:port>`：监听地址与端口（默认使用系统动态分配端口，如 `0.0.0.0:18001`）；
- `--auth-mode <none|password>`：认证模式（`none` 或 `password`）；
- `--username <string>`：认证用户名（亦可通过环境变量 `SOCKS5_USERNAME` 注入）；
- `--password <string>`：认证密码（亦可通过环境变量 `SOCKS5_PASSWORD` 注入）；
- `--connect-timeout <seconds>`：向上游建立连接的超时秒数（默认 10s）。
- `--max-active-sessions <n>`：最大活跃入站连接数（默认 512）；超限时快速关闭新连接。
- `--max-http-conns-per-host <n>`：HTTP 明文转发到单个 host 的最大上游连接数（默认 128）。
- `--http-max-pending-per-host <n>`：单个 host 最多等待上游请求槽位的请求数（默认 128）；队列满时返回 503。
- `--http-queue-timeout <duration>`：等待单 host 请求槽位的上限（默认 `5s`）；超时返回 504。
- `--http-response-header-timeout <duration>`：等待 HTTP 上游响应头的上限（默认 `30s`，使用 Go duration 格式）；只限制响应头等待，不限制响应体总时长。

也可通过 `SOCKS5_MAX_ACTIVE_SESSIONS`、`SOCKS5_HTTP_MAX_CONNS_PER_HOST`、`SOCKS5_HTTP_MAX_PENDING_PER_HOST`、`SOCKS5_HTTP_QUEUE_TIMEOUT` 和 `SOCKS5_HTTP_RESPONSE_HEADER_TIMEOUT` 设置上述参数。状态接口 `/api/status` 会报告当前/峰值活跃连接、拒绝数和生效上限，以及 HTTP 上游请求 active/pending/拒绝/排队超时计数。

## 自定义 DNS 与域名分流

可通过进程环境变量 `SOCKS5_DNS_RULES` 提供 JSON 数组。未设置或设为 `[]` 时维持系统 DNS；规则按最长域名后缀匹配，`*` 表示所有未被更具体规则匹配的域名。示例中的 DNS 地址为文档保留地址，必须替换为实际可从代理容器访问的解析器：

```json
[
  { "suffix": "cdn.example.com", "server": "192.0.2.53:53", "softTtl": "30s", "hardTtl": "1m" },
  { "suffix": "*", "server": "198.51.100.53:53" }
]
```

例如在 PowerShell 中启动独立进程时，可以设置 `$env:SOCKS5_DNS_RULES = '[{"suffix":"cdn.example.com","server":"192.0.2.53:53"}]'`；通过插件平台运行时，可将同一 JSON 数组作为字符串放入 manifest 的 `environment.SOCKS5_DNS_RULES`。示例地址不能直接使用。

`server` 必须是 IP 字面量与端口（IPv6 使用 `[地址]:端口`）；规则后缀使用 ASCII 域名或已转换的 punycode，不接受 `*.example.com` 形式。最多 64 条规则；各规则缓存独立，最多 512 项。`softTtl`、`hardTtl` 可省略，默认分别为 3 分钟和 10 分钟；均使用 Go duration，软 TTL 至少 1 秒，硬 TTL 不能小于软 TTL 且不能超过 24 小时。软 TTL 过期后首次命中仍返回旧地址并在后台刷新；固定 TTL 并不代表 DNS 响应中的权威 TTL。

DNS 使用标准 UDP/TCP 查询，不提供 DoH/DoT；匹配规则的 DNS 失败不会静默回退系统 DNS。Go 解析器仍遵循运行环境的 hosts 解析顺序，容器内 hosts 已有记录可能先于指定 DNS 生效；例如 `localhost` 可直接由 hosts 返回。DNS 应答和 hosts 返回的 IP 仍经过原有目标 IP ACL 校验，客户端直接提供 IP 时不会进行域名分流。配置只在进程启动时读取，修改后需重启插件；`/api/status` 中 `dnsRuleCount` 用于确认生效规则数，DNS 缓存统计为各规则与默认缓存的合计。平台支持在插件 manifest 的 `environment` 中注入该变量；当前插件页面没有 DNS 规则编辑器。

HTTP 明文代理通过 Go 标准库 `net/http.Server` 接管每条入站连接，再由共享 `http.Transport` / `httputil.ReverseProxy` 转发。浏览器或客户端断开时，请求 context 会取消上游请求并释放 active permit。HTTPS `CONNECT` 在完成 HTTP 握手后切换为原始字节隧道，保留请求解析时已缓冲的首批 TLS 数据。实现与回归验证见 [`docs/socks5-proxy-concurrency-and-capacity-review.md`](../../docs/socks5-proxy-concurrency-and-capacity-review.md)。
