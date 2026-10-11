# EasyTier 容器局域网路由冲突：原因与处理方案

日期：2026-10-11。状态：完成调查记录，暂不实施修复或调整部署。

## 1. 现场与结论

宿主机 Tower 的局域网地址为 `10.126.1.61/24`，接口为 `br0`。LinuxWebTool 使用 Docker `bridge` 网络，容器地址为 `172.17.0.5/16`，网关为 `172.17.0.1`。EasyTier 为 CoreBinary 2.6.4，节点 fn 的虚拟地址为 `10.126.127.3/24`；另一台服务器的 `10.126.127.1/24` 与其属于同一虚拟网络，地址写法正确。

启动前，容器经 Docker 网关访问局域网：

```text
default via 172.17.0.1 dev eth0
172.17.0.0/16 dev eth0 scope link src 172.17.0.5
10.126.1.55 via 172.17.0.1 dev eth0 src 172.17.0.5
10.126.1.60 via 172.17.0.1 dev eth0 src 172.17.0.5
```

EasyTier 启动后，容器新增：

```text
10.126.1.0/24 dev tun0 metric 65535
10.126.127.0/24 dev tun0 scope link src 10.126.127.3
```

`10.126.1.0/24` 比默认路由 `/0` 更具体，因此即使 metric 为 65535，仍会优先进入 tun0。这会改变 SMB 目标 `.55/.60` 的出口；对于源地址属于该局域网的管理请求，响应也可能走错出口，表现为登录和 API 长时间无响应。

已确认的是容器内发生路由接管，并非已经证明 .NET 进程死锁，也不代表所有请求来源都必然受影响。尚未确认 `10.126.1.0/24` 来自哪个对端、手动路由或其他配置；需要核对运行配置和 EasyTier 路由详情。不要导出完整含密钥的 TOML。

时间线还显示：本次 2026-10-11 02:20:39 UTC 启动前，已有 SMB `df/umount` 的 `State=D Wchan=wait_for_response`。本次路由问题可能影响当前 SMB 连接，但不足以解释全部历史 SMB 故障。

## 2. 为什么特权容器也会发生

特权决定可执行的操作，网络模式决定所处的网络命名空间。`privileged + bridge` 默认仍读取容器自己的网卡和路由，并不会自动看到宿主机 `br0` 的直连网段。

容器只看到 `172.17.0.0/16` 和默认网关，不知道网关后面的 `10.126.1.0/24` 是宿主机本地局域网。特权允许 EasyTier 创建 TUN 和修改容器路由，并不能自动提供宿主机网段保护。

## 3. 官方机制与本项目缺口

