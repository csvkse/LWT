# 终端方案实施记录

依据：`terminal-directory-and-detached-session-plan.md`。

## 实施边界与决策

- 在当前工作区继续实施，保留已有方案文档修改；本次授权不包含 Git 提交或推送。
- 范围确认（2026-10-02）：仅保证软件运行期间的浏览器断线续跑；原阶段 5 已移除，不实施跨软件、容器或系统重启恢复，也不新增独立终端宿主。
- 包管理安装操作仅实现管理员主动触发入口，开发期间不修改宿主系统权限或安装终端系统包。

## 当前进度

> Windows 后续实施已启用原生 ConPTY 并补齐文件目录支持；下面阶段记录保留首次交付状态，最新结果见 `windows-terminal-and-file-integration-plan.md`。

- 阶段 1：已加入目录打开终端入口与后端目录校验；无效目录拒绝测试先失败，修改后两项目录测试通过。
- 阶段 2：Linux 原生 PTY 已通过 Alpine Native AOT 容器验证；Windows 现有 ConPTY 开关为关闭状态，保持管道回退并在页面明确提示交互受限。
- 阶段 3：已加入会话列表、详情、更新、短期一次性附着票据、持续输出读取、有界回放、单连接控制、默认后台会话、未使用会话清理和系统依赖检测。Linux 主机具备 root 权限时提供主动安装 tmux 入口；Docker 镜像预装 tmux，运行时禁用安装。真实 Windows 终端测试确认断线后 PID 保持、重新附着成功。
- 阶段 4：Bash 的 OSC 7 目录报告与提示符标记经解析、前台进程空闲检查后写入会话状态；终端跳转前再次调用文件 API 验证。Windows 文件 API 当前只处理 POSIX 路径，隐藏 Windows 反向跳转入口。
- 页面：后台列表、显式结束、保留、刷新恢复和有限重连已在浏览器操作验证；刷新和断线接回保持相同 PID，文件管理器与终端双向跳转已验证。
- 原阶段 5：已根据用户确认移出计划，不再列为待实施项。Windows ConPTY 与 Linux 原生 PTY 足以支持当前应用内后台模式，tmux 为可选工具。

## 验证记录

- `dotnet test tests/LinuxWebTool.ArchitectureTests -c Release --verbosity quiet`：57 项通过。
- `dotnet test tests/LinuxWebTool.IntegrationTests -c Release --verbosity quiet`：16 项通过。首次并行构建发生同一 DLL 写入争用，串行重跑通过。
- `node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs`：通过；修改范围内的 ESLint：通过。全量 ESLint 仍有其他页面既有问题，不并入本次修改。
- Linux Alpine Native AOT 容器真实验证：`test -t 0`、窗口大小调整、中文及空格目录报告、文件 API、60 秒断线续跑、同 PID 接回、输出截断及未使用终端自动清理均通过。
- 最终镜像 `linuxwebtool-terminal-final` 重新构建成功；新镜像的真实容器验证增加了 Ctrl-C、`top` 全屏交互、tmux 预装检查，均通过。空闲清理使用测试专用 2 秒宽限配置验证；产品默认值仍为 120 秒。
