# SMB 卸载阻塞与登录请求长期等待：调查和修复

调查日期：2026-10-10。依据：用户提供的启动/周期挂载日志、当前源码、真实 SQLite 锁冲突测试与前端请求行为测试。

## 结论与证据边界

日志确认部分 `umount` 进程为 `State=D`，等待位置是 `__flush_workqueue`。挂载记录已消失（`After=null`），但进程仍存活（`StillAlive=True`）。这表示懒卸载已从命名空间解除挂载，内核中的后续工作尚未完成；不能把记录消失当作进程已经退出。

状态机单次恢复约 1.3 秒后返回 `RecoveryFailed`，之后以 `Busy` 暂停创建新探针和挂载操作。这是保护机制，并非日志所显示的任务一直没有返回。这里的“上次文件系统探针”是通用提示，实际也可能是未退出的卸载进程。

登录接口没有等待挂载协调锁，启动调度也已与 HTTP 启动分离。因此，仅凭这份日志不能证明 SMB 卸载直接导致登录卡死，更不能判断具体是 SMB 服务端、网络、内核缺陷还是其他内核工作导致阻塞。缺少现场登录请求状态、内核栈及应用线程证据。

源码及测试确认两个会放大登录等待的缺口：

1. `AuthController.Login` 在成功或失败响应前等待 `IOperationLogger.LogAsync`；挂载恢复也写同一 SQLite 数据库。操作日志插入原先继承数据库默认 30 秒锁重试时间。真实锁冲突测试中，原实现 5 秒仍未完成。SQLite 异步 API 实际同步执行，锁竞争期间会占用调用线程；这是一条可能拖慢登录的调用链，尚未证明现场发生了数据库锁竞争。
2. `http()` 没有请求截止时间，且读取响应正文在异常处理外。服务器或代理无响应时，登录按钮会一直显示“登录中”；读取正文失败或浏览器存储异常时，原登录组件也不能保证复原按钮。

## 已执行的最小修复

- `OperationLogStore.InsertAsync` 明确设置 `commandTimeout: 2`，减少所有操作日志插入的 SQLite 锁重试时间。现有 `OperationLogger` 捕获写入失败并输出程序错误日志，认证仍按实际凭据结果返回。代价：锁争用超过 2 秒时数据库审计记录可能缺失，需通过程序日志定位。
- 请求层新增可选 `timeoutMs`，使用 `AbortController`；截止时间同时覆盖等待响应头和读取响应正文，失败时返回统一错误结构并清理定时器。
- 只有登录请求启用 15 秒超时，避免影响其他可能合理耗时较长的操作。
- 登录组件使用 `try/catch/finally`，超时或异常后恢复按钮并展示错误，允许重试。

2 秒命令超时只约束 SQLite busy/locked 重试，不能保证磁盘 I/O、文件日志或整个 HTTP 请求在 2 秒内结束。前端中止请求也不能终止宿主机内核中的 `D` 状态进程。

## Linux 现场定位与恢复方案

### 新线索：开启 EasyTier 后所有请求同时等待

用户补充故障发生在启动 EasyTier 后，且怀疑 IP 冲突。该时间关联使地址/路由冲突成为优先排查假设，而不是把所有请求等待归因于操作日志。前述超时修复只处理等待边界，不能修复网络路径。

随后用户确认同时开启两个 `10.126.126.0/24` 虚拟网络。这明确构成网段重叠；若两个独立网络的 TUN 接口处于同一个网络命名空间，系统无法仅凭目标地址区分它们，实际选路取决于路由前缀、metric 和策略，可能把连接或回包送入另一个虚拟网络。如果浏览器通过 `10.126.126.x` 访问管理服务，所有接口都可能一起等待，这是网络路径失效，不等于每个接口内部都死锁。若访问的是原 LAN 地址，单独这条 `/24` 重叠并不足以解释 LAN 上所有请求失败，还需检查代理路由、源地址与实际选路。尚待确认两个节点是否位于同一服务器/命名空间及浏览器使用的地址。

用户进一步确认两个节点位于同一服务器。需继续确认是否共享网络命名空间及实际访问地址；若均由该应用管理并运行在相同网络命名空间，两个独立虚拟网络使用同一 `/24` 的冲突风险成立。日志中的 SMB 地址为 `10.126.1.x`，不属于 `10.126.126.0/24`，因此不能仅凭这两个虚拟网段重叠，断言 SMB 故障也由它导致。

可能链路：EasyTier 虚拟网段或学习到的远端代理路由与真实 LAN 重叠，管理客户端的回包或 SMB 流量走错接口；浏览器收不到响应，同时 CIFS 连接异常，继而出现卸载内核等待。这条链路能够同时解释两类现象，但还没有现场路由证据，不能当作已确认根因。另一种情况是浏览器访问 EasyTier 虚拟地址，而 DHCP 因虚拟网内地址冲突换了 IP，旧地址随即失效。

源码显示配置支持虚拟 IP、DHCP、代理网段和路由，启动会交给原生引擎或 core 进程。当前工作区另有静态 IP 与 DHCP 互斥的未提交修改，本次未覆盖；它不能证明生产版本或已运行节点已采用新配置。指定静态 IP 也不能防止与 LAN、其他节点或容器网段重叠。

优先在故障服务器收集以下只读输出，并提供实际管理客户端 IP 和虚拟 IP/掩码：

