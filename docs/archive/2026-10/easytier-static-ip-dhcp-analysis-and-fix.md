# EasyTier 静态 IP 被 DHCP 覆盖：原因、修复与验证

日期：2026-10-10。

## 结论与证据边界

当前项目存在已复现的配置生成问题：填写静态地址后，默认开启的 DHCP 仍被写入配置。`ipv4 = "10.126.127.1/24"` 与 `dhcp = true` 并存时，地址不能被视为固定地址。EasyTier 2.6.4 的 DHCP 任务可以重新设置 IPv4；无可用对端地址时，其回退网段是 `10.126.126.0/24`。

参考项目 ProxyByCF 的中继实现没有广播固定 IPv4，未发现服务器把客户端强制配置为 `10.126.126.0/24` 的代码。因此，现有证据支持优先修复当前项目的静态 IP / DHCP 冲突，而不是修改服务器网段。

本次确认了代码缺陷及对应内核机制，未取得发生问题的线上节点数据库、有效 TOML、路由快照或服务器日志，不能据此断言线上实例必然只有这一原因。本地 `data/easytier/nodes/node_76.toml` 的相关字段只有 `dhcp = true`，没有 `ipv4`；它不构成用户描述的静态地址实例证据。

## 当前项目的数据流

1. `src/LinuxWebTool.WebHost/wwwroot/app/views/EasyTierView.js`：新建表单默认 `enableDhcp: true`，原来填写 `virtualIpv4` 不会关闭 DHCP，提交时两者原样发送。
2. `EasyTierNodeManager.cs`：创建、更新分别保存地址和 DHCP 标志；没有把静态地址和 DHCP 设为互斥。
3. `EasyTierConfigGenerator.cs`：请求和持久化实体两条生成路径原来都直接写入 DHCP 标志，导致预览和启动配置均出现冲突。
4. `StartNodeInternalAsync`：FFI 直接接收生成的 TOML；CoreBinary 将同一 TOML 写到节点文件并通过 `-c` 加载。启动命令没有另加 DHCP 或固定网段参数。
5. 非空 `RawTomlOverride` 在生成器入口直接返回，优先于所有表单字段。修改表单不能覆盖原始 TOML。

## EasyTier 内核机制

本地内核执行 `easytier-core.exe -V` 返回 `2.6.4-8428a89d`。核对 [官方 v2.6.4 源码 instance.rs](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier/src/instance/instance.rs)：`run()` 在 DHCP 开启时启动重新选址任务；该任务从路由收集对端 IP，有地址时选择对端网段，没有地址时回退到 `10.126.126.0/24`。因此同时开启 DHCP 不保证保留已填写的静态地址。

这也解释了为什么连上中继后会表现得像“网段固化”。它是动态选择的结果。单个中继的无地址路由可能满足连接条件，但没有提供自定义网段种子；已有其他对端地址也可能影响选择。只有关闭 DHCP 才能明确要求固定本机 IP。

上述源码还区分“没有任何路由”与“有路由但没有可用 IPv4”：前者等待，后者可回退分配。原文档关于“没有静态种子一定死锁”的表述过于绝对，不能用来排除默认网段回退。

## 参考项目的作用

只读检查 `E:/WorkProject/Node/_CFWorkerProject/ProxyByCF`：

- `apps/worker/src/features/easytier/peer-manager.ts` 的 `buildServerInfo()` 包含 `networkLength: 24`，没有 `ipv4Addr`。仅有前缀长度不等于指定 `10.126.126.0/24`。
- `durable-object.ts` 和 `rpc.ts` 记录客户端上报地址；所见代码没有为客户端生成静态 IP 配置。
- `apps/web/src/features/easytier/EasyTierView.vue` 的命令示例同时包含 `-d` 与 `--ipv4`，不能作为静态 IP 保证。其使用 `-d wss://...` 的示例也应另行核对：官方参数中 `-d` 是 DHCP，连接对端使用 `-p/--peers`。

