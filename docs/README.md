# LinuxWebTool (LWT) 技术知识库与文档中心

> 欢迎查阅 LinuxWebTool (LWT) 官方技术文档中心。本中心按照知识库全生命周期治理规范构建，分为**常青规范库 (Living Standards)** 与 **不可变历史归档库 (Immutable Archives)**。

---

## 1. 核心常青技术规范 (Living Standards)

| 分册 / 目录 | 规范定位 | 目标受众与内容概要 | 快速入口 |
| :--- | :--- | :--- | :--- |
| **`plugin-development-guide/`** | 🧩 **通用微服务与插件生态 Schema v3.1 开发全景指南** | 10 大标准分册、18 项黄金军规、三大正交工作负载（常驻微服务、Serverless 微函数、运维单次任务）开发、调试、热更与发布规范。 | [👉 查看插件开发全景指南](plugin-development-guide/README.md) |
| **`architecture/`** | 🏛️ **主系统架构总纲、底座设计与核心治理方案** | .NET 10 Native AOT 架构设计、PTY 伪终端引擎、Linux 双平面网络隔离模型、[插件 Layout 目录规范方案](architecture/plugin-layout-specification-and-directory-structure-adjustment-plan.md)、[文档收件箱与定期归档规范](architecture/docs-inbox-lifecycle-and-periodic-archival-specification.md)、[知识库分册重构总纲](architecture/docs-archival-and-plugin-guide-upgrade-architecture-plan.md)。 | [👉 查看系统架构总纲](architecture/) |
| **`dev/`** | 🛡️ **工程流水线与门禁质量体系** | 极速双轨制开发流水线、CI/CD 门禁测试、前端代码与样式规范、凭据安全扫描治理。 | [👉 查看开发与质量门禁](dev/) |
| **`articles/`** | 📰 **对外发布与技术深度文章** | 深度架构剖析、行业方案对标与最佳实践分享。 | [👉 查看技术文章库](articles/) |
| **`references/`** | 📚 **标准协议与底层技术参考** | POSIX PTY、RFC 规范、Linux 特权穿透参考。 | [👉 查看协议参考库](references/) |

---

## 2. 历史方案与技术决议归档库 (Historical Archives)

根据 ADR (Architecture Decision Record) 与 RFC 不可变版本演进标准，已实施并结项的技术调研、排障复盘、可行性论证与阶段性架构方案已按**制定日期与年月**归档：

- **归档根目录**：[`docs/archive/`](archive/README.md)
- **归档年月分布**：
  - [📅 2026-11 历史方案库 (137 篇)](archive/README.md#202611)
  - [📅 2026-10 历史方案库 (80 篇)](archive/README.md#202610)
  - [📅 2026-09 历史方案库 (1 篇)](archive/README.md#202609)
- **全景索引检索**：详见 [docs/archive/README.md 全景大纲索引矩阵](archive/README.md)，支持按主题分类（插件与微服务、安全鉴权、备份存储、网络穿透等）快速跳转。

---

## 3. 文档生命周期与维护工具

- **归档自动化脚本**：[`scripts/docs-archiver.cjs`](../scripts/docs-archiver.cjs)
  - 支持多源日期提取策略链（`HeaderMetadata` -> `GitLogInitial` -> `FileSystem`）；
  - 自动创建并维护原路径重定向别名桩 (Stub Proxy)，彻底免疫外部死链；
  - 自动重新汇编生成 `docs/archive/README.md` 全景矩阵。
- **文档维护命令**：
  ```bash
  # 试运行核验当前顶层未归档文档的时间与目标路径
  node scripts/docs-archiver.cjs --dry-run

  # 执行正式物理归档并更新全景索引
  node scripts/docs-archiver.cjs --execute
  ```

---

> *LinuxWebTool 架构治理组 · 持续更新*
