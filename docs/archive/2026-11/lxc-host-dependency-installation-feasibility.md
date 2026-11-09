# LXC 插件宿主依赖安装与环境选择方案

## 结论

可以让 LinuxWebTool 继续运行在容器里，同时将 Incus 服务安装在指定 Linux 宿主，并让 LXC 插件通过受控通道连接它。需要明确的是：安装位置、依赖探测位置、插件进程位置和 Incus 服务位置是四个不同概念。宿主安装成功本身不能让容器内进程自动看到宿主二进制或 socket，也不能证明创建、资源配额和网络已经可用。

建议将依赖目标建模为 `container`、`host` 或 `external`，将容器检测结果用作**建议和展示信息**；宿主目标及具体主机必须由部署配置明确指定。检测不确定时显示 `Unknown`，禁止自动切换目标。当前尚无已验证的隔离 Incus 测试宿主，因此本调研没有安装软件、修改宿主网络或迁移进程。

## 当前项目可复用能力与缺口

| 现状 | 证据 | 对宿主依赖的限制 |
|---|---|---|
| 插件依赖元数据 | `PluginModels.cs` 的 `PluginDependencies`、`PluginDependencyConfig` 支持二进制探测和按发行版安装引导 | 模型没有依赖安装目标、宿主身份、服务健康或 quota 能力；引导字符串不等于可靠安装器 |
| 环境和依赖探测 | `PluginManager.Environment.cs` 读取当前进程的 `/etc/os-release` 并运行 `command --version`，结果有短时缓存 | 容器内检查的是容器发行版与 PATH；即使找到客户端，也不能证明宿主 daemon、授权和配置可用 |
| 前端安装引导 | `PluginInstallGuideModal.js` 和 `pluginCommandBuilder.js` 已提供安装指引，界面提示容器和宿主文件系统隔离 | 可复用展示和交互，不能直接把当前探测结果解释为宿主状态；安装命令须按目标和发行版独立生成 |
| 宿主执行能力 | `OneShotExecutionStrategy.cs` 存在 `nsenter` 宿主执行路径，系统服务页面也具备相关功能 | 这是一项高权限通用能力，不自动构成可信的依赖安装协议；容器内 PID 1 未必是宿主 PID 1，也不能据此证明目标身份 |
| 插件运行 | `plugins/lxc-manager/plugin.json` 当前是 `resident`、`native`，没有声明 Incus 依赖；其客户端支持配置本地 Unix socket | 现有 resident 插件仍由容器内平台调度；主机 socket 未挂载时不可访问。只安装宿主客户端或服务不会自动建立连接 |
| 验证基础 | `plugins/lxc-manager/internal/incus` 已有能力探测、模拟测试与默认跳过的真实生命周期测试 | 当前生产创建预检没有可信容量和硬配额证据，依然拒绝创建；安装成功不能绕过它 |

现有 `PluginStandardEnvBuilder` 使用当前发行版检测值作为环境投影；名称中的 `HOST` 不能单独证明这是物理宿主。首次实现应先明确其语义和兼容性，避免改变已有插件行为。

## 依赖声明与探测设计

在保持旧 manifest 行为的前提下，为新版依赖项设计结构化字段，例如：依赖 ID、`scope`（`container`/`host`/`external`）、能力类型（客户端、服务、socket、内核控制器、存储配额）、版本约束、只读探测方法、安装策略 ID、是否必需。示例仅表示意向，字段尚未实现：

```json
{
  "id": "incus-service",
  "scope": "host",
  "kind": "service",
  "required": true,
  "installPolicy": "incus-supported-package",
  "probe": { "kind": "incus-api" }
}
```

不要把 manifest 中任意 shell 字符串作为宿主 root 命令执行。宿主安装器只能接受受信任、版本化的策略 ID 和经校验的参数；第三方插件声明只能请求能力，不能自行扩展可执行命令。普通容器内运行时依赖继续沿用现有探测，宿主能力使用宿主执行代理或明确配置的远程管理通道分别探测。依赖状态至少区分 `Unknown`、`Missing`、`Installed`、`ServiceUnavailable`、`Unauthorized`、`Ready`、`Unsupported`；`Ready` 只说明对应探测成立，不代表存储硬配额已验证。

