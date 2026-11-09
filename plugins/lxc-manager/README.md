# LXC 容器管理插件（开发中）

目前提供 Incus 服务认证、版本及容器、镜像、存储池、受管网桥的只读发现和面板。`Ready` 只代表 Incus 已认证，不表示存储硬配额、网络或创建条件已满足。创建和核对接口已连接任务流程，但当前没有可信容量与硬配额证据源，预检会拒绝所有创建请求。管理员可对本插件已完成创建且归属验证通过的实例提交启动、停止、重启任务；此功能尚未在真实 Incus 环境验收。资源查询失败会显示错误，不能视为空列表。

插件 manifest 将 Incus 标为 `scope: host`。普通依赖继续在当前容器探测；宿主依赖显示为“尚未验证”，不会误用容器内 `command --version` 结果，也不会阻止插件启动。安装引导会优先使用环境服务确认的宿主发行版；检测不到时不生成默认宿主命令。宿主包安装需管理员审查后在终端执行，当前尚无自动宿主代理。安装 daemon 不会自动向 LWT 容器开放 Incus socket/TLS API，连接授权和存储/网络初始化需单独配置。

平台通过 `LWT_INTERNAL_SECRET` 和管理员身份将请求代理到本机监听端口。服务拒绝直接访问及 API Key 身份；不要将此端口暴露到网络。默认连接本地 Unix socket `/var/lib/incus/unix.socket`，受信任的部署管理员可用 `INCUS_SOCKET` 覆盖。访问宿主 socket 可能等同管理宿主；当前 Docker 运行环境不会自动拥有该 socket，也不应为了方便直接挂载 `incus-admin` socket。

也可由部署管理员配置受限 TLS API 连接：设置 `INCUS_ENDPOINT`（仅 HTTPS）、`INCUS_PROJECT`、`INCUS_TLS_SERVER_CA_FILE`、`INCUS_TLS_SERVER_CERT_SHA256`、`INCUS_TLS_CLIENT_CERT_FILE` 和 `INCUS_TLS_CLIENT_KEY_FILE`。证书内容不得放进环境变量或插件清单；证书文件应由部署层以只读方式提供，并限制读取权限。远程模式要求 TLS 1.3、CA/主机名校验及服务端证书 SHA-256 指纹匹配，关闭 HTTP 代理且不会跟随重定向；不提供跳过证书校验选项。TLS 模式与 Unix socket 模式互斥。服务端证书轮换时须审查并同步更新 CA/指纹配置。

`INCUS_PROJECT` 只用于把 API 请求明确路由到目标项目，不能作为权限边界。Incus 服务端必须把该客户端证书登记为仅允许专用项目的 restricted certificate，并确认 `authorization.client.tls-restricted=tls`；还需预先配置该项目可用的镜像、存储和网络。当前插件没有证书发放/轮换界面，也没有在真实 Incus 服务上验收 TLS 授权。没有可信的容量预留和磁盘硬配额证据时，创建预检仍会拒绝请求。

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
