# 业界主流插件架构对比与第三方库选型评估

> 日期：2026-11-01  
> 目标：系统对比业界主流插件/扩展系统的进程隔离与前端呈现方案，评估可在 .NET 宿主与 Go Worker 中复用或引入的成熟库。

---

## 1. 业界五大标杆插件架构深度剖析

```mermaid
flowchart TD
    subgraph VSCode["VS Code 插件体系"]
        VH[Extension Host 进程] <-->|JSON-RPC postMessage| CW[Sandboxed Webview iframe]
        VH <-->|LSP / DAP| Core[VS Code Core Shell]
    end

    subgraph HomeAssistant["Home Assistant Ingress 体系"]
        HACore[HA Core Host] -->|Dynamic Ingress Reverse Proxy\n/api/hassio_ingress/{token}| Addon[Docker/Process Add-on\n(e.g. Node-RED / AdGuard)]
        UI[HA Frontend] -->|Generic Iframe or Direct Open| Addon
    end

    subgraph Grafana["Grafana 插件体系"]
        GC[Grafana Server] <-->|gRPC / hashicorp-go-plugin| GP[Backend Plugin Process]
        GF[Grafana WebApp] -->|Runtime SystemJS / Module Fed| GFE[Frontend Plugin Bundle]
    end
```

### 1.1 VS Code 插件架构
- **隔离机制**：插件业务代码运行在独立的 `Extension Host` 辅助进程中，绝不与主 UI 进程共享内存。
- **界面交互**：插件无法直接操纵主 DOM。若插件需要复杂自定义界面，通过 `Webview API` 创建沙盒 `<iframe>`。主进程与 Webview 之间通过受限的消息总线通信：`acquireVsCodeApi().postMessage(data)` 与 `window.addEventListener('message', ...)`.
- **对本项目的启示**：宿主核心源码绝不为任何特定插件编写组件或写 `if (extension.id === 'xxx')`。宿主只提供沙盒挂载点与通信桥梁。

### 1.2 Home Assistant Add-on & Ingress 架构（最契合 NAS/单机平台）
- **隔离机制**：每个 Add-on 是一个独立容器或本地常驻进程，拥有专属端口。
- **界面交互**：**Home Assistant Ingress 动态反向代理**。宿主生成一次性会话令牌，通过 Ingress 动态重写 Cookie 与路径头，将外网请求安全映射至插件自身的 Web 端口。
- **对本项目的启示**：Home Assistant 纳管数百种完全不同的复杂软件（如 Node-RED 流程编辑器、AdGuard Home 仪表盘、Zigbee2MQTT 网络拓扑），**Home Assistant 前端源码中没有包含这些软件的任何一行 Vue/React 代码**，全部通过通用的 Ingress 页面无缝呈现。这是 LinuxWebTool 应当遵循的最高解耦准则。

### 1.3 Grafana 插件架构
- **隔离机制**：后端插件采用 HashiCorp 的 `go-plugin`，通过标准输入输出建立 gRPC 双向流通道与健康心跳。
- **界面交互**：前端采用前端微模块（Runtime Module Federation），将插件编译后的静态 JS 文件动态挂载到仪表盘面板容器中。
- **对本项目的启示**：若未来需要将 Worker 前端深度嵌入宿主主界面仪表盘，可采用微前端或动态挂载协议；但若 Worker 是自成体系的管理服务，Ingress 代理是最简且最稳固的方案。

### 1.4 Docker Extensions & Portainer
- **隔离机制**：扩展由后端 API 容器与一组静态 HTML/JS 静态资源组成。
- **界面交互**：安装扩展后，宿主 Docker Desktop 仅在左侧菜单栏添加一个由扩展 `metadata.json` 声明的标签项，点击标签时直接在 WebView 中加载扩展自带的静态页面。

### 1.5 Kubernetes Dashboard & Octant（声明式模式）
- **隔离机制**：CRD（Custom Resource Definition）仅声明数据 Schema 与控制状态。
- **界面交互**：前端根据 CRD 的 OpenAPI v3 Schema 动态递归生成表格、表单和属性检查器，完全不需要编写专有视图组件。

---

## 2. 业界主流方案对比矩阵

