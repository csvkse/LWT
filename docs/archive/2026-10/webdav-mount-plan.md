# WebDAV 挂载支持方案

## 目标与范围

在现有“磁盘挂载”页面增加 WebDAV 类型，使 Linux 容器内的文件管理器及其他应用功能可通过本地目录访问 WebDAV。保留现有 SMB 数据、API 和行为。首版支持 HTTPS WebDAV、用户名与密码（含应用密码）、手动挂载/卸载、启动自动挂载、定时健康检查和失败恢复；不支持 OAuth、客户端证书或将容器内新挂载传播给宿主机。

## 技术路线

采用 rclone 的 WebDAV 后端和 FUSE mount。Docker 镜像增加 `rclone` 与 `fuse3`；运行环境需提供 `/dev/fuse` 及挂载权限。每条 WebDAV 配置使用独立的、仅应用用户可读的 rclone 配置与缓存目录；密码不进入 URL、进程参数或日志。缓存目录位于持久化数据卷，恢复时不清除未上传缓存。只允许显式配置的 HTTPS URL、用户名、密码和受控缓存参数，不接受任意 rclone 命令行选项。

首版保留 `smb_mount`，新增 `webdav_mount`。WebDAV 与 SMB 本地路径在保存时交叉校验；运行时共用 `MountOperationCoordinator` 的路径锁。WebDAV 的挂载状态须核对 `/proc/self/mountinfo` 中挂载点和 FUSE 文件系统类型，不能只检查目录存在。保留现有 SMB API，增加 WebDAV API；页面统一展示两种类型。

## 健康与恢复

WebDAV 健康判断依次检查挂载记录、限时本地目录枚举、带认证的远端 `PROPFIND Depth: 0`。远端请求可识别 DNS/TLS/超时、401/403 和服务端错误；本地枚举可能命中缓存，因此不能单独代表远端可用。仅在配置启用且自动挂载、连续三次探测失败、远端可达时自动正常卸载并重挂；卸载 busy 时保留旧进程及缓存，报告失败并交由人工处理，避免新旧进程共用写缓存；失败后退避重试。重挂后再次验证本地目录和远端请求，才标为 Healthy。401/403、TLS 错误等配置性问题只标记失败，不循环重挂。文件管理器访问受管挂载根目录失败时请求即时复查。手动卸载不应被健康巡检立即重新挂载。

WebDAV 容量可能不可查询；此时磁盘状态显示容量未知，不采用 rclone 的合成容量值。健康状态与容量状态分别展示。

## 实施顺序

1. 新增 WebDAV 持久化模型、请求/响应与 API；交叉校验路径，保留 SMB 表和接口。
2. 新增 rclone 配置生成和 WebDAV 挂载执行器；限定命令参数，处理凭据、缓存、超时、挂载状态与卸载。
3. 新增 WebDAV 启动挂载和健康监控，复用路径锁与恢复策略；接入文件访问失败即时复查、系统磁盘状态和操作日志。
4. 扩展挂载页面，按协议显示表单、类型、状态和操作；更新 Docker 依赖与部署说明。
5. 在可运行 FUSE 的 Linux 容器中验证创建、挂载、读写、断线、服务恢复、认证失败、重启以及 SMB/WebDAV 路径冲突。

## 代码落点与兼容性

| 工作 | 文件 | 兼容要求 |
| --- | --- | --- |
| 配置持久化 | `DbSetup.cs`、`WebDavMountStore.cs`、`Entities/WebDavMount.cs` | 只新增 `webdav_mount`，不改 `smb_mount` 结构或历史记录 |
| 挂载、启动及健康 | `WebDavMountService.cs`、`WebDavMountStartupService.cs`、`WebDavMountProbe.cs`、`WebDavMountHealthService.cs` | 共用 `MountOperationCoordinator` 路径锁，WebDAV 独立判断 FUSE 类型与远端请求 |
| API 与序列化 | `WebDavMountsController.cs`、`EndpointsMapper.g.cs`、`AppJsonSerializerContext.cs` | 新增 `/api/WebDavMounts`，原 `/api/SmbMounts` 保留；跨协议检查挂载路径冲突 |
| 页面与状态 | `SmbMountsView.js`、`FilesController.cs`、`DiskStatusCacheService.cs` | 同一页面区分类型；WebDAV 无容量时显示未知，不影响 SMB 容量 |
| 部署 | `Dockerfile`、`README.md` | 加入 `rclone`/`fuse3`，记录 `/dev/fuse` 与权限要求 |

密码保留现有 SMB 的数据存储模式：SQLite 内保存凭据，返回的列表不含密码；rclone 配置文件在创建时即设置为 600。rclone 的 `obscure` 只是可逆编码，不视为加密。WebDAV 的 Healthy 表示远端认证请求和本地目录读取成功，不证明服务端授予写入权限；写入能力须在有写权限的测试服务端单独验证。

## 验收标准

- 原有 SMB 记录、API 和挂载功能不受影响。
- WebDAV 配置能保存、编辑、删除；密码不在响应中回传，更新时可保留旧密码。
- 成功挂载后，本地路径可浏览；有写权限的服务端能够读写文件。挂载记录存在但远端不可用时显示异常，不误报 Healthy。
- 自动恢复遵守启用和自动挂载开关、连续失败门槛与路径锁；恢复后复查通过才标健康。
- 容器缺少 FUSE 能力时给出明确错误；不清理待上传缓存。
- 构建与相关验证通过；真实挂载能力必须在 Linux/FUSE 环境单独确认。

## 主要风险

- FUSE 依赖宿主内核和容器权限，Windows 开发环境无法证明真实挂载可用。
- rclone 写缓存可能延迟上传；断线、卸载与重启须保留缓存并明确显示待同步风险。
- WebDAV 服务端对锁、修改时间、空间配额和重命名的实现不一致；首版不承诺完全 POSIX 语义。

参考：[rclone WebDAV](https://rclone.org/webdav/)、[rclone mount 与缓存](https://rclone.org/commands/rclone_mount/)、[WebDAV RFC 4918](https://datatracker.ietf.org/doc/rfc4918/)、[Docker 挂载传播](https://docs.docker.com/engine/storage/bind-mounts/#configure-bind-propagation)。
