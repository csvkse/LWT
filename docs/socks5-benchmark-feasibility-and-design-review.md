# SOCKS5 基准现状、可行性与设计审查

## 范围与结论

本文核对安全代理微服务 `plugins/socks5-proxy`、`tests/benchmark` 中现有脚本，以及既有代理性能调研。结论：基准脚本确实存在，但只能提供初步诊断；目前的采样和指标不足以支撑此前文档中较强的 SLA 结论，也不能单独证明“浏览器慢”的根因。可行路径是先修正测量方法，再按证据优化数据面。本文是静态审查，没有执行远程基准。

## 脚本现状与调研文档差异

| 文件/说法 | 实际实现 | 差异与影响 |
|---|---|---|
| `tests/benchmark/proxy-web-performance.mjs` | 每模式重复冷启动 Chrome 导航；报告 p50、请求失败计数、HTTP 状态合计和运行环境；默认每模式 5 次，可配置。 | 仍未控制代理 DNS 冷/热缓存，失败/状态按跨样本聚合；`networkidle` 受后台请求影响；目标默认内网登录页。脚本名没有 `.test` 后缀。 |
| `tests/benchmark/proxy-vs-direct-comparison.mjs` | 含直连 TCP、HTTP CONNECT、SOCKS5、SOCKS5h、HTTP TTFB 和浏览器页面测量；计算样本统计。 | 浏览器页面测量仍是单次；脚本的“全维度”描述不能替代实际记录的维度。原始协议握手测时包含客户端到代理、协议协商、代理 DNS/上游 connect，不能称为纯协议握手开销。 |
| `tests/benchmark/proxy-benchmark.mjs` | 默认 3 次预热、30 次采样；按协议分片安全读取回复；支持 `SOCKS5_USERNAME/PASSWORD` RFC 1929 认证，随机交错场景并报告成功数与 p50/p90/p99。 | 吞吐函数仍未纳入主流程；解析阶段耗时未由此脚本直接分离，分位数小样本仅作诊断。 |
| 既有调研 `docs/network-proxy-performance-testing-and-optimization-research.md` | 描述 3ms 握手、1ms DNS 缓存、吞吐衰减 <15% 等门禁，并提及浏览器评测脚本。 | 这些阈值不能视为已由可靠样本验证；当前浏览器脚本没有可重复的统计门禁。旧文档将 NODELAY 与 splice 等优化描述为收益，也需由实测确认。 |
| 既有文档 `docs/socks5-browser-latency-audit-and-regression-gates.md` | 建议至少 30 轮冷/热样本、p50/p95、按阶段观测，并引用浏览器脚本。 | 文档原先引用了不存在的 `proxy-web-performance.test.mjs`，实际文件是 `proxy-web-performance.mjs`。现浏览器脚本已支持每模式重复冷启动采样（默认 5 次，可用 `BENCH_BROWSER_SAMPLES` 调整）并汇总 p50；默认仍不是 30 次。 |

另有 `tests/benchmark/test-cdn-multihoming.mjs`，适合检查特定多地址场景，不等同于浏览器端到端性能基准。

## 当前系统已有能力

- 安全代理 Go 模块 `plugins/socks5-proxy/go.mod` 仅声明 Go 1.24，没有第三方模块依赖。
- 代码已有 DNS 缓存与 singleflight、IPv4 优先解析、域名 ACL 与解析 IP ACL 复核、Happy-Eyeballs 式多 IP 拨号、TCP_NODELAY/KeepAlive，以及 SOCKS5/HTTP 转发共用的连接中继路径。应继续复用这些能力，先补诊断维度和聚焦测试。
- 中继已有 Linux TCP `io.Copy` 路径（满足 Go 运行时条件时可走 `TCPConn.ReadFrom`/splice 优化），非该路径使用 `io.CopyBuffer` 与 `sync.Pool`。一侧结束后还有 10 秒读写 deadline 做排空保护；再次实现 splice 或增加长流 timeout 前，先验证这条现有路径，尤其检查慢速长下载是否会被排空 deadline 提前截断。
- 现有基准依赖仓库 `tests/smoke/node_modules/@playwright/test` 中的 Playwright/Chromium；可用于真实浏览器对照。浏览器代理支持、QUIC 选择和回退仍须由 NetLog/抓包验证，不能从页面耗时推断。
- TCP CONNECT 是不解密的字节流隧道。代理不能在不终止 TLS 的情况下合并不同浏览器请求为 HTTP/2 多路复用；连接池首先属于浏览器到站点的行为。
- 插件支持 `none` 和 RFC 1929 用户名密码认证；当前原始 SOCKS 基准探针只提供 `NO AUTH`。应让探针支持与目标运行配置匹配的 auth mode，并只从本机环境变量/被忽略的 `env/.envdev` 读取凭据，不能打印凭据。浏览器脚本的 Playwright proxy 参数也需在启用代理认证时设置 credentials。

