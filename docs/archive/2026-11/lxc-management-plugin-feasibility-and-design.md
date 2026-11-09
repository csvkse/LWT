# LXC 管理插件可行性与设计

调研日期：2026-11-06。范围是 LWT 插件方案调研，未安装运行时、修改宿主或执行实机压测。

## 结论与产品边界

可以创建插件，通过已有的 Linux 容器管理服务创建和管理系统容器，配置 CPU、内存、磁盘和网络；具体功能受宿主权限、存储和网络后端约束。LXC 共用宿主 Linux 内核，不是拥有独立内核的 KVM 虚拟机，也不能运行 Windows 客户机。对不可信租户或独立内核需求，应另选 VM。

首期建议接入**现有 Incus 服务**，用户已有 PVE 时规划独立 PVE API 适配器；若目标是尽量少安装服务，可另做原生 LXC Agent，但网络、存储和恢复工作显著增加。当前没有确认目标宿主安装了 Incus、PVE 或 LXC，不能声称现网已经支持创建。不要为验证而在生产宿主自动安装或重配网络。

参考材料为用户提供的 LXC 笔记目录和 NAT 小鸡截图。截图包含实例创建、CPU 核数与限额、内存、磁盘与 I/O、NAT IPv4、IPv6、网速、流量、到期、自启、监控和策略；其中账户密码、公网地址等不应复制到文档或日志。截图同时包含 KVM 和特权嵌套选项，均不属于本期 LXC 基础功能。

## 现有项目可复用能力

| 资产 | 可复用 | 仍需补充或核实 |
|---|---|---|
| `docs/plugin-development-guide/01-architecture-and-manifest-spec.md`、`plugins/socks5-proxy/plugin.json` | resident 生命周期、端口、Webview、状态目录、监督 | 以实际 manifest 和平台实现为准；指南示例字段与实际清单存在差异，不能直接复制示例 |
| `plugins/mcp-bridge/internal/admin/jobs.go`、`internal/state/`、`internal/watchdog/` | 任务、状态存储、监督的实现经验 | 核对代码耦合和持久化语义后提取；不假定可直接引用 |
| `src/LinuxWebTool.Infrastructure/Features/Services/Detectors/ServiceEnvironmentDetector.cs` | 容器、Docker socket、部分防火墙能力发现思路 | 增加 LXC、userns、cgroup 控制器、存储 quota、网络及实际授权能力探测 |
| `src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Docker/DockerEngineAdapter.cs` | Unix socket HTTP 和 CLI 适配设计参考 | Docker API 不管理 Incus/LXC；不能把 Docker socket 当成 LXC 管理接口 |
| 插件开发指南 02、04、06、08 | UI 接入、认证、测试和宿主元数据约定 | 实测实际插件 API 鉴权链及代理身份传递；高权限操作需要更细的授权与审计 |

项目文档宣称多语言运行时可用，但此前生产浏览器显示 Go 编译环境缺失。插件发行应提供各架构经验证的预构建产物，并验证平台实际启动路径；不要假设线上有编译器。

## 主流方案与现有库

