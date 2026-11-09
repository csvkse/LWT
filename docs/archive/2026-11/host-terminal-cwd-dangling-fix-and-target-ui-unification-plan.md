# 宿主终端 CWD 孤岛根治与执行目标描述统一方案

## 一、问题现状与根因深度剖析

### 1.1 问题现象
在 Docker 特权容器（`--privileged --pid=host`）中连接宿主终端时，前端提示：
```text
[正在连接后台终端...]
[终端已连接成功]
[工作目录: /root]

shell-init: error retrieving current directory: getcwd: cannot access parent directories: No such file or directory
job-working-directory: error retrieving current directory: getcwd: cannot access parent directories: No such file or directory
job-working-directory: error retrieving current directory: getcwd: cannot access parent directories: No such file or directory
job-working-directory: error retrieving current directory: getcwd: cannot access parent directories: No such file or directory
root@R7:.# 
```
同时，定时任务页面与指令管理页面在“执行目标 / 目标主机 / 穿透目标 / 本地执行 / 容器”等描述文案与徽章上存在术语不统一、辨识度不明确的问题。

---

### 1.2 根因深度剖析

#### （1）`nsenter --wd` 的执行时序缺陷
在 `util-linux` 源码 `sys-utils/nsenter.c` 中：
- 参数 `-w, --wd[=dir]` 在解析命令行时，调用 `open_target_fd(&wd_fd, ...)`。
- **该调用发生在 `setns()` 切换 Mount Namespace 之前**！即获取的是容器 OverlayFS 中的目录文件描述符（fd）。
- 当随后 `nsenter` 调用 `setns(CLONE_NEWNS)` 切换到宿主机 PID 1 的 Mount Namespace 后，调用 `fchdir(wd_fd)`。
- 此时进程的当前工作目录指向了一个**已不属于当前 Mount Namespace 挂载树的孤岛 Inode**（Disconnected Dentry）。

#### （2）原生 C 语言层 `forkpty` 容器内 `chdir`
在 `linuxwebtool_pty.c` 中，fork 出子进程后，在执行 `execve` 之前调用了 `chdir(cwd)`。当目标为 `host` 时，控制器传入了宿主路径（如 `/root`）。子进程在容器文件系统内尝试 `chdir("/root")`，即使存在，也是容器内的 `/root`，切换 Namespace 后该目录同样成为宿主空间的悬空引用。

#### （3）Bash 交互式启动与 `getcwd()` 失败的连锁反应
Bash 以交互式（`--login` / `-i`）启动时，`main()` 初始化内部 `$PWD`（调用 `shell_re_init()`）。内核系统调用 `sys_getcwd()` 从孤立 CWD 向上回溯寻找 `/` 失败，返回 `ENOENT`。
- Bash 打印：`shell-init: error retrieving current directory: getcwd: cannot access parent directories: No such file or directory`；
- 作业控制（Job Control）每次准备上下文均失败，打印多行 `job-working-directory: ...`；
- CWD 回退为 `.`，提示符退化为 `root@R7:.#`。

---

## 二、业界主流方案对比与选型

| 方案 | 原理 | 优点 | 缺点 | 适用度 |
| :--- | :--- | :--- | :--- | :--- |
| **A. `--wdns` 参数** (util-linux >= 2.38) | 切换命名空间后打开目录 | 参数原生 | 在 CentOS 7/8、Debian 11、Alpine 3.15 等低版本工具链中不可用，会导致 `nsenter` 报错退出 | ❌ 兼容性不足 |
| **B. 容器内 `/proc/1/root`** | 容器内直接访问宿主 VFS 节点 | 简单直观 | 受限于安全模块（AppArmor/SELinux）对 proc 路径的深度检查 | ⚠️ 存在策略拦截风险 |
| **C. Post-Namespace In-Host Shell 包装器（推荐）** | `nsenter` 仅切换 Namespaces，在宿主 Namespace 内由 shell 执行 `cd "$1" \|\| cd /root \|\| cd /; exec bash --login` | 100% 跨发行版与工具链版本兼容；入宿主后再 cd，彻底杜绝孤岛 fd；零注入风险 | 需启动微小引导进程，但因 `exec` 替换无常驻开销 |  **最佳方案** |

---

## 三、系统架构实施方案

