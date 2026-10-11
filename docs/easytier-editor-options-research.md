# EasyTier 节点编辑页面可扩展选项调研

日期：2026-10-11。基准：当前项目源码与官方 EasyTier v2.6.4。状态：调研与方案，未实现页面、接口或数据库变更。

## 1. 现有能力

`src/LinuxWebTool.WebHost/wwwroot/app/views/EasyTierView.js` 的节点向导已包含：实例名、网络名、网络密钥、静态 IPv4、DHCP、自动启动、监听地址、初始对端、子网代理和 Routes；另有原始 TOML 覆盖模式。

契约在 `src/LinuxWebTool.Contracts/Features/EasyTier/Contracts/EasyTierNodeModels.cs`，实体在 `src/LinuxWebTool.Infrastructure/Shared/Persistence/Entities/EasyTierNodeEntity.cs`。原始 TOML 可承载内核支持的高级字段，但不能等同于向导已实现结构化编辑、验证与保存回填。

运行时补丁抽屉包含对端、代理网段和路由。当前 CoreBinary 分支实际上只处理新增 connector，随后返回成功；不处理其他补丁字段。NativeFfi 分支构造部分字段，但不能凭界面文案认定已验证全部补丁语义。后续选项默认按“保存并重启生效”设计，只有逐项验证后才开放热更新。

## 2. 调研依据和字段映射

以下选项来自版本固定的官方资料，不以 main 分支新增字段作为 v2.6.4 的可用保证：

