# SOCKS5 安全代理优化路线与可行性评估

## 结论

当前可以优化，但应先修正并扩展诊断，再决定是否改服务数据面。最近一次内网实测中，5 轮浏览器登录页 Load p50 为直连 237.8ms、HTTP 代理 243.1ms、SOCKS5 代理 245.7ms；页面样本少、仅一个内网目标，差异约 5–8ms，尚不能证明真实外网网站存在可感知回归。微基准 HTTP TTFB 代理约多 1.5ms，SOCKS5 CONNECT p50 约 2.1ms。它们包括连接路径和上游连接时间，不能视为代理纯计算开销。

因此推荐顺序是：**先测量口径和错误率 → 补连接阶段观测 → 修正已发现的 DNS/拨号/中继边界 → 用同环境复测 → 再决定是否改调度或数据面**。目前没有依据立即引入新的代理库、实现另一套 splice、或启用 SOCKS UDP/QUIC。

## 当前系统资产与缺口

| 能力 | 当前实现 | 优化含义 |
|---|---|---|
| 基准工具 | `tests/benchmark/proxy-benchmark.mjs` 已支持可配置 30 轮、3 次预热、交错顺序、协议完整读取、p50/p90/p99 与成功数；`proxy-web-performance.mjs` 每模式默认 5 个冷启动浏览器样本。 | 足以做短诊断；浏览器样本数少、只跑单一页面，缺热启动/冷 DNS 控制、失败请求计数和硬件/版本元数据。 |
| DNS | `IDnsResolverStrategy`、IPv4 优先 Resolver、LRU、正/负 TTL、singleflight、stale-while-revalidate；`/api/status` 暴露缓存 hits/misses/stale/evictions 等计数。 | 复用现有抽象和计数器；增加耗时直方图和冷/热场景即可，不需要马上换 DNS 库。 |
| DNS 地址族 | `Ipv4PreferResolver.Resolve` 先查 `ip4`，只要得到 IPv4 就立即返回；只有 IPv4 无结果时才执行 `ip` 双栈查询。 | 当前所谓 IPv4-prefer 实际优先 IPv4 命中路径，通常不会并行或收集 IPv6 候选；此时后续连接竞速不是 IPv4/IPv6 Happy Eyeballs。先通过 A/AAAA 双栈测试验证，再决定改为收集并交错两族地址。 |
| 拨号竞速 | `dialUpstreamWithRace` 预筛 ACL、最多取前 4 个 IP、120ms 阶梯拨号、赢家后取消其它 DialContext。 | 有基础并发竞速；需测地址排序、慢地址取消、快速失败推进和超时。只取前 4 个未按地址族交错时，可能漏掉较优地址。 |
| 中继 | Linux TCP 走 `io.Copy`（Go 运行时条件允许时可能使用 `TCPConn.ReadFrom`/splice）；其它路径使用 `io.CopyBuffer` + `sync.Pool`。双向 pipe 忽略 copy error；一侧结束后设置 10 秒 deadline。 | splice/池已存在，不能作为待新增能力。优先验证慢速长下载是否被 10 秒 deadline 截断、错误是否需要记录、半关闭与双向 drain 的生命周期。 |
| TCP Socket | `optimizeTcpConn` 已设置 NODELAY、KeepAlive 和 30 秒间隔。 | 不重复补 NODELAY；KeepAlive 主要探测失效连接，不直接减少常规首屏时延。 |
| 依赖 | `plugins/socks5-proxy/go.mod` 仅有 Go 1.24 声明，没有外部库。 | 标准库已覆盖 TCP、HTTP 和转发。代理服务端当前不需要新依赖才能做 P0/P1。 |
| 观测 | `/api/status` 有连接/流量和 DNS 缓存计数；连接建立有日志。 | 缺每阶段耗时、拨号候选/赢家族、连接失败分类、请求成功率直方图；日志不可逐包记录。 |

## 根因与优先级

### P0：基准工具可信度与复现性

已修复 `proxy-benchmark.mjs` 的分片读取/剩余字节丢弃问题，默认 30 次采样并报告成功数；浏览器脚本默认每模式 5 次、报告 p50 和成功数。建议下一步：