## 性能原因假设与可验证性

将用户可见延迟拆为：浏览器到代理连接与握手、代理 DNS、代理到目标 TCP 建连、TLS 握手、首字节、响应传输和页面资源瀑布。代理增加网络路径和处理步骤，但具体谁占主导需测量。

1. **拓扑与出口 RTT**：对同一目标同时测客户端直连 RTT、代理主机到目标 connect RTT，并记录丢包/失败；两段路径不同，单纯比较总时间无法归因代码。
2. **DNS 冷/热差异**：基准需控制代理进程缓存状态并记录命中/解析耗时；SOCKS5h 请求完成时间减 SOCKS5(IP) 请求时间不是纯 DNS 耗时，因为地址、目标及服务端路径也可能不同。
3. **上游连接**：记录代理收到 CONNECT 至上游 connect 成功的阶段延迟和失败率；当前端到端 CONNECT 总时间混合了客户端到代理 RTT、握手与上游建连。
4. **浏览器协议和连接复用**：用 Chromium NetLog/开发者工具确认请求是否走代理、HTTP/2/HTTP/3、连接复用、失败重试；当前仅查看 Navigation Timing 不足以确认 QUIC 回退。
5. **中继数据面**：使用固定大小 payload 的本地受控目标，比较吞吐、CPU、内存分配和短流 p95；不可用 loopback 结果代替真实网络结论。

## 设计模式与具体落地

采用适量抽象，不先重写代理架构：

- **适配器**：为 DNS Resolver、Dialer、Clock/测量记录器提供小接口或函数注入，使测试能构造冷/热解析、慢地址、快速失败和取消场景。生产仍适配现有标准库实现。
- **策略**：保留解析策略（IPv4 优先/系统默认）、连接竞速策略、传输策略（当前 `io.Copy`；只有 Linux 基准证明收益后才考虑 splice）。每个策略都经过相同 ACL 与协议验证边界，禁止优化分支绕过授权检查。
- **状态机**：把连接生命周期明确为 Accepted → Handshake → Auth/ACL → Resolve → Dial → Relay → Closing → Closed，用于定义超时、取消、计数器和资源回收不变量。仅当状态能驱动观测或减少分支错误时才实现显式状态对象；简单线性控制流不必引入通用状态机库。
- **基准场景策略**：把目标、协议、缓存状态、样本数、预热数、并发度和超时作为场景配置，公共采样器输出 JSON/CSV；浏览器与原始 socket 测量保留不同指标定义，不能混成一个分数。

推荐阶段：P0 修正脚本错误与指标定义；P1 增加多轮、随机/交错场景顺序、成功率、p50/p95 和机器/代理版本元信息；P2 加入阶段耗时观测与真实浏览器 NetLog；P3 再根据证据试验连接竞速参数、缓存策略、缓冲池或 Linux splice。性能阻断门槛应先基于同一稳定环境建立基线，采用多次运行的 p95/置信区间或相对回归幅度，避免任意固定毫秒值造成噪声误报。

### 执行记录（2026-11-05）

- 已更新 `proxy-benchmark.mjs` 的 CONNECT 探针、样本默认值与报告口径；移除了未经同环境校准的固定 SLA 判定。TCP 样例现按协议完整读取响应，CONNECT 用时明确是端到端建隧道时间。
- 已更新 `proxy-web-performance.mjs`：每模式默认 5 次冷启动浏览器采样并报告中位数，失败样本单独计数，不再用单次 200ms 差值作门禁。
- 修复协议读取器时发现一个真实探针缺陷：一次 TCP `data` 事件可能带有完整 SOCKS 回复，旧版分段读取丢弃了当前字段之后的剩余字节，导致第二段读取永远等不到数据。增加每连接剩余字节缓冲后，热更新后最终 `BENCH_SAMPLES=5` 实测 Direct TCP 5/5、SOCKS5 5/5、SOCKS5h 5/5、HTTP CONNECT 5/5；CONNECT p50 分别约 0.67、1.90、1.89、1.39ms。HTTP TTFB 5 轮均成功，均值 2.44ms vs 4.43ms（代理差 +1.99ms）。这是本次目标和时间窗口下的短样本诊断值，不是一般化结论。
- `BENCH_BROWSER_SAMPLES=5 node tests/benchmark/proxy-web-performance.mjs` 随机交错运行后，三种模式均 5/5 成功，失败请求均为 0，状态为每模式 110 个 200 与 5 个 302（跨五次累计）。最终一轮 DOMContentLoaded p50：直连 239.9ms、HTTP 代理 246.5ms、SOCKS5 代理 245.5ms；Load p50：240.9、247.6、246.4ms。此前几轮直连 Load p50 曾在约 237.8–298.9ms 之间波动，环境方差已接近或超过代理差值。URL 使用 IP，因此浏览器 DNS 时间为 0ms；冷启动浏览器不代表代理 DNS 冷缓存。样本仅 5 个，不能据此认定某一种代理更快。
- 尚未实现 SOCKS5 RFC 1929 认证探针、浏览器失败请求计数、单请求内部阶段观测、NetLog 自动采集、吞吐/CPU 基准和正式阻断门禁；这些需按后续 P1/P2/P3 有序补齐。

