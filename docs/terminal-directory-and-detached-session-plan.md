# 终端与文件目录联动、掉线后继续运行方案

## 目标与边界

1. 从文件管理器的当前目录或指定文件夹打开新终端，终端的初始工作目录与所选目录一致。
2. 终端内执行 `cd` 后，能从终端打开对应的文件管理器目录；目录状态以实际 shell 报告为准。
3. 浏览器切页、刷新、关闭或网络断开时，交互式会话继续运行；恢复连接后接回同一个会话，不重新启动命令。
4. 用户显式结束会话才终止进程。应用进程或容器重启后的继续运行属于独立的第二阶段能力，不纳入首版承诺。

## 现状依据

- `FilesView.js` 已通过 `/files?path=...` 保存当前目录；`TerminalView.js` 目前创建会话时只发送行列数，未传 `workingDirectory`。`TerminalController` 接受工作目录，但目录不存在会静默退回进程目录。
- `TerminalView.js` 在页面卸载、标签关闭和“重连”时调用会话 DELETE；“重连”实为关闭旧进程后新建。
- `TerminalEndpoints.cs` 只在 WebSocket 存在时读取会话输出。连接断开后若子进程不断输出，管道可能填满，令子进程阻塞。
- `PtySessionManager` 只保存进程对象，没有会话列表、附着状态、输出缓冲或清理策略。Windows 使用 ConPTY；Linux 的 `CrossPlatformPtyEngine` 使用重定向标准流，不是真正 PTY。

## 决策

| 能力 | 首选方案 | 取舍 |
|---|---|---|
| 文件管理器 → 终端 | 路由参数传工作目录，新建标签页 | 不修改正在运行的 shell，避免打断前台命令 |
| 终端 → 文件管理器 | shell integration 报告 CWD；仅在已校验目录上显示跳转 | 不解析提示符、不通过盲发 `pwd` 干扰交互程序 |
| 网络掉线续接 | 应用内会话 broker + 常驻输出泵 + 有界回放 | 可跨 WebSocket/页面生命周期，不能跨应用进程重启 |
| 完整 Linux 交互 | 原生 PTY 替换 Linux 管道实现 | 终端尺寸、信号、全屏应用才可靠 |
| 跨应用进程重启 | 后续独立会话宿主（Linux 优先评估 tmux） | 需要进程运行于不会随 Web 服务退出的环境；Windows 另行设计 |

## 阶段 1：单向目录联动

涉及：`FilesView.js`、`TerminalView.js`、`TerminalController.cs`、终端请求模型。

1. 文件管理器工具栏加入“在此打开终端”，文件夹操作中加入“在终端打开”。跳转使用 `/terminal?cwd=<编码后的绝对路径>`。
2. 终端读取路由参数，为该目录创建**新标签页**并在创建请求中传 `workingDirectory`。标签保存初始目录，后续普通切换标签不重复建会话。
3. 后端校验路径是当前运行环境可访问的目录；不存在、不是目录或无权访问时返回明确错误，不回退默认目录。限制控制字符、路径长度及目录范围；Windows 与 Linux 分别按本机路径规则处理。
4. 文件管理器只在目录加载成功后更新当前路径，打开终端使用已确认的目录。已存在的终端标签保持原工作目录。

验收：根目录、普通目录、含空格/中文的目录和网络挂载目录均能正确打开；无效路径显示错误；当前前台终端不被切换目录命令打断。

## 阶段 2：真正的 Linux PTY

涉及：`CrossPlatformPtyEngine.cs`、`IPtySession` 实现、Docker 运行依赖。

1. Linux 使用 PTY master/slave 启动 shell，设置会话和控制终端；Windows 继续使用现有 ConPTY。不要把重定向 stdin/stdout 管道称作 PTY。
2. 实现 `ResizeAsync` 的窗口大小更新与 SIGWINCH；处理 UTF-8 流分片、EOF、子进程退出、取消和资源释放。
3. 新实现与 WebSocket 层保持独立，使后台会话管理器始终持有 PTY master；不得由某一个 WebSocket 独占读取该流。
4. 若 Native AOT/Alpine 的 PTY 调用或权限无法满足要求，明确保留受限的管道回退，并在 UI 显示“交互能力受限”。

验收：Bash、`vim`/`top` 等全屏程序的输入、颜色、窗口缩放和 Ctrl-C 在 Linux 容器内正常；Windows ConPTY 行为不回退。