1. 为 SOCKS 探针实现 RFC 1929 可选用户名密码认证，凭据仅从环境变量或被忽略的 `env/.envdev` 读取；绝不打印凭据。现服务默认配置可能是 none，也可能由持久配置覆盖，必须确保探针模式匹配实际运行模式。
2. 浏览器同时运行冷启动与热启动两组；随机化 Direct/HTTP/SOCKS 每轮顺序，并统计 `requestfailed`、HTTP 错误状态及导航超时，避免把“请求完成数”误当成功率。
3. 目标至少分为本机受控短请求、受控大流、稳定外网多资源站点；保持直连和代理相同 URL/客户端/时间窗口。结果带浏览器版本、代理版本、运行主机、地址族、缓存模式和成功率。
4. 30 个样本只适合看大致分布，p99 仍很不稳定。当前以 p50/p90 做诊断，收集多轮结果后估计稳定区间，再设相对回归门禁。

### P1：低开销阶段观测

每次 SOCKS/HTTP 连接记录阶段 duration：

```text
accept → protocol sniff → auth/ACL → DNS(cache hit/miss/stale) →
candidate dial → upstream connected → reply sent → relay first byte → close
```

建议 metrics 只存边界时间、结果码、地址族、候选数、缓存状态、传输字节数；按固定直方图 bucket 聚合到现有 `/api/status` 或独立 metrics endpoint。不要把完整 URL、认证头、密码、每个数据包或每条长流都写日志。先通过阶段数据确定是 DNS、连接、TLS/客户端等待还是中继。

### P2：优先修正逻辑边界，再调性能

**DNS 与取消**

- `SingleflightGroup.Do` 的等待者使用 `WaitGroup.Wait()`，不会因自己的 context 取消而提前返回；leader 的 resolver context 又被所有等待者共享。建议新增 `DoContext`，让等待者可选取消等待，同时 leader 查询使用独立且有界的 context；不要让单个客户端断开取消仍有其它等待者需要的查询。
- stale 命中每次都启动 goroutine，再进入 singleflight；热点过期期间可能产生大量短命 goroutine。可在缓存条目或 refresh registry 上加入 per-key refresh-in-progress 原子标记，最多一个后台刷新任务。
- 维持 soft/hard TTL 与负缓存语义；刷新失败时继续用旧 IP 直到 hard TTL，但每次拨号仍执行 IP ACL，保留 anti-rebinding 防护。

**地址族与 Happy Eyeballs**

- 让双栈 resolver 一次取到 A/AAAA 地址，稳定交错候选地址族；用 `net.Dialer`/RFC 8305 语义控制尝试间隔和取消。继续设置候选数/并发上界及全局 connect deadline。
- ACL 预筛后保留每族候选顺序；测试只有 IPv4、只有 IPv6、IPv6 慢/IPv4 快、相反顺序、第一候选快速拒绝、全失败、父 context 取消。避免将 DNS 解析结果顺序当作健康度。
- 微基准显示本地 SOCKS CONNECT p50 约 2.1ms，尚没有证据支持只为减少 0.1–1ms 而增加并发拨号或健康状态缓存。

**中继生命周期**

- 为双向 `io.Copy` 分别记录 bytes 与 error 分类，修正一侧 EOF 后 deadline 的状态语义。10 秒属于现有行为，需要针对目标业务确认；慢速传输中一方半关闭后，对反向连接的固定 10 秒 deadline 可能过早结束仍活跃的数据流。
- 加入固定 payload 哈希校验、双向半关闭、慢速反向响应超过 10 秒、RST/客户端取消、预读缓冲等回归场景。
- 当前 Linux 分支已尝试标准库优化路径；不要叠加第三方 splice 库。通过 `go test -bench`/pprof 或系统调用观测验证具体运行时是否走 splice、CPU 与吞吐是否改善。

**QUIC/HTTP/3**

- 当前 SOCKS5 UDP ASSOCIATE 未支持，代理是 TCP 转发器；但本次内网登录页三种路径均完成请求，不能归因于 QUIC。
- 只有 Chromium NetLog/抓包证实目标站点尝试 UDP/443 且失败等待显著影响 LCP/Load，才立项支持 SOCKS5 UDP relay。实施需要 TCP 控制连接绑定 UDP 客户端来源、UDP ASSOCIATE 生命周期、数据报地址解析、ACL、限制/超时、反射滥用防护以及 IPv4/IPv6 覆盖。SOCKS UDP 支持只是转发能力，不保证浏览器一定选择 HTTP/3。

