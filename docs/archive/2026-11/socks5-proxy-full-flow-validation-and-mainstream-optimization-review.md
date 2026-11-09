# SOCKS5 代理全链路验证与主流优化方案复核

> 日期：2026-11-05  
> 范围：SOCKS5 / HTTP 单端口代理、DNS、ACL、拨号竞速、TCP 中继、基准测试与现有架构门禁  
> 结论性质：代码路径复核 + 回归测试先红后绿 + 本地构建/门禁 + 开发实例热更新与浏览器/协议采样。性能数字是小样本诊断结果，不构成 SLA。

## 结论摘要

常规 SOCKS5 CONNECT 与 HTTP CONNECT 链路已有本地和开发实例验证。本轮增加真实 socket 的无认证 IPv4 SOCKS5 全链路测试，覆盖协商、CONNECT、首段 payload 与请求合并、双向回传；并修复硬错误导致的 relay 阻塞、预读缓冲排空失败未及时返回、HTTP keep-alive 跨目标误路由、明文 HTTP 请求的握手 deadline 截断和 407 Content-Length 错误。未经证实的 splice 零拷贝声明已移除，浏览器基准列名已校准，SOCKS5h DNS 场景要求明确配置域名。修复已热更新到开发实例，协议微基准与 3 轮浏览器采样成功；小样本只说明本次页面未重现明显性能退化。

本次复核发现的优先事项：

1. **域名 ACL 语义仍需产品决策。** 本轮为 HTTP 明文逐请求路径保留现有授权语义，并在每次请求解析后校验候选 IP；域名规则是否应独立构成白名单仍未明确，SOCKS 与 HTTP 的统一决策表尚未建立。
2. **relay 错误退出已修复。** 两方向 copy 任一返回硬错误、预读缓冲排空失败或 CloseWrite 失败时关闭两端以释放另一方向；EOF 则仍半关闭并等对端合法响应。真实 TCP RST、缓冲写失败和延迟响应回归用例已覆盖。
3. **splice 误声明已从运行代码与架构门禁中移除。** relay 当前统一使用 `io.CopyBuffer` 和池化缓冲区；这不是 TCP-to-TCP 零拷贝。是否实现 Linux 专用路径仍需同机 syscall、CPU、吞吐数据。
4. **HTTP 明文代理已改为逐请求 L7 转发。** 每个 HTTP 请求重新提取目标、鉴权和 ACL/DNS 检查；共享标准库 `http.Transport` 复用上游 keep-alive 连接。CONNECT 仍使用独立原始 TCP 隧道。真实 TCP 集成测试覆盖同一代理连接访问两个不同 Host；普通 HTTP 转发之后在同一客户端连接发起 CONNECT 目前明确返回 400，混合请求序列仍未支持。
5. **基准入口已避免误标 DNS 和错读列。** 若 `TARGET_HOST` 是 IP，SOCKS5h DNS 场景默认跳过；可配置 `SOCKS5H_TARGET_HOST` 指向域名。浏览器表头已补齐 TLS 与 Content Download。当前脚本尚无可控大流量直连/代理对照，因此不能评价吞吐衰减。

继续使用标准库连接池和池化 copy buffer；在建立受控吞吐基线前不增加自定义 splice、UDP/QUIC 或动态缓冲区算法。现有域名 ACL 语义与连接容量上限仍是下一步优先事项。

## 当前全链路

~~~mermaid
flowchart TD
    A[Accept TCP] --> B[客户端 IP ACL]
    B --> C[Peek 首字节识别 SOCKS5 / HTTP]
    C -->|SOCKS5| D[方法协商 / 可选 RFC1929 / CONNECT 解析]
    C -->|HTTP CONNECT| E1[单次授权与上游拨号]
    C -->|明文 HTTP| E2[逐请求鉴权 / ACL / DNS / HTTP Transport]
    D --> F[主机名与端口策略检查]
    E1 --> F
    E2 --> F
    F --> G{目标是 IP?}
    G -->|否| H[LRU DNS 缓存 / singleflight / 过期异步刷新]
    G -->|是| I[候选 IP]
    H --> I
    I --> J[IP 子网 ACL 过滤]
    J --> K[单 IP 拨号或最多 4 个候选交错竞速]
    K --> L[发送 SOCKS 成功 / HTTP 200]
    L --> M[两条 io.CopyBuffer 双向转发]
    M --> N[字节、连接数和阶段聚合指标]
    E2 --> O[按请求写回响应并处理下一请求]
    O --> N
