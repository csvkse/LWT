# 现有 21+ 扩展插件全量升级至 Schema v3.1 规范与目录标准化方案

> **文件标识**：`docs/existing-plugins-schema-v3-migration-and-directory-standardization-plan.md`  
> **状态**：方案制定与架构调研阶段（已获授权：由于未正式发布，完全无需负担向下兼容）  
> **核心目标**：全面清零二元推断与硬编码历史包袱，统一现有 21 个扩展至 Schema v3.1 规约，实现物理目录标准化与声明式构建

---

## 一、 重构契机与背景分析 (Why Now & Zero Legacy Burden)

在软件工程演进中，**“未正式发版且明确无需负担向下兼容包袱”是清零技术债务、进行体系化规范化重构的最佳窗口期**。

### 1. 现有插件现状与技术债务诊断
目前 `plugins/` 目录下共托管了 21 个插件与扩展示例，经排查发现如下结构性缺陷：
- **Schema 严重割裂**：仅有 2 个插件使用了最新的 Schema v3.1，其余 19 个插件仍停留在旧版 `manifestVersion: 2`，缺失 `archetype`、`layout`、`build`、`interface` 等核心声明；
- **隐式推断带来的隐患**：宿主后端为了适配旧插件，不得不保留多处基于语言和 Profile 的后备推断（如“若为 Wasm 或 Go 则推断为 SourceCompiled，否则推断为 Interpreted”），破坏了系统的声明式纯粹性；
- **目录结构野蛮混杂**：部分插件源码直接暴露在根目录（如 `index.js`, `main.py`），部分插件构建产物（ELF 二进制、`.wasm` 模块）与源代码混在一起，未实现产物与源码的物理隔离；
- **构建逻辑分散在外部**：部分 Go 插件（如 `mcp-bridge`, `lxc-manager`）的编译命令硬编码在 `Dockerfile.dev` 中，插件清单自身并未声明自己的构建管道。

### 2. 改造后收益
1. **统一契约标准**：所有 21 个插件 100% 对齐 Schema v3.1，消灭所有模糊猜测与后备推断分支；
2. **物理目录规整**：所有插件统一采用标准的物理布局（`src/`, `bin/`, `storage/`, `run/`, `ui/`）；
3. **构建管道自包含**：每个编译型插件自包含声明式 `build.command`，宿主引擎与 Dockerfile 零侵入；
4. **服务发现全量覆盖**：全量插件通过 `interface.services` 显式导出其 Operations，使 `GET /api/Plugins/Discovery` 成为完整、严谨的系统级微服务网关拓扑图。

---

## 二、 现有 21 个插件的分类与 Schema v3.1 映射蓝图

根据计算机科学本质（生命周期 × 并发调度 × 通信协议），全量 21 个插件分为 **5 大标准形态族（Archetype Families）**：

```
+---------------------------------------------------------------------------------------------------------+
|                                21 个插件的 5 大标准架构形态族 (Archetype Families)                       |
+---------------------------------------------------------------------------------------------------------+
| 族 1: 声明式源码编译型微服务 (SourceCompiled + Resident + Http)                                         |
|       - mcp-bridge (Go)           - lxc-manager (Go)                                                    |
|       - socks5-proxy (Go)         - socks5-forwarder (Go)                                               |
|       - demo-dotnet-service (C#)                                                                        |
+---------------------------------------------------------------------------------------------------------+
| 族 2: WebAssembly 极速微函数 (SourceCompiled + Wasm + WasmAbi)                                          |
|       - demo-nativeaot-wasm (C# 14 NativeAOT-LLVM bflat)                                                |
|       - demo-dotnet-wasm (.NET 10 wasi-wasm)                                                            |
|       - demo-wasm-function (WAT WebAssembly Text 汇编)                                                  |
+---------------------------------------------------------------------------------------------------------+
| 族 3: 解释型按需动态微服务 (Interpreted + Resident / Serverless + Http)                                 |
|       - demo-serverless-api (Node.js FaaS)   - demo-js-webhook (Node.js)                                |
|       - chatgpt-channel (Python)             - demo-py-api (Python)                                     |
|       - demo-sh-daemon (Bash)                - demo-ps-daemon (PowerShell)                              |
+---------------------------------------------------------------------------------------------------------+
| 族 4: 原生系统级服务与运维工具 (PrebuiltBinary / Interpreted + OneShot + CliArgs)                       |
|       - caddy-manager (Native Caddy)         - caddy-server-manager (Node 控制)                         |
|       - ssh-access-guard (Bash 审计)         - sample-healthcheck (Bash 探针)                           |
|       - native-command-demo (POSIX 工具)                                                                |
+---------------------------------------------------------------------------------------------------------+
| 族 5: 原生受控宿主守护微服务 (PrebuiltBinary + Resident + Http)                                         |
|       - native-managed (C/Rust ELF)          - native-resident (C/Rust ELF)                             |
+---------------------------------------------------------------------------------------------------------+
```