## 设计模式建议

| 模式 | 可落点 | 是否现在实施 |
|---|---|---|
| Adapter | 把 `net.Dialer`、DNS strategy、单调时钟和阶段 recorder 包装为最小测试接口；生产 Adapter 调用当前标准库实现。 | P1 可按测试需求逐步注入，避免给每个 `net.Conn` 再套多层包装。 |
| Strategy | DNS 地址策略（IPv4-only/双栈交错）、拨号策略（单地址/竞速）、传输策略（当前 `io.Copy` 与 buffer fallback）通过小接口选择。 | 先对边界已有 DNS `IDnsResolverStrategy` 延续；Dial/relay 只有出现第二个真实实现或需要隔离测试时再抽象。 |
| State machine | 连接状态 `Accepted → Negotiated → Authenticated → Authorized → Resolved → Dialed → Relaying → Closing → Closed`，用不变量约束 deadline、上游连接所有权和计数器。 | 先写成生命周期表和测试断言。当前流程线性且短，不建议引入通用状态机库/大量状态对象。 |
| Decorator/metrics | `net.Conn` 计数包装器可测字节和首字节。 | 可选；阶段耗时应由显式 recorder 在边界采样，避免每次 Read/Write 加锁和高频 metric 开销。 |

设计模式用于隔离可替换点和可验证边界，不是优化本身。未测出耗时前增加抽象会增加热路径分支、同步和维护负担。

## 库与主流实现参考

| 方案 | 功能边界 | 与本项目的比较/建议 |
|---|---|---|
| Go 标准库 `net`, `io`, `net/http`, `net/http/httptrace` | TCP、拷贝、HTTP 客户端与 DNS/connect/TLS/首字节 trace。 | 当前已经使用；优先用于 client 基准和数据面。`httptrace` 不能直接观测原始 SOCKS 转发内部阶段。 |
| `golang.org/x/net/proxy` | 支持 SOCKS5 客户端拨号、可传可选用户名密码与 forward Dialer。 | 适合作为 Go 基准客户端/测试适配器；它不是 SOCKS 服务端或通用高性能转发框架，不能替换当前 ACL、安全边界。当前模块未依赖它。 |
| Envoy | L4/L7 listener/cluster、健康检查、限流与丰富指标；HTTP 上游连接池可复用连接，HTTP/2/3 可多路复用。 | 适合需要集中式可观测、路由、熔断和 HTTP 语义的网关。当前安全 SOCKS 端到端 TCP 隧道不解密 TLS，无法复用 Envoy HTTP 路由层的请求池；整体引入显著增加运行复杂度。借鉴 cluster/dial/metrics 分层即可。 |
| Squid | 成熟 HTTP/HTTPS forward proxy、缓存/ACL/访问日志配置。 | 可用来对照 HTTP 代理成熟能力；不是 SOCKS5 原样替代，HTTP 缓存也不适用于端到端加密 SOCKS 隧道。 |
| SOCKS5 RFC 1928 UDP ASSOCIATE | UDP 数据报 relay。 | 是扩展功能，不是 TCP 性能调参。必须按 RFC 的 TCP 控制/UDP relay 关联生命周期实现并做安全评审。 |

目前没有发现必须加入的第三方库。生产代理依赖少是优点。第三方适配仅建议在测试/工具层引入 `x/net/proxy`（如现有手写客户端难以覆盖认证和地址解析），不建议为了做单一 SOCKS 服务引入 Envoy 或新的通用状态机框架。

一手资料：