参考项目作为比对依据，本次没有修改或部署该项目。官方参数参考：[完整配置选项](https://easytier.rs/guide/network/configurations.html)。

## 已执行的修复

静态地址优先规则：

| 表单地址 | DHCP 标志 | 实际生成配置 |
| --- | --- | --- |
| 非空 | 任意 | 保留 IPv4，`dhcp = false` |
| 空或纯空白 | true | 不写 IPv4，`dhcp = true` |
| 空或纯空白 | false | 不写 IPv4，`dhcp = false`，不请求自动分配 |
| 任意，且原始 TOML 非空 | 任意 | 原样使用原始 TOML |

- 两个生成器重载均执行此规则，覆盖创建、配置预览、已有节点重启、自动启动和内核更新恢复，不需要批量改数据库。
- 前端填写地址后禁用 DHCP 控件，显示静态模式说明；提交静态地址时发送 `enableDhcp: false`。
- 未更改 API 字段、原始 TOML 的高级功能和未填写地址的 DHCP 行为。
- 原数据库中若同时保存地址与 `EnableDhcp = true`，标志仍可出现在配置详情里，但修复后的有效 TOML 为 `dhcp = false`；通过表单再次保存后标志也变为 false。

## 节点应用步骤

1. 部署本次代码构建的 WebHost，刷新页面。运行旧二进制不会获得生成器修复。
2. 编辑目标节点，填写 `10.126.127.1/24`。没有高级需求时清空原始 TOML 覆盖；保留它时必须在顶层显式设置：

   ```toml
   ipv4 = "10.126.127.1/24"
   dhcp = false
   ```

   这两行需位于任何 `[table]` 或 `[[table]]` 之前，否则不再是顶层配置。不要把该片段当成完整组网配置替换现有网络身份和对端参数。

3. 保存后查看配置详情中的有效 TOML，确认 IPv4 正确且 DHCP 为 false，再停止、启动目标节点。已有进程不会仅因生成器代码变化而自动更换配置。
4. 使用节点自己的 RPC 地址查询 `easytier-cli -p 127.0.0.1:<RPC端口> --output json node` 和 `peer`，确认运行态本机 IP 为 `10.126.127.1/24`，并检查 Linux 网卡的实际地址。详情/卡片在运行状态查询失败时可能回退显示配置地址，不能只凭卡片判断。
5. 多台节点统一目标网段并使用唯一地址，例如 `.1/24`、`.2/24`。需要固定地址的节点关闭 DHCP；自动分配节点则清空地址并启用 DHCP。在已存在其他网段的网络中，不保证 DHCP 自动迁移，先检查实际对端路由。
6. 如果有效 TOML 已是静态配置但仍显示旧网段，依次检查：是否运行旧版本、原始覆盖、查询的节点/RPC 是否正确、是否有独立 EasyTier GUI/系统服务实例、实际进程加载的配置路径和运行态日志。若 IP 正确但不通，再排查 TUN 权限、重复 IP、路由和防火墙。

未修改现有节点文件、数据库或外部服务器，也未重启用户运行中的 EasyTier GUI。当前没有明确的线上地址和目标节点，本次执行范围为工作区修复及验证；上述部署和目标节点重启尚未执行。

## 实际验证

- 修复前新增聚焦测试：6 项中 1 项因“静态 IPv4 + DHCP=true”仍生成 true 而失败，其余 5 项通过，确认缺陷可复现。
- 修复后 `dotnet test tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj --filter FullyQualifiedName~EasyTierStaticIpTests --nologo --verbosity quiet`：6 项全部通过。两条生成路径均验证静态 IP 优先、无地址 DHCP、显式关闭 DHCP及原始覆盖保留。
- `dotnet test tests/LinuxWebTool.IntegrationTests/LinuxWebTool.IntegrationTests.csproj --filter FullyQualifiedName~EasyTierFunctionalTests --nologo --verbosity quiet`：4 项全部通过。现有 CRUD 流程新增断言，确认通过 API 创建“静态 IP + DHCP=true”的节点后，配置详情返回 `dhcp = false` 并保留静态地址。测试使用独立临时数据目录。
- `node --check src/LinuxWebTool.WebHost/wwwroot/app/views/EasyTierView.js`：通过。
- 本次测试实际编译相关 .NET 项目，未执行全量测试、浏览器交互或真实 TUN/线上端到端组网验证。内核版本查询成功不等于组网验证成功。
- 保留任务开始前已有的登录、导航和操作日志相关修改，未提交 Git。

## 回归门禁

后续新增 `FE-EASYTIER-IP-MODE`，由既有前端门禁自动执行组件提交行为测试，新建与编辑都检查静态地址关闭 DHCP、空地址保持 DHCP 选择和原始 TOML 保留。测试还模拟旧错误写法，确认断言能够拦截回归。后端 API 更新流程也加入“静态地址 + DHCP=true”验证，与已有生成器测试共同覆盖输入、持久化后生成配置及 API 输出。

规则自动进入既有 CI 和本地快速门禁，不允许用前端静态扫描基线豁免。具体入口和规则说明见 [架构门禁](dev/architecture-gates.md#easytier-地址模式门禁)。

门禁新增后的实际验证：前端组件行为测试 15 项全部通过（含旧错误写法被断言拒绝）；完整前端架构门禁通过；EasyTier 生成器测试 6 项、API 集成测试 4 项全部通过；相关差异的 `git diff --check` 通过。没有运行全量快速检查或远程 CI。