- [v2.6.4 Config / 默认 Flags](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier/src/common/config.rs)：顶层字段、表结构与默认值。
- [v2.6.4 FlagsInConfig](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier/src/proto/common.proto)：flags 可用字段。
- [v2.6.4 命令行选项说明](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier/locales/app.yml)：CLI 功能语义。
- [官方子网代理说明](https://www.easytier.cn/guide/network/point-to-networking.html)：手动路由与网段映射。

CLI 名称不一定是 TOML 键名。典型映射是 `--manual-routes` → 顶层 `routes`，关闭 IPv6 → `[flags].enable_ipv6 = false`，关闭加密 → `[flags].enable_encryption = false`，压缩 → `[flags].data_compress_algo`。不能把 CLI 开关原样拼进 TOML。

## 3. 第一批：适合纳入常用/高级编辑

P0 表示先补齐配置语义；P1 为首批体验增强。所有项目均为候选，当前未实施。

| 优先级 | 页面选项 | 内核配置位置 | 用途与约束 |
| --- | --- | --- | --- |
| P0 | 路由模式：自动学习 / 仅虚拟节点互通 / 指定远端子网 | 顶层 routes 缺省 / `[]` / 非空列表 | 显式区分三种状态，不能把空列表转换成缺省；切换后重启验收 |
| P0 | 子网映射 | `[[proxy_network]]` 的 cidr、mapped_cidr | 重叠 LAN 通过不同映射网段访问；两端前缀长度需匹配 |
| P1 | 节点显示名称 | 顶层 hostname | 对端容易识别；独立于本项目实例 ID 和实例名 |
| P1 | TUN 网卡名 | `[flags].dev_name` | 多节点辨识、诊断；校验平台名称和接口占用 |
| P1 | MTU | `[flags].mtu` | 排查分片与部分连接异常；默认继承内核，显示配置值与实际值区别 |
| P1 | IPv6 连接开关 | `[flags].enable_ipv6` | 控制底层 IPv6 连接能力；不等同于虚拟 IPv6 地址 |
| P1 | 虚拟 IPv6 地址 | 顶层 ipv6 | 虚拟双栈；校验 IPv6/CIDR 和现有网段保护能力 |
| P1 | 延迟优先 | `[flags].latency_first` | 路径选择偏向延迟；结合延迟/丢包观察，不保证吞吐提升 |
| P1 | 绑定物理出口 | `[flags].bind_device` | 保护 EasyTier 自己的连接器出口；不会保护 WebHost/SMB 套接字 |
| P1 | 连接模式 | `[flags].disable_p2p`、p2p_only、lazy_p2p、need_p2p | 使用互斥或明确的组合选择，避免堆叠含义冲突的开关 |
| P1 | TCP/UDP/对称 NAT 打洞 | `[flags].disable_tcp_hole_punching`、disable_udp_hole_punching、disable_sym_hole_punching | 高级诊断；默认继承内核，失败时仍需检查实际中继状态 |
| P1 | UPnP 自动映射 | `[flags].disable_upnp` | 开关标签采用正向语义，写入时正确反转 |
| P1 | 默认连接协议 | `[flags].default_protocol` | 按内核支持值提供选择；与显式 peer/listener URI 分开解释 |
| P1 | 对外监听地址 | 顶层 mapped_listeners | NAT 后向对端声明可达地址；不会代替路由器端口映射 |

网段映射不能仅让用户在现有 Proxy Networks 文本框中填 `A->B`：当前生成器把整个字符串写进 cidr，并未拆成 cidr/mapped_cidr。需要结构化模型和配置序列化支持。

## 4. 第二批：按场景展开

| 类别 | 候选选项 | 内核位置 | 编辑与验证重点 |
| --- | --- | --- | --- |
| 代理转发 | 用户态 TCP 栈、系统转发 | `[flags].use_smoltcp`、proxy_forward_by_system | 两种工作方式需清楚说明；系统转发依赖宿主机转发/防火墙，不由普通保存操作自动修改 |
| 无 TUN | 不创建虚拟网卡 | `[flags].no_tun` | 独立角色选项；不等同于“仅虚拟节点互通” |
| 连接优化 | KCP/QUIC 代理及入站接收开关 | `[flags].enable_kcp_proxy`、disable_kcp_input、enable_quic_proxy、disable_quic_input | 依赖内核编译能力；提供性能对照，不承诺必然提速 |
| 压缩与资源 | 压缩、多线程、线程数 | `[flags].data_compress_algo`、multi_thread、multi_thread_count | 以目标内核校验范围；不会限制 WebHost 线程池或 SMB D-state |
| 中继 | 禁止中继数据、网络白名单、外部网络速率、实例接收速率 | `[flags].disable_relay_data`、relay_network_whitelist、foreign_relay_bps_limit、instance_recv_bps_limit | 速率字段以字节/秒表示；不能把外部中继限速描述成总出站限速 |
| 中继高级 | RPC/KCP/QUIC 转发控制 | `[flags].relay_all_peer_rpc`、disable_relay_kcp、disable_relay_quic、enable_relay_foreign_network_kcp、enable_relay_foreign_network_quic | 面向共享节点，收起默认展示；逐项说明作用范围 |
| DNS | 魔法 DNS、域名后缀 | `[flags].accept_dns`、tld_dns_zone | 可能修改运行环境 DNS；host 时影响宿主机，需展示生效范围 |
| 出口节点 | 允许作为出口、选择出口节点 | `[flags].enable_exit_node`、顶层 exit_nodes | 两者不同；可能改变默认流量路径，提供变更预览和重启验证 |
| 访问控制 | TCP/UDP 端口白名单、ACL | 顶层 tcp_whitelist、udp_whitelist、acl | 校验端口/范围；ACL 宜使用独立规则编辑器，不作为简单文本开关 |
| 服务接入 | SOCKS5、端口转发、WireGuard 门户 | 顶层 socks5_proxy、port_forward、vpn_portal_config | URI/对象结构按 Config 序列化，校验占用及访问范围；避免套用 CLI 字符串结构 |
| 安全 | 私有模式、加密算法、安全模式/凭据 | `[flags].private_mode、encryption_algorithm`；顶层 secure_mode、credential_file 等 | 算法受构建功能影响；安全模式密钥需专门保护，日志/配置预览脱敏 |
| NAT 探测 | 自定义 IPv4/IPv6 STUN 列表 | 顶层 stun_servers、stun_servers_v6 | 缺省、显式空、非空需要分别保存 |
| 公网 IPv6 | 共享/自动获取/指定公网前缀 | 顶层 ipv6_public_addr_provider、ipv6_public_addr_auto、ipv6_public_addr_prefix | Linux 场景；后续独立网络规划，不与普通 IPv6 开关混为一谈 |

CLI 帮助中的默认值文字与实际 Config 默认值可能不同，例如多线程。页面默认选“继承内核”，展示经过固定版本核对的默认值，避免帮助文本成为配置行为来源。

暂不提供默认关闭加密的快捷选项；是否开放此项应另行决定。Windows UDP 广播转发在官方版本中存在，但 Linux 页面无需默认展示。日志大小/数量等进程启动参数可放内核管理页，不强行纳入每节点编辑。

## 5. 配置与页面设计

分组建议：基础组网、路由与子网、连接与性能、安全与服务。第一屏保留基础项；按需展开高级项，并显示“继承内核”或显式设置。host/bridge 是容器部署选项，不是可以通过节点保存立即改变的 EasyTier 字段。

路由模式必须有独立枚举，不从 Routes 文本是否为空猜测。旧数据无模式字段时保留既有语义，不悄悄把所有节点改成仅虚拟互通；本方案未批准默认模式变更。

Boolean 高级字段采用可空状态；数组区分未设置与明确空数组。向导与原始 TOML 只能有一个权威来源，切换前先展示差异，不覆盖未知高级字段。原始模式不承诺所有字段可无损转换回向导。

持久化字段需经过契约、实体/存储、JSON 源生成上下文、配置生成器、保存回填和升级恢复的完整链路。采用强类型字段或受控高级配置对象；不要引入不校验的任意 flags 字典来代替结构化支持。

## 6. 实施前置条件与后续验收

1. 先修复顶层字段/子表输出顺序，并核对官方 instance_name 与项目 inst_name 的兼容策略；不得把“包含关键字”当成配置有效。
2. 使用目标 CoreBinary 的配置校验能力，以及 FFI 对应接口；错误反馈脱敏。内核校验通过仍不等于真实网络可达。
3. 给路由模式补三态保存、重启、回填测试；不能只断言输出字符串存在。
4. 网段映射验证双字段、前缀大小、重复/重叠与保存后语义。
5. 功能按内核版本、平台和编译能力启用，未知能力不静默保存并宣称生效。
6. CoreBinary 未支持的热补丁字段应明确拒绝或要求重启，不应返回泛化成功。NativeFfi 则逐项验收响应与实际配置。
7. 浏览器验证保存、错误、忙碌恢复和回填；真实双节点验证路由/连接变化。性能选项记录前后延迟、丢包、吞吐和资源使用，不以进程启动成功代替效果验收。

本轮仅形成候选清单和实施约束；未实施上述功能或测试。当前路由问题详见 [容器局域网路由冲突记录](easytier-container-lan-route-conflict-analysis.md)。