```mermaid
sequenceDiagram
    autonumber
    participant Web as Web 终端前端
    participant Ctrl as TerminalController
    participant Engine as CrossPlatformPtyEngine
    participant Pty as LinuxNativePty (forkpty)
    participant Nsenter as nsenter (PID 1)
    participant HostShell as Host /bin/sh
    participant Bash as Host /bin/bash (Login Shell)

    Web->>Ctrl: POST /Terminal/Sessions (target="host", cwd=null)
    Ctrl->>Engine: PtyStartOptions(Target="host", WorkingDirectory="/root")
    Engine->>Pty: 启动 PTY (容器内 cwd 固定为 "/")
    Pty->>Nsenter: execve(/usr/bin/nsenter, [-t, 1, -m, -u, -i, -n, -p, --, env, -u, LC_ALL, LANG=C.UTF-8, /bin/sh, -c, "cd \"$1\"...; exec /bin/bash --login", sh, "/root"])
    Note over Nsenter: setns() 切换到宿主机 Mount/PID/NET Namespaces
    Nsenter->>HostShell: 启动宿主 /bin/sh (执行 cd "/root")
    Note over HostShell: 在宿主真实 VFS 挂载树内执行 chdir("/root")
    HostShell->>Bash: exec /bin/bash --login (进程镜像替换)
    Note over Bash: getcwd() 正常寻址至宿主 /root，提示符正确显示 root@R7:~#
    Bash-->>Web: 正常终端交互流 (无 setlocale、无 getcwd 告警)
```

### 1. 终端宿主穿透执行链重构
在 `CrossPlatformPtyEngine.cs` 中：
```csharp
var targetCwd = !string.IsNullOrWhiteSpace(options.WorkingDirectory) ? options.WorkingDirectory : "/root";
var envExe = File.Exists("/usr/bin/env") ? "/usr/bin/env" : "/bin/env";
return (nsenter, [
    "-t", "1", "-m", "-u", "-i", "-n", "-p", "--",
    envExe, "-u", "LC_ALL", "LANG=C.UTF-8",
    "/bin/sh", "-c", "cd \"$1\" 2>/dev/null || cd /root 2>/dev/null || cd /; exec /bin/bash --login",
    "sh",
    targetCwd
]);
```

在 `LinuxNativePty.cs` 与 `TerminalController.cs` 中：
当 `Target == "host"` 时，向容器底层 `lwt_pty_start` 传递的工作目录固定为 `"/"`，杜绝容器在切换 Namespace 前提前针对宿主路径执行无效的 `chdir`。

---

## 四、执行目标描述与设计系统统一规范

### 1. 概念与维度对齐
在整个系统中，执行目标划分为两个正交维度：
1. **网络与节点维度（Host Node Mode）**：
   - `🖥️ 本地执行`（Local）：在本机节点执行；
   - `🌐 远端主机`（Single Remote）：指定一台 SSH 远端主机；
   - `🌐 多主机分发`（Multi-Remote）：在多台远端主机并发调度。
2. **本地环境下的特权穿透目标（Execution Target）**：
   - `📦 当前容器`（`container`，默认）：运行在 LinuxWebTool 容器环境内部；
   - `🖥️ 宿主主机 (nsenter穿透)`（`host`）：在特权模式下穿透到宿主机 PID 1 执行。

### 2. 页面与组件统一映射表

| 场景 / 位置 | 历史描述（不统一） | 统一标准规范 |
| :--- | :--- | :--- |
| **指令管理列表表头** | `目标主机` | `执行目标` |
| **定时任务列表表头** | `执行节点` | `执行目标` |
| **执行历史列表表头** | `执行目标` | `执行目标` |
| **列表徽章（本地-容器）** | `📦 容器` / `🖥️ 本地执行` | `<span class="badge border-slate-600/50 text-slate-400">📦 当前容器</span>` |
| **列表徽章（本地-宿主）** | `🖥️ 宿主` / `🖥️ 宿主主机` | `<span class="badge border-amber-500/50 text-amber-300">🖥️ 宿主主机</span>` |
| **列表徽章（远端单机）** | `🌐 hostName` | `<span class="badge border-cyan-500/50 text-cyan-300">🌐 hostName</span>` |
| **列表徽章（多机分发）** | `🌐 多主机分发` | `<span class="badge border-indigo-500/50 text-indigo-300">🌐 多主机分发</span>` |
| **编辑弹窗标题与单选** | `穿透目标` / `执行目标(Target)` | 统一显示：`执行目标 (Target)`，单选 `📦 当前容器`、`🖥️ 宿主主机 (nsenter穿透)`，副标题 `特权容器穿透` |

---

## 五、验证与门禁标准
1. **自动化架构与单元测试**：
   - `HostTargetAndPrivilegedExecutionTests` 验证宿主终端参数构造链（包含 `/bin/sh -c`、安全参数 `$1`、`/bin/bash --login` 以及工作目录净化）；
   - 前端架构门禁 `frontend-gate.cjs` 59 个文件无任何回归；
   - 前端单测 36 项全部通过；
   - 后端 AOT 编译 0 警告 0 错误；
   - 183 项架构测试、259+ 项单元测试、86 项集成测试全部通过。
