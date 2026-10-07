---
created: 2026-10-07
updated: 2026-10-07
---

# LinuxWebTool 架构演进、门禁治理与基线体系索引

> 本目录对齐外部权威架构规范 `SoftwareArchitecture/backend`，为 **LinuxWebTool** 建立完整的模块化单体架构蓝图、L0~L5 门禁矩阵以及精确基线治理体系。

---

## 1. 架构文档索引树

```text
docs/architecture/
├── README.md                               # 本索引入口导航
├── binding.yaml                            # 项目架构版本与项目绑定声明 (Project Binding)
├── architecture-optimization-plan.md       # 架构现状调研与优化方案 (五大核心架构视图)
├── gates-and-baseline-governance.md        # L0~L5 门禁分级体系与精确基线治理规范
└── feasibility-and-preview-plan.md         # Native AOT 优化可行性论证、重构预览与实施路线
```

---

## 2. 核心成果概览

### 2.1 架构现状与痛点诊断
- **上帝程序集问题**：`LinuxWebTool.Infrastructure` 集中了 115 个源文件（占生产源码 67.6%），不仅包含底层适配，更错误地吞噬了全部 13 个子域的核心业务用例与状态机；
- **肥胖控制器问题**：`LinuxWebTool.WebHost/Routes/` 控制器直接持有底层持久化仓储与后台服务，缺乏统一的业务用例层（Application Layer），导致 MCP 协议等新入口无法直接复用业务逻辑；
- **隐式依赖隐患**：`WebHost` 依赖 `Contracts` 中的几乎所有模型，但未显式声明直接引用；
- **门禁与基线空白**：缺乏系统化的 L0~L5 门禁分层模型，后端 C# 缺乏精确基线（Baseline）管理机制。

### 2.2 目标物理架构：轻量 4 层分层 + 垂直 Feature 切片 (方案 C)
```text
LinuxWebTool.WebHost        (Host 宿主 / Minimal API 路由 / MCP 端点 / 组合根 / SPA 前端)
        ↓                     ↓ (显式直接引用)
LinuxWebTool.Application    LinuxWebTool.Infrastructure
(业务用例 / 状态机 / 调度)   (Dapper.AOT / PInvoke / C 动态库 / YARP)
        ↓                     ↓
        LinuxWebTool.Contracts
        (纯净业务契约 / 技术端口契约 / 纯 BCL)
```

### 2.3 门禁与基线体系升级
- **L0 物理所有权**：目录白名单与文件命名归属校验；
- **L1 项目依赖**：直接引用显式化与拓扑无环检验；
- **L2 源码边界**：Roslyn 命名空间一致性与跨 Feature 私有访问拦截；
- **L3 实现与 AOT**：`AppJsonSerializerContext` 序列化完备性与 SQLite 大小写比对；
- **L4 运行时契约**：Minimal API 参数安全性与 57 项端到端 API 集成测试保护；
- **L5 构建与发布**：Docker Native AOT 单二进制编译与启动 Smoke 冒烟测试；
- **精确基线机制**：引入 `backend-baseline.json`，遵循“匹配放行、新增必死、失效（Stale）必死、只降不增”铁律，杜绝假绿。

### 2.4 可行性与平滑演进保障
- **Native AOT 零破坏**：.NET 10 对纯类库跨程序集 AOT 编译具有天然静态分析支持，单二进制单文件体积（~38MB）、启动时间（~85ms）和常驻内存（~25MB）零性能劣变；
- **测试保护网常绿**：现存 120 项架构测试与 57 项集成测试作为基准保护网，采用四阶段演进策略（Phase 0 治理先行 -> Phase 1 契约重组 -> Phase 2 用例抽取 -> Phase 3 全量迁洁），保证研发日常无感平滑迁移。
