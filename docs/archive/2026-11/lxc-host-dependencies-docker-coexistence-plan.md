# LXC 宿主依赖与 Docker 共存方案

日期：2026-11-07。本文为设计和验证计划；未在共享宿主安装 Incus，尚无 Docker 与 Incus 同机实测数据。

## 结论和边界

推荐首期采用**宿主安装 Incus daemon、LWT 容器内运行插件、经受控管理通道连接 daemon**的架构，先在独占 Debian 12 测试 VM 验证，再决定是否在目标宿主启用。安装包、启动服务、初始化存储、初始化网络和授权插件是独立操作。仅安装包通常不需要重建 Docker 容器；这不保证任意软件源、包脚本或宿主环境都不会影响业务。

已有只读检查显示当前 Dev 目标是 Debian 12，宿主同时运行 Docker、LWT 正式和 Dev 容器，未发现 Incus 客户端及默认 socket。此观察有时间范围，实施前须重新核对。当前没有该宿主上的 Incus 安装、Docker 共存、配额和网络实测证据，因此不能承诺对现有宿主与 Docker 零影响。

## 当前系统可复用的基础与缺口

| 能力 | 已有证据 | 缺口 |
|---|---|---|
| 插件平台 | `plugins/lxc-manager/plugin.json` 声明 `host` 范围的 Incus 服务依赖；平台区分依赖范围并提供安装引导 | 宿主依赖状态仍为未知；引导是人工命令，不是可信安装代理。Debian 12 需先配置受支持的软件源，当前 `apt install` 指令不会完成此步骤 |
| 环境发现 | `ServiceEnvironmentDetector` 已提供宿主发行版探测；现有终端可选择宿主目标 | 特权容器与 `nsenter` 不构成可靠的目标身份或最小权限边界；不能由第三方 manifest 自动执行任意宿主命令 |
| Incus 接入 | `cmd/lxc-manager/main.go` 用 Go 标准库 HTTP 客户端连接 `INCUS_SOCKET`，默认 `/var/lib/incus/unix.socket`；已实现能力发现和资源采样 | 仅支持 Unix socket；当前 Docker 部署不会自动挂载该 socket，TLS API 和受限代理尚未实现 |
| 任务与测试 | `internal/jobs`、`workflow` 已有持久任务、幂等、操作核对和模拟测试；`TestRealIncusLifecycle` 可在隔离环境显式运行 | 真实测试直接覆盖 Incus 适配器，不证明生产 HTTP 创建链路、限额和网络实际生效 |
| 创建预检 | `main.go` 传入空 `PreflightEvidence`，缺少证据时拒绝创建 | 缺可信容量预留和指定池硬配额证据；不能通过放宽检查实现假成功 |
| 底座 | Dev 部署脚本使用 Docker 特权容器和宿主 PID；正式部署使用 host 网络 | 这些是现有高权限能力，不代表为插件提供受限 Incus 授权；共享宿主变更须评估正式容器及其他 Docker 工作负载 |

`plugins/lxc-manager/go.mod` 当前只有 Go 版本声明，无第三方模块。现有标准库 HTTP、Incus REST 客户端和任务设施足以支持首轮只读原型，无需为了安装引导引入新库。

## 推荐实施架构