## 阶段 3：掉线续接与后台输出

涉及：`PtySessionManager`、`TerminalEndpoints.cs`、`TerminalController.cs`、`TerminalView.js`、终端 API 模型。

1. 将会话状态设计为 `RunningAttached`、`RunningDetached`、`Exited`；会话创建与 WebSocket 附着分离。每个会话只允许一个输入控制者；新附着需显式接管或拒绝，避免多标签同时写入。
2. 后端为每个会话启动唯一的持续输出泵，始终读取 PTY 输出。有界环形缓冲保存最近输出及递增序号；满额时丢弃最旧内容并标记截断。WebSocket 只订阅输出，不直接读取 PTY。
3. 增加 `GET /api/Terminal/Sessions`、`GET /api/Terminal/Sessions/{id}`；`POST` 创建，WebSocket 按会话 ID 附着，`DELETE` 显式结束。会话列表返回所有者、创建时间、运行/附着状态、退出码、初始/最近工作目录和缓冲截断信息，不返回敏感命令输入。
4. 页面卸载和网络断开只关闭 WebSocket。浏览器保存会话 ID；刷新后先查服务端会话，再附着原 ID。`404` 或已退出时明确告知，不能无提示地创建新进程。采用有上限的退避重连。
5. 重连先取得输出序号及有限回放，再继续实时输出；避免回放与实时消息交错、重复。全屏程序屏幕恢复先以重新绘制为目标，不能保证时明确显示限制；必要时引入服务端终端屏幕状态或 tmux 屏幕恢复。
6. 配置会话数上限、输出缓冲上限、最长闲置时间、进程退出后保留时长；清理过期会话和缓冲。用户关闭标签时提供“仅断开”与“结束会话”两个明确动作。
7. WebSocket 消息区分输入、控制与输出；处理消息分片及尺寸上限。鉴权沿用现有管理员权限，复核 URL 查询参数中的 JWT 暴露，优先改为短时效附着票据。

验收：运行持续输出命令时断网 1 分钟，进程持续前进，重连后仍是相同会话 ID/PID，看到截断提示或完整回放；刷新、切页、关闭浏览器后的行为一致；显式结束能终止进程并释放资源；同时附着不会双重消费输出或重复发送输入。

## 阶段 4：终端目录反向联动

涉及：shell 初始化、PTY 输出解析、终端标签状态、文件管理器导航。

1. 在支持的 Bash/PowerShell shell 中注入最小的目录报告钩子，使用 OSC 7 或同等结构化序列在提示符出现时报告 CWD；不修改用户的命令文本。
2. 每个终端会话保存最近可信的 CWD。解析器检查协议长度、URL 主机及路径编码；目录跳转前仍由文件管理器 API 验证该路径可访问。普通程序伪造控制序列不得直接触发文件操作。
3. 终端标签展示当前目录及“在文件管理器打开”按钮；不支持的自定义 shell 显示初始目录与“当前目录未知”，不推断提示符。

验收：执行 `cd` 后按钮跳转至新目录；中英文及空格路径正确；`vim`、远程 SSH 会话或不支持的 shell 不显示错误的本机目录。

## 阶段 5：跨 Web 服务重启的可选能力

若需求提升到“Web 服务重启后继续运行”，在 Linux 上评估由独立进程/容器持有 tmux 服务，再让 Web 应用附着。tmux 支持 detach/attach，但 tmux 与 Web 服务同在会停止的容器内时，容器停止仍会终止会话。需要单独定义宿主生命周期、权限、会话发现、输出恢复与备份边界；Windows 需独立进程宿主，不复用 Linux 实现。该阶段单独评审和实施。

## 执行顺序与交付门槛

按阶段 1 → 2 → 3 → 4 交付；阶段 5 需另行决定部署形态。每阶段先完成对应最小可观察行为，再做相关构建和实际浏览器/容器场景验证。跨平台失败要分别报告，不以 Linux 验证代替 Windows 验证。当前仓库中的 SFTP/S3 改动与本计划分别提交或按用户要求合并提交，不改变本计划的实施边界。

参考：[tmux 官方 Getting Started](https://github.com/tmux/tmux/wiki/Getting-Started)、[VS Code Shell Integration](https://code.visualstudio.com/docs/terminal/shell-integration)、[ASP.NET Core WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets)。