容器识别可综合 `/.dockerenv`、`/run/.containerenv`、cgroup、mount 和 PID namespace 信息，但这些信号可能缺失、被覆盖或处于嵌套容器；只能得出带来源的判断，不应作为宿主身份和授权依据。安装目标需要稳定主机 ID、明确的连接配置和操作前二次核对。Docker Desktop 的 daemon 可能位于 Linux VM，所谓宿主也可能是该 VM，而不是 Windows 系统。

## 安装与连接架构

推荐优先在宿主部署独立、最小权限的管理代理，由管理员预先安装和注册。容器内 LWT 通过经过认证的本地受控通道向代理请求有限操作：探测、生成计划、执行已批准计划、查询任务和核对。代理只允许内置策略，不接受任意 shell、任意路径、包名或 URL；安装行为采用结构化参数、固定可执行程序及参数数组，记录目标、操作者、操作 ID、步骤和结果，日志不含凭据。代理本身仍可能需要高权限，其信任边界、更新、身份认证、审计、撤销和卸载必须单独设计和验证。

可选部署方式对比：

| 方式 | 优点 | 限制和风险 | 建议 |
|---|---|---|---|
| 宿主管理代理 + 受控操作 | 容器保留现有部署，操作可白名单和审计 | 要单独安装、认证和维护高权限代理；需处理通信中断和版本兼容 | 首选原型；先做只读探测 |
| 管理员手工在宿主安装 Incus，明确挂载宿主 socket | 实现较少，复用现有 Incus 客户端 | socket 是高权限管理接口；挂载要求重建容器，访问控制和部署需单独审查 | 隔离测试环境的首期方案 |
| Incus 远程 TLS API | 容器无需绑定宿主文件路径，可连接独立管理节点 | 证书、服务端身份、权限、网络暴露和撤销需要管理；当前插件仅支持 Unix socket | 后续独立适配，先验证需求 |
| 容器直接 `nsenter` 执行安装命令 | 可借鉴现有执行基础设施 | 依赖容器特权、PID namespace 和目标识别，权限范围大；错误目标或发行版可破坏宿主 | 不作为自动安装默认方案 |
| 整个 LWT 改成宿主服务 | 理论上可减少一层容器边界 | 涉及安装包、服务账号、端口、持久数据、插件、升级和回滚；明显扩大本次范围 | 不属于本方案前提 |