```bash
ip -br addr
ip route show table all
ip rule show
ip route get 管理客户端IP
ip route get 10.126.1.55
```

最后一个地址来自本次 SMB 日志，代表受影响服务端之一。若普通 LAN 客户端或本地 SMB 服务端的路由指向 EasyTier/TUN，需结合策略路由、源地址和部署网络命名空间确认是否走错接口；仅看到路由表里存在相同网段不足以证明实际选中了它。真实 LAN 掩码尚未知，不能从 `10.126.1.x` 推断它一定是 `/24`；换成相邻网段也可能仍处于真实 LAN 的 `/16` 内。

在具备本机控制台或独立管理连接时，可临时停止对应 EasyTier 节点，比较 HTTP 和路由是否恢复；若当前连接依赖 EasyTier，停止会断开该连接。确认后为虚拟网络选择与实际 LAN、容器网络及其他 VPN 不重叠的网段，确保各节点地址唯一，调整冲突的代理/手动路由后重新验证。Docker host 网络或直接宿主机运行会影响宿主网络；bridge 模式需在应用容器内检查路由，不能直接认定宿主路由被改变。

资料：[EasyTier 官方配置项](https://www.easytier.cn/guide/network/configurations.html)说明 DHCP 遇到虚拟网络地址冲突可能换址；[官方快速组网指南](https://easytier.cn/guide/network/fast-networking.html)提醒多个虚拟网络网段重叠会产生路由冲突。

先在浏览器 Network 中检查 `POST /api/Auth/Login`：是否已发送、是否收到响应头、等待多久、返回状态是什么。不要记录或分享密码、JWT、请求体。随后比较浏览器路径与服务器本机路径，区分代理与应用问题。

在故障 Linux 主机执行以下只读检查；端口按实际监听端口替换：

```bash
curl --max-time 5 -sS -o /dev/null -w 'health code=%{http_code} time=%{time_total}\n' http://127.0.0.1:实际端口/health
curl --max-time 5 -sS -o /dev/null -w 'static code=%{http_code} time=%{time_total}\n' http://127.0.0.1:实际端口/app/index.html
ps -eo pid,ppid,stat,wchan:32,comm
cat /proc/故障PID/status
cat /proc/故障PID/wchan
cat /proc/故障PID/stack
cat /proc/self/mountinfo
dmesg -T | tail -n 300
```

`stack` 可能因权限不足不可读，不要因此自动提权。容器内外 PID 和挂载命名空间可能不同，应在对应环境检查。`/health` 只验证数据库连接能否打开，不证明写入未被锁住。

判断方式：

- 本机正常、浏览器访问等待：检查反向代理、穿透连接和网络。
- 静态文件正常、登录等待：重点查操作日志锁冲突/错误、数据目录位置、应用线程栈；必要时使用已有 .NET 诊断工具观察线程池和请求栈，不扩大探测异常共享。
- 本机静态文件也等待：检查服务是否监听、CPU/内存/线程、文件日志写入和宿主机 I/O；不能直接归因于 JWT 或密码。
- `D` 状态持续：结合内核栈与 CIFS 内核日志检查远端 SMB 服务及网络。TCP 445 可连接只说明端口可达，不保证 CIFS 会话及目录读取可用。

恢复时先处理远端 SMB 可用性和网络，使旧内核操作有机会完成；保持应用的存活进程保护，避免反复生成新的 `umount`/`ls`/`df`。不在异常共享上通过全局 `df`、目录遍历来收集证据。持久化数据及程序日志应使用可用的本地磁盘，具体迁移需根据现场配置决定。

若恢复远端后旧进程仍不退出，应保存栈和内核日志，再安排服务维护与宿主机恢复；单纯重启应用/容器不保证消除宿主机 CIFS 内核阻塞。强制卸载、重启 SMB 服务或重启宿主机可能影响其他业务，本次没有连接生产主机，也没有执行这些操作。

## 验证

- 前端行为测试先复现无限等待，再验证超时能够结束请求、涵盖响应正文、正常 JSON 响应保持可用。
- SQLite 测试使用真实共享内存数据库和写事务：修复前 5 秒等待失败；修复后约 2 秒返回锁错误，测试通过。
- 实际 API 集成验证：数据库写事务持锁期间，正确密码登录仍能返回 HTTP 200 和 JWT；错误密码拒绝和正常登录/续期也通过验证。
- 验证命令：

```text
node --experimental-vm-modules --test tests/frontend/login-request-timeout.test.mjs
dotnet test tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj --filter FullyQualifiedName~OperationLogTimeoutTests --no-restore --verbosity quiet
```

JavaScript 测试执行真实请求层模块并模拟网络等待，不是源码字符串匹配。Node 的 VM 模块实验性提示不影响测试结果。Windows 测试无法复现 Linux CIFS 内核阻塞；修复落在工作区，未部署到生产环境，现场是否恢复仍需部署后验证。

## 一手资料

- [Microsoft.Data.Sqlite 异步限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)：异步 ADO.NET 方法同步执行。
- [Microsoft.Data.Sqlite 数据库错误与锁重试](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)：busy/locked 自动重试受命令超时限制。
- [Linux CIFS 官方使用与诊断说明](https://www.kernel.org/doc/html/latest/admin-guide/cifs/usage.html)：CIFS 挂载、内核日志及诊断选项。