- [RFC 1928 SOCKS Protocol Version 5](https://www.rfc-editor.org/rfc/rfc1928)
- [RFC 8305 Happy Eyeballs Version 2](https://www.rfc-editor.org/rfc/rfc8305)
- [Go `net/http/httptrace`](https://pkg.go.dev/net/http/httptrace)
- [Go `golang.org/x/net/proxy`](https://pkg.go.dev/golang.org/x/net/proxy)
- [Go `TCPConn.ReadFrom`](https://go.dev/src/net/tcpsock.go)
- [Envoy upstream connection pooling](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/connection_pooling)
- [Envoy upstream cluster manager](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/cluster_manager)

## 分阶段交付与门禁

1. **P0（已落地）**：协议读取正确、随机交错采样、成功数、浏览器冷启动重复样本；固定毫秒 SLA 已撤下。验收：两脚本语法通过，5 次受控内网采样三模式/协议全成功。
2. **P1（本轮部分落地）**：探针现在从 `SOCKS5_USERNAME` / `SOCKS5_PASSWORD` 协商可选 RFC 1929，并将同一凭据用于 HTTP Basic Proxy-Authorization；浏览器脚本报告失败请求数、HTTP 状态、Node/OS/架构/Chrome 版本，并按轮随机交错三种模式。Go `/api/status` 新增 DNS resolve、upstream dial 两阶段的 count/totalNs/maxNs 原子聚合计时，SOCKS 与 HTTP 两路径均记录成功和失败阶段。尚未增加 relay 首字节、拨号族直方图、代理 build version 或完整 failure reason 分类。
3. **P2（按现象）**：DNS singleflight cancellation、stale refresh 单任务约束；双栈 resolver 与候选排序；relay deadline/错误行为修正。每项先增加针对性失败测试，再最小修改并跑相关 Go 测试。
4. **P3（证据触发）**：若 NetLog 证实 QUIC 回退影响体验，再开发 UDP ASSOCIATE；若 profile 证实 copy CPU/吞吐瓶颈，再量化替代传输策略。否则不扩大协议和运行时范围。

硬门禁：SOCKS/HTTP 协议、认证、域名/IP ACL 和 DNS rebinding；竞速单赢家及 loser 关闭；双向完整性、半关闭和慢流存活；连接取消与资源回收。性能先报告多轮 p50/p90、成功率和相对变化，稳定基线后才设相对回归阻断阈值，不再用跨环境固定毫秒门槛。

## P1 执行记录（2026-11-05）

- 添加 `PhaseMetric` 原子计数器及测试；`/api/status` 返回 `phases.dnsResolve` 与 `phases.upstreamDial` 的 `count`、`totalNs`、`maxNs`。不记录 host、IP、认证信息。
- 两个基准脚本均支持凭据从现有服务变量读取；浏览器结果含 `requestfailed` 数量、HTTP 状态合计与运行环境，模式顺序按每轮随机交错。
- `go test ./...` 通过；浏览器与 Node 脚本语法检查通过。热更新后的内网微基准 5 次采样各协议均 5/5：SOCKS5 p50 1.90ms、SOCKS5h 1.89ms、HTTP CONNECT 1.39ms、Direct TCP 0.67ms；HTTP TTFB 代理相对直连均值多约 1.99ms。随机交错浏览器 5 轮结果记录在 `docs/socks5-benchmark-feasibility-and-design-review.md`；两组结果受目标和操作系统负载影响，不作为 SLA。
- 按仓库插件热更新流程更新了 `linuxwebtool-dev` 内的 SOCKS5 插件：原子替换成功，平台 API 返回重启成功，PID `1986808` → `312648`，本地/远端二进制 SHA256 一致；开发容器未重启，生产容器未触碰。
- 热更新后运行 5 次采样，代理/直连所有连接和 TTFB 样本成功；Direct TCP p50 0.67ms、SOCKS5 1.90ms、SOCKS5h 1.89ms、HTTP CONNECT 1.39ms；HTTP TTFB 均值直连 2.44ms、代理 4.43ms，差 +1.99ms。容器内 `/api/status` 已确认 `upstreamDial` 更新为 count=26、total=4.01ms、max=0.42ms；DNS count=0 符合测试目标使用 IP（`10.126.1.70`），因此本轮未覆盖域名解析阶段。
- 紧接着再次运行 5 轮后，Direct TCP p50 0.88ms、SOCKS5 2.63ms、SOCKS5h 2.33ms、HTTP CONNECT 1.58ms；HTTP TTFB 直连均值 5.19ms、HTTP 代理 4.10ms，差值反转为 -1.10ms。该反转进一步说明 5 样本下网络/目标抖动大于代理处理差异，不能以这些数字做性能门禁或判断代理快于直连。
- `go test -race ./...` 在当前 Windows 环境因 `CGO_ENABLED=0` 且没有 gcc 而不可用；常规 `go test ./...` 通过。剩余 P1 缺口是 relay 首字节/结束原因、拨号地址族/每候选耗时、请求失败分类和代理 build version；P2 的 DNS 等待者取消、IPv4/IPv6 真实竞速及 relay 10 秒 deadline 仍需聚焦测试后再修改。
