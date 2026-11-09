# Incus 宿主安装影响评估

评估日期：2026-11-07。本文依据此前的只读环境检查和 Incus 官方文档，分析在当前 LWT 宿主安装 Incus 的影响；并非实机安装结果。

## 结论与当前环境

此前检查显示，目标宿主运行 Debian 12，承载 Docker、LWT 正式容器及开发容器；未发现 Incus 客户端和默认 Unix socket。安装软件包会修改宿主软件包及服务状态，初始化存储和网络、授权管理接口及运行实例还会产生各自的影响。这些操作不必然导致现有服务中断。

Debian 12 的受支持 Incus 包需先配置适用的软件源；官方文档列出 Zabbly 软件源。当前插件向导提示该前置条件，但生成的 `apt install` 命令不会配置软件源。[Incus 安装说明](https://linuxcontainers.org/incus/docs/main/installing/)

## 各阶段影响

| 阶段 | 宿主变化及影响 | 风险判断 |
|---|---|---|
| 更新索引、配置软件源 | `apt update` 刷新包索引；添加软件源及签名信任会改变后续包的来源。应先审核来源和候选版本。 | 低至中 |
| 安装软件包 | 写入程序、依赖及服务相关文件，修改包数据库并占用磁盘。依赖变更和服务是否自动启动取决于具体包及系统配置。 | 中 |
| 启动 daemon | 增加宿主管理进程和本地 API socket。Incus 默认不对网络开放管理 API，远程访问需另行配置。 | 中 |
| 初始化存储 | 根据所选驱动创建或指定存储池；镜像、实例和快照消耗空间与 I/O。共享宿主文件系统时可能与现有业务争用容量。 | 中至高 |
| 初始化网络 | 可能创建网桥、地址、DHCP/DNS、NAT 和防火墙规则；具体变化由初始化选择决定。 | 高，需结合现有 Docker 网络评估 |
| 授权 LWT 接入 | 挂载管理 socket 或开放 TLS API 会授予插件管理能力。`incus-admin` 可通过宿主路径挂载等操作获得宿主 root 能力。 | 高，取决于授权范围 |
| 运行实例 | 实例与现有业务共享 CPU、内存、磁盘 I/O、网络和宿主内核；实际影响取决于负载、限额和存储配置。 | 随负载变化 |

安装软件包、启动服务、执行 `incus admin init` 和创建实例是独立阶段。安装 Incus 不会将 LWT 从 Docker 容器迁移到宿主，也不等于已验证实例的配额和网络行为。

## 对当前 Docker 宿主的重点影响

Incus 受管网桥可创建 Linux bridge 和 `dnsmasq` 服务，并配置 NAT 及防火墙规则。Incus 官方指出，与 Docker 同机时，Docker 的全局 `FORWARD` 策略可能阻断 Incus 网桥转发；处理方式可能涉及宿主转发设置或防火墙规则，应先盘点现有子网、路由和规则，不能直接套用通用命令。[Incus 网桥](https://linuxcontainers.org/incus/docs/main/reference/network_bridge/)、[Docker 共存说明](https://linuxcontainers.org/incus/docs/main/howto/network_bridge_firewalld/)

存储池可使用共享文件系统或独立设备。共享文件系统会与宿主业务争用空间；独立设备需要预先确认目标和容量。硬配额是否可靠取决于实际存储驱动、文件系统和配置，须在目标组合上实测。[Incus 存储说明](https://linuxcontainers.org/incus/docs/main/explanation/storage/)

## 低影响实施路径

优先在独占测试 VM 或独立 Linux 节点安装并初始化 Incus，再通过 TLS API 接入 LWT。远程端应校验服务端身份，使用可撤销的受限证书和受限项目。当前插件仅支持本地 Unix socket，远程 TLS 接入需要增加适配能力；如果测试 VM 与正式服务共用物理宿主，仍需限制其资源占用。[远程认证](https://linuxcontainers.org/incus/docs/main/authentication/)、[限制项目访问](https://linuxcontainers.org/incus/docs/main/howto/projects_confine/)

如果后续确需在当前宿主安装，应先只读盘点 APT 源、候选版本和模拟安装结果，检查磁盘余量、Docker 网络、路由、防火墙及恢复方式。软件安装、存储初始化、网络初始化和插件授权应分别形成可审查的步骤；安装前不能假定某个包会如何启停服务。涉及生产宿主的变更须针对具体计划授权。

## 验证状态

已验证 Dev 容器可以通过宿主终端执行只读发行版检查，结果为 Debian。尚未在该宿主添加软件源、安装 Incus、初始化存储或网络，也未实测 Incus 与 Docker 的共存、真实实例创建或资源配额。具体包变更、服务行为和网络影响仍需在目标环境核对。
