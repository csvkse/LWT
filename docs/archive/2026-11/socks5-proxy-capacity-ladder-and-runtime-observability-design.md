# SOCKS5 代理高并发容量阶梯与运行时观测方案

> 日期：2026-11-05  
> 类型：实现审查、执行记录与后续验证门禁。第 9 节记录本轮已执行的 Linux 连接阶梯、500 路长时双向 tunnel、浏览器多标签与剩余覆盖范围。

## 1. 结论

当前代理已经具备全局会话准入、HTTP 上游 active/pending 上限、排队超时、连接拒绝计数和 DNS/拨号耗时累计值。本轮已在隔离 Linux 开发实例完成 50/100/500 真实 socket 阶梯、两种协议各 500 路 5 分钟双向 tunnel、资源采样和 1/5/20 标签浏览器测试；结果与限制见第 9 节。这些结果仍不能代表三次重复的稳定 SLA、资源受限 cgroup 下的尾延迟或任意公网网站体验。

优先级建议：

1. **Linux 真实连接基线和轻量资源采样已完成首轮。** 下一步应补重复样本、profile 与受限 cgroup 对照；当前数据仍不足以判断事件循环或手写 HTTP parser 是否值得承担语义回归成本。
2. **保留 `net/http.Server` 的 HTTP 取消语义。** `singleConnListener` 使标准库每个入站 HTTP 连接多一个连接服务 goroutine；这是有界、可 profile 的代价。它不是多标签共享单线程。一个活跃 tunnel 还会有两条双向 relay copy goroutine；HTTP CONNECT 至少比纯 SOCKS5 relay 多一个服务 goroutine，具体总数要用运行时 profile 测量。
3. **先使用 Go 标准库和 Linux 只读观测。** 第一阶段用 `runtime/metrics`、`net/http/pprof`、`/proc`、cgroup v2 和现有 `/api/status`，不新增生产依赖。确认 Prometheus/OpenTelemetry 有持续采集需求后再选 exporter。
4. **500 是接近当前默认会话上限的容量点。** 默认 `SOCKS5_MAX_ACTIVE_SESSIONS=512`，500 只留 12 个会话余量；测试要同时观察拒绝、文件描述符、目标端连接和部署容器的资源，而不能只看客户端数量。

## 2. 仓库和 Linux 开发环境现状

| 检查项 | 已核实情况 | 证据与边界 |
|---|---|---|
| 全局会话准入 | 默认上限 512，可由 `SOCKS5_MAX_ACTIVE_SESSIONS` 配置；状态接口暴露 active/peak/rejected/limit。 | `plugins/socks5-proxy/cmd/socks5-proxy/admission.go`、`main.go`、README。 |
| HTTP 上游准入 | 有每目标 active/pending、拒绝数、排队超时数；排队 context 可取消。 | `http_admission.go`、`http_server.go`、`/api/status`。 |
| 当前运行观测 | `/api/status` 暴露连接、字节、ACL、DNS cache、DNS resolve/upstream dial 的 count/total/max，并新增 Go runtime goroutine、GOMAXPROCS、heap/GC 快照；metrics server 绑定 `127.0.0.1`。 | `main.go` 的 `startMetricsHttp`、`runtime_snapshot.go`；仍没有 pprof 或延迟直方图。RSS、FD、进程 limits 和 cgroup 数据由压测 sampler 读取，不由每次 status 请求扫描；DNS/dial 统计也不是 p95/p99。 |
| 现有浏览器负载脚本 | `tests/benchmark/proxy-multitab-performance.mjs` 默认标签数为 1/5/20，最大支持 128；测页面导航、TTFB、load、失败请求。 | 它验证浏览器场景，不是可保持 500 条 SOCKS5/CONNECT tunnel 的连接压测器，也没有资源采样。 |
| 现有并发单测 | 准入器以 128 个竞争者争抢 8 个 permit；HTTP 限流测试以 4 个请求验证 2 个 active。 | `concurrency_test.go`、`http_concurrency_test.go`；测试使用 `net.Pipe`/本地 server，不代表真实 Linux socket 与容器容量。 |
| SOCKS5 模块依赖 | `plugins/socks5-proxy/go.mod` 只有模块声明和 `go 1.24`，暂无第三方运行依赖。 | 高并发观测第一阶段可以保持零新增依赖。 |
| 相邻插件依赖 | `plugins/mcp-bridge/go.mod` 的依赖图含 Prometheus、OpenTelemetry、`prometheus/procfs`，但它们是 indirect；在该插件源码中没有找到对应直接 import/collector 使用。 | 两个插件是独立 Go module，不能因为另一个 module 的 `go.sum` 存在就视作 SOCKS5 已有可复用运行基础设施。 |
| 开发容器资源边界 | `docker inspect` 显示 Memory=0、NanoCpus=0、PidsLimit 未设置；容器内 cgroup v2 显示 `cpu.max=max`、`memory.max=max`、`pids.max=4641`，本轮 soak CPU throttling 与 OOM 均为 0。代理进程 `/proc/<pid>/limits` 的 nofile soft/hard 均为 1,048,576。 | 这是共享开发容器/cgroup 快照，不是代理进程的单独用量，也不代表生产限制。`memory.current` 是容器 cgroup 口径；运行中和结束后的代理 RSS 见第 9 节。 |