官方默认接收代理网段并配置路由；手动路由可覆盖自动学习结果。官方曾有“首节点正常，第二节点启动后本地网段进入 tun0”的报告，不能认为官方在所有部署方式下都免疫。[官方子网代理文档](https://www.easytier.cn/guide/network/point-to-networking.html)、[issue #1840](https://github.com/EasyTier/EasyTier/issues/1840)

官方修复 #2079 改用有效代理路由集合，#2149 排除自身 peer ID；v2.6.4 已包含自身 peer ID 过滤。这个机制不等于识别并排除所有经默认网关可达的宿主机局域网，单纯升级不能保证解决当前问题。[#2079](https://github.com/EasyTier/EasyTier/pull/2079)、[#2149](https://github.com/EasyTier/EasyTier/pull/2149)、[v2.6.4 路由源码](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier/src/peers/peer_ospf_route.rs#L3644-L3653)

本项目源码核对：

- `src/LinuxWebTool.Infrastructure/Features/EasyTier/Adapters/EasyTierNetworkGuard.cs`：只比较虚拟 IPv4 与运行节点、本机接口网段，不检查对端动态发布的代理路由，也无法通过容器接口列表发现宿主机 LAN。
- `src/LinuxWebTool.Infrastructure/Features/EasyTier/Adapters/EasyTierConfigGenerator.cs`：两个生成方法先输出 `[network_identity]`，再输出应位于顶层的 listeners/routes。空行不会结束 TOML 表；routes 会落到 network_identity 或最后一个 peer/proxy_network 表中。手动路由配置可能未被内核读取。
- 同一生成器仅在 routes 非空时输出字段，无法表达显式 `routes = []`。无原生库时的校验主要检查 `inst_name` 字符串，不能验证实际字段作用域。
- `EasyTierNodeManager.StartNodeInternalAsync` 拉起进程后记录成功，未验证管理目标路由和网络就绪。现有同机虚拟网段冲突测试不模拟真实对端代理路由。

这些缺口已记录，按本次要求暂不修改。

## 4. 方案 A：Linux 宿主机使用 host 网络

适用：希望 EasyTier 和 WebTool 使用宿主机网络，接受网络命名空间共享。

调整部署时将网络模式设为 host，保留已有数据目录、镜像、环境变量、设备与必要权限配置。以下仅为需合入现有 Compose 服务的片段，并非完整部署文件：

```yaml
services:
  linuxwebtool:
    network_mode: host
    # 移除 ports；保留原有其他配置。
```

Linux Docker Engine 的 host 模式共享宿主机网络命名空间，端口映射不生效。WebTool 直接占用宿主机 5270，EasyTier 监听端口与 RPC 端口也需避免宿主机其他进程冲突。[Docker 官方说明](https://docs.docker.com/engine/network/drivers/host/)

现场宿主机已有 `10.126.1.0/24 dev br0 metric 1009`。在通常的 main 表选路中，若 EasyTier 只新增同样 `/24`、metric 65535 的路由，较低 metric 的物理路由应优先。因此该方案预计能避免此次 bridge 中“代理 /24 胜过默认路由”的问题。

限制：host 不是无条件保证。更具体的 `/32`、策略路由或默认出口设置仍可能改变路径；EasyTier 将直接修改宿主机网络，影响范围更大。Docker Desktop 的 host 实现与 Linux Engine 不同，本文结论针对 Tower 的 Linux 部署。

实施与回退：先停止 EasyTier，保留数据与原部署配置，再调整网络模式并重建容器；先验证 WebTool，再启动节点验证。失败时停止节点，恢复原 bridge 配置和端口映射并重建。不要删除数据目录，不自动清空宿主机路由。

## 5. 方案 B：仅虚拟节点互通

适用：只需要访问 `10.126.127.x` 虚拟地址，不需要通过 EasyTier 接管远端 LAN 或 VPN 门户客户端子网。可保留 bridge 网络。

在实际生效的 TOML 顶层，也就是第一个 `[表名]` 前，设置：

```toml
ipv4 = "10.126.127.3/24"
dhcp = false
routes = []

# 原有 network_identity、peer 等表放在此后，保持网络身份与对端配置。
```

这是需要合入已有配置的片段，不能拿它覆盖完整节点配置。其效果是明确用空手动路由集合代替自动代理路由集合，保留虚拟网段通信。不是关闭 TUN，也不是禁止所有中继；本地是否发布代理网段属于独立配置，不应混淆。[v2.6.4 ProxyCidrsMonitor](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier/src/instance/proxy_cidrs_monitor.rs)

当前向导清空 Routes 会省略该字段，不能表达本方案。后续实施需使用正确的原生 TOML，或先修正生成器并新增明确路由模式。切换模式采用停止、保存配置、重启节点；不依赖当前热补丁对空路由的处理来保证语义。

需要部分远端 LAN 时，使用顶层 `routes = ["不冲突的远端网段"]`；远端 LAN 与本地地址重叠时采用网段映射。回退到自动学习是删除顶层 routes 字段并重启，回退前应确认对端发布网段不会再次接管管理网络。

## 6. 后续验收条件（尚未执行）

| 检查 | host | 仅虚拟节点互通 |
| --- | --- | --- |
| `.55/.60` 出口 | 保持宿主机 br0 原路径 | 保持 eth0 → Docker 网关 |
| 局域网登录/API | 节点启动前后正常 | 节点启动前后正常 |
| 虚拟 `.127.1/.127.3` | 双向通信正常 | 双向通信正常 |
| 对端发布本地 `/24` | 检查物理路由仍优先 | 不安装对应代理路由 |
| 对端发布本地 `/32` | 不能假定安全，实际检查 | 不安装对应代理路由 |
| 节点停止/容器重启 | 路由与访问无异常遗留 | 路由模式持久化有效 |

采集实际网络模式、启动前后容器/宿主机 `ip route` 与 `ip rule`、`.55/.60` 的 `ip route get`，以及从局域网发起的登录/API 响应。CI 后续应使用真实双节点和网络命名空间，覆盖 `/24`、`/32` 发布；TestServer 登录成功不能代替真实网络路径验收。

## 7. 决策记录

暂不添加宿主机子网环境变量、策略路由维护器或自动路由修复。优先选择已有官方机制：需要共享宿主机网络时评估 host；只需虚拟节点通信时使用显式空手动路由。本文是当前现场结论；早期多节点安全文档中的同机重叠假设与此不同，不能把两者当作同一根因。