### 全量 21 个插件的详细映射清单表

| 插件 ID | 插件名称 | 目标 Archetype | Lifecycle | Protocol | 标准入口 | 标准构建命令 (`build.command`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `mcp-bridge` | MCP 智能安全网关 | Resident | SourceCompiled | Http | `bin/mcp-bridge` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o bin/mcp-bridge ./cmd/mcp-bridge` |
| `lxc-manager` | Incus/LXC 容器管理器 | Resident | SourceCompiled | Http | `bin/lxc-manager` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o bin/lxc-manager ./cmd/lxc-manager` |
| `socks5-proxy` | SOCKS5 代理网关 | Resident | SourceCompiled | Http | `bin/socks5-proxy` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o bin/socks5-proxy ./cmd/socks5-proxy` |
| `socks5-forwarder`| 流量分发转发器 | Resident | SourceCompiled | Http | `bin/socks5-forwarder` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o bin/socks5-forwarder ./cmd/socks5-forwarder` |
| `demo-dotnet-service`| C# 现代微服务 | Resident | SourceCompiled | Http | `bin/publish/DemoDotnetService.dll` | `dotnet publish -c Release -o bin/publish` |
| `demo-nativeaot-wasm`| C# NativeAOT 极速微函数 | Wasm | SourceCompiled | WasmAbi | `bin/demo-nativeaot-wasm.wasm` | `bflat build --arch wasm --os wasi src/Program.cs -o bin/demo-nativeaot-wasm.wasm` |
| `demo-dotnet-wasm`| C# WASI 微函数 | Wasm | SourceCompiled | WasmAbi | `bin/demo-dotnet-wasm.wasm` | `dotnet publish -c Release /p:TargetArchitecture=wasm` |
| `demo-wasm-function`| WAT 原生微函数 | Wasm | SourceCompiled | WasmAbi | `bin/main.wasm` | `wat2wasm src/main.wat -o bin/main.wasm` |
| `demo-serverless-api`| Serverless 动态函数 | Serverless | Interpreted | Http | `src/index.js` | *无需构建 (Interpreted No-Op)* |
| `demo-js-webhook` | Node.js Webhook | Resident | Interpreted | Http | `src/index.js` | *无需构建 (Interpreted No-Op)* |
| `chatgpt-channel` | ChatGPT 消息通道 | Resident | Interpreted | Http | `src/app.py` | *无需构建 (Interpreted No-Op)* |
| `demo-py-api` | Python 轻量 API | Resident | Interpreted | Http | `src/main.py` | *无需构建 (Interpreted No-Op)* |
| `demo-sh-daemon` | Bash 系统常驻监控 | Resident | Interpreted | Http | `src/daemon.sh` | *无需构建 (Interpreted No-Op)* |
| `demo-ps-daemon` | PowerShell 资源守护 | Resident | Interpreted | Http | `src/daemon.ps1` | *无需构建 (Interpreted No-Op)* |
| `caddy-manager` | Caddy 原生代理网关 | OneShot | PrebuiltBinary | CliArgs | `caddy` | *无需构建 (PrebuiltBinary No-Op)* |
| `caddy-server-manager`| Caddy 脚本化控制器 | OneShot | Interpreted | CliArgs | `src/status.js` | *无需构建 (Interpreted No-Op)* |
| `ssh-access-guard` | SSH 访问审计守卫 | OneShot | Interpreted | CliArgs | `src/records.sh` | *无需构建 (Interpreted No-Op)* |
| `sample-healthcheck`| 宿主健康检查探针 | OneShot | Interpreted | CliArgs | `src/check.sh` | *无需构建 (Interpreted No-Op)* |
| `native-command-demo`| 原生命令封装示例 | OneShot | PrebuiltBinary | CliArgs | `/bin/ls` | *无需构建 (PrebuiltBinary No-Op)* |
| `native-managed` | 原生受控常驻微服务 | Resident | PrebuiltBinary | Http | `bin/native-service` | *无需构建 (PrebuiltBinary No-Op)* |
| `native-resident` | 原生常驻守护进程 | Resident | PrebuiltBinary | Http | `bin/resident-app` | *无需构建 (PrebuiltBinary No-Op)* |

---

## 三、 标准化物理目录规范 (Standardized Layout Specification)

为杜绝源码与产物混杂，所有插件统一执行如下结构约束：

```
plugins/<plugin-id>/
├── plugin.json                 # 核心唯一描述清单 (Schema v3.1 规范)
├── README.md                   # 插件说明文档与接口指南
├── src/                        # 【标准源码目录】：C# / Go / JS / Python / Shell / WAT 源码
│   ├── Program.cs / main.go / app.py / index.js / main.wat
│   └── ...
├── bin/                        # 【标准产物目录】：只存放编译出的可执行文件或 .wasm 模块
│   └── <plugin-id> / <plugin-id>.wasm
├── storage/                    # 【标准持久化目录】：插件独占 SQLite、本地配置、KV 存储
│   └── data.db
├── run/                        # 【标准运行时目录】：存放 PID 文件、UDS (.sock) 套接字、临时锁
│   └── service.sock
├── ui/                         # 【标准前端专属视图】：index.html, app.js, style.css (若有专属看板)
└── hooks/                      # 【标准扩展钩子】：仅限辅助运维脚本
```

### 清单中的 `layout` 映射声明示例：
```json
"layout": {
  "sourceEntry": "src/Program.cs",
  "targetArtifact": "bin/demo-nativeaot-wasm.wasm",
  "storageDir": "storage",
  "runtimeDir": "run"
}
```

---

## 四、 声明式构建规约与自包含设计 (Self-Contained Build Spec)

对于生命周期为 `SourceCompiled` 的插件，`build` 节点必须严格遵循如下契约：

```json
"build": {
  "toolchain": "go",
  "command": "CGO_ENABLED=0 go build -ldflags '-s -w' -o bin/mcp-bridge ./cmd/mcp-bridge",
  "fallbackCommand": "go build -o bin/mcp-bridge ./cmd/mcp-bridge",
  "artifacts": ["bin/mcp-bridge"],
  "watch": ["cmd/**/*.go", "internal/**/*.go", "go.mod"],
  "autoCompileOnSave": true,
  "timeoutSeconds": 120,
  "cleanTargets": ["bin/"]
}
```

### 规则收益：
1. **解耦宿主与 Dockerfile**：`Dockerfile.dev` 中不再需要编写各插件的编译逻辑，只需遍历插件并调用其声明式 `build.command`，或者在容器启动时按需编译；
2. **在线代码编辑秒级热更联动**：开发者在 Web 控制台编辑 `cmd/mcp-bridge/main.go`，点击保存即通过 `build.command` 自动重新编译出静态 Linux ELF 并执行原子 Inode 覆盖，体验极佳。

---

## 五、 标准化微服务接口契约与动态发现暴露 (Interface Spec)

每个插件显式声明自身导出的微服务名称与操作列表，消除任何隐式推断：

```json
"interface": {
  "protocol": "Http",
  "endpoint": "http://127.0.0.1:{port}",
  "healthEndpoint": "/health",
  "services": {
    "mcp-gateway": {
      "description": "MCP 智能安全网关中继服务",
      "operations": ["inspect", "tunnel", "status", "metrics"]
    }
  }
}
```

### 发现端点聚合效果 (`GET /api/Plugins/Discovery`)：
当全部 21 个插件升级完毕后，调用服务发现将呈现整齐划一的工业级微服务注册表：
```json
[
  {
    "id": "mcp-gateway",
    "name": "MCP 智能安全网关中继服务",
    "protocol": "Http",
    "lifecycle": "SourceCompiled",
    "operations": ["inspect", "tunnel", "status", "metrics"]
  },
  {
    "id": "calculator",
    "name": "NativeAOT-LLVM 极速算力服务",
    "protocol": "WasmAbi",
    "lifecycle": "SourceCompiled",
    "operations": ["fib", "calc", "echo", "info"]
  },
  {
    "id": "ssh-guard",
    "name": "SSH 访问审计守卫",
    "protocol": "CliArgs",
    "lifecycle": "Interpreted",
    "operations": ["status", "records", "failed", "banned_list", "ban_ip", "unban_ip"]
  }
]
```

---

## 六、 演进路线图与安全门禁保障 (Evolution Roadmap)

```
阶段 1: 规范化规约与目录演进
  -> 创建各插件标准的 src/ 与 bin/ 目录；
  -> 迁移散落的代码文件，调整 entrypoint 指向规范目录。

阶段 2: 清单升级 (manifestVersion: 3)
  -> 逐一为 21 个插件写入 archetype, layout, build, interface 节点；
  -> 校验每个插件的 JSON 语法合规性。

阶段 3: 本地编译与门禁验证
  -> 运行 422 单元测试与 228 架构门禁测试；
  -> 运行前端架构门禁与全仓凭据扫描；
  -> 验证 DiscoverServices 发现全量 21 个插件的服务描述符。

阶段 4: Dev 容器部署与真实验收
  -> 使用 scripts/deploy-remote.ps1 -Mode Dev 极速同步并部署；
  -> 使用 Chrome DevTools MCP 内置浏览器全面验收各插件卡片渲染与调用。
```