1. **宿主安装与初始化由管理员控制。** 针对 Debian 12 使用官方列出的受支持源，固定来源、签名及目标版本；先检查候选版本、依赖变更和包脚本，再分开执行安装、服务启动、存储初始化、网络初始化。当前 manifest 中的 shell 安装字符串只作为展示和人工审查内容，不作为宿主代理的可执行输入。[Incus 安装](https://linuxcontainers.org/incus/docs/main/installing/)
2. **管理通道首选宿主受限代理。** 在宿主部署独立服务，由管理员注册和维护；仅暴露发现、检查、受控实例操作等明确 API，验证调用方身份、项目、实例所有权及参数，记录审计。代理使用本地 socket 访问 Incus，不接受通用 shell、任意宿主路径或任意包/URL。代理仍具有较高权限，需要单独评审、认证、升级、撤销与故障恢复。该代理目前不存在。
3. **可选替代为 Incus TLS API。** Incus 原生支持 HTTPS 与客户端证书；限定监听地址、校验服务端证书、使用可撤销的受限证书和受限项目。插件需新增 TLS 适配器、证书存储与轮换。项目范围限制并不自动等于逐操作最小权限，具体操作授权需按实际版本与授权后端验证。[远程认证](https://linuxcontainers.org/incus/docs/main/authentication/)、[项目限制](https://linuxcontainers.org/incus/docs/main/howto/projects_confine/)
4. **直接挂载宿主 socket 仅用于独占测试。** 即使挂载标记为只读，socket 协议仍可能允许管理操作；`incus-admin` 可借宿主路径挂载等能力获得宿主 root 权限。现有特权容器已经具有较大宿主权限，新增 socket 仍会扩大插件可直接调用的接口面。[权限说明](https://linuxcontainers.org/incus/docs/main/explanation/projects/)
5. **管理状态与创建能力分开。** daemon 可达只说明连接成功；真实创建还需服务端可信容量预留、池配置指纹、硬配额验证及网络能力。证据缺失、过期、池配置改变或未知时继续拒绝创建。

## Docker 共存与影响范围

| 阶段 | 对宿主与 Docker 的可能影响 | 控制方式 |
|---|---|---|
| 添加源与安装包 | 改变软件源信任、包数据库、磁盘及可能的服务启动；不会仅因安装就必然创建网桥 | 审查来源和版本，APT 模拟解析新增/升级/移除包，核对服务单元及包维护脚本；保留包变更记录 |
| daemon 与接口 | 增加高权限服务、socket 和授权面 | 默认本地监听；限制代理调用与身份，TLS 仅监听选定管理网络，禁止跳过证书校验 |
| 存储池 | 与 Docker 数据卷、系统盘争用空间和 I/O；驱动可能涉及设备和挂载 | 测试使用明确标识的独立池、容量上限与预警；不得自动格式化现有设备；配额按驱动实测 |
| 网络 | 受管 bridge 可增加网卡、地址、dnsmasq、NAT 和 netfilter 规则；可能与 Docker 的转发策略、现有子网或规则冲突 | 先记录 Docker 网络、路由、nftables/iptables、FORWARD 策略及 IP forwarding；使用不重叠的测试网段，先测试无外网的隔离网络，再单独设计 NAT |
| 实例负载 | 与现有容器竞争 CPU、内存、PID、I/O、磁盘和出口带宽 | 设资源预算与告警，保留宿主余量；分级压测并观察现有业务延迟、错误率和资源 |

Incus 官方指出 Docker 在部分配置下将全局 `FORWARD` 默认策略设为 `DROP`，可能阻断 Incus bridge 流量；更改 Docker daemon、全局转发或防火墙策略也可能扩大现有服务可达范围。应针对实际规则设计和验证，不自动关闭防火墙、不卸载 Docker、不照搬全局放通规则。[Incus 与 Docker 网络说明](https://linuxcontainers.org/incus/docs/main/howto/network_bridge_firewalld/)、[Incus bridge](https://linuxcontainers.org/incus/docs/main/reference/network_bridge/)

## 模式、可靠性与性能

- **适配器**：抽象 `Backend` 的能力发现、实例操作和异步事件；分别实现现有 Unix socket、宿主受限代理及未来 TLS。连接目标携带稳定身份、证书指纹、版本和能力快照，避免仅凭容器检测推断宿主身份。
- **策略**：按发行版和版本选择受信任的包安装策略，按存储驱动选择配额证据策略，按网络能力选择隔离网络或经过审核的 NAT 策略。未知版本、证据失效和不支持的组合返回明确状态，不尝试通用 shell 回退。
- **状态机与协调**：安装任务采用 `Detected → Planned → AwaitingApproval → Installing → Verifying → Ready`；超时或中断进入 `NeedsReconciliation`，由实际包、服务及后端状态核对后继续。初始化存储、网络与授权各自有计划、锁和审计，禁止盲目重试或删除已有资源。
- **并发与开销**：包管理同一宿主串行；控制面复用 HTTP 连接、设置超时、限制并发并使用 Incus 操作 ID 和事件回查。只读能力缓存需携带目标、版本、配置指纹与过期时间；不可将瞬时空闲量当作可预留容量。实例性能由 cgroup、磁盘驱动、I/O 与网络决定，须实测而非预设结论。
- **容量预留**：在统一控制域中对容量做原子预留、提交和释放，按后端操作 ID 恢复；外部管理员和其他工作负载仍可能竞争资源，预留必须包含余量且明确覆盖范围。Incus 项目限制可补充约束，但部分限制约束的是实例配置总和，不能单独证明宿主可用容量或池硬配额。[项目配置](https://linuxcontainers.org/incus/docs/main/reference/projects/)

## 现有库与主流方案

| 方案 | 可借鉴内容 | 选型判断 |
|---|---|---|
| Incus REST API / 官方 Go 客户端 | Unix socket、TLS、API 扩展发现、异步操作及事件；[REST API](https://linuxcontainers.org/incus/docs/main/rest-api/)、[Go 客户端](https://github.com/lxc/incus/tree/main/client) | 与现有插件后端一致。现有标准库实现可先复用；引入官方客户端前核对 Go 版本、依赖体积、许可证和目标架构 |
| LXD | daemon、项目、存储和网络等管理模型；[LXD 文档](https://documentation.ubuntu.com/lxd/latest/) | 可以参考能力模型，版本、授权及 API 差异需独立适配，不能直接当作 Incus |
| Proxmox VE | `pct` 管理容器、cgroup 与存储集成；[PVE 容器文档](https://pve.proxmox.com/pve-docs/pct.1.html) | 适合已有 PVE 的节点；是独立平台与 API，不适合为当前宿主仅装依赖而整体迁移 |
| 直接 LXC 工具及绑定库 | Linux 容器基础设施与配置；[LXC 官方项目](https://linuxcontainers.org/lxc/introduction/) | 可借鉴底层机制；管理 API、网络、存储和权限编排仍需自行解决，增加维护面 |

## 执行和回归门禁

### 安装前宿主与 Docker 基线（2026-11-07）

在目标宿主通过只读 SSH 命令采集，未安装或配置 Incus。外部地址及 Docker 网段值不写入文档。

| 指标 | 基线 |
|---|---|
| 宿主系统 / 内核 | Debian 12；Linux `6.1.0-10-amd64` |
| 宿主负载容量快照 | 6 vCPU；总内存约 3915 MiB，可用约 2083 MiB；根分区使用率 80% |
| Docker | Engine `27.4.0`；`overlay2`；cgroup v2；Docker 数据目录 `/var/lib/docker` |
| Docker 工作负载 | 8 个运行容器；Docker 报告 0 个 unhealthy；3 个 Docker network，其中 1 个 bridge |
| 转发基线 | `net.ipv4.ip_forward=1`；iptables `FORWARD` policy 为 `DROP` |
| LWT 正式 / Dev | 均为 healthy；`/health` 均返回 HTTP 200；采样响应时间分别约 1.29 ms / 2.32 ms |
| Incus | CLI 未安装；默认 `/var/lib/incus/unix.socket` 不存在；APT 候选 `6.0.4-2+deb13u7~bpo12+1` |

这是一次瞬时基线，不代表长期容量或业务 SLA。 Docker 网络名称和子网、完整防火墙规则、端口占用及非 LWT 容器的业务探针尚未采集；需要在隔离测试机记录后再比较。

### 目标宿主 APT 只读安装模拟（2026-11-07）

在目标 Debian 12 宿主仅运行 `apt-cache` 与 `apt-get -s`，没有安装、升级或删除软件，也没有启动服务。

| 方案 | 模拟结果 | 判断 |
|---|---|---|
| 完整 `incus` 包，默认候选 | 当前候选为 Debian Backports `6.0.4-2+deb13u7~bpo12+1`；默认选中的 `qemu-system-x86` 为 `1:7.2+dfsg-7+deb12u18+b3`，低于 Incus 要求的 1:8.0 | 默认候选解析失败；宿主当前未安装 QEMU 包 |
| 完整 `incus` 包，指定 Backports | `apt-get -s -t bookworm-backports install --no-install-recommends incus` 可解；选择 QEMU `1:10.0.2+ds-2+deb13u1~bpo12+1`，模拟为升级 2 个、新增 37 个、移除 0 个包 | 技术上可以升级依赖并安装完整包；影响范围比容器专用方案大，尚未实际安装 |
| `incus-base`（当前容器能力） | 默认源模拟可解：升级 `login`、`passwd` 共 2 个包，新增 15 个包，移除 0 个包；不引入 QEMU | 当前插件创建请求固定为 `type: container`（见 `plugins/lxc-manager/internal/incus/instances.go`）；这是满足现有功能的较小依赖方案，但 daemon、存储、权限与网络行为仍未验证 |

这说明完整包的依赖可以通过现有 Backports 满足，不必升级 Debian 主版本；但 `-t bookworm-backports` 会让本次事务新增 37 个包。现有插件只创建容器，优先评估 `incus-base`，避免为未使用的 VM 能力引入 QEMU。Debian 12 的 Incus 官方安装文档也列出 Zabbly 支持的软件源；本机临时容器已验证 Zabbly 7.0 LTS 包级安装。目标宿主没有改源、安装或启动服务，Docker 共存仍未实测。参考 [Incus 官方安装说明](https://linuxcontainers.org/incus/docs/main/installing/) 和 [Zabbly 软件源说明](https://github.com/zabbly/incus/blob/daily/README.md)。

### 本机隔离环境包级验证（2026-11-07）

在 Windows 的 WSLC 临时 Debian 12 容器中验证了软件源和包安装；该容器不是独占 Linux VM，也没有启动 Incus daemon。

| 检查项 | 结果 |
|---|---|
| 软件源 | 使用 Zabbly Incus 7.0 LTS 的 Debian 12 `bookworm` 仓库；下载的签名密钥指纹与官方发布值 `4EFC590696CB15B87C73A3AD82CC8797C838DCFD` 一致 |
| 包候选 | `incus` 候选为 `1:7.0.2-debian12-202611021329`；`apt-get -s install --no-install-recommends incus` 显示 89 个安装动作 |
| 安装结果 | `incus`、`incus-base`、`incus-client` 均安装为 `1:7.0.2-debian12-202611021329`；客户端和 daemon 二进制版本均为 `7.0.2` |
| 运行路径 | Zabbly daemon 位于 `/opt/incus/bin/incusd`；直接启动需要包内库路径 `/opt/incus/lib`。设置 `LD_LIBRARY_PATH=/opt/incus/lib` 后版本检查通过 |
| 服务与初始化 | 容器没有 systemd；安装前设置临时 `policy-rc.d` 拒绝包脚本启动服务。没有运行 `incus admin init`，没有创建存储池、网桥、NAT 或防火墙规则 |
| 容器内核权限 | `CAP_SYS_ADMIN`、`CAP_NET_ADMIN` 缺失；cgroup 不可写，`/dev/fuse`、`/dev/kvm` 不存在 |
| Docker 共存 | 未验证。WSLC 后端只提供普通容器运行选项，且临时 Debian 容器缺少上述内核权限；不能运行 Incus daemon 或代表宿主网络行为 |

仓库文档确认 Debian 12 使用 Zabbly 包仓库，且该仓库提供 7.0 LTS；安装方法和密钥指纹参考 [Incus 官方安装说明](https://linuxcontainers.org/incus/docs/main/installing/) 与 [Zabbly Incus 仓库说明](https://github.com/zabbly/incus/blob/daily/README.md)。本次只证明仓库签名核验、APT 解析、依赖安装和二进制可加载，不证明服务启动、创建实例、硬配额或网络行为。

本机未发现 QEMU、VirtualBox、VMware `vmrun` 或 Vagrant 命令。尝试导入独占 Debian WSL 实例时，导入与后续 WSL 管理调用卡住；临时实例 `LwtIncusDebian12Lab` 留有未完成注册状态。为避免破坏 WSL 数据，没有手工清理注册信息或磁盘文件。当前没有可用的隔离 VM，所以本轮没有安装/初始化 daemon、Docker 共存测试、性能复测或代码优化。缺少有效宿主对照数据时，不应通过改变 Docker 转发、防火墙、Incus 网桥或宿主服务来声称完成优化。

**后续门槛**：先恢复 WSL 管理状态，或提供可销毁快照的独占 Debian 12 VM；在该环境完成 daemon 启动、Docker 业务探针、Incus 初始化与网络前后差异采集，再根据真实回归点实施最小优化并复测。共享生产宿主仍保持未安装 Incus、未改配置的状态。

1. **只读基线**：记录宿主发行版/内核、包源、包候选和模拟事务、systemd、cgroup、磁盘/挂载、Docker 版本及容器健康、Docker bridge/子网、路由、转发和防火墙摘要。记录生产接口和代表性 Docker 业务的健康、连接、P50/P95 延迟与错误率；日志脱敏。执行前检查诊断命令的实际副作用。
2. **隔离机安装**：在独占、可恢复的 Debian 12 VM 完成上述基线，安装指定版本后检查包变更、服务、socket 和重启行为；先不初始化网络/存储，验证现有 Docker 业务。依赖安装失败、源不可用、包锁、重启中断及重复请求要有可恢复状态。
3. **存储与网络分步验证**：先在专用受限存储池验证创建、写满、快照、回收、重启及硬配额；再使用不重叠测试网段验证 DHCP、DNS、NAT、端口冲突、路由和防火墙重载。分别比较 Docker 出入站、容器间、宿主到容器及现有正式端口；记录规则差异与回滚结果。存储写满和规则注入仅在隔离机执行。
4. **插件端到端**：先运行现有 `TestRealIncusLifecycle`，再在可信证据提供者完成后通过插件平台验证认证、授权、资源预留、创建/启动/停止、幂等和断线重启核对。缺失或过期证据必须拒绝创建；未经授权项目/路径及其他实例必须被拒绝。
5. **性能与恢复**：以 1、5、10 等逐级实例和并发请求测试，采集宿主与 Docker 工作负载 CPU、RSS、磁盘、I/O、连接数、网络吞吐及 P50/P95/P99；检查 daemon/代理重启、磁盘不足、包锁、证书撤销、网络中断及操作返回丢失。阈值先依据业务基线和资源预算约定，超阈值停止，不预先声称零影响。
6. **共享宿主放行**：在目标机重新做只读基线和具体安装/初始化计划，列包版本、设备、网段、规则差异、权限、影响与可执行恢复步骤；确认维护窗口和该具体变更授权后逐阶段执行并复测。卸载包不自动等于恢复网络、存储或实例数据；只清理经确认属于本次测试的资源。

相关记录：[宿主安装影响](incus-host-install-impact-assessment.md)、[依赖安装可行性](lxc-host-dependency-installation-feasibility.md)、[真实环境验证计划](lxc-real-environment-validation-plan.md)。

### 本次真实宿主安装执行记录（2026-11-07）

**用户授权范围**：在现有 Debian 12 宿主仅安装 `incus-base`（不使用 `--no-install-recommends` 以外的额外包范围）；不运行 `incus admin init`，不初始化存储池或网桥，不开放 Incus HTTPS API，不连接插件 socket，不创建或启动实例。安装前必须重新确认目标身份、Docker 基线和 APT 模拟事务；若与上文记录的 2 个升级、15 个新增、0 个移除不一致，停止安装并先复核差异。

**本次结果：未执行安装。** 本地已确认目标主机的 SSH 主机密钥曾记录在 `known_hosts`，但此次可用的安全远程执行通道未能通过自动审查；仓库远程部署脚本会关闭 SSH 主机密钥校验，不适用于本次宿主写操作。为避免在未能通过强制主机身份校验和实时基线复核的情况下修改共享宿主，没有继续调用该脚本或绕过校验。

用户要求继续后再次检查远程通道，Tunnel runtime 返回 `tunnel_client_not_seen`（客户端超过 300 秒未连接）。因此本轮无法执行目标主机只读复核或安装；没有对宿主产生写操作。

随后再次重试时 Tunnel 已恢复，Runner 状态为在线；但严格 SSH 预检命令在本机进程启动前被执行策略拦截，未建立 SSH 会话，也未运行任何宿主命令。故安装仍未执行。不能将恢复 Tunnel 误记为宿主可达或安装成功。

因此，本次没有在宿主运行 APT 安装，也没有改变软件包、服务、socket、存储、网络、防火墙或 Docker 状态。上述 2026-11-07 的只读基线与 APT 模拟记录仍是此前采样，不能当作本次安装前复核或安装后验证。恢复可信 SSH 执行通道后，按本节授权范围重新采集基线并核对 APT 模拟结果，再执行安装和 Docker/Incus 状态复测；若安装包脚本自动启动服务，只记录其状态，不自动初始化或调整宿主网络。
