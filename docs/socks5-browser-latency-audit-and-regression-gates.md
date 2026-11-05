# SOCKS5 安全代理浏览器延迟审计与回归门禁

## 结论

仍有优化空间，但“浏览器比直连慢”不能仅凭页面观感归因于代理代码。每次新建 TCP 隧道都至少经过客户端到代理、代理到目标两段连接；代理还承担 DNS、ACL 与中继工作。若代理与浏览器网络路径不同，出口、RTT、丢包和目标站点调度也会主导结果。首先应以同一浏览器配置、同一目标和多轮分位数测量分开比较 DNS、TCP 建连、TLS、首字节、资源总载入时间。

本次只做代码审计与调研，没有修改插件代码或连接远程环境。仓库工作区已有 SOCKS5 插件及其他文件的未提交修改，均予以保留。

## 当前实现与可复用基础

审计对象为 `plugins/socks5-proxy/cmd/socks5-proxy`（安全代理微服务）；仓库另有 `plugins/socks5-forwarder`，用途和代码路径不同，不应混为同一个性能对象。

- SOCKS5 CONNECT 与 HTTP 代理共用目标解析、ACL、出站 TCP 拨号及 `relay`；连接复用通常发生在浏览器与站点的多路请求侧，当前 SOCKS CONNECT 转发本身是原始 TCP 隧道，不会解析或合并浏览器请求。
- 已有 DNS 缓存（LRU / singleflight）、IPv4 优先解析、ACL 前置和解析 IP 子网复核，以及 Happy Eyeballs 式多 IP 连接竞速。多 IP 上限 4、阶梯间隔 120ms。应核对具体 RFC 8305 行为与 IPv4/IPv6 地址排序；“并发竞速”不等于完整 RFC 实现。
- 已有 `optimizeTcpConn`：TCP_NODELAY、TCP keepalive 与 30 秒探测周期；relay 也调用它。因此旧方案中“补 TCP_NODELAY”已不是缺失项。Keepalive 主要发现死连接，不会直接缩短正常页面的首屏时延。
- `relay` 已为 Linux TCP 使用 `io.Copy`（Go 运行时条件满足时可走 `TCPConn.ReadFrom`/splice 优化），并对其它连接使用 `io.CopyBuffer` + `sync.Pool`；半关闭后一侧还设置 10 秒读/写 deadline。性能调优应先验证当前既有路径，特别测试 deadline 是否影响慢速长下载，不应再照旧文档把“加 splice/加半关闭超时”列成待实现项。
- Go 标准库 `net` / `io` 足以支撑当前 TCP 中继；`golang.org/x/net/proxy` 可作 SOCKS 客户端拨号适配器参考，但不是高性能服务端转发器，也不应因“有库”就替换安全 ACL 数据路径。当前 Go 模块没有外部依赖。
- 现有单端口 HTTP/CONNECT 入口可复用，但 HTTP CONNECT 不会让浏览器隧道具备 HTTP/2 上游复用；对普通网页最重要的仍是浏览器自身连接池、TLS 会话恢复、HTTP/2/3 支持和网络路径。

## 主要候选原因（需测量确认）

1. **额外网络跳数与代理出口**：代理转发引入两段网络传输；若代理主机到目标 RTT 较高或出口拥塞，延迟差来自拓扑而非 Go copy 循环。
2. **DNS 冷缓存/缓存策略**：首访可能发生解析等待；热缓存不能代表冷启动。分别记录 resolver 耗时、命中/未命中与目标 TTL，避免用无限缓存破坏 DNS 变更语义。
3. **每个新连接串行成本**：代理每条 CONNECT 在上游建立 TCP 后才回复成功；TLS 随后由浏览器端到站点完成。连接建立耗时直接叠加到新连接首字节前。
4. **QUIC/HTTP/3 路径差异**：当前实现只转发 TCP，SOCKS5 UDP ASSOCIATE 未实现。浏览器是否尝试 QUIC、是否回退及等待多久取决于客户端和网络策略，不能笼统断言“UDP 拒绝必然增加固定延迟”。用浏览器网络日志/抓包确认协议与回退。
5. **转发生命周期/缓冲**：检查 `relay` 的双向复制、半关闭、缓冲预读、取消与连接关闭。TCP_NODELAY 已存在；增加超时或零拷贝不能未经基准就宣称有性能收益，任意固定半关闭超时还可能截断长下载。
6. **目标页自身变量**：缓存命中、广告/第三方资源、服务端负载、浏览器缓存和并发连接数可显著改变页面总时间。直连与代理必须控制这些条件。

## 主流做法与库/基础设施对比

| 方案 | 借鉴点 | 对本服务的适用性与限制 |
|---|---|---|
| Go `net/http.Transport` | 长连接池、每主机空闲连接上限、HTTP/2 能力 | 适用于代理自己发起 HTTP 请求；不能直接替代通用 SOCKS TCP 隧道，也不能在不终止 TLS 的前提下合并浏览器到不同站点的 CONNECT。 |
| Go `golang.org/x/net/proxy` | SOCKS5 客户端拨号、可注入底层 Dialer | 可用于集成/基准客户端或拨号器参考；引入依赖前需验证维护状态、认证、DNS 语义和 ACL 集成。不是服务端性能捷径。 |
| Chromium 代理栈 | 显式代理规则、代理失败回退及协议选择由客户端管理 | 应用 Chromium NetLog 核验代理选择、连接复用、HTTP/3 尝试和回退；不要假设浏览器所有流量都经 SOCKS。 |
| RFC 8305 Happy Eyeballs v2 | IPv4/IPv6 地址排序与交错、按延迟启动尝试、快速失败推进 | 当前代码有多 IP 阶梯拨号，需针对真实双栈和慢地址基准，不应只依据注释称符合 RFC。 |
| SOCKS5 UDP ASSOCIATE / UDP 代理 | 可代理 UDP 流量，为 QUIC 提供可能路径 | 需要 UDP relay、来源地址绑定、ACL、生命周期与反射/放大防护；实施前验证浏览器是否能对该 SOCKS 端点使用 UDP。范围明显大于 TCP 优化，不能作为无条件首选。 |

