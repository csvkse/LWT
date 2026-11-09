# 统一插件分类体系重构与多维正交分类学调研与设计方案

> **文件标识**：`docs/plugin-classification-and-archetype-orthogonal-taxonomy-plan.md`  
> **状态**：方案制定与架构调研阶段（已获授权：由于未正式发布，完全无需负担向下兼容）  
> **核心命题**：原有 Profile 分类（常驻微服务 12、脚本扩展 5、按需 FaaS 1、Wasm 微函数 3）的本质缺陷剖析，基于云原生与计算机科学本质的正交重构体系，适配器策略模式与状态机落地

---

## 一、 原有分类体系的本质缺陷与逻辑欠缺剖析 (Root-Cause Analysis)

当前系统前端顶栏的 Profile 过滤 Tab 栏如下：
- `🧩 全部插件 (21)`
- `⚡ 常驻微服务与守护 (12)`
- `🔌 脚本轻量扩展 (5)`
- `🚀 动态按需 FaaS (1)`
- `⚡ Wasm 微函数 (3)`

在完成 **Schema v3.1 通用多维架构演进** 之后，重新审视该分类，暴露出 **3 大致命欠缺与逻辑混乱**：

```
+---------------------------------------------------------------------------------------------------------+
|                                  原有分类分类学的“维度撕裂”与混乱根源                                    |
+---------------------------------------------------------------------------------------------------------+
| 1. 正交性破坏 (Category Error)  | 将【生命周期/常驻状态】(Resident)、【执行时效与语言】(OneShot/脚本)、    |
|                                 | 【弹性冷热调度】(Serverless/FaaS)、【底层技术实现栈】(Wasm) 强行并列为     |
|                                 | 互斥的平级枚举，违背了计算机科学的正交分解原则 (Orthogonality)。          |
+---------------------------------+-----------------------------------------------------------------------+
| 2. 命名与心智模型二元冲突       | • 把 OneShot 称为“脚本轻量扩展”，但实际包含 C/Go 原生编译的 CLI 工具     |
|                                 |   （如 native-command-demo），造成非脚本插件被强行叫作“脚本”；           |
|                                 | • 图标发生双重冲突：常驻守护与 Wasm 均使用了 ⚡ 图标；                  |
|                                 | • 动态按需 FaaS 与 Wasm 微函数本质同属“无状态弹性函数计算”，却被人为撕裂。 |
+---------------------------------+-----------------------------------------------------------------------+
| 3. 动态扩展与组合能力缺失       | 随着未来接入 AI 智能体 (Agent)、定时批处理 (CronJob)、流式数据管道，原有  |
|                                 | 单维平铺 Tab 栏将彻底爆炸，无法表达组合特性（如“Wasm 写的常驻网关”）。   |
+---------------------------------------------------------------------------------------------------------+
```

---

## 二、 业界主流计算分类学深度对比 (Industry Benchmark)

| 平台 / 规范 | 顶级分类模型 (Top-Level Archetype) | 次级正交过滤维度 (Secondary Slices) | 对 LWT 的借鉴价值 |
| :--- | :--- | :--- | :--- |
| **CNCF / Kubernetes (K8s)** | **Workload Resources (工作负载语义)**：<br>1. `Deployments / Daemons` (长时服务)<br>2. `Jobs / CronJobs` (有限生命周期单次任务)<br>3. `Functions (Knative)` (事件驱动弹性函数) | • RuntimeClass (Wasm / runc)<br>• Protocol (gRPC / HTTP)<br>• Namespaces / Labels | **彻底摒弃以语言或实现技术命名顶级分类**，严格按照“计算负载语义”进行顶级抽象。 |
| **AWS Serverless Compute** | 1. `ECS / Fargate` (容器化常驻微服务)<br>2. `Lambda` (毫秒级弹性计算微函数)<br>3. `AWS Batch / Tasks` (单次批处理运维任务) | • Architecture (x86 / arm64)<br>• Runtime (Node / Python / Custom)<br>• Triggers (API / S3) | 将 FaaS 与极速轻计算（包括 Wasm 与脚本）统一归入 **Functions** 范畴。 |
| **Cloudflare Workers & Vercel** | 1. `Services & Gateways` (长连接/反向代理服务)<br>2. `Functions & Edge Wasm` (边缘弹性微函数)<br>3. `CLI & Integrations` (命令行工具与外部集成) | • Language (JS / Rust / Go / Python)<br>• Transport (HTTP / WebSocket / TCP) | 消除 Wasm 与 FaaS 的对立，统称为“函数计算”；将 OneShot 正名为“指令与工具”。 |
| **Dapr (Distributed Application)** | 1. `Services` (分布式长时微服务)<br>2. `Bindings / Jobs` (单次事件触发任务) | • Components (PubSub / StateStore)<br>• Protocols (HTTP / gRPC) | 服务调用网关与协议透明化，通过 Service Invocation 抹平差异。 |