~~~

核心代码位置：

- 接入、客户端 ACL、协议嗅探与连接 goroutine：[main.go](../plugins/socks5-proxy/cmd/socks5-proxy/main.go#L389)
- SOCKS5 协商、域名解析与上游拨号：[main.go](../plugins/socks5-proxy/cmd/socks5-proxy/main.go#L523)
- HTTP CONNECT / 明文 HTTP 处理：[http_proxy.go](../plugins/socks5-proxy/cmd/socks5-proxy/http_proxy.go#L42)
- DNS 策略、LRU、过期刷新与并发合并：[dns_cache.go](../plugins/socks5-proxy/cmd/socks5-proxy/dns_cache.go#L29)
- 上游 ACL 过滤与连接竞速：[dialer.go](../plugins/socks5-proxy/cmd/socks5-proxy/dialer.go#L22)
- 双向中继：[main.go](../plugins/socks5-proxy/cmd/socks5-proxy/main.go#L755)
- 回环状态接口与 DNS/拨号聚合指标：[main.go](../plugins/socks5-proxy/cmd/socks5-proxy/main.go#L810)

## 发现的问题与风险

| 优先级 | 发现 | 代码证据与影响 | 建议 |
|---|---|---|---|
| P0：策略语义 | 域名规则目前不影响域名目标的最终 permit 结果 | EvaluateTarget 对匹配域名返回 (true, true)，不匹配域名返回 (false, true)；两个协议处理器只在 !allowed && !needDns 时直接拒绝，因此域名都继续解析。随后 dialUpstreamWithRace 只调用 IsIpAllowed 过滤已解析 IP。结果是“是否匹配域名规则”对域名请求的最终拨号没有区别。若目标是严格域名白名单，未列域名可能通过已允许网段；若目标是域名 + IP 双重限制，允许的外网域名又会被 subnet 限制挡住。相关注释/字段描述与行为需统一。 | 先写清真值表：IP 命中、域名命中、域名未命中、解析到允许/拒绝网段分别如何处理；然后给 SOCKS 和 HTTP handler 增加端到端用例。域名命中仍应对最终 IP 做 rebinding 防护。 |
| P1：资源生命周期（已修复） | 硬错误可能使反向 copy 永久阻塞 | relay 现在保留 EOF 半关闭语义；硬 copy error 或 CloseWrite 错误会关闭 client 与 upstream，促使另一 copy 返回。`TestRelayHardReadErrorClosesBothConnections` 用 TCP RST 复现旧行为并验证退出，`TestRelayClientHalfClosePreservesDelayedUpstreamResponse` 验证不会因半关闭截断合法响应。 | 现有修复没有引入未定义的全局 idle timeout；未来若要限制空闲连接，应先确定产品配置和关闭语义。 |
| P1：传输路径误判（声明已纠正） | 标准库 copy 分派不能证明 TCP-to-TCP splice | 运行代码和架构门禁不再声称 splice 已生效，使用 `io.CopyBuffer` 与 `sync.Pool`。此处不主张零拷贝或固定吞吐提升。 | 只有受控 Linux syscall、CPU、吞吐对比证明收益后，再增加专用实现。 |
| P1：HTTP 明文代理（已修复） | 单连接跨 Host 请求被送到首个上游 | 新路径逐个 `ReadRequest`，按本次请求 URL 提取目标并重新鉴权、ACL/DNS 校验；标准库 `http.Transport` 负责上游 HTTP 复用。`TestHandleHttpProxyRoutesEachKeepAliveRequestToItsOwnHost` 以两个真实 loopback HTTP 服务验证路由。ACL reload 会关闭上游空闲连接。CONNECT 与 Upgrade 保持独立隧道处理。 | 仍需增加跨 Host ACL 拒绝、代理认证重试、HTTPS 和 WebSocket Upgrade 回归用例；域名 ACL 语义待定。 |
| P1：容量保护 | 入站连接无显式并发上限 | Accept 后每条连接立即启动 goroutine；没有最大活动连接或连接预算。每条转发隧道至少占用两个 TCP socket 和两条 copy goroutine，突发流量可能耗尽 FD、内存或上游连接配额。 | 增加配置化并发上限、拒绝/排队策略和“达到上限”指标；不要对所有连接无界排队。压测确定默认值。 |
| P2：DNS 地址族 | 当前解析顺序不是完整双栈 Happy Eyeballs | Ipv4PreferResolver 先查 ip4，有 IPv4 就立即返回；只有无 IPv4 时才查 ip。通常传给竞速拨号的候选只有 IPv4，dialUpstreamWithRace 的地址竞速就不是 A/AAAA 交错竞速。固定间隔 120ms 和前 4 个候选也需要数据验证。 | 如确需改善 IPv6/IPv4 可达性，异步获取 A/AAAA、按 RFC 8305 规则排序并交错尝试；保留 subnet ACL 预检和输家连接关闭。 |
| P2：DNS 等待与刷新 | 自制 singleflight 等待者不能单独取消；过期命中可能启动许多等待 goroutine | SingleflightGroup.Do 的重复调用直接 WaitGroup.Wait()；调用者 context 已取消时仍等 leader。每个 stale hit 都先启动一个 goroutine 再进入同键合并，突发时会产生许多短命等待者。 | 让共享 DNS 工作拥有独立、有界的 resolver context；每个请求通过可取消等待退出；过期刷新使用单任务 keyed registry 或 DoChan，不为每个请求额外启动阻塞 goroutine。 |
| P2：缓存新鲜度 | 正/负缓存 TTL 是固定配置，不是 DNS 应答 TTL | 当前默认软/硬 TTL 为 3/10 分钟，负 TTL 为 5 秒；net.Resolver.LookupIP 返回 IP 而非原始资源记录 TTL。硬 TTL 内可能持续使用陈旧地址。IP ACL 仍会再次检查，降低 rebinding 风险，但不保证目标地址及时更新。 | 若业务要求遵守 DNS TTL，改用能保留 RR TTL 的解析器；否则将固定 TTL 明确作为产品策略，并为刷新失败、陈旧命中和最大陈旧时长增加指标/测试。 |
| P2：可观测性 | 阶段指标不足以解释长尾 | /api/status 仅给 DNS 和拨号 count/total/max，没有 p50/p95 桶、候选地址尝试数、relay 首字节/结束原因、请求失败分类；IP 字面量也不会增加 DNS resolve 计数。 | 保持低开销，补固定桶直方图/计数，并按阶段记录 reason、协议和地址族；不记录完整目标 URL、凭据或可识别用户的数据。 |
| P2：认证传输 | RFC1929 和 HTTP Basic 不提供代理链路加密 | 当前 SOCKS5 用户名密码与 HTTP Basic 在代理 TCP 会话中传递；RFC 1929 明确指出可被嗅探时不建议使用。 | 不可信网络上通过 TLS 代理、SSH/VPN 或受保护的传输层承载；界面明确提醒认证不等于加密。 |

### ACL 语义必须成为首要门禁

当前两条域名处理链路都执行 EvaluateTarget，但没有在解析后再次读取“域名规则是否命中”的结论；拨号器只根据 IP 子网规则筛选结果。因此，现有 TestDomainWildcardAndRegex 只证明 EvaluateTarget 单函数的返回值，并没有证明代理 handler 会按域名白名单放行/拒绝。现有 HTTP ACL 用例的拒绝结果也可能完全由解析后的 subnet 拒绝触发。

实现前应由产品配置语义选定以下之一：

- **严格域名白名单**：域名未命中即在 DNS 前拒绝；命中后仍验证解析 IP 不能落入明确禁止的内网/回环/链路本地范围。
- **域名与网段联合白名单**：目标域名和解析 IP 都必须通过各自规则，规则组合为 AND。
- **域名或网段白名单**：命中域名或命中网段均可授权，但域名命中后仍要应用 anti-rebinding 的危险地址策略。

不要靠当前 (allowed, needDns) 二元返回值隐式表达三种策略。建议返回明确的决策结果，例如 Allow / ResolveAndCheckIP / Deny，并为规则来源保留可测试的 reason。

## 基准、测试与文档差异

### 上一轮开发环境基线（早于本轮修改）

上一轮采样结果：

- go test -count=1 ./...（SOCKS5 Go 模块）：通过，单测约 1.35 秒。
- node --check tests/benchmark/proxy-benchmark.mjs：通过。
- node --check tests/benchmark/proxy-web-performance.mjs：通过。
- dotnet test 过滤 LinuxArch072 到 LinuxArch075：4/4 通过。当前机器使用 .NET SDK 11 RC，测试项目目标是 net10.0；只出现预览 SDK 提示。
- 微基准 5 次/场景全部成功：Direct TCP p50 0.58ms、SOCKS5 IP 1.86ms、旧脚本标为 SOCKS5h 的 IP 场景 1.80ms、HTTP CONNECT 1.60ms；Direct HTTP TTFB mean 1.94ms，HTTP Proxy mean 3.80ms，差值 +1.86ms。旧 SOCKS5h 场景的目标实际是 IP 字面量，没有发生代理 DNS，不能作为 SOCKS5h DNS 数据。HTTP 差值包含代理握手/网络往返和上游拨号，不是代理纯计算成本。
- 浏览器每模式 5 次、全部成功且失败请求为 0；结果对象中的 loadEventTime p50：Direct 256.7ms、HTTP Proxy 252.6ms、SOCKS5 248.2ms；状态码汇总一致。该页 URL 是 IP 字面量，所以 DNS Timing 为 0，不能验证浏览器 DNS 路径。代理较快的 4.1ms 差值也不具统计意义，不能解读为代理更快。

该短样本中，TTFB 代理差值曾由正转负；当前内网页面没有稳定复现明显的浏览器代理慢化。它不代表外网、多资源、冷 DNS 或高并发场景，也不能作为本轮源码修复后的性能结果。

### 本轮代码修改验证

真实 TCP 回归测试覆盖了硬 RST 在 750ms 内释放两方向、半关闭后延迟响应完整返回、预读缓冲写失败在启动双向 copy 前返回、同一 keep-alive 连接的两个 Host 各自到达正确上游、慢响应超过原握手期限仍成功，以及每个请求重新鉴权并剥离代理凭据、缺失凭据时 407 body 长度正确。HTTP 双 Host、握手 deadline、逐请求鉴权和 RST 用例曾在对应旧实现下复现失败后通过修复验证。

- `go test -count=1 -timeout=90s ./...`（SOCKS5 Go 模块）：通过，2.56 秒。
- `go vet ./...`：通过。
- `CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build ./...`：通过。
- `go test -race ./...`：当前 Windows 环境 CGO 关闭且没有 GCC，race detector 无法启动；普通 Go 测试和 vet 已通过。
- 两个 Node benchmark 脚本 `node --check`：通过。
- LinuxArch072–LinuxArch075：4/4 通过；ArchitectureTests 使用 .NET SDK 11 RC 构建 net10.0 项目，打印预览 SDK 提示。
- 插件热更新：脚本确认远端运行二进制 SHA-256 与本地 Linux 构建一致，开发容器未重启；后续浏览器和协议请求均成功。
- 更新后微基准 5 次/场景：Direct TCP p50 0.73ms、SOCKS5 CONNECT(IP) 2.20ms、HTTP CONNECT 1.62ms；Direct HTTP TTFB mean 2.70ms、HTTP Proxy mean 3.14ms，样本差值 +0.45ms。未配置 `SOCKS5H_TARGET_HOST`，因此 DNS 场景正确跳过。
- 更新后浏览器每模式 3/3 成功，失败请求为 0、每次导航约 23 个请求；PageLoad p50：Direct 249.8ms、HTTP Proxy 252.4ms（+2.6ms / +1.0%）、SOCKS5 241.1ms。目标仍是 IP 字面量，浏览器 DNS Timing 为 0。

这些数据只代表当前开发实例、内网页面和少量样本。它们没有覆盖外网、多资源域名、冷 DNS、高并发或吞吐，也不能说明 SOCKS5 在所有目标上比直连更快。

### 当前已有资产和覆盖边界

| 资产 | 当前能验证 | 当前不能证明 |
|---|---|---|
| plugins/socks5-proxy/cmd/socks5-proxy/*_test.go | DNS LRU/TTL/negative cache/singleflight；单 IP/多 IP 快速失败转拨号/ACL 子网拒绝；无认证 IPv4 SOCKS5 协商、CONNECT、首段 coalesced payload 和响应回传；HTTP Basic、ACL 拒绝、CONNECT 成功；RST 硬错误关闭双端、预读缓冲排空写错误、半关闭延迟响应；HTTP 双 Host 路由、慢响应、逐请求认证与凭据剥离。 | RFC1929 SOCKS5 完整路径、域名/IPv6 ATYP、分片读、HTTP 明文跨请求 ACL 拒绝未覆盖；普通 HTTP 后续接 CONNECT 当前返回 400，混合序列不支持。 |
| tests/benchmark/proxy-benchmark.mjs | 直连 TCP、SOCKS5、可选 SOCKS5h DNS、HTTP CONNECT 和 HTTP GET TTFB；随机交错、失败数与分位数。 | 必须提供 `SOCKS5H_TARGET_HOST` 域名才运行 DNS 场景；仍无可控大流量直连/代理对照。旧命名为 Legacy 的未使用握手函数仍待清理。 |
| tests/benchmark/proxy-web-performance.mjs | Playwright 新建冷浏览器，Direct/HTTP/SOCKS5 随机交错；请求失败、状态码和 Navigation Timing，列头与时间指标已经对齐。 | 默认目标为 IP 字面量的单一内网页面；不能证明域名解析、外部站点或大资源吞吐。每模式 5 个冷启动样本只适合诊断，不足以设 SLA。 |
| LinuxArch072–LinuxArch075 | 验证关键类型、函数、字符串、脚本与文档存在；LinuxArch074 已与缓冲 copy 实现对齐。 | 仍主要是源码字符串断言；行为由 Go RST、半关闭和 HTTP 双目标集成测试覆盖。 |
| CI 工作流 | 有 ArchitectureTests 门禁。 | 当前工作流搜索未发现 SOCKS5 模块的 go test 或代理浏览器/微基准步骤；本轮 Go 测试由本地手工执行，Go 行为测试尚未接入 CI。 |

完整基准脚本：

- [proxy-benchmark.mjs](../tests/benchmark/proxy-benchmark.mjs)
- [proxy-web-performance.mjs](../tests/benchmark/proxy-web-performance.mjs)
- 相关源码门禁：[ArchitectureTests.cs](../tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs#L2315)

### 既有调研文档需要校准的内容

现有 [network-proxy-performance-testing-and-optimization-research.md](network-proxy-performance-testing-and-optimization-research.md) 仍适合作为测试维度草案，但若作为当前实现说明，需要修订以下结论：

1. “TCP-to-TCP Linux splice 已在 relay 落地”已从运行代码和 LinuxArch074 门禁中移除；30%–60% 吞吐提升没有实测依据。
2. “10 秒 deadline 杜绝僵尸连接”已由硬错误关闭两端和真实 TCP RST 测试替代；未配置业务 idle timeout 时，不再暗示该 deadline 可结束另一方向。
3. 规划中的 10MB/50MB 吞吐评测没有实际调用；当前 helper 甚至只访问 loopback 测试服务。
4. HTTP 明文代理已经逐请求做 L7 处理，并使用共享 `http.Transport` 连接池；HTTP CONNECT 与 SOCKS5 隧道仍通过 L4 字节中继。
5. TCP_NODELAY 已是 Go TCPConn 默认行为；显式设置可作为配置自述，但不能据此宣称消除了 40–200ms 延迟。
6. 固定的 3ms、DNS <1ms、吞吐损失 <15% 等门槛没有与运行环境和成功率绑定，不应直接当作 CI 硬门禁。
7. “固定 32KB 用户态 buffer 限制长肥网络 TCP 窗口”表述不严谨：TCP 窗口由内核 socket 缓冲区/拥塞控制等机制决定，应用 copy buffer 的大小不等价于 TCP 窗口上限。
8. RFC 5861 定义的是 HTTP Cache-Control 的 stale-while-revalidate / stale-if-error 扩展；当前 DNS cache 借用了“软过期后继续返回旧值并后台刷新”的模式，但不等同于实现该 HTTP RFC，也没有遵循 DNS 答案 TTL。

较新的 [SOCKS5 优化路线](socks5-proxy-optimization-roadmap-and-feasibility.md) 已更谨慎地记录短样本噪声和 P0–P3 阶段，但其中对 Linux io.Copy 优化路径的描述也应按本报告所列 Go 标准库行为复核。此文记录现状与待验证门禁，不能用静态源码断言替代真实数据面测试。

## 系统已有基础设施与可复用库

| 能力 | 当前已有 | 取舍 |
|---|---|---|
| 插件运行与恢复 | Go 插件 runtime、resident 生命周期、分配端口、state dir、auto-restart、开发环境插件热更新脚本。 | 本轮已按脚本热更新开发实例并核验二进制 hash；生产容器保持未重启。 |
| 代理内核 | Go 1.24 module 声明；生产代码只有 Go 标准库依赖。net.Conn、net.Dialer、net.Resolver、`io.CopyBuffer`、`sync.Pool`、`net/http.Transport` 已覆盖基本链路。 | 保持轻依赖，除非具体缺口由库显著降低风险。 |
| DNS/访问策略 | IDnsResolverStrategy、LRU、正/负缓存、singleflight、stale refresh；ACL 使用原子快照 reload；HTTP 每请求重新解析/预检候选 IP，ACL reload 会关闭 HTTP 空闲上游连接。 | 域名规则产品语义仍待统一，再补 SOCKS 与 HTTP 同等门禁。 |
| 可观测性 | /health、回环 /api/status、Active/Total connections、流量、ACL、DNS cache 和 DNS/dial 聚合耗时。 | 复用并补阶段桶、错误原因、竞速尝试数、relay 结束原因；避免记录敏感目标信息。 |
| 测试运行时 | Node/Playwright 已在 smoke 项目依赖中；Go 单测和 C# ArchitectureTests 已存在。 | 不需要安装浏览器或代理运行库；把 Go 单测纳入 CI 即可补最大空档。 |

## 库与业界方案对比

| 方案 | 可借鉴能力 | 与本服务的边界 | 建议 |
|---|---|---|---|
| Go 标准库 net / io / net/http | 连接、解析、协议读取、io.Copy 优化分派、HTTP Transport 的安全连接复用。 | 当前自定义 ACL、SOCKS 会话和 raw CONNECT 隧道仍需自行编排。标准库接口名含 ReadFrom 不代表任意 TCP-to-TCP 组合都走 splice。 | 继续作为默认实现；基准先修到测真实路径，再决定是否替换任何数据面组件。 |
| golang.org/x/sync/singleflight | 去重结果、DoChan 让等待者自行选择 context 取消。 | 不替代 DNS cache，也不会自动取消底层查询；应由独立有界 context 控制共享 lookup。 | 可替换当前少量自制 singleflight，或借鉴 DoChan；依赖增加很小，但不是当前用户可感知延迟的首要来源。 |
| golang.org/x/net/proxy | SOCKS5 客户端 Dialer、认证与 forward Dialer。 | 是客户端拨号包，不是 SOCKS 服务端或 ACL/relay 引擎。 | 只适合作 Go 测试/诊断客户端；当前 Node benchmark 已实现协议报文，无需为了生产代理引入。 |
| armon/go-socks5 | Server、Authenticator、Resolver、RuleSet、Dial 注入的边界拆分。 | 上游 README 明确列出 BIND 和 UDP ASSOCIATE 未完成；没有当前 HTTP 双模、平台配置与 ACL 语义。 | 可参考接口划分，不建议直接替换。 |
| GOST / go-gost/core | Service、Listener、Handler、Dialer、Router、Chain、Strategy 分层，适合作为适配器/策略边界的实现样例。 | GOST 面向多协议、多传输、多跳代理；引入其完整运行时会显著扩大构建、配置和运维面。 | 只借鉴组件边界；若未来需要代理链、TLS/WSS/QUIC，再单独评估。 |
| Envoy | L4 TCP proxy、并发连接资源管理、状态/指标、RFC8305 地址族尝试；L7 HTTP 池与 HTTP/2/3 多路复用。 | 本服务的 SOCKS/CONNECT payload 对代理是 opaque TCP；Envoy 的 HTTP 请求池不能用于任意 SOCKS TCP 隧道。独立部署体量和配置成本高。 | 借鉴 circuit breaker、受限连接、池化和地址族调度；当前不建议引入 Envoy。 |
| 3proxy | 单一轻量服务覆盖 HTTP/HTTPS、SOCKS5、BIND、UDP、keep-alive、ACL、统计和代理链。 | 配置格式、插件管理、动态 ACL API、端口生命周期与 LWT 平台集成需要适配；不是零改动替换。 | 若要扩展到 UDP/BIND 或生产级代理功能，作为可运行替代物做 POC 对照，而非只比微基准数字。 |
| Squid | 成熟 HTTP forward proxy、CONNECT、HTTP 语义、缓存层级和 peer 策略。 | 它是 HTTP 代理，不会替代 SOCKS5 通用 TCP 隧道；CONNECT 内部 TLS payload 不能由普通 HTTP cache 复用。 | 适合作为 HTTP 明文转发、连接池/缓存能力的参考；不适合优化当前 SOCKS 隧道。 |
| miekg/dns | DNS 报文和 RR 级 API 可保留应答 TTL，支持构造自定义 resolver。 | 比 net.Resolver 需要更多 resolver、超时、DNSSEC/传输策略与版本维护责任；其上游 README 也说明 v1 仅计划特定修复，v2 已迁往新仓库。 | 仅当确实要求遵守应答 TTL 或自定义 DoT/DoH 时评估，先确认维护版本；不是低延迟的默认选择。 |

### 设计模式是否值得引入

| 模式 | 合适位置 | 当前判断 |
|---|---|---|
| Adapter | 把标准 net.Resolver、net.Dialer 和计时/指标接到 resolver/dialer 契约。 | DNS 已有 IDnsResolverStrategy；HTTP Transport 用窄 `DialContext` 回调接入 ACL 与竞速拨号。无需再套通用 net.Conn 装饰器。 |
| Strategy | 可替换 DNS 地址族策略、拨号策略或 L7 HTTP forward 实现。 | 本轮复用已有 DNS 策略和标准 Transport，不新增选择器；目前只有一套生产实现，额外策略对象没有可量化收益。 |
| State machine | SOCKS 会话状态：Accepted → Negotiated → Authenticated → Authorized → Resolved → Dialed → Relaying → Closing → Closed；失败边从任意握手阶段转到 Closing。 | 目前主流程线性，先定义状态不变量并用测试断言 deadline、连接所有权、回复、指标计数。若之后出现重试/热 reload/UDP association，再考虑显式状态对象；不需要通用 FSM 依赖。 |
| Decorator | 统计 copy 字节数、首字节、结束原因或注入故障测试。 | 可用薄层 recorder/函数钩子，不要让每个小块 Read/Write 都获取锁。现有 relay 每条方向结束后聚合字节数是低成本做法。 |

## 可执行的修复与回归门禁

### 阶段 1：先定语义、补行为测试

1. 为 AllowedDomains、AllowedSubnets 和 preset 写决策真值表，覆盖 IP / 域名命中 / 域名不命中 / resolved IP 允许 / 拒绝；这是当前未决的安全边界。
2. 已建立本机 loopback upstream 的无认证 IPv4 SOCKS5 集成夹具，验证握手、CONNECT、合并首段 payload 和响应回传；RFC1929、domain/IPv6 ATYP、错误 REP 与分片读待补。
3. 已用真实 TCP 连接覆盖 RST 硬错误后双端关闭、预读缓冲写失败及时返回和 CloseWrite 后延迟响应；双向大 payload、空闲预算与 Run 层 ActiveConns 收敛待补。
4. 已用同一 keep-alive 客户端连接向两个不同 loopback HTTP Host 发请求并分别命中正确 upstream；跨请求 ACL 拒绝门禁待补。
5. DNS 并发测试分别取消等待者与共享 lookup；断言单个解析任务、等待者可返回、刷新 goroutine 有上界。

### 阶段 2：让性能工具测到真实路径

- 增加可配置的测试域名/本地 DNS fixture，使 SOCKS5 IP 与 SOCKS5h 域名场景分别记录 DNS cache miss、hit、stale。
- 添加代理与直连都访问同一个可控本机/测试网络 HTTP 服务的吞吐测试；多次测量 payload 下载、上传和双向流量，记录成功率、bytes、吞吐和 CPU。避免使用 loopback 单边结果推导代理衰减。
- 增加并发连接与长流压力组，采集 active connections、goroutine/FD、内存、CPU、p50/p95/p99、错误率。
- 浏览器基准分开记录冷启动浏览器、热启动浏览器、冷/热 DNS；对 HTTPS/多域名、多资源页记录 Navigation Timing 和失败请求。使用目标域名才能测 DNS。
- 微基准分位数要和重复轮数、失败率一起看；稳定同环境基线前不设跨机器固定毫秒 SLA。

### 阶段 3：修正语义与扩展

- 在 Linux CI 增加 go test -race ./...（或单独 SOCKS5 job）与普通 go test -count=1 ./...；不依赖 C# 源码 contains 断言作为 Go 行为验证。
- 连接限流、共享 DNS 查询取消、A/AAAA 异步解析、HTTP 跨请求 ACL 拒绝门禁分开实施，每个行为有一个聚焦失败测试和一个通过门禁。
- 只有性能剖析证明 relay CPU/内存 copy 是瓶颈时，才 spike Linux 专用转发；通过受控吞吐、CPU 和 syscall 检查对比标准 io.Copy，保证非 Linux fallback 与半关闭语义。
- 只有浏览器 NetLog / 抓包证实 UDP/443 回退显著影响首屏，才立项 SOCKS UDP ASSOCIATE。实现时要包含 TCP 控制连接生命周期、UDP 客户端绑定、每个 datagram 的 ACL、来源校验和防反射限制。

### 建议 CI 硬门禁

| 类别 | 通过条件 |
|---|---|
| 策略正确性 | 域名/IP 真值表在 SOCKS 和 HTTP 两入口结果一致；rebind 结果不可绕过禁止网段策略。 |
| 协议可靠性 | 合法握手成功；无效认证、命令、地址族与端口返回正确错误并尽快关闭；分片/合并读不丢数据。 |
| 中继完整性 | 两向字节哈希一致；半关闭后的慢响应按策略完成；错误或取消后 goroutine、FD 和 ActiveConns 收敛。 |
| 抗过载 | 到达连接预算时按策略拒绝/排队，指标可见；超过预算时内存与 FD 不无限增长。 |
| 性能 | 对固定受控测试环境比较 p50/p95、成功率和 CPU；只有经过多轮、低错误率的同机基线才启用相对阈值。吞吐 benchmark 作为 Linux 定时或手动性能任务，不把一般 CI 硬件的绝对 MB/s 当门禁。 |
| 依赖 | 默认保持标准库；引入新库时说明它解决的具体失败用例、维护状态和许可证。 |

## 参考资料

- [RFC 1928: SOCKS Protocol Version 5](https://www.rfc-editor.org/rfc/rfc1928) — CONNECT、BIND、UDP ASSOCIATE 与地址类型。
- [RFC 1929: Username/Password Authentication for SOCKS V5](https://datatracker.ietf.org/doc/rfc1929/) — 用户名密码在明文子协商中的风险说明。
- [RFC 8305: Happy Eyeballs Version 2](https://www.rfc-editor.org/rfc/rfc8305) — 异步 A/AAAA、地址排序与阶梯连接尝试。
- [RFC 5861: HTTP Cache-Control Extensions for Stale Content](https://www.rfc-editor.org/rfc/rfc5861) — HTTP 缓存的 stale-while-revalidate 语义。
- [Go io.Copy](https://pkg.go.dev/io#Copy) — WriterTo 优先于目标 ReaderFrom。
- [Go net TCPConn](https://pkg.go.dev/net#TCPConn) — SetNoDelay 默认 true 的说明、ReadFrom/WriteTo 接口。
- [Go Linux splice source](https://go.dev/src/net/splice_linux.go) 与 [TCPConn read/write source](https://go.dev/src/net/tcpsock_posix.go) — 当前 spliceFrom/spliceTo 的支持条件。
- [Go x/sync singleflight](https://pkg.go.dev/golang.org/x/sync/singleflight) — DoChan 结果通道和重复调用合并。
- [Go x/net/proxy](https://pkg.go.dev/golang.org/x/net/proxy) — SOCKS5 client Dialer，不是 server。
- [GOST core](https://github.com/go-gost/core) — Service/Listener/Handler/Dialer/Router/Strategy 分层参考。
- [Envoy upstream connection pooling](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/upstream/connection_pooling) 与 [TCP proxy](https://www.envoyproxy.io/docs/envoy/latest/intro/arch_overview/listeners/tcp_proxy) — L7 池与 L4 tunnel 资源管理边界。
- [3proxy features](https://github.com/3proxy/3proxy) — 轻量多协议代理和 UDP/BIND/配置能力。
- [Squid http_port](https://www.squid-cache.org/Doc/config/http_port/) — HTTP forward proxy 与 CONNECT 能力。
- [miekg/dns](https://github.com/miekg/dns) — DNS RR API 及当前仓库版本维护说明。

## 相关本地文档

- [SOCKS5 浏览器延迟审计与回归门禁](socks5-browser-latency-audit-and-regression-gates.md)
- [SOCKS5 基准可行性与设计评审](socks5-benchmark-feasibility-and-design-review.md)
- [SOCKS5 优化路线与可行性](socks5-proxy-optimization-roadmap-and-feasibility.md)
- [网络代理性能测试与优化调研](network-proxy-performance-testing-and-optimization-research.md)（含需按本报告校准的早期假设）