## 建议方案（先证据，后改造）

### P0：建立可重复基线

使用现有 `tests/benchmark/proxy-web-performance.mjs` 及 `tests/benchmark/proxy-vs-direct-comparison.mjs`，核对脚本参数和测量定义；建议至少记录 30 次冷连接和热连接的 p50/p95：DNS、代理握手、上游 TCP 建连、TLS、TTFB、页面总载入、成功率。当前浏览器脚本默认每模式 5 次冷启动，微基准默认 30 轮；均可通过 `BENCH_BROWSER_SAMPLES` / `BENCH_SAMPLES` 调整。使用同一客户端、同一目标、相同缓存策略与时间窗口，分别测试 IPv4/IPv6、IP/域名、冷/热 DNS、单站点/多资源页面。输出不含凭据与私有目标的汇总数据。

### P1：补齐分阶段可观测性

对每条连接采集低开销阶段耗时：代理握手、ACL、DNS（命中标志及耗时）、各 IP 拨号尝试及胜出族、上游建连、relay 首字节和字节数；聚合直方图而非每包日志。日志不得输出代理密码、完整认证头或敏感 URL 查询参数。以测量结果决定优化层，不先加复杂缓存或调度。

### P2：按证据逐项优化

- 若冷 DNS 占比高：复核缓存 TTL、负缓存和并发击穿；再评估软过期预刷新，确保 ACL 对解析出的每个 IP 仍复核。
- 若建连慢：记录代理到目的地址 RTT/失败分布；验证 Happy Eyeballs 地址排序、间隔和取消行为，按实测调节，不增加无边界并发。
- 若 TTFB/吞吐受中继影响：基于长流和短请求验证当前 Linux `io.Copy`/非 Linux `io.CopyBuffer`+pool 的吞吐、CPU、分配量及 10 秒半关闭 deadline；只有确认当前路径有缺陷或存在稳定统计收益，才调整缓冲参数或实现替代策略。
- 若确认 HTTP/3 回退成本：先验证浏览器日志与网络策略；再独立设计 SOCKS UDP ASSOCIATE/QUIC 支持及安全审查。否则将其列为明确能力限制，不将未经确认的回退当根因。
- 若页面资源并发/重复 TLS 建连占主导：检查客户端连接池和代理端连接关闭行为。通用 TCP 隧道不能安全地代替浏览器 HTTP/2 多路复用。

## 防回归门禁

1. **协议与安全**：现有 SOCKS 握手、用户名密码、域名 ATYP、ACL 域名/IP 复核、DNS rebinding 防护测试持续通过；禁止优化路径绕过 ACL。
2. **中继正确性**：双向大流量校验内容哈希；半关闭后仍可接收对向数据；异常断开后连接与 goroutine 回收；缓冲预读字节不丢失。
3. **DNS**：冷/热命中、singleflight 并发合并、正/负 TTL 到期、解析结果变化、超时与取消、ACL 拒绝均有行为测试。
4. **连接竞速**：慢 IPv6/快 IPv4、快 IPv6/慢 IPv4、失败后推进、父 context 取消、仅一个赢家且其他成功连接关闭；确保赢家连接不因竞速取消而被误关闭。
5. **性能基线**：基准报告固定运行环境/版本与数据集；比较 p50/p95、吞吐、CPU、分配量、连接成功率。门禁先设宽松告警阈值，重复多轮确认稳定后才升级为阻断阈值，避免噪声导致误报。
6. **真实浏览器验收**：使用 Chromium NetLog 或开发者工具核对代理实际使用、TCP/QUIC 协议、重试/回退、连接复用；至少对一个 HTTP/2 与一个可用 HTTP/3 站点做重复对照。

建议把协议正确性与 ACL 门禁设为硬阻断；性能以同环境 p95 回归阈值作为告警起点，建立稳定基线后再阻断。当前仓库已有 `tests/benchmark/proxy-web-performance.mjs`、`proxy-vs-direct-comparison.mjs` 等脚本候选，需先审查其实现和输出定义，再复用而非另造重复基准。

## 资料

- [RFC 1928: SOCKS Protocol Version 5](https://www.rfc-editor.org/rfc/rfc1928)
- [RFC 8305: Happy Eyeballs Version 2](https://www.rfc-editor.org/rfc/rfc8305)
- [Go `net/http` Transport 文档](https://pkg.go.dev/net/http#Transport)
- [Go `golang.org/x/net/proxy` 文档](https://pkg.go.dev/golang.org/x/net/proxy)
- [Chromium Proxy Support](https://chromium.googlesource.com/chromium/src/net/+/main/docs/proxy.md)

以上资料说明协议与 API 能力，不构成当前部署性能结论；性能结论必须由同环境测量得出。
