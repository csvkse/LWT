# GitHub 白盒、黑盒与发布冒烟实施方案

## 目标

PR 和主分支执行白盒门禁；对真实 Native AOT 容器执行 API、浏览器、Linux PTY 黑盒测试；只有通过的同一镜像才能发布。桌面版本在对应 Linux x64、Linux ARM64、Windows x64 runner 上启动发布产物，成功后才能打包和创建 Release。

保留已有 EasyTier、登录超时、数据库锁回归测试。不修改生产网络、不提交或推送代码、不修改远程仓库保护规则。

## 已确认的问题

1. 原 AOT 冒烟仅拒绝 5xx 和连接失败，成功路由的 401/404/405 也会通过。
2. 固定等待三秒，登录无超时，通过扫描中文日志提取密码。
3. CI 构建镜像直接推送；独立 AOT workflow 失败不阻止推送。
4. 桌面 Release 不启动发布程序；前端没有真实浏览器验证。
5. FRP 外部测试缺配置直接返回，普通 CI 显示通过但没有执行真实外部测试。

## 流程与门禁

| 流程 | 条件 | 验证 |
| --- | --- | --- |
| CI / Build & Gates | PR、main、手动 | Release 构建、架构测试、非外部集成测试、前端门禁、冒烟工具回归；上传 TRX |
| CI / Docker Image | 白盒通过 | 四种镜像逐个构建并加载；API 合约、Chromium 桌面/窄屏、真实 Linux PTY；上传证据 |
| 镜像发布 | main 且上述通过 | 推送本地已经验证的镜像，禁止重新构建后直接发布；PR 不登录 GHCR |
| Native AOT | push、PR | 保留独立验证，升级严格接口契约和日志证据 |
| Desktop Release | tag 或手动 | 各平台发布程序的启动、鉴权、核心读取、指令 CRUD 和执行；成功后打包 |
| Resilience Whitebox | 每日 19:17 UTC、手动 | 连续十轮 EasyTier 生命周期与登录审计锁竞争回归；保存每轮 TRX |

GPU 镜像验证服务可用性，不代表真实 GPU 编解码验证。黑盒使用实际服务，白盒集成测试使用 TestServer。

## 实施步骤和契约

1. `scripts/smoke-http.mjs`：一分钟有界就绪轮询；每个请求十秒超时（含响应体）；错误密码与未授权必须 401；成功必须 200 且返回可消费 JSON。
2. 读取认证、指令、分组、计划、挂载、EasyTier、概览、监控、历史和审计；创建唯一指令，验证查询、执行输出/退出码、更新和删除结果；临时文本文件写入、读取、删除并确认不存在。
3. 原 AOT 路由扫描保留，GET 必须 200，不存在日志为 404，空请求为 400，不存在资源为 404。同时包含空请求和不存在 ID 的负向用例允许 400/404；401、405、5xx 均失败。
4. 容器唯一命名、仅 loopback 暴露端口，生成临时凭据；结束保存脱敏日志并移除本次容器，不删除其他容器。
5. `tests/smoke` 独立锁定 Playwright 依赖；桌面和手机尺寸真实登录，进入指令/EasyTier/日志页面并退出；另注入登录网络失败，验证按钮恢复和再次登录成功。页面异常和 API 错误失败；保存截图、trace、HTML 报告。
6. Linux 镜像接入 `scripts/verify-terminal.mjs`，使用 Node 22，验证真实 PTY、尺寸、Ctrl-C、重连等。所有 job 有整体超时，浏览器不重试掩盖失败。
7. 桌面程序使用唯一数据目录和独立日志目录；结束仅停止本次启动的进程、还原环境变量。仅上传日志，不上传包含凭据的数据目录。同时设置 `Urls` 覆盖 appsettings 中默认的 5270，避免 `ASPNETCORE_URLS` 被更高优先级配置覆盖。
8. FRP 外部测试标记 `Category=External`，普通 CI 明确排除，不能把无配置测试算作成功证据。后续外部专用 workflow 必须显式检查配置，缺配置应失败或跳过。

浏览器 trace 可能含临时 token，仅运行临时服务，不输入生产凭据。

## 真实网络故障专项（后续范围）

每日任务为可重复白盒故障注入，不代表真实 EasyTier/TUN 或 SMB 故障测试。生产路由或内核 D 状态不能由普通 hosted CI 保证复现。

专项需先建立独立 Linux 网络命名空间/容器 fixture，固定 EasyTier 二进制版本和校验和，仅给专项容器 TUN 和必要权限。验收：不同网段通信、停止、重命名；同网段第二节点拒绝启动且登录有界返回。Samba 使用临时共享验收挂载、读写、卸载和断链恢复。禁止在生产服务器或 runner 主机网络上制造冲突。本次不把私人 FRP/SMB 服务器接入 CI。

## 本地执行

```powershell
node --test tests/smoke/http-contract.test.mjs
cd tests/smoke
npm ci
npx playwright install chromium
cd ../..
./scripts/smoke-aot.ps1 -ImageTag linuxwebtool:aot-verify -Browser -Terminal
./scripts/smoke-published.ps1 -PublishDirectory ./publish -Browser
```

Linux CI 安装浏览器使用 `npx playwright install --with-deps chromium`。先构建对应候选镜像/程序。冒烟包含写入，不得使用生产 SMOKE_URL。

## 仓库设置与验收记录

性能指标扩展已接入，指标定义、采样方法与实际结果见 [性能方案](github-smoke-performance-plan.md)。

工作流随代码进入 GitHub 后生效。要阻止绕过失败检查合并，维护者还需将 `Build & Gates` 和四个 Docker Image 检查设为分支保护必需项。本次未修改远程设置或触发远程发布。

- [x] 严格 HTTP 合约、就绪轮询、请求超时和工具回归测试。
- [x] 浏览器、Linux PTY 接入、脱敏日志与报告。
- [x] 候选镜像先验证再推送；桌面产物启动门禁。
- [x] 每日白盒故障回归；外部 FRP 测试明确排除。
- [x] 修复原桌面发布工作流 Release 文本 heredoc 缩进错误（原文件无法通过 YAML 解析）。
- 本地 Release 发布（`PublishAot=false`）成功，真实 Windows 服务 API 冒烟通过，覆盖指令执行与文件读写删除。
- 白盒：157 个架构测试、66 个非外部集成测试通过；冒烟工具两项回归通过；前端门禁通过。
- 浏览器：本机 Edge 桌面/窄屏共四项通过，覆盖登录网络失败后重试和正常登录/页面/退出。CI 使用锁定 Playwright Chromium；本地 Chromium 下载未完成时启动失败，不能将其算作通过。可用 `SMOKE_BROWSER_CHANNEL=msedge` 选择已安装的 Edge 做本地验证。
- 四份 workflow YAML 解析、PowerShell 脚本解析及差异空白检查通过。
- Linux AOT 容器、Linux PTY、ARM64 和 GitHub runner 发布链路未在本地验证，必须以首次远程执行结果为准。本机 `docker` 是 wslc 包装器，不是 CI 的 Docker Engine，未把它的输出当作容器测试结果。