以上资源快照说明当前开发容器没有显式 CPU/内存 quota，适合验证协议和调度正确性，却不能作为受限容器容量证明。不要以宿主 `/proc/1` 代替插件进程观测；开发容器使用 `--pid=host`，PID 1 可能属于宿主命名空间。优先采集代理 PID 的 `/proc/<pid>/...`，cgroup 文件则需通过 `/proc/<pid>/cgroup` 和 mountinfo 解析对应层级。

## 3. 当前逻辑和性能风险

### 3.1 多标签不是单线程竞争问题

Go runtime 将大量 goroutine 调度到少量 OS 线程；多标签共用的是代理进程、准入器和连接池，并不意味着所有请求由一个线程串行处理。当前需实测的共享争用点是：全局准入短临界区、每 host 的 FIFO active/pending limiter、HTTP Transport 连接池、DNS cache/ACL 快照，以及主机 CPU/网络和 FD 上限。并发锁正确性已有单测与 Linux race 结果，但吞吐和 P99 仍需独立测量。

### 3.2 HTTP 连接适配的 goroutine 成本

生产 HTTP 路径由 `handleConn` 的会话 goroutine 调用 `http.Server.Serve(singleConnListener)`；`net/http` 为该入站连接启动连接服务 goroutine。HTTP keep-alive 请求复用该连接服务 goroutine，不是每条请求再增加一个连接服务 goroutine。CONNECT hijack 后仍由现有 relay 复制上下行数据。应比较同一 workload 下的 `/sched/goroutines:goroutines`、heap、CPU、FD 和 p95/p99，再判断多一层是否值得优化。

在引入 `net/http.Server` 之前，手动 `http.ReadRequest` 路径曾缺少入站断开到上游 `Request.Context` 的取消传播；已新增的 active 请求断连测试正是保护该行为。退回手动解析会重新承担 keep-alive、请求取消、pipelining、认证、超时、hop-by-hop header 和 CONNECT 缓冲字节等状态管理。除非 profile 显示额外 goroutine 已成为容量瓶颈，并且候选实现完整通过这些回归门禁，否则不建议为了少一个 goroutine 牺牲标准库生命周期语义。

### 3.3 FD、内存与 goroutine 的容量账本