| 方案 | 优势 | 代价及建议 |
|---|---|---|
| [Incus](https://linuxcontainers.org/incus/docs/main/) | 系统容器、镜像、存储、网络、操作任务及 REST API；可使用 [Go 客户端](https://github.com/lxc/incus/tree/main/client) 或直接调用 API | 需要独立服务及版本兼容验证；首期优先已有 Incus 的单节点，按 API extensions 检查能力 |
| [Proxmox VE](https://pve.proxmox.com/pve-docs/pct.1.html) | 已有 PVE 时可复用容器、存储和管理机制 | 对接 API、权限及异步任务；不直接修改 PVE 所管理实例的底层 LXC 配置 |
| [LXD](https://documentation.ubuntu.com/lxd/latest/) | 完整管理服务、REST API 和授权机制 | 独立适配器及版本、许可、部署维护评估；不可假设与 Incus 完全兼容 |
| [原生 LXC](https://linuxcontainers.org/lxc/) | 依赖较少、宿主控制灵活 | 需自行实现镜像、网络、存储、隔离和恢复；CLI 参数必须结构化传递，禁止 shell 拼接 |
| Docker/OCI | 适合应用容器 | 生命周期与完整系统容器需求不同；现有 Docker 适配器不能直接替代 |

低层工具可评估 [vishvananda/netlink](https://github.com/vishvananda/netlink)、[google/nftables](https://github.com/google/nftables)；它们只是 Linux 网络接口，并不提供完整 VPS 管理。Go 版本兼容、维护状态、权限和所需 API 应在实现前核对；首期 Incus 路径优先使用其管理能力，避免重复管理同一网络。Incus 的 [REST API](https://linuxcontainers.org/incus/docs/main/rest-api/) 支持 Unix socket 或 TLS、异步操作与 ETag；socket 或管理证书本身是高权限凭据。

## 方案与权限边界

```text
浏览器 → LWT 登录与权限 → 插件 UI/API → 编排与持久化任务
                                      → RuntimeAdapter → Incus API
                                      → 能力探测 / 配额 / 审计
```

优先连接明确指定的现有后端：本机使用受保护的 Unix socket；跨主机使用已验证的 TLS 客户端身份和服务器证书。插件 API 不暴露任意 shell、任意路径挂载或原始管理 socket；为高权限操作设置明确授权、审计和来源校验。Incus API 支持什么权限粒度须按安装版本配置验证；如果无法满足授权边界，拒绝启用写操作。容器默认非特权，禁止自动退化为特权模式。管理 socket 访问通常相当于很高的宿主权限，挂载进入 LWT 容器须作为单独的部署决策。LWT 本身运行在 Docker 中，容器内 root、host 网络或 privileged 并不自动证明对物理宿主 namespaces、cgroup、存储和防火墙有正确的控制能力；避免在 LWT 容器内嵌套运行一套未验证的 LXC。

## 功能规格与实现边界

1. **能力检测**：返回 `Ready / RequiresSetup / Unsupported / Unknown` 和原因，探测后端版本、认证、镜像、storage pool、网络、cgroup 控制器和 quota；只读发现不等于功能已验证。
2. **创建**：选择已信任镜像及版本、唯一名称、CPU 数量/配额、内存、存储池和容量、网络配置；预检宿主余量与资源能力，显示变更预览。创建返回任务 ID，完成后核对实际配置和运行状态。
3. **资源**：CPU 核数和 CPU 时间比例分开；内存限制、swap 和 OOM 行为明确；磁盘容量硬限制必须由实际存储后端支持，目录占用统计不是硬配额；I/O 限速需确认后端和设备映射。

   补充：Incus 官方说明 [Btrfs qgroup 配额存在可绕过情形](https://linuxcontainers.org/incus/docs/main/reference/storage_btrfs/)，不能仅凭驱动名称认定严格容量隔离成立；`dir` 也依赖底层文件系统正确启用 project quota。必须对选定宿主和存储池实测，当前插件预检保持拒绝状态。
4. **网络**：优先接入专用受管 bridge；NAT IPv4 使用私网地址与端口租约，唯一键为宿主、公网地址、协议、端口。公网 IPv6 需要可路由前缀或上游支持。验证 TCP/UDP、DNS、hairpin、容器间隔离和原有 Docker/VPN 网络。不要同时让 Incus 和自研规则管理同一转发；[Incus 外部网络不支持所有受管网络功能](https://linuxcontainers.org/incus/docs/main/explanation/networks/)。
5. **运维**：启停、重启、删除、状态、用量和可追溯任务；删除明确区分实例、卷和快照，预览影响并避免误删外部资源。SSH 初始凭据只经安全渠道一次性提供，禁止写入日志与默认值。
6. **截图中的高级项**：带宽整形、月流量、到期与超限处罚排入后续阶段。tc 方向、计数器重置、宿主宕机采样缺口、账期边界及恢复需单独设计，不能把普通计数器宣称为精确计费。嵌套 Docker、设备直通及特权容器默认关闭并单独评估。

## 设计模式与数据一致性

- **适配器**：`RuntimeAdapter` 暴露 capabilities、创建、查询、启停和删除；Incus、PVE、原生 LXC 各自维护语义，同一实例只有一个权威管理器。避免提前实现未使用的多后端。
- **策略**：网络、存储、资源限制按已验证能力选择；不支持就报错，不静默降级。Incus 的 [项目配额](https://linuxcontainers.org/incus/docs/main/reference/projects/)可用于限制资源总量，但不取代宿主预留和实测。
- **状态机**：持久化任务状态 `Pending → Provisioning → Starting → Ready`，失败进入 `Failed`；运行状态与任务状态分开，避免把 API 接收成功当作实例就绪。
- **幂等与补偿**：请求携带幂等键；预留名称/IP/端口后创建卷、实例和映射，记录每一步所有权。失败只回收本任务创建的资源，补偿失败进入可重试状态；重启后对照后端实际状态协调。后端原生异步操作 ID 和 ETag 用于恢复和并发冲突检测。
- 单节点状态可评估 SQLite 和事务唯一约束；项目尚未确认有可直接用于插件的 SQLite 基础设施，选型前检查依赖、备份及 Native AOT/目标架构兼容性。多节点协调不能依赖独立 SQLite 各自分配同一端口。

## 性能与可观测性

控制面不要每个页面请求都逐容器串行调用后端；使用有界并发、批量查询、事件订阅和短 TTL 缓存，提供数据时间戳及失联状态。采样与创建任务分离，限制并发和队列长度。性能瓶颈可能在镜像拉取、存储 IOPS、conntrack、网络软中断和宿主内存压力；不能预设容器性能百分比。

记录任务各阶段耗时、失败原因、后端请求延迟、队列深度、实例数、宿主 CPU/内存/PSI、存储空间与 I/O、网络吞吐与丢包。标签避免实例名等无限增长维度，日志不含密码或令牌。

## 分阶段交付与回归门禁

| 阶段 | 范围 | 验收 |
|---|---|---|
| 0 | 隔离测试机只读探测；确定 Incus 版本、权限、存储和网络 | 输出能力矩阵；明确不支持项，不操作生产网络 |
| 1 | 单节点 Incus、可信镜像、非特权实例、CPU/内存/磁盘、受管网络、生命周期和任务 | 实际创建、配置生效、启动、停止、删除；重启恢复；用户可见失败原因 |
| 2 | NAT 端口租约、监控与并发控制 | 三实例 TCP/UDP 和 DNS，IP/端口冲突、hairpin、隔离及 Docker/VPN 共存；异常清理和宿主重启恢复 |
| 3 | 限速、流量账本、到期策略，按需 PVE 适配 | 上下行限速与突发实测；计数器归零、重启、重复采集、账期边界和缺口告警 |

安全门禁：未认证和低权限请求拒绝写操作；拒绝特权容器、任意挂载和命令注入；检查密码与令牌脱敏。资源门禁：运行负载实测 CPU、内存、磁盘硬配额和 I/O 限速；无能力时必须拒绝或明确标记不可用。故障门禁：在每一步注入故障、重复请求、并发创建和进程中断，确认不重复分配且不删除他人资源。性能门禁：在同一宿主、镜像、配额下比较单实例与并发创建，记录 P50/P95 创建时间、API 延迟、吞吐、宿主资源和失败率；先建立基线，再依据实际容量设阈值，不虚构达标数字。

## 实施验证记录（2026-11-06）

- Windows 本机使用 Edge 无头浏览器和本地模拟 Incus API，验证预检通过、创建请求响应中断、刷新后恢复待核对任务及完成核对；未连接真实 Incus，也未创建真实容器。
- 测试中观察到浏览器在连接中断时可能重发创建 POST；每次重发的幂等键一致。Go 编排测试覆盖同键并发重试仅调用一次模拟后端。
- 当前目标 Linux 宿主只读探测未发现 Incus、原生 LXC、PVE 命令或 Incus 默认 socket。真实内核、磁盘硬配额、网络、资源限制、异常恢复与性能尚未验收，创建预检仍拒绝未验证的能力。
- 本机使用已有隔离 Linux 容器执行 Linux amd64 编译的 `incus`、`jobs`、`server`、`workflow` 测试二进制，均通过；新增跨进程锁测试确认一个进程持锁时另一进程等待、释放后可获取。该容器的 cgroup 为只读且未安装 Incus，因此这些结果不能证明真实 LXC 创建或配额生效。
- 插件 HTTP、任务存储及 Incus 客户端的联测使用内存 Incus 模拟服务和测试专用预检证据，覆盖创建请求去重、重建处理器后的任务恢复、启动、托管列表及停止。生产代码仍没有可信预检证据源，不能据此声称生产创建已可用。
- 新增 Linux 专属、默认跳过的真实 Incus 生命周期验收 `plugins/lxc-manager/internal/incus/real_integration_test.go`。运行者必须同时显式设置启用开关和隔离主机确认值，并提供 Unix socket、专用存储池、受管网络和本地镜像；测试随后创建随机命名的非特权测试容器，核对配置、启动、停止并尝试清理。清理会核对随机所有权标记、停止状态和无快照条件；这些检查与删除不是原子操作，因此测试只允许在无并发管理者修改测试实例的专用隔离主机执行。中断时可能残留实例，需按测试日志中的名字和标记人工核查。
- 将含 Linux 测试的 `internal/incus` 包交叉编译为 Linux amd64 测试二进制，并在本机已有隔离 Linux 容器中运行：模拟 Incus 测试通过，错误所有权和存在快照时拒绝删除测试通过，真实生命周期测试按设计跳过。该容器未安装 Incus，**没有执行真实创建**；目标宿主仍缺少 Incus/配额实测证据，生产预检继续拒绝创建。运行条件和限制见 `plugins/lxc-manager/README.md`。

## 参考资料

- [Incus REST API](https://linuxcontainers.org/incus/docs/main/rest-api/)；[Incus 网络转发](https://linuxcontainers.org/incus/docs/main/howto/network_forwards/)
- [Proxmox 容器管理](https://pve.proxmox.com/pve-docs/pct.1.html)；[LXD 文档](https://documentation.ubuntu.com/lxd/latest/)
- [Linux cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html)；[LXC 项目](https://linuxcontainers.org/lxc/)
- 本地：`docs/plugin-development-guide/`、`plugins/socks5-proxy/`、`plugins/mcp-bridge/`，以及用户提供的 LXC 知识库和截图。