## 库与业界方案对比

| 项目 | 能借鉴什么 | 是否建议直接引入 |
|---|---|---|
| Go `net`、`io`、`net/http/httptest` | 连接、拷贝、HTTP 受控测试服务；当前中继已可由标准库实现。 | 是，优先复用，无额外依赖。 |
| Go `net/http/httptrace` | 对 HTTP 客户端 DNS、连接复用、connect、TLS、首字节等阶段打点。 | 可用于 HTTP 基准客户端；不能观测不经过该客户端的通用 SOCKS 隧道内部阶段。 |
| `golang.org/x/net/proxy` | SOCKS5 客户端 Dialer 参考与基准工具。 | 不替换服务端；若仅用于 Node/Go 基准，可先用现有手写客户端并修好协议读取，再评估引入。 |
| Playwright + Chromium NetLog | 浏览器真实导航、请求生命周期和代理/协议诊断。 | 仓库已有 Playwright，优先复用；NetLog 用于诊断，输出注意脱敏。 |
| RFC 8305 Happy Eyeballs v2 | IPv4/IPv6 候选排序、交错拨号、延迟与取消语义。 | 现有拨号竞速可测试并对齐；不能仅凭“120ms stagger”就宣称完整实现 RFC。 |
| Squid / Envoy 等通用代理 | 连接池、指标、超时与成熟运维观测；Envoy 主要是 L7/L4 代理能力组合。 | 可比较功能与观测模型；替换当前轻量 SOCKS 微服务成本大，且不能直接解决出口 RTT。 |
| SOCKS5 UDP ASSOCIATE | UDP 转发能力，潜在支持客户端 UDP/QUIC。 | 不是默认优化项；需先证明浏览器会使用、当前缺失导致回退，再设计 UDP relay、ACL、源地址绑定和资源限制。 |

标准与一手资料：

- [RFC 1928 SOCKS Protocol Version 5](https://www.rfc-editor.org/rfc/rfc1928)
- [RFC 8305 Happy Eyeballs Version 2](https://www.rfc-editor.org/rfc/rfc8305)
- [Go `net/http/httptrace`](https://pkg.go.dev/net/http/httptrace)
- [Go `net/http.Transport`](https://pkg.go.dev/net/http#Transport)
- [Go `golang.org/x/net/proxy`](https://pkg.go.dev/golang.org/x/net/proxy)
- [Chromium Proxy Support](https://chromium.googlesource.com/chromium/src/net/+/main/docs/proxy.md)
- [Envoy Architecture Overview](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/arch_overview)
- [Squid Wiki](https://wiki.squid-cache.org/)

## 建议的回归门禁

### 协议与安全（硬门禁）

- SOCKS5 握手分片/合并读取、认证协商、IPv4/IPv6/域名 ATYP、CONNECT 成功与错误回复。
- ACL 域名和解析 IP 双重校验、DNS rebinding、超时与取消；任何策略优化不得跳过 ACL。
- 连接竞速保证仅一个赢家、失败及时推进、父 context 取消后全部退出，未胜连接关闭且赢家存活。
- relay 双向校验内容哈希、半关闭继续传输对向数据、异常断开清理连接/goroutine、预读数据无丢失。

### 性能（先告警再阻断）

- 分离冷 DNS、热 DNS、IP 直连、域名 SOCKS5h；每场景预热与正式样本分开，至少 30 次只是初步建议，正式样本数根据波动/置信区间确定。
- 交错或随机执行 Direct/HTTP/SOCKS 场景，报告成功率、p50/p95、错误分类；样本失败不得静默丢弃。
- 页面基准报告浏览器版本、代理版本、目标、缓存条件、并发和协议；分别记录 FCP、LCP（若可得）、DOMContentLoaded、load、请求数与失败请求，不把 networkidle 单独当页面完成标准。
- 长流吞吐与短流延迟分别测，报告 CPU/内存；只有在固定基准环境多轮稳定回归时才设置阻断阈值。

## 最终判断

测试脚本“存在”，但此前调研与脚本之间有三类差异：对实际脚本文件名引用错误；将单次浏览器采样描述成可建立稳定性能门禁；以及将混合测量值解释为纯握手/DNS开销。当前可复用 Playwright、Go 标准库、DNS/ACL/拨号/中继能力，不需要先引入服务端代理库。优先修复基准采样和阶段观测，再决定优化策略。本文没有对脚本、服务或远程主机进行运行验证。