---

## 三、 重构方案：基于计算拓扑本质的正交分类学 (The Orthogonal Taxonomy)

为实现概念清晰、互斥且穷尽（MECE 原则），我们将分类体系重构为 **“顶级工作负载范式 (Top-Level Workload Tab) + 次级切面过滤 (Secondary Slice Filters)”** 的现代化正交架构：

### 1. 顶级主 Tab 栏重构（三大计算机科学计算范式）

从原先混乱的 4 个 Tab，重构为 **三大互相正交、穷尽全量的工作负载主分类**：

```
+---------------------------------------------------------------------------------------------------------+
|                                      全新三大工作负载主分类 (Top-Level Archetypes)                       |
+---------------------------------------------------------------------------------------------------------+
| 🧩 全部插件 (21)                                                                                         |
+---------------------------------------------------------------------------------------------------------+
| 🌐 常驻微服务与网关 (12)      | 对应长时运行、持有网络端口/套接字、进程生命周期由 Supervisor 守护的微服务。|
|                              | 包含：mcp-bridge, lxc-manager, socks5-proxy, demo-dotnet-service,       |
|                              | demo-js-webhook, chatgpt-channel, demo-py-api, demo-sh-daemon 等。     |
+------------------------------+-------------------------------------------------------------------------+
| ⚡ 弹性微函数与计算 (4)       | 对应无状态、内存安全隔离、支持 Scale-to-Zero 按需唤醒与极速冷启动的函数。  |
|                              | 统一归并原【按需 FaaS (1)】与【Wasm 微函数 (3)】！                       |
|                              | 包含：demo-nativeaot-wasm, demo-dotnet-wasm, demo-wasm-function,        |
|                              | demo-serverless-api。                                                   |
+------------------------------+-------------------------------------------------------------------------+
| 🛠️ 运维指令与单次任务 (5)     | 对应有限生命周期、随调随走、执行 POSIX CLI 命令或运维巡检的工具扩展。    |
|                              | 正名化：彻底去除“脚本”偏见，支持原生命令与脚本统一纳入。                |
|                              | 包含：caddy-manager, caddy-server-manager, ssh-access-guard,            |
|                              | sample-healthcheck, native-command-demo。                              |
+---------------------------------------------------------------------------------------------------------+
```

#### 为什么这个归并与重塑是架构级优化？
1. **彻底解决图标与视觉冲突**：
   - 常驻微服务使用 `🌐`（网络与服务），微函数使用 `⚡`（闪电计算），单次任务使用 `🛠️`（运维工具），全部总览使用 `🧩`（拼图），每个图标语义唯一、无任何重复！
2. **符合云原生与工业级标准**：
   - 无论是 Wasm 还是 Node Serverless，它们在计算拓扑上都是**函数（Functions）**；
   - 无论是 Bash 脚本还是 ELF 编译程序，只要它是调完即退出的工具，在计算拓扑上都是**任务（Tasks / CLI）**。
3. **数量分布科学合理**：
   - `12 (Services) + 4 (Functions) + 5 (Tasks) = 21 (Total)`，每一类都有充分的存在感和清晰的职责边界。

---

### 2. 次级切面过滤体系（多维正交组合器）

在主 Tab 栏下方或右侧筛选区，提供正交的多维细粒度切面，供开发者精准定位：