- 一条建立后的 tunnel 通常至少涉及客户端和上游两个 socket FD；再加监听 socket、metrics listener、resolver/HTTP idle pool 等。500 条活跃 tunnel 的连接 FD 粗略量级可达到约 1,000，但 HTTP keep-alive/上游连接池会改变实际比例，必须采样代理进程的 FD 数。
- 纯 tunnel relay 有两条方向 copy goroutine；HTTP 适配路径另外存在 `net/http` 连接服务 goroutine。goroutine 数不能换算成 Linux 线程数；同时要看 pprof 栈、堆和 `pids.current`，后者是 cgroup task 口径。
- 代码从 `sync.Pool` 取 32 KiB buffer 后调用 `io.CopyBuffer`，但 Go 的 `io.CopyBuffer` 在 src 实现 `WriterTo` 或 dst 实现 `ReaderFrom` 时可以走快速路径、不使用传入 buffer。标准 TCP 连接可能命中这些接口，所以不能直接把 `2 × 32 KiB × 500` 当成实际常驻内存；用 allocation/heap profile 验证 pool 是否实际减少分配。
- `runtime/metrics` 的 Go heap 与进程 RSS、cgroup memory 不是同一个口径。`runtime.MemStats`/runtime metrics 用于 Go 管理堆；`/proc/<pid>/status` 或 `smaps_rollup` 看进程 RSS/PSS；`memory.current` 看 cgroup 及其后代总用量。
- 当前 `go.mod` 为 `go 1.24`。即便 Linux 构建工具链是 Go 1.25+，运行时 container-aware `GOMAXPROCS` 的兼容默认也可能仍按 go directive 1.24 关闭。应记录 `runtime.Version()`、`runtime.GOMAXPROCS(0)`、`GOMAXPROCS`/`GODEBUG` 和有效 cgroup CPU quota，再决定要不要升级 go directive 或引入 `automaxprocs`；不要在没有 CPU quota 观测的情况下盲设并行度。参见 Go 的 [GOMAXPROCS 文档](https://pkg.go.dev/runtime#GOMAXPROCS) 与 [Go 1.25 release notes](https://go.dev/doc/go1.25)。

### 3.4 长时 tunnel 的语义和公平性

SOCKS5/CONNECT tunnel 通常需要允许较长时间无数据；当前会话上限能限制总量，但每条 idle tunnel 仍占用会话 permit、socket、goroutine 和内核缓冲区，长时间占满后会挤压新连接。首轮验证应记录 idle tunnel 的稳定资源成本和断开后的回收时间，不应擅自添加默认 idle timeout（可能破坏长连接应用）。如果产品后续确实要求控制长期占用，再增加可配置、默认兼容现状的 idle policy，并单独验证半关闭和低速应用。

## 4. 可执行的 Linux 连接阶梯

### 4.1 场景

压测目标应是隔离的 `linuxwebtool-dev` 和本地可控的回环上游，不触碰生产实例；上游只提供确定性 echo/慢响应，不访问公网业务。客户端负载产生器使用仓库新增的 Go 标准库实现，确保 SOCKS5 握手、HTTP CONNECT 和客户端关闭行为可控。浏览器 Playwright 脚本保留为页面级体验补充。

| 场景 | 主要覆盖 | 负载保持方式 |
|---|---|---|
| SOCKS5 CONNECT idle | 握手后保持空闲、空闲资源成本、client close 回收 | 建立后不发应用数据，按时关闭。 |
| SOCKS5 CONNECT duplex | relay 吞吐、方向字节、半关闭/RST | 固定大小可复核 payload 双向传输，记录 bytes 和 checksum。 |
| HTTP CONNECT idle/duplex | HTTP server adapter、Hijack、预读字节保留、raw relay | 分别保持 idle 与双向 payload；第一批 tunnel bytes 带唯一序号校验。 |
| HTTP forward active request | `req.Context` 取消、per-host active/pending、排队超时 | 本地上游分别模拟快响应、慢响应头和慢 body；客户端中途断开。 |
| 浏览器多标签 | 真实页面的 TTFB/load/请求失败 | 继续使用现有 1/5/20 标签脚本；不要把标签数映射成 tunnel 数。 |

### 4.2 阶梯和时长

1. 先用 10 条连接确认握手、target 可达、日志无凭据、collector 与断开回收正常。
2. 对 50、100、500 分别运行 idle tunnel 与 full-duplex tunnel：30 秒 warm-up、120 秒稳定采样、60 秒 drain，每档至少 3 次。记录准确二进制 SHA256、Go 版本、配置值、主机负载和采样时间。
3. 阶梯稳定后做 soak：50/100 条 idle 和 duplex tunnel 各 15 分钟；500 条先做 5 分钟保护性 soak。若 FD/RSS/goroutine 曲线持续上升或宿主负载异常，立即停止，不向生产扩展。
4. 额外执行超过准入上限的隔离用例：用较小可控 limit 做网络集成验证；已有 permit 单测保留精确 `limit` 和 `rejected` 计数断言。生产默认 512 的 513+ 压测仅在隔离容器资源已确认、客户端具备立即停止机制时执行。
5. 测试期间关闭一部分客户端、RST、半关闭写端并完整停止全部客户端；检查 active/pending 回零、FD/goroutine 回落和 cgroup 指标停止增长。

每轮均以固定连接建立速率启动，不要瞬间创建 500 个本地 socket 造成压测端自身先耗尽端口/FD。客户端和目标端也需采集 CPU、RSS、FD、失败率，确保测得的是代理瓶颈而非 load generator 或目标 server。

### 4.3 采样指标

每 1–5 秒写一行 JSONL/CSV，至少包括：

| 范围 | 必采指标 | 推荐来源 |
|---|---|---|
| 代理业务 | active/peak/rejected/total sessions；HTTP active/pending/rejected/timeouts；rx/tx bytes；DNS/dial count/total/max；连接成功/失败/超时/取消数 | 现有 `/api/status` + 压测客户端统计。 |
| Go runtime | goroutines；heap live/alloc/objects；GC 次数、pause/CPU；GOMAXPROCS | `runtime/metrics`，低频快照；profile 时再采样。 |
| 进程/Linux | `/proc/<pid>/fd` 个数；RSS；线程数；soft/hard `Max open files`；代理 PID 的 cgroup 相对路径 | `/proc/<pid>/fd`、`status`、`limits`、`cgroup`。 |
| cgroup v2 | `cpu.max`、`cpu.stat` 的 `nr_throttled`/`throttled_usec` 增量；`memory.current/max`；`pids.current/max`；可用时 OOM/events | 找到代理所属 cgroup 后读取控制器文件；数据必须注明 process/container/host scope。 |
| 用户体验 | 建连/握手、首字节、请求完成时间的 p50/p95/p99；吞吐和错误率 | 负载发生器端单调时钟；按场景单独聚合。 |

不要把目标 hostname、原始 URL、用户、客户端地址作为指标 label；高基数标签会增加自身内存与后端开销，也可能暴露用户信息。 `/api/status` 应提供快照而不是在每次请求路径扫描整个 `/proc`；Linux 资源 collector 建议独立每 5 秒采样、缓存结果并记录 `unsupported/error`，不能将缺指标误报成 0。

## 5. 诊断、库和业界方案对比

| 方案 | 优点 | 成本/风险 | 适用建议 |
|---|---|---|---|
| Go `runtime/metrics`、`runtime/pprof`、`net/http/pprof` | 标准库；可观测 goroutine、heap、GC；goroutine/heap/CPU/block 等 profile 可回答额外 goroutine 和分配热点。 | pprof 数据敏感且会消耗 CPU/内存；不能无保护地暴露到公网。 | 第一阶段采用；metrics 定时采样，pprof 仅在 Linux 开发容器显式启用且绑定 loopback/临时诊断端口。用自建 `ServeMux`，避免默认注册器意外暴露。见 [runtime/metrics](https://pkg.go.dev/runtime/metrics)、[net/http/pprof](https://pkg.go.dev/net/http/pprof)、[Go diagnostics](https://go.dev/doc/diagnostics)。 |
| `github.com/prometheus/client_golang` | Prometheus Go 常用 SDK；可把现有计数器、直方图接入 scrape。 | 增加生产依赖、registry/exporter/安全和 cardinality 管理；当前 SOCKS5 module 没有该依赖。 | 若系统已有 Prometheus scrape 目标、告警和仪表盘，再引入；否则先用本机 JSONL/现有状态快照。见 [client_golang](https://github.com/prometheus/client_golang)。 |
| `github.com/prometheus/procfs` | 复用 Linux `/proc` 的解析器，避免自己解析格式。 | SOCKS5 module 当前无此依赖；procfs/cgroup mount 位置、PID namespace 与权限仍需正确处理；这里只需要少数字段时引入可能过重。 | 如果后续要兼容多种 Linux procfs 格式或实现 Prometheus collector，再评估；当前优先直接用小范围、受测的标准库读取。见 [prometheus/procfs](https://github.com/prometheus/procfs)。 |
| OpenTelemetry Go Metrics/Exporter | 统一 traces/metrics，并接 Collector/OTLP 生态。 | 需要 Collector/exporter/配置和上下文传播；对当前容量基线不是前置条件。 | 只有主系统确定已有 OTel collector 及统一 telemetry 约定时采用；`mcp-bridge` 的间接依赖不构成 SOCKS5 可复用实现。见 [OpenTelemetry Go instrumentation](https://opentelemetry.io/docs/languages/go/instrumentation/)。 |
| `go.uber.org/automaxprocs` | 为旧兼容默认提供 Linux cgroup CPU quota 到 `GOMAXPROCS` 的桥接。 | 只解决并行度默认值，不解决连接上限、FD、内存、调度和排队；若 Go module 升到新版语义可能变成重复设置。 | 先根据 `go.mod go 1.24`、有效 CPU quota 和 `runtime.GOMAXPROCS(0)` 做有/无库对照；不能仅凭 Go 1.26 工具链认定新默认生效。 |
| Envoy circuit breakers/connection pools | 同时限制 upstream active connections、pending requests、requests，并提供 overflow stats；worker 共享有界资源。 | 完整 Envoy 作为 sidecar/代理会大幅增加部署和配置复杂度。 | 借鉴“active + pending + overflow 计数 + 可观测性”的边界；项目已有 per-host limiter 和拒绝/超时指标，不迁移整套代理。见 [Envoy circuit breaking](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/circuit_breaking.html) 与 [connection pooling](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/connection_pooling.html)。 |
| NGINX/HAProxy worker/FD 管理 | `worker_connections`、`maxconn`、backlog 和 nofile 共同构成容量预算，连接数不是孤立配置。 | 不同架构的 worker 与连接语义不能直接搬到 Go goroutine 模型；修改宿主 nofile/sysctl 影响其他服务。 | 借鉴完整资源预算和溢出观察，不直接照抄数值或修改共享宿主设置。见 [NGINX worker_connections](https://nginx.org/en/docs/ngx_core_module.html#worker_connections)、[HAProxy maxconn](https://docs.haproxy.org/3.4/configuration.html#4.2-maxconn)。 |
| `gnet` 等 epoll/event-loop 框架 | 可减少部分 goroutine/连接调度开销，适合经 profile 确认的极高连接/高吞吐场景。 | 与当前阻塞式 `net.Conn`、`ReverseProxy`、HTTP cancellation、ACL/DNS 插点和 half-close 语义不直接兼容；需要重写和重新验证。 | 不是首选优化。只有 Linux profile 显示调度/堆/CPU 成为主因，且 500 阶梯已证明瓶颈后，才制作独立原型做 A/B。 |

### 当前可复用基础设施

- 连接与 HTTP admission、atomic 业务计数器、阶段耗时累计、`/api/status`、DNS cache/singleflight、共享 `http.Transport`、`ReverseProxy` 和 relay tests 已存在。
- Go 标准库已经提供 Go runtime 指标和 pprof；Linux kernel 提供 procfs/cgroup v2 指标。它们不需要主系统 C# 监控模块改造即可用于插件级诊断。
- `plugins/mcp-bridge` 的 Prometheus/OTel/procfs 仅是其 module dependency graph 中的 indirect 项；插件独立构建，且未发现其源码实际注册对应 metrics exporter。应避免依赖另一个插件的 `go.sum`，否则形成隐式版本/构建耦合。
- 主系统已有容器检测（Docker 与 `/proc/1/cgroup`）能力，但它服务于宿主环境识别，不等价于 SOCKS5 Go 进程的 `runtime/metrics`、FD 或 cgroup 资源采样。

## 6. 设计模式建议

| 模式 | 可落位置 | 是否建议现在引入 | 约束 |
|---|---|---|---|
| Adapter | 压测端提供 `SOCKS5Connector`、`HTTPConnectConnector`，统一返回 `net.Conn`，把协议握手和 tunnel 场景解耦。生产侧 Linux `ProcCgroupSnapshotProvider` 适配 procfs/cgroup，Go runtime provider 适配 runtime API。 | **建议用于测试工具和观测边界。** | 只抽象目前确实存在的两种协议/数据来源，不建立空泛网络框架。Linux 不支持时返回明确 unsupported。 |
| Strategy | 压测场景用 idle/duplex/slow-reader 策略；未来若产品需要 drop-fast 与 bounded-wait 可配置，才对过载策略抽接口。 | **压测工具可用；生产过载策略暂不扩展。** | 当前 production FIFO + 有界 pending + 503/504 已是一套策略；为一种行为新建多个策略类只增加跳转。 |
| State Machine | 压测任务 `Idle → Warmup → Ramp → Steady → Drain → Complete/Failed`；连接 `Created → Admitted → Handshaking → Relaying → HalfClosed → Closed`，任一步进入 `Rejected/Cancelled/Failed`。 | **建议在压测生命周期/状态断言中显式建模；不需要 FSM 第三方库。** | 代理 HTTP 已由 `net/http` 管理主要连接状态。手写 CONNECT/relay 状态只保留实际需要的 typed state 和转换测试，避免和标准库重复。 |

压测 runner 应在每次状态迁移时记录 timestamp、连接 ID（测试内部随机 ID）和失败类别，客户端关闭、超时、代理拒绝都要有终态；defer/finally 确保所有 socket 被关闭。取消测试用 barrier 保证服务端已进入对应状态，再关闭客户端，避免依赖固定 sleep 导致假通过。

## 7. 验收门禁与回归测试

### 正确性门禁

- 小 limit 网络测试：并发竞争下 active 不超过 limit；达到 limit 后新连接关闭/拒绝；释放已有连接后 permit 重新可用；Rejected 与 Active 计数收敛。
- SOCKS5 与 HTTP CONNECT：握手/首包不丢、双向 checksum 正确、FIN 半关闭能传完尾部数据、RST 能释放两方向、客户端取消能在设定门限内关闭上游。
- HTTP forward：继续覆盖 keep-alive、认证、Proxy-Authorization 不转发、上游慢响应、client disconnect、pending 队列 cancellation 和 permit 释放。
- 所有负载客户端结束后，在 10 秒内 `/api/status` active/pending 回零；超过 10 秒则保存 goroutine、FD、socket 状态用于定位，不以“最后退出了”掩盖泄漏。

### 容量/资源门禁

- 50/100/500 各 tier 达到目标并稳定 120 秒；低于准入上限时无非预期拒绝，成功 tunnel 数据校验 100%。
- active sessions 从不超过配置上限；超限负载产生拒绝，HTTP pending 不超过配置上限，排队 timeout 和 client cancel 后 permit 均释放。
- 对同一 Linux host 的 3 次样本报告建连/TTFB/完成时间 p50/p95/p99、吞吐、错误率，以及 CPU、RSS、FD、goroutine、GC、cgroup throttle。
- 长时测试曲线应在 warm-up 后稳定，不出现 FD/goroutine/RSS 持续单调上升；停载后 active 回 0，FD/goroutine 在 60 秒内回到测试前基线附近。具体容差用首轮噪声和 allocator/cache 行为校准后固化，不能先编造跨主机的绝对内存 SLA。
- 在观测到 CPU quota 时，对照 `GOMAXPROCS` 与 `cpu.stat` throttle 增量；无 quota 的当前 dev 数据不得用来判定生产 throttle 风险。
- HTTP `singleConnListener` goroutine 开销只比较同一个 workload 的采样与 profile。若其比例很小，不改路径；若明显增长，再做最小候选实现并重复上述功能回归。候选方案不得移除 HTTP disconnect cancellation 门禁。

### profile 执行顺序

1. 轻量采样先跑完整阶梯，不开启 CPU profile。
2. 在 100/500 稳定阶段各抓一次 goroutine 和 heap profile，检查 `net/http.(*conn).serve`、relay copy、buffer allocation、锁/GC 栈。
3. 只有 CPU 明显饱和才抓短时 CPU profile；疑似锁竞争才启用 block/mutex profile，避免一直开启高开销诊断。
4. 对比基线与候选的 p95/p99、成功率、每 tunnel FD/goroutine/heap、CPU 和 throttle；只有成功率与正确性不退化，且目标瓶颈指标有可重复改善，才接受优化。

## 8. 参考资料

- [Go `runtime/metrics`](https://pkg.go.dev/runtime/metrics)、[Go `net/http/pprof`](https://pkg.go.dev/net/http/pprof)、[Go diagnostics](https://go.dev/doc/diagnostics)、[Go 1.25 container-aware GOMAXPROCS](https://go.dev/doc/go1.25)。
- [Go `io.CopyBuffer`](https://pkg.go.dev/io#CopyBuffer)：使用 `WriterTo`/`ReaderFrom` 快速路径时，调用者传入的缓冲区未必实际参与复制。
- [Linux cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html)：CPU、memory、PID controller 与统计接口的语义。
- [Envoy circuit breaking](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/circuit_breaking.html)、[Envoy connection pooling](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/connection_pooling.html)。
- [NGINX worker connections](https://nginx.org/en/docs/ngx_core_module.html#worker_connections)、[HAProxy maxconn](https://docs.haproxy.org/3.4/configuration.html#4.2-maxconn)。
- [Prometheus Go client](https://github.com/prometheus/client_golang)、[Prometheus procfs](https://github.com/prometheus/procfs)、[OpenTelemetry Go instrumentation](https://opentelemetry.io/docs/languages/go/instrumentation/)。
- 相关仓库报告：[并发与容量审查](socks5-proxy-concurrency-and-capacity-review.md)。

## 9. 本轮执行记录（2026-11-05）

### 9.1 已执行的实现和测试修正

- `/api/status` 增加 `runtime` 快照，使用 Go 标准库 `runtime/metrics`，报告 PID、Go 版本、goroutine、GOMAXPROCS、heap objects、GC 后 live heap、累计分配字节/对象和 GC 次数；没有增加第三方运行依赖。`gcHeapLiveBytes` 表示最近一次 GC 标记的 live heap，启动后尚未发生 GC 时可以为 0；`gcHeapAllocBytes` 是进程生命周期累计值，不能当成当前内存。更多指标语义见 [Go runtime/metrics](https://pkg.go.dev/runtime/metrics)。
- 修正 `proxy-capacity-load.mjs` 的 duplex drain 逻辑：停止发送后继续监听回包，直到 drain 完成或 socket 关闭；此前提前移除 data listener 会低报接收字节。本轮结果要求每轮发送字节等于接收字节、无 checksum mismatch、无未完成尾部字节，否则负载脚本以失败退出。
- 增加 `CAPACITY_DUPLEX_DRAIN_MS`，允许长时双向流量留出排空窗口；报告场景开始/结束 Unix 时间，便于与 Linux 资源快照对齐。
- 保留 `net/http.Server`、双向 `io.Copy` relay 和 Linux TCP splice 快速路径，没有在缺少 CPU/heap profile 证据时替换成手写 HTTP parser 或 event loop。

### 9.2 验证结果

| 验证 | 执行结果 | 说明 |
|---|---|---|
| SOCKS5 模块单测 | `go test -count=1 ./...` 通过 | 本机模块目录执行，覆盖当前工作区代码。 |
| Go 静态检查 | `go vet ./...` 通过 | 本机 SOCKS5 模块。 |
| 负载脚本单测 | `node --test tests/benchmark/proxy-capacity-load.test.mjs`，6/6 通过 | 包括两种握手预读字节、超时关闭、字节不匹配失败门禁和停止发送后的回包 drain。 |
| 负载脚本语法 | `node --check tests/benchmark/proxy-capacity-load.mjs` 通过 | Node.js 语法检查。 |
| Linux 连接阶梯 | 50/100/500 × SOCKS5/HTTP CONNECT × idle/duplex；每组保持 30 秒，10ms ramp；全部连接成功、无拒绝，双向流量校验一致 | 运行在隔离 `linuxwebtool-dev`，上游为本地 echo；不是公网目标，也不是饱和带宽测试。 |
| Linux 500 路 soak | SOCKS5 和 HTTP CONNECT 各 300 秒双向流量，随后约 10.2 秒 drain；两组均 500/500 成功、0 mismatch、0 partial，发送/接收字节完全一致 | 精确连接和字节数据见下表。 |
| 浏览器多标签 | 1/5/20 标签，direct/HTTP proxy/SOCKS5 proxy，3 个样本；234 次导航成功，页面请求失败为 0 | 本地开发应用页面，不能代表任意互联网网站或生产网络路径。 |
| 最新源码 race detector | **未能执行** | `linuxwebtool-dev` 是运行镜像，镜像内没有 `go`/`gcc`；远程 Linux 主机也没有二者，本机 `CGO_ENABLED=0`。不能把之前源码版本的 race 结果外推到新增 runtime snapshot 后的源码；需在具备 Go+cgo+GCC 的 Linux CI/build image 上补跑。 |

连接阶梯的 500 路双向 soak 摘要（`connectMs` 是客户端完成代理协议建连的耗时，不是网页 TTFB）：

| 协议 | 建连 p50 | 建连 p95 | 建连 p99 | 发送/接收字节 | 活跃保持 / drain | 错误 |
|---|---:|---:|---:|---:|---:|---|
| SOCKS5 | 2.612 ms | 6.156 ms | 8.545 ms | 89,471,616 / 89,471,616 | 300.013 s / 10.228 s | 500/500；0 mismatch；0 partial |
| HTTP CONNECT | 2.288 ms | 6.940 ms | 10.894 ms | 89,468,736 / 89,468,736 | 300.009 s / 10.222 s | 500/500；0 mismatch；0 partial |

500 路 HTTP CONNECT 稳定阶段的一次采样：active=500、rejected=0、goroutines=2,019、GOMAXPROCS=6、heap objects≈76.9 MB、最近 GC live heap≈76.4 MB、累计 heap allocation≈301.5 MB、进程 RSS≈52.2 MB、FD=1,007。SOCKS5 的 500 路采样约 1,519 goroutine，符合 HTTP 每连接额外约 1 个 `net/http` service goroutine 的实现成本；这证明了可观察到的差异，但尚不足以证明其为性能瓶颈。

FD 在活跃流量期间曾观察到约 1,007 至 3,007 个。该代理使用标准库 TCP relay，Linux `io.Copy`/`TCPConn.ReadFrom` 可走 splice，并缓存 splice pipe；Go 源码说明这些 pipe 会通过 pool 复用，因此活跃期的 FD 高水位不应直接判成泄漏，见 [Go Linux splice 实现](https://go.dev/src/internal/poll/splice_linux.go) 与 [TCP splice 路径](https://go.dev/src/net/splice_linux.go)。负载结束后 active=0、goroutines=19、FD=7、进程 RSS≈38.4 MB；后续采样 heap objects≈34.8 MB。FD 与连接 goroutine 回落到空闲量级，没有观测到本轮 soak 后的连接残留。

同一时间的 cgroup 观测为 `cpu.max=max`、CPU throttling=0、`memory.max=max`、OOM=0、`pids.max=4641`、`nofile` soft/hard=1,048,576。`memory.current≈502 MB` 是容器 cgroup 总量，不是代理进程 RSS。该环境没有 CPU/内存 quota，因此本轮不能证明资源受限部署的容量或尾延迟。

浏览器三样本页面 load p50（毫秒）：

| 标签数 | direct | HTTP proxy | SOCKS5 proxy |
|---:|---:|---:|---:|
| 1 | 250.6 | 264.2 | 263.8 |
| 5 | 408.2 | 404.7 | 399.4 |
| 20 | 1,110.9 | 1,114.2 | 1,049.2 |

20 标签的 page load p95 分别为 direct 1,385 ms、HTTP proxy 1,221.5 ms、SOCKS5 proxy 1,133.3 ms。代理与直连在本地样本中没有显示多标签退化；小样本环境噪声较大，不应据此宣称代理更快。1 标签 p50 的约 13 ms 差值也不能解释用户访问任意公网目标时的完整响应差异，后者还受 DNS、路由、目标服务和上游网络影响。

### 9.3 本轮结论与后续门禁

1. 当前默认 512 会话上限下，500 路 idle/duplex 连接在隔离开发容器成功建立；两种协议各 5 分钟双向 soak 后 active、FD 与 goroutine 回落，没有证据支持立即替换为事件循环或移除标准库 HTTP 适配器。
2. HTTP CONNECT 确实比 SOCKS5 多约一个 `net/http` 服务 goroutine/活跃连接；本轮 500 路进程 RSS 约 52 MB、无 cgroup throttling，尚未构成需要牺牲 HTTP 取消/keep-alive 语义来换取的证据。
3. 本轮负载用于连接调度、长期存活与双向完整性，不是高带宽吞吐压测。没有拿少量单机样本声称性能提升百分比；需要在相同环境重复基线与候选并记录 p50/p95/p99、吞吐、CPU、heap 与 FD 后才能接受数据面性能改动。
4. 原计划中尚未完成：50/100 路各 15 分钟 idle/duplex soak、500 路 idle soak、每档 3 次且每次 120 秒稳定窗、100/500 路 goroutine/heap/短时 CPU profile、受限 cgroup 配额下的对照、最新源码 Linux `go test -race`，以及用户所述实际公网目标的浏览器端到端对照。生产容器没有参与本轮测试。
5. 下一步优先把 Linux race 检查接入已有 CI/Go build image，并复跑当前源码；其后通过 cgroup quota 与 profile 验证容量边界。若 profile 仍显示 `net/http` 连接服务 goroutine 是主要成本，再制作独立 A/B 原型；候选实现必须保留 HTTP client disconnect 取消、CONNECT 预读字节、半关闭、keep-alive 和拒绝/排队门禁。