| 评估维度 | VS Code Webview | Home Assistant Ingress | Grafana Plugin | Docker Extension | LinuxWebTool Worker (纯粹解耦目标) |
|---|---|---|---|---|---|
| **进程隔离性** | 极强（独立 Extension Host） | 极强（独立容器/进程） | 极强（独立 gRPC 进程） | 极强（独立容器） | **极强（Native/Go 独立进程，JobObject/cgroups 隔离）** |
| **宿主源码侵入度** | **0（零侵入）** | **0（零侵入）** | **0（零侵入）** | **0（零侵入）** | **0（坚决彻底清除所有专有判断与组件）** |
| **界面呈现方式** | Sandboxed iframe | Ingress 反向代理 + iframe/独立页 | Micro-Frontend 动态挂载 | WebView 加载扩展静态包 | **宿主 Ingress `/w/{id}/*` 反向代理 + 自包含 UI** |
| **插件界面自治技术栈** | 任意（React/Vue/纯HTML） | 任意（依赖自身 Web 服务） | 必须契约兼容 Grafana UI 库 | 任意 | **任意（Go `embed` 内置单页，无外部环境依赖）** |
| **通信与控制协议** | JSON-RPC 消息总线 | HTTP Ingress + Session Header | gRPC over Stdio | REST / Docker Engine API | **统一 `/control/*` 治理协议 + Ingress 数据面** |
| **更新与卸载清理** | 删插件包即完全干净 | 停止容器即完全干净 | 移除二进制即完全干净 | 移除扩展包即完全干净 | **删除 `data/workers/{id}` 即 100% 干净，宿主无残留** |

---

## 3. 候选第三方库选型评估（按 Native AOT 与纯粹性把关）

### 3.1 .NET 宿主侧候选库评估

| 候选库 | 功能与定位 | Native AOT 兼容性 | 引入决策 | 理由与收益 |
|---|---|---|---|---|
| **`Yarp.ReverseProxy`** | 工业级高性能反向代理引擎 | **100% 兼容** (已有) | **复用现有** | 宿主已有成熟反向代理，已在 `PipelineExtensions.cs` 实现 `/w/{workerId}` 动态路由转发。 |
| **`Stateless`** | 纯 C# 轻量状态机库 | **100% 兼容** (零反射、纯强类型构建) | **可选引入** | 用于替换当前 `WorkerStateMachine` 的手写 switch 分支，支持带参状态迁移（如携带 RunID/Generation 栅栏），代码可读性与健壮性倍增。 |
| **`CliWrap`** | 现代化强类型跨平台进程调度库 | **100% 兼容** (基于纯 BCL 进程流封装) | **建议引入** | 替换原生手写 `Process.Start`。优雅支持：实时流式读取 stdout/stderr 且有界截断、管道超时强杀、跨平台级联杀死进程树，彻底杜绝僵死孤儿。 |
| **`JsonSchema.Net`** | 符合 JSON Schema 标准的验证器 | **100% 兼容** (支持代码生成上下文) | **可选引入** | 用于验证 Worker 提交的动作参数与配置包，避免手写繁重的 JSON 校验规则。 |

### 3.2 Go 插件侧候选库评估

| 候选库 | 功能与定位 | 许可证 | 引入决策 | 理由与收益 |
|---|---|---|---|---|
| **`embed` (Go BCL)** | Go 1.16+ 语言原生内嵌静态文件 | BSD-3-Clause | **核心采纳** | 将 Worker 专属的前端单页 HTML/JS/CSS 直接编译进单可执行文件中，打包后外部 0 依赖，解压即运行。 |
| **`hashicorp/go-plugin`** | HashiCorp 出品的生产级 RPC 插件系统 | MPL-2.0 | **借鉴其设计思想，不强引入** | 适合复杂多语言双向 gRPC 协同。本项目当前基于 HTTP/JSON 与 MCP 协议，直接使用标准库更轻量（无需引入重量级 gRPC 运行时与 Protobuf 工具链）。 |
| **`panjf2000/ants/v2`** | 高性能百万级协程池 | Apache-2.0 | **复用已有依赖** | 已包含在 `tunnel-client` 依赖树中，可用于第三方 MCP 调用的受限并发队列。 |

---

## 4. 选型实施结论

1. **宿主代码净化与架构解耦**：
   - 宿主 `WorkersView.js` 坚决恢复为纯粹的通用管理面板；
   - 宿主利用现有的 `Yarp` / `PipelineExtensions.cs` 反向代理能力，将 `/w/{workerId}/` 作为统一的 Worker Ingress 入口；
   - 宿主卡片在 Worker 处于 Service 模式时，显示通用的“访问服务 / 控制台”（`w.accessUrl`），点击即可直接打开 Worker 的自包含 WebUI；
2. **Worker 自包含全栈交付（Zero Host Dependency）**：
   - `workers/mcp-bridge` 内部集成一套极轻量的自包含 Web 管理界面（采用原生 JavaScript + Tailwind CDN，通过 `//go:embed` 打包），在启动时绑定回环端口；
   - Worker 拥有自己的路由处理、草稿管理与工具勾选，不向宿主泄漏任何专有 DTO。
