# LinuxWebTool 插件系统架构演化与技术债务清零方案

> **文档版本**：v2.0 (Next-Gen Architecture)  
> **发布日期**：2026-11-08  
> **适用范围**：`LinuxWebTool.Contracts`、`LinuxWebTool.Infrastructure`、`LinuxWebTool.WebHost` 及 `plugins/` 生态

---

## 1. 背景与核心决策

### 1.1 现状与窗口期
LinuxWebTool (LWT) 经过前期重构，已实现 **四维执行规格矩阵**（`Resident` 常驻微服务、`OneShot` 瞬态脚本、`Serverless` 动态按需冷启、`Wasm` 内存微函数沙箱）以及环境变量安全沙箱清洗、Polly v8 弹性熔断与 Linux Cgroups v2 资源隔离。

**核心决策点**：
由于当前产品**尚未正式上线**，完全没有存量生产用户及历史数据迁移包袱。这是**彻底斩断历史兼容代码、统一目录规范、将技术债务彻底清零**的唯一黄金窗口期。

### 1.2 改造三大原则
1. **单一真相源 (Single Source of Truth)**：
   - 彻底废除在 C# 代码中硬编码写出几百行假模板插件的机制；
   - 根目录物理 `plugins/` 目录为平台唯一合法的默认模板与开发真相源。
2. **纯粹强类型契约 (Schema v2 Zero-Legacy)**：
   - 彻底废除 `dependencies` 旧式对象 `{ "runners": [...], "binaries": [...] }` 格式；
   - 统一为一等公民的强类型依赖数组 `[ { "id": "...", "scope": "host"|"container", ... } ]`；
   - 删除所有针对旧版本清单的双向兼容与字段降级代码。
3. **分层清晰的物理目录布局 (Standard Layout v2)**：
   - 隔离源码、编译产物（`bin/`）、动作脚本（`hooks/`）与前端视图（`webview/`），避免 Git 工作区污染与运行覆盖冲突。

---

## 2. 之前方案文档完成度全量审计

对标 [`docs/microservices-faas-platform-research-and-plugin-system-optimization-plan.md`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/docs/microservices-faas-platform-research-and-plugin-system-optimization-plan.md)：

| 模块 | 核心指标 | 完成状态 | 演进与闭环措施 |
| :--- | :--- | :--- | :--- |
| **凭据清洗防火墙** | 清空隐式继承，阻断敏感 Key，保留安全系统变量 | ✅ 100% 闭环 | `PluginEnvironmentSanitizer` 已全面生效 |
| **主动就绪探测器** | HTTP GET `/health` 与 TCP Connect 探测 | ✅ 100% 闭环 | `PluginReadinessProber` 已接入启动状态机 |
| **弹性容错流水线** | Polly v8 超时、重试、断路器 | ✅ 100% 闭环 | `PluginResiliencePipeline` 已注入 |
| **Linux Cgroups v2** | 限制 `cpu.max`, `memory.max`, `pids.max` | ✅ 100% 闭环 | `PluginCgroupManager` 已在启动期注入 |
| **优雅停机排空** | SIGTERM 优雅排空与超时强制杀死 | ✅ 100% 闭环 | `ResidentExecutionStrategy.StopAsync` 已实现 |
| **四维执行规格矩阵** | Resident / OneShot / Serverless / Wasm | ✅ 100% 闭环 | 前后端全部打通，浏览器现场验收通过 |
| **DAG 静态判环** | Tarjan 强连通分量 (SCC) 检测有向环 | ✅ 100% 闭环 | `WorkflowDagValidator` 已接入工作流控制器 |
| **不可变版本快照** | `revisions/rev-xxx/` 目录与 `aliases.json` 软链 | 🟡 70% 闭环 | 需全面接入微服务启动、更新与热回滚主流程 |
| **事务发件/收件总线** | SQLite WAL Outbox/Inbox + 内存 Channels | 🟡 75% 闭环 | 需向插件暴露标准 HTTP `/events/publish` 端点 |
| **逆向 Saga 工作流补偿** | 流程中止时反向执行已成功步骤的补偿操作 | 🟡 50% 闭环 | 需在工作流模型中增加 `compensateStep` 并接入执行器 |

---

## 3. 标准化插件目录结构规范 (Plugin Standard Layout v2)

所有官方与第三方插件必须严格遵守统一结构：

```text
plugins/<plugin-id>/
├── plugin.json                 # [必需] 统一元数据与能力契约清单 (Schema v2)
├── README.md                   # [推荐] 插件说明与使用文档
├── bin/                        # [产物] 跨架构预编译二进制或 Wasm 模块
│   ├── app                     # 对应 Resident/Serverless 微服务独立二进制
│   └── main.wasm               # 对应 Wasm 微函数
├── hooks/                      # [动作] OneShot 动作脚本 (hooks/<action>.(sh|ps1|py|js))
├── webview/                    # [视图] 插件专属前端页面 (原 ui/ 统一更名为 webview/)
│   ├── index.html
│   ├── app.js
│   └── style.css
├── src/                        # [可选] 源码开发工程 (如 Go cmd/internal、.NET 项目)
└── manifests/                  # [可选] 预置配置、数据字典或模版文件
```

---

## 4. 清除历史兼容代码实施清单

1. **`PluginModels.cs` 契约纯粹化**：
   - 将 `PluginManifest.Dependencies` 的类型直接设为 `IReadOnlyList<PluginDependencyConfig>`；
   - 彻底删除旧版 `PluginDependencies` 包装类（`runners`, `binaries`, `items`）；
   - 彻底删除 `OptionalDependencies` 与 `GetUnifiedDependencies()`；
   - 删除 `PluginDependenciesJsonConverter.cs` 中所有多格式降级分支。
2. **`PluginManager.Defaults.cs` & `Samples.cs` 瘦身**：
   - 删除所有写死的字符串 mock 模板（`EnsureHealthcheckCreated()`, `EnsureSshGuardCreated()`, `EnsureChatGptChannelCreated()` 等）；
   - 保留纯粹的 `EnsureTemplatePluginsSeeded()`，直接扫描并同步物理 `plugins/` 目录。
3. **`PluginStandardEnvBuilder.cs` 历史迁移清除**：
   - 删除 `MigrateLegacyPluginData` 探测与迁移代码，数据直接挂载在标准卷中。

---

## 5. 高级功能闭环设计与实现方案

### 5.1 不可变版本快照与软链接原子切换 (Revision Main Pipeline)
- 启动 Resident / Serverless 插件时，通过 `PluginRevisionManager.ResolveEffectiveDirectory(pluginDir, "stable")` 获取有效目录；
- 在更新部署时，生成不可变快照 `revisions/rev-{timestamp}`，调用 `PluginReadinessProber` 验证新端口 `/health`，通过后原子切换 `aliases.json` 的 `stable` 指针，旧版本进入 `Draining` 优雅排空。

### 5.2 统一事件网关端点 (Transactional Outbox HTTP API)
- 控制器提供两个标准端点：
  1. `POST /api/plugins/{id}/events/publish`：将事件写入 `plugin_event_outbox` 表，经信道广播。
  2. `GET /api/plugins/{id}/events/poll`：插件拉取并自动记录 `plugin_event_inbox` 幂等收件。

### 5.3 工作流逆向 Saga 补偿机制 (Workflow Saga Pattern)
- 在 `WorkflowStep` 中新增 `compensateStep` 定义；
- 当流水线策略为 `Abort` 且发生失败时，逆向调度执行已完成步骤的补偿操作。