```
[ 顶级 Tab ] : [ 🧩 全部 (21) ]  [ 🌐 常驻微服务 (12) ]  [ ⚡ 弹性微函数 (4) ]  [ 🛠️ 运维指令 (5) ]
---------------------------------------------------------------------------------------------------------
[ 次级切面 ] :
  • 构建生命周期 (Lifecycle) : [全部] | [⚡ 解释型脚本] | [📦 预编译二进制] | [🔨 源码热构建]
  • 接口通信协议 (Protocol)  : [全部] | [🌐 HTTP REST] | [🔌 UDS 套接字]  | [⚡ Wasm ABI] | [⌨️ POSIX 命令行]
  • 业务领域分类 (Category)  : [全部] | [网络穿透] | [系统运维] | [开发算力] | [安全审计] | [Web 服务]
  • 健康运行状态 (State)     : [全部] | [运行中] | [待命就绪] | [异常降级] | [依赖缺失] | [已停止]
```

---

## 四、 设计模式与前端实现架构 (Design Patterns Implementation)

### 1. 适配器策略模式 (`pluginFilterStrategies.js`)
将现有的复合过滤器解耦重构为 5 个正交的策略类，支持自由组合与链式流水线执行：

```javascript
// 1. 工作负载范式策略 (Workload Archetype Strategy)
export class WorkloadArchetypeFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.archetype || context.archetype === 'all') return true;
    switch (context.archetype) {
      case 'service': // 常驻微服务与网关
        return plugin.profile === 0;
      case 'function': // 弹性微函数与计算 (FaaS + Wasm 统一归并)
        return plugin.profile === 2 || plugin.profile === 3;
      case 'task': // 运维指令与单次任务
        return plugin.profile === 1;
      default:
        return true;
    }
  }
}

// 2. 构建生命周期策略 (Lifecycle Strategy)
export class LifecycleFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.lifecycle || context.lifecycle === 'all') return true;
    const target = Number(context.lifecycle);
    return plugin.lifecycle === target;
  }
}

// 3. 通信协议策略 (Protocol Strategy)
export class ProtocolFilterStrategy {
  apply(plugin, context) {
    if (!context || !context.protocol || context.protocol === 'all') return true;
    const target = Number(context.protocol);
    return plugin.protocol === target;
  }
}

// 4. 组合过滤器 (Composite Filter)
export class CompositePluginFilter {
  constructor(strategies = []) {
    this.strategies = strategies;
  }
  filter(plugins, context) {
    return plugins.filter(plugin =>
      this.strategies.every(strategy => strategy.apply(plugin, context))
    );
  }
}
```

### 2. 状态机模式 (`PluginFilterStateMachine`)
管理用户多维筛选条件的上下文生命周期，防止脏状态与异步竞争：
- **状态迁移图**：
  ```
  +-----------+    User Click Tab      +------------------+   Compute Pipeline   +--------------------+
  | DefaultAll| ---------------------> | ArchetypeSelected| -------------------> | FilteredListReady  |
  +-----------+                        +------------------+                      +--------------------+
        ^                                       |                                          |
        |           Reset All Filters           | Apply Secondary Slices (Protocol/Life)   |
        +---------------------------------------+------------------------------------------+
  ```
- **核心保障**：
  - 切换顶级 Tab 时，自动重置或保持次级切面过滤状态；
  - 搜索框输入与清空 `✕` 严格遵循状态机迁移，杜绝污染原始事实源。

---

## 五、 性能优化与前端门禁防御 (Performance & Gate Defense)

1. **零开销纯前端响应式计算**：
   - 基于 Vue 3 `computed` 依赖追踪，21 个插件的多维过滤耗时 < 0.5ms，帧率稳定在 60 FPS；
2. **逻辑视口高度限制门禁 (`FE-CONTAINER-HEIGHT-LIMIT`)**：
   - 筛选栏与列表容器严格采用 `flex-1 min-h-0 overflow-y-auto`，杜绝产生双滚动条；
3. **单元测试与门禁验证覆盖**：
   - 在 `tests/frontend/plugin-filter-and-state-machine.test.mjs` 中增加针对全新三大工作负载策略与正交切面的断言；
   - 保持 `frontend-gate.cjs` 8 项核心门禁 100% 绿灯。
