# LXC 容器管理插件（开发中）

目前提供 Incus 服务认证、版本及容器、镜像、存储池、受管网桥的只读发现和面板。`Ready` 只代表 Incus 已认证，不表示存储硬配额、网络或创建条件已满足。创建和核对接口已连接任务流程，但当前没有可信容量与硬配额证据源，预检会拒绝所有创建请求。管理员可对本插件已完成创建且归属验证通过的实例提交启动、停止、重启任务；此功能尚未在真实 Incus 环境验收。资源查询失败会显示错误，不能视为空列表。

平台通过 `LWT_INTERNAL_SECRET` 和管理员身份将请求代理到本机监听端口。服务拒绝直接访问及 API Key 身份；不要将此端口暴露到网络。插件只连接本地 Unix socket，默认为 `/var/lib/incus/unix.socket`；`INCUS_SOCKET` 可由受信任的部署管理员配置。访问此 socket 的权限可能等同管理宿主，须先确认权限边界。当前 Docker 运行环境不会自动拥有该 socket。

创建界面在浏览器本地保存无凭据的待核对任务，并使用 Web Locks 避免标签页相互覆盖；浏览器需支持该 API，通常需要 HTTPS 或 localhost 安全上下文。清除浏览器站点数据后，可从服务端待核对列表恢复完整任务；若任务记录缺少参数，必须人工核对。未能读取任务列表或存在待核对任务时，界面暂停新操作。

该目录是源码，不包含可运行二进制。发布时按目标架构构建静态 Linux ELF 并放入插件根目录的 `lxc-manager`，确保插件清单与实际打包产物一致；不要假设生产容器安装 Go 编译器。示例（从本目录执行）：

```sh
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -o lxc-manager ./cmd/lxc-manager
```

测试：`go test ./...`。普通测试使用模拟 Incus 后端，不代表目标宿主的 LXC、配额和网络已经过实机验证。

真实 Incus 生命周期验收仅适用于**可丢弃且与生产隔离的 Linux 测试主机**。测试会创建一个带随机所有权标记的非特权容器，配置 1 CPU、256 MiB 内存、2 GiB 磁盘，启动、停止并尝试删除它；要求预先存在专用存储池、受管网络和本地镜像。它不会安装 Incus、配置宿主网络或验证硬配额是否真正生效。进程被强制中断时可能遗留实例，测试会在日志中输出实例名和所有权标记供人工核查；不得对生产主机运行。

```sh
export LWT_INCUS_TEST_ENABLE=1
export LWT_INCUS_TEST_CONFIRM_DISPOSABLE_HOST=I_UNDERSTAND_THIS_IS_A_DISPOSABLE_HOST
export LWT_INCUS_TEST_SOCKET=/path/to/isolated/incus.socket
export LWT_INCUS_TEST_POOL=lwt-test-pool
export LWT_INCUS_TEST_NETWORK=lwt-test-bridge
export LWT_INCUS_TEST_IMAGE=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
go test ./internal/incus -run '^TestRealIncusLifecycle$' -v -count=1
```

未显式设置 `LWT_INCUS_TEST_ENABLE=1` 时，真实环境测试跳过；启用后还必须设置精确的 `LWT_INCUS_TEST_CONFIRM_DISPOSABLE_HOST` 确认值。清理逻辑只接受随机生成名称、匹配的所有权标记、已停止且无快照的实例；发现所有权不符、仍在运行或存在快照时拒绝删除。所有权检查和删除之间并非原子操作，因此必须使用没有其他管理者同时修改这些测试实例的隔离主机。该测试仍是单实例适配器验收，不覆盖并发压力、宿主重启恢复、NAT/公网连通和 CPU/内存/磁盘配额实效。