Docker 文档说明绑定挂载默认可写且可能修改宿主文件；`--privileged` 会显著扩大权限。即使 socket 文件以只读挂载，协议本身仍可能允许写操作，不能把只读挂载视为只读 API。参见 [Docker bind mounts](https://docs.docker.com/engine/storage/bind-mounts/) 和 [容器运行权限](https://docs.docker.com/engine/containers/run/)。Incus 官方提供 Linux 包安装和服务初始化指引，具体发行版、版本与包源必须在目标机确认：[Incus 安装](https://linuxcontainers.org/incus/docs/main/installing/)。

## 安装事务与状态机

将安装设计为持久任务：`Detected → Planned → AwaitingApproval → Installing → Verifying → Ready`；失败进入 `Failed`，结果不确定进入 `NeedsReconciliation`。规划阶段只读，输出目标主机身份、发行版、包源、待安装/升级包及版本、服务和组变更、预期网络与存储影响、预计中断、回滚限制和审计信息。对生产宿主、包管理器和系统服务的写操作需要明确授权该具体计划。

执行时串行化同一宿主上的包管理任务，检查包管理锁和剩余空间；每一步持久记录开始、结果和外部任务 ID。网络超时或进程退出后先核对实际状态，再决定继续，不能盲目重试安装、初始化或删除。验证至少包括包版本、daemon、Unix socket/TLS 身份、认证、API 扩展、user namespace、cgroup、存储池和网络的只读检查。安装 Incus 与初始化存储池/网桥分别列计划：后者会影响宿主磁盘、路由和防火墙，不能自动跟随安装。

回滚需要逐项评估：自动卸载新装包可能删除其他服务开始使用的依赖，初始化后的池或网络可能已有数据；默认只停止继续执行并给出人工恢复步骤，不自动删除共享包、池、实例或网络。安装器必须区分已存在资源和本任务创建的资源，保留重启后的审计与核对信息。

## 模式、性能和主流参考

- **适配器**：以 `EnvironmentTarget` 和 `DependencyBackend` 区分容器、本机宿主和远程节点；宿主代理、现有容器探测、未来 TLS 客户端各实现相同的能力查询接口，返回来源和目标 ID。
- **策略**：按发行版、版本与包源选择内置安装策略；未知发行版、版本冲突或不支持的权限模型直接显示不支持，不降级为通用 shell。库选型可评估官方 [Incus Go 客户端](https://github.com/lxc/incus/tree/main/client) 和 REST API；先验证依赖体积、Go 版本、授权、并发与目标架构，不为现有标准库能力重复引入依赖。
- **状态机和协调器**：复用项目已有插件状态机与任务持久化思路，但宿主安装任务应与插件运行状态分开；通过幂等键、目标锁、步骤状态和重启核对处理并发及失败。
- **性能**：安装是低频控制面操作，瓶颈主要在包下载、包管理锁和服务启动；后台任务、合理超时及缓存只读探测即可。缓存必须带目标和时间戳，安装后主动失效；不得用缓存或瞬时空闲量承诺硬容量。
- **业界方案**：Incus 的管理服务与客户端分离，支持 Unix socket 或经认证的远程 API；LXD 有类似的服务、网络和存储管理，但协议、版本和权限不应假定相同；PVE 使用其独立的主机和容器管理模型，需要单独适配。参见 [Incus REST API](https://linuxcontainers.org/incus/docs/main/rest-api/)、[LXD 文档](https://documentation.ubuntu.com/lxd/latest/) 和 [PVE 容器管理](https://pve.proxmox.com/pve-docs/pct.1.html)。

## 验证与实施顺序

1. 在不修改宿主的条件下实现目标识别和只读能力探测；测试容器、裸机、嵌套容器、远程 Docker daemon、PID namespace 不共享及未知环境，确保不会错误选择宿主。
2. 扩展 manifest 模型与 UI，兼容旧插件；测试容器依赖与宿主依赖分离、缺少代理、错误主机 ID、过期缓存和未授权请求。原始 manifest 命令不得进入高权限执行路径。
3. 在独占、可丢弃 Linux VM 上实现宿主代理的安装计划和执行，验证不同发行版、包锁、断网、失败、重复请求、进程重启和计划过期；记录实际版本和步骤。
4. 在同一隔离环境验证 Incus 服务连接和既有真实生命周期测试，再验证插件端到端创建、CPU/内存/磁盘实际限制、网络与并发恢复；测试条件见 [实机验证方案](lxc-real-environment-validation-plan.md)。
5. 验证完成后，针对具体生产宿主生成可审查的安装与初始化计划，由授权操作者确认目标和影响，再执行；容量预留与硬配额证据未建立前保持创建预检拒绝。

本方案为调研与设计，不声称已实现宿主安装、自动迁移、实机配额或网络验证。

## 首轮实现与开发环境检查（2026-11-07）

- 依赖元数据新增 `scope`、`kind`、`probeStatus` 和可选安装说明 URL。旧 manifest 缺少 `scope` 时继续按 `container` 处理；未知 scope 不会被当作本机依赖执行或安装。
- 插件安装引导按目标过滤命令；仅宿主范围依赖时默认选择宿主，并按 `Services/Environment` 可信探测的 `HostDistroKey` 选择包管理器。未确认发行版时宿主命令留空。无宿主执行通道时只允许复制，终端执行会拒绝。
- 环境检测只在已确认特权容器且宿主 PID/systemd 探测成功后，通过固定只读 `nsenter ... cat /etc/os-release` 获取宿主发行版；结果缓存遵循现有环境服务 TTL。此能力不是宿主安装代理，也不确认 Incus 服务、socket、授权、存储配额或网络状态。
- `-CheckOnly` 与只读宿主探测确认当前 Dev 目标是 Debian 12；同一主机同时运行正式 `linuxwebtool` 与开发 `linuxwebtool-dev`，未发现 Incus 客户端和默认 Unix socket。为避免在承载正式服务的共享主机上安装高权限容器管理服务，本轮只部署应用到 Dev，不安装 Incus、不添加包仓库、不初始化存储池或网桥。
- 单元测试覆盖旧 manifest 默认容器范围、宿主范围往返序列化、范围隔离和发行版映射；前端测试覆盖按目标生成安装命令和未知宿主发行版失败关闭。实机创建、宿主 daemon 安装、socket/TLS 授权、配额与网络实效仍待独占测试 VM 验收。

## 开发部署与浏览器验证（2026-11-07）

- 完整 `.NET` 单元测试通过：403/403；LXC 插件 `go test ./...` 通过；前端依赖范围测试 4/4、插件打包门禁 1/1、前端架构门禁通过。
- `scripts/deploy-remote.ps1 -Mode Dev` 成功重建并启动 `linuxwebtool-dev`；开发容器健康状态正常，`/health` 返回 HTTP 200。正式容器未重建。
- 浏览器验收确认 LXC 插件保持“缺少依赖”，安装向导默认目标为宿主，识别宿主发行版 `debian`，并显示相应安装说明与命令；页面无 Console Error。唯一浏览器 warning 是现有 Tailwind CDN 生产使用提示。
- 将执行目标中的“当前容器（推荐）”改为中性“当前容器”，避免宿主依赖默认选择宿主时 UI 推荐与当前选择冲突；前端热更约 4 秒完成，强制刷新后浏览器确认新文案生效。
- 为验证宿主执行路由，向“宿主终端”发送只读命令 `grep '^ID=' /etc/os-release`，结果为 `ID=debian`，与宿主发行版探测一致；随后结束了本次终端会话。该结果证明在当前 Dev 部署权限下可以通过宿主命名空间执行命令，不表示 Incus 已安装或连接已就绪。
- 未安装 Incus：开发和正式实例共用 Debian 12 宿主，目标没有 Incus 客户端或默认 socket；添加受支持包源、安装 daemon、初始化存储/网络都不属于无风险的快速部署步骤。宿主安装与 Incus 生命周期、实际 CPU/内存/磁盘配额及网络行为仍须在独占测试 VM 验收。

## 宿主安装影响评估（2026-11-07）

整理后的独立报告见 [Incus 宿主安装影响评估](incus-host-install-impact-assessment.md)。

### 目标主机现状

本轮只读检查显示，Dev 部署目标为 Debian 12，同时承载 LWT 正式容器、开发容器和 Docker 工作负载；此前未发现 Incus 客户端或 `/var/lib/incus/unix.socket`。因此以下是按 Incus 官方文档和已知主机现状作出的影响评估，不代表已在该机执行安装或变更。

### 按操作阶段拆分

| 操作阶段 | 会改变什么 | 对现有宿主服务的影响面 | 评估 |
|---|---|---|---|
| `apt update` | 刷新本机 APT 软件包索引，需要访问软件源 | 不会启动 Incus，也不会配置 Docker 网络；索引和下载会产生少量磁盘、网络与镜像源访问 | 低 |
| 配置 Debian 12 支持的软件源 | 添加 APT 源和签名信任配置 | 改变后续软件选择与更新来源；Debian 12 的 Incus 包按官方安装说明来自 Zabbly，而非 Debian 13 起才提供的原生包 | 低至中 |
| 安装 Incus 包 | 写入程序、依赖、服务单元及系统配置，产生包管理器事务 | 改变宿主包数据库和磁盘占用；服务启停行为应以目标包及其维护脚本为准。单独安装包不等于已初始化网桥、存储池或实例 | 中 |
| 启动 Incus daemon | 运行宿主级管理服务并提供本地 API socket | 扩大常驻服务和管理权限面；默认通过本地 Unix socket 使用，网络 API 需另外配置 | 中 |
| 配置用户/证书授权 | 调整谁能访问管理 API | `incus-admin` 权限可管理宿主文件系统挂载和实例安全选项，接近宿主 root；不应直接授予 LWT 插件。优先使用受限 TLS 证书和受限项目 | 高（授权错误时） |
| `incus admin init` 配置存储 | 创建或指定存储池，下载镜像/创建实例后会消耗池容量 | 使用共享目录、loop 文件或现有文件系统可能与宿主数据争用磁盘；使用专用块设备/分区可以隔离容量，但需要独立设备与预先规划 | 中至高 |
| `incus admin init` 配置网络 | 可能创建 `incusbr0`，并配置地址、DHCP、DNS、IPv6 RA、NAT 和防火墙规则 | 添加宿主网络接口、转发和 netfilter 状态；错误子网、路由或防火墙策略可能影响容器出入站或与现有 Docker 网络冲突 | 高 |
| 创建并运行容器 | 启动宿主内核上的额外工作负载 | 共享 CPU、内存、I/O、磁盘、PID 和网络；限制设置与存储后端不匹配时可能超用资源 | 随实例规格和负载变化 |
| LWT 接入 Incus | 绑定宿主 socket，或开放并连接 TLS API | socket 接入会把宿主管理能力交给 LWT；远程 TLS 可不挂宿主路径，但需证书、服务端身份校验、网络访问策略和范围受限的项目 | 高（需单独设计） |

### 对 Docker/LWT 的具体关联

- Incus 与 Docker 都会使用 Linux 网络转发和 netfilter。Incus 官方文档明确记录同机运行时可能遇到网络转发问题；常见成因是 Docker 把全局 `FORWARD` 默认策略设为 `DROP`，导致 Incus bridge 的流量转发失败。修复方案可能涉及 Docker daemon 转发选项、IPv4 forwarding 顺序或 `DOCKER-USER` 规则，均会改变宿主网络策略，不能未经盘点直接套用。[官方 Docker/Incus 网络兼容说明](https://linuxcontainers.org/incus/docs/main/howto/network_bridge_firewalld/)
- 创建受管 bridge 时，Incus 会在宿主创建 Linux bridge，并运行本地 `dnsmasq` 提供 DHCP/DNS 等服务；默认可以配置 NAT 和防火墙规则。宿主已有 Docker bridge、转发规则、子网路由及防火墙管理器都需要先盘点。[Incus bridge 文档](https://linuxcontainers.org/incus/docs/main/reference/network_bridge/)
- Incus 本地 API 默认通过 Unix socket；开放 HTTPS API 是单独的配置步骤。将宿主 socket 挂载进 LWT 容器虽然省去端口与 TLS 配置，但会扩大 LWT 受损后的宿主控制范围。Incus 文档指出管理员可通过挂载宿主路径等操作获取宿主 root 能力；应优先考虑单独管理节点及受限 TLS 凭证。[Incus REST API](https://linuxcontainers.org/incus/docs/main/rest-api/)、[Incus 权限边界](https://linuxcontainers.org/incus/docs/main/explanation/projects/)
- 安装 Incus 不会把 LWT 从 Docker 容器迁移到宿主，也不等于自动配置创建实例所需的网络和存储。`apt install incus`、daemon 启动、`incus admin init` 和实际创建实例是四种不同阶段。

### 本机建议与预检清单

1. 不在承载正式和 Dev LWT 的共享宿主上直接执行初始化向导。即使包安装本身没有修改现有网络，也必须先确认包源、依赖变更、systemd 单元、磁盘余量和恢复方式。
2. 如果确需评估“只安装包”，先在目标机执行不产生变更的 APT 模拟解析并审核新增/升级/移除包，再维护窗口内安装；禁止在安装命令中隐式运行 `incus admin init`、改转发或添加防火墙规则。
3. 在独占测试 VM 先验证 daemon、存储池、bridge、Docker 共存、重启持久性、回滚和配额行为。测试 VM 应有独立资源上限与快照；若 VM 仍运行于同一物理宿主，宿主仍承担其资源和网络影响。
4. 插件生产接入优先使用独立 Incus 节点的 TLS API、服务端证书指纹校验、短期可撤销凭证和受限项目；不把 `incus-admin` 组、宿主 root socket 或任意 `nsenter` shell 命令交给插件。

官方参考：[Incus 安装（Debian 版本与软件源）](https://linuxcontainers.org/incus/docs/main/installing/)、[Incus 初始化](https://linuxcontainers.org/incus/docs/main/howto/initialize/)、[存储池与数据位置](https://linuxcontainers.org/incus/docs/main/explanation/storage/)、[Docker 与 Incus 网络兼容](https://linuxcontainers.org/incus/docs/main/howto/network_bridge_firewalld/)、[TLS 远程认证](https://linuxcontainers.org/incus/docs/main/authentication/)。
