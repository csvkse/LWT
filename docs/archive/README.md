# LinuxWebTool (LWT) 历史架构方案与技术决议全景索引库

> **治理规范**：依据 ADR (Architecture Decision Record) 与 RFC 不可变历史版本治理原则，所有已实施落地的架构提案、排障复盘、可行性调研与设计方案均已按制定时间与业务分类归档于本目录中。  
> **常青指南规范**：如需查阅当前系统的现行业务开发规范与清单，请直接访问顶层常青目录：
> - [docs/plugin-development-guide/](../plugin-development-guide/README.md) (通用微服务与插件生态 Schema v3.1 开发全景指南)
> - [docs/architecture/](../architecture/) (主系统架构总纲、底座设计与 Layout 规范)
> - [docs/dev/](../dev/) (开发流水线与门禁质量体系)

---

## 1. 核心主题分类大纲矩阵 (Topic Categories)

| 核心主题分类 | 文档篇数 | 快速导航 | 核心涉及领域 |
| :--- | :---: | :--- | :--- |
| **📝 架构方案与决议** | **82 篇** | [👉 查看该分类方案](#架构方案与决议) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **🧩 插件与微服务** | **47 篇** | [👉 查看该分类方案](#插件与微服务) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **🌐 组网与网络穿透** | **36 篇** | [👉 查看该分类方案](#组网与网络穿透) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **🛡️ 安全鉴权与凭据** | **15 篇** | [👉 查看该分类方案](#安全鉴权与凭据) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **📦 备份与存储同步** | **14 篇** | [👉 查看该分类方案](#备份与存储同步) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **📊 审计与复盘报告** | **9 篇** | [👉 查看该分类方案](#审计与复盘报告) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **💻 终端与 PTY 引擎** | **9 篇** | [👉 查看该分类方案](#终端与PTY引擎) | 微服务生态、RPC 发现、插件生命周期与热更 |
| **🚀 容器与部署架构** | **8 篇** | [👉 查看该分类方案](#容器与部署架构) | 微服务生态、RPC 发现、插件生命周期与热更 |

---

## 2. 归档年月时间线大纲 (Timeline Archives)

| 归档年月 | 归档文档篇数 | 快速导航 |
| :--- | :---: | :--- |
| **2026-11** | **139 篇** | [👉 跳转至 2026-11 时间线](#202611) |
| **2026-10** | **80 篇** | [👉 跳转至 2026-10 时间线](#202610) |
| **2026-09** | **1 篇** | [👉 跳转至 2026-09 时间线](#202609) |

---

## 3. 视图 A：按主题分类方案矩阵 (Topic Matrix)

<a id="架构方案与决议"></a>
### 📝 架构方案与决议 (82 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-08` | `2026-11` | [AI 辅助系统级复杂工程落地可行性与能力边界深度剖析](2026-11/ai-assisted-engineering-boundary-and-system-feasibility-analysis.md) | `ai-assisted-engineering-boundary-and-system-feasibility-analysis.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [ChatGPT 渠道常驻服务方案复核](2026-11/chatgpt-channel-service-reassessment.md) | `chatgpt-channel-service-reassessment.md` | `Implemented` |
| 3 | `2026-11-08` | `2026-11` | [Linux 极速开发与快速测试工作流架构设计与落地方案](2026-11/fast-linux-dev-pipeline-and-architecture-design.md) | `fast-linux-dev-pipeline-and-architecture-design.md` | `Implemented` |
| 4 | `2026-11-08` | `2026-11` | [LinuxWebTool 文件管理器目标源切换（容器 vs 宿主穿透）技术可行性调研与架构设计方案](2026-11/file-manager-host-target-switching-feasibility-and-architecture-design.md) | `file-manager-host-target-switching-feasibility-and-architecture-design.md` | `Implemented` |
| 5 | `2026-11-08` | `2026-11` | [前端列表相关容器显示门禁与逻辑高度限制调研及架构设计方案](2026-11/frontend-list-container-height-guard-and-patterns-plan.md) | `frontend-list-container-height-guard-and-patterns-plan.md` | `Implemented` |
| 6 | `2026-11-08` | `2026-11` | [SOCKS5 边缘中继网关：三阶分层解耦架构与交互重塑方案 (Tripartite Architecture)](2026-11/inbound-outbound-routing-tripartite-architecture-and-ux-refactor.md) | `inbound-outbound-routing-tripartite-architecture-and-ux-refactor.md` | `Implemented` |
| 7 | `2026-11-08` | `2026-11` | [Incus 宿主安装影响评估](2026-11/incus-host-install-impact-assessment.md) | `incus-host-install-impact-assessment.md` | `Implemented` |
| 8 | `2026-11-08` | `2026-11` | [平台级 Ingress 动态路径别名、WebSocket 主动探测深度分析与多层防特征伪装流架构调研](2026-11/ingress-path-aliases-and-anti-probing-camouflaged-duplex-stream-research.md) | `ingress-path-aliases-and-anti-probing-camouflaged-duplex-stream-research.md` | `Implemented` |
| 9 | `2026-11-08` | `2026-11` | [宿主中转路径范围治理、路由冲突仲裁与双轨制命名空间（Dual-Track Namespacing）架构调研与优化方案](2026-11/ingress-path-scope-conflict-arbitration-and-dual-track-namespacing-plan.md) | `ingress-path-scope-conflict-arbitration-and-dual-track-namespacing-plan.md` | `Implemented` |
| 10 | `2026-11-08` | `2026-11` | [LXC 系统容器管理架构调研与演进方案](2026-11/lxc-container-management-architecture-and-evolution-plan.md) | `lxc-container-management-architecture-and-evolution-plan.md` | `Implemented` |
| 11 | `2026-11-08` | `2026-11` | [LXC/Incus 容器细粒度硬件配额治理（CPU占用比、磁盘IO速率/IOPS、防Fork炸弹与Swap策略）深度方案](2026-11/lxc-fine-grained-hardware-limits-and-cgroupv2-tuning-plan.md) | `lxc-fine-grained-hardware-limits-and-cgroupv2-tuning-plan.md` | `Implemented` |
| 12 | `2026-11-08` | `2026-11` | [LXC/Incus 容器实例导入导出与冷热迁移架构设计方案](2026-11/lxc-instance-import-export-and-migration-plan.md) | `lxc-instance-import-export-and-migration-plan.md` | `Implemented` |
| 13 | `2026-11-08` | `2026-11` | [MCP Bridge 自动连接与看门狗自动守护架构方案设计](2026-11/mcp-bridge-auto-connect-and-watchdog-supervisor-design.md) | `mcp-bridge-auto-connect-and-watchdog-supervisor-design.md` | `Implemented` |
| 14 | `2026-11-08` | `2026-11` | [MCP Bridge 原生二进制跨平台启动异常 (Exec format error) 调查分析与全链路二进制治理方案](2026-11/mcp-bridge-exec-format-error-investigation-and-binary-governance-plan.md) | `mcp-bridge-exec-format-error-investigation-and-binary-governance-plan.md` | `Implemented` |
| 15 | `2026-11-08` | `2026-11` | [MCP Bridge 修改文件范围与验收标准](2026-11/mcp-bridge-file-scope-and-acceptance.md) | `mcp-bridge-file-scope-and-acceptance.md` | `Implemented` |
| 16 | `2026-11-08` | `2026-11` | [Go 单进程 MCP Bridge 实施计划](2026-11/mcp-bridge-implementation-plan.md) | `mcp-bridge-implementation-plan.md` | `Implemented` |
| 17 | `2026-11-08` | `2026-11` | [OpenAI MCP 隧道长轮询超时、空闲断开与弹性自愈治理方案](2026-11/mcp-bridge-long-polling-timeout-idle-disconnect-and-resilience-governance.md) | `mcp-bridge-long-polling-timeout-idle-disconnect-and-resilience-governance.md` | `Implemented` |
| 18 | `2026-11-08` | `2026-11` | [MCP Bridge 控制台持久化生效机制、渠道自启与日志管理深度治理方案](2026-11/mcp-bridge-persistence-and-wire-logs-governance-plan.md) | `mcp-bridge-persistence-and-wire-logs-governance-plan.md` | `Implemented` |
| 19 | `2026-11-08` | `2026-11` | [MCP 智能安全网关桥接服务配置持久化丢失根本原因剖析、架构缺欠头脑风暴与设计模式重构方案](2026-11/mcp-bridge-persistence-loss-root-cause-and-storage-state-machine-architecture-plan.md) | `mcp-bridge-persistence-loss-root-cause-and-storage-state-machine-architecture-plan.md` | `Implemented` |
| 20 | `2026-11-08` | `2026-11` | [MCP Bridge 敏感字段变活动作与前后端契约容错方案](2026-11/mcp-bridge-secret-mutation-action-compatibility-plan.md) | `mcp-bridge-secret-mutation-action-compatibility-plan.md` | `Implemented` |
| 21 | `2026-11-08` | `2026-11` | [MCP 智能安全网关桥接服务：支持标准 MCP JSON 格式直接导入调研与落地实施方案](2026-11/mcp-bridge-standard-json-import-and-adapter-plan.md) | `mcp-bridge-standard-json-import-and-adapter-plan.md` | `Implemented` |
| 22 | `2026-11-08` | `2026-11` | [MCP Bridge 状态投影、服务降级治理与工具激活生命周期研发方案](2026-11/mcp-bridge-status-projection-degradation-governance-and-tool-activation-plan.md) | `mcp-bridge-status-projection-degradation-governance-and-tool-activation-plan.md` | `Implemented` |
| 23 | `2026-11-08` | `2026-11` | [MCP Bridge 工具自动激活与轻量脚本自适应执行深度治理方案](2026-11/mcp-bridge-tool-activation-and-oneshot-execution-governance-plan.md) | `mcp-bridge-tool-activation-and-oneshot-execution-governance-plan.md` | `Implemented` |
| 24 | `2026-11-08` | `2026-11` | [SOCKS5 中继多端口多线路入站架构设计与远端节点离线深度排障](2026-11/multi-port-inbound-routing-and-upstream-offline-diagnosis.md) | `multi-port-inbound-routing-and-upstream-offline-diagnosis.md` | `Implemented` |
| 25 | `2026-11-08` | `2026-11` | [原生工具服务化实施计划](2026-11/native-tool-service-implementation-plan.md) | `native-tool-service-implementation-plan.md` | `Implemented` |
| 26 | `2026-11-08` | `2026-11` | [原生工具服务化：修改范围、示例与验收标准](2026-11/native-tool-service-scope-and-acceptance.md) | `native-tool-service-scope-and-acceptance.md` | `Implemented` |
| 27 | `2026-11-08` | `2026-11` | [NativeAOT-LLVM Wasm 架构剖析、在线源码编辑编译可行性调研与系统方案](2026-11/nativeaot-llvm-wasm-architecture-and-online-compilation-plan.md) | `nativeaot-llvm-wasm-architecture-and-online-compilation-plan.md` | `Implemented` |
| 28 | `2026-11-08` | `2026-11` | [次世代全协议多跳路由边缘网关（NextGen Poly-Protocol Edge Gateway）架构演进调研与深度设计](2026-11/nextgen-forwarder-chained-multi-protocol-routing-and-tun-architecture.md) | `nextgen-forwarder-chained-multi-protocol-routing-and-tun-architecture.md` | `Implemented` |
| 29 | `2026-11-08` | `2026-11` | [非 HTTP 扩展的前端注入与界面呈现架构调研与设计](2026-11/non-http-extension-frontend-injection-research-and-design.md) | `non-http-extension-frontend-injection-research-and-design.md` | `Implemented` |
| 30 | `2026-11-08` | `2026-11` | [页面加载抖动与滚动条闪现根因深度调研、业界方案对比与抗抖动治理方案](2026-11/page-jitter-scrollbar-flicker-root-cause-and-solution.md) | `page-jitter-scrollbar-flicker-root-cause-and-solution.md` | `Implemented` |
| 31 | `2026-11-08` | `2026-11` | [多协议智能分流中继网关全生命周期开发流程与实施路线图 (Development & Implementation Workflow)](2026-11/poly-protocol-forwarder-development-and-implementation-workflow.md) | `poly-protocol-forwarder-development-and-implementation-workflow.md` | `Implemented` |
| 32 | `2026-11-08` | `2026-11` | [脚本扩展、ChatGPT 渠道 Worker、无 HTTP 前端扩展与指令 API Key 细粒度授权调研与设计方案](2026-11/script-extension-and-command-scoping-feasibility-and-design.md) | `script-extension-and-command-scoping-feasibility-and-design.md` | `Implemented` |
| 33 | `2026-11-08` | `2026-11` | [SOCKS5 基准现状、可行性与设计审查](2026-11/socks5-benchmark-feasibility-and-design-review.md) | `socks5-benchmark-feasibility-and-design-review.md` | `Implemented` |
| 34 | `2026-11-08` | `2026-11` | [SOCKS5 客户端中继转发器（SOCKS5 Forwarder）与多入站多节点边缘网关架构调研与深度设计](2026-11/socks5-client-forwarder-and-multi-inbound-edge-relay-architecture.md) | `socks5-client-forwarder-and-multi-inbound-edge-relay-architecture.md` | `Implemented` |
| 35 | `2026-11-08` | `2026-11` | [SOCKS5 伪装封装客户端生态权威盘点与多节点边缘中继网关（Edge Relay Gateway）架构深度调研](2026-11/socks5-encapsulation-clients-and-multi-node-edge-relay-research.md) | `socks5-encapsulation-clients-and-multi-node-edge-relay-research.md` | `Implemented` |
| 36 | `2026-11-08` | `2026-11` | [SOCKS5 客户端边缘中继网关：CRUD 闭环审计、拓扑端口解绑与全生命周期治理方案](2026-11/socks5-forwarder-crud-integrity-and-topology-unbind-governance.md) | `socks5-forwarder-crud-integrity-and-topology-unbind-governance.md` | `Implemented` |
| 37 | `2026-11-08` | `2026-11` | [SOCKS5 客户端边缘中继网关：下一代现代化 Webview 拓扑控制台架构与无模态交互设计方案](2026-11/socks5-forwarder-nextgen-webview-topology-and-modeless-ux-design.md) | `socks5-forwarder-nextgen-webview-topology-and-modeless-ux-design.md` | `Implemented` |
| 38 | `2026-11-08` | `2026-11` | [SOCKS5 边缘中继网关节点“离线”根因排查与两级分层健康探针架构方案](2026-11/socks5-forwarder-node-offline-root-cause-and-two-tier-health-probe-architecture.md) | `socks5-forwarder-node-offline-root-cause-and-two-tier-health-probe-architecture.md` | `Implemented` |
| 39 | `2026-11-08` | `2026-11` | [SSH 访问安全守卫宿主穿透管理、管理对象动态切换与架构重构调研方案](2026-11/ssh-guard-host-piercing-and-target-switching-plan.md) | `ssh-guard-host-piercing-and-target-switching-plan.md` | `Implemented` |
| 40 | `2026-11-08` | `2026-11` | [无兼容包袱的终极纯粹网关架构：控制面/数据面解耦、多维路由矩阵与 UDS 零端口流式内核调研](2026-11/ultimate-pure-gateway-architecture-and-multi-dimensional-routing-research.md) | `ultimate-pure-gateway-architecture-and-multi-dimensional-routing-research.md` | `Implemented` |
| 41 | `2026-11-08` | `2026-11` | [上游节点与入站隧道延迟分析与高并发零开销路由架构设计](2026-11/upstream-ingress-latency-diagnosis-and-optimization-architecture.md) | `upstream-ingress-latency-diagnosis-and-optimization-architecture.md` | `Implemented` |
| 42 | `2026-11-08` | `2026-11` | [VLESS 与 XTLS-Vision 协议兼容排障与深度适配治理方案](2026-11/vless-xtls-vision-compatibility-and-protocol-governance.md) | `vless-xtls-vision-compatibility-and-protocol-governance.md` | `Implemented` |
| 43 | `2026-11-08` | `2026-11` | [Wasm 与多语言脚本微函数架构对比、可行性调研与系统演进方案](2026-11/wasm-vs-script-microfunctions-architecture-and-feasibility-study.md) | `wasm-vs-script-microfunctions-architecture-and-feasibility-study.md` | `Implemented` |
| 44 | `2026-11-08` | `2026-11` | [纯粹 Worker 扩展服务架构调研与总体设计方案](2026-11/worker-pure-architecture-research-and-design.md) | `worker-pure-architecture-research-and-design.md` | `Implemented` |
| 45 | `2026-11-02` | `2026-11` | [MCP Bridge 控制台刷新异常与添加服务点击无效根因分析、业界方案调研与加固落地方案](2026-11/mcp-bridge-web-console-path-resolution-and-modal-dialog-plan.md) | `mcp-bridge-web-console-path-resolution-and-modal-dialog-plan.md` | `Implemented` |
| 46 | `2026-10-11` | `2026-10` | [磁盘挂载管理：新增与完善 SSH (SFTP) 后端挂载功能方案](2026-10/ssh-backend-mount-plan.md) | `ssh-backend-mount-plan.md` | `Implemented` |
| 47 | `2026-10-08` | `2026-10` | [`artifacts` 目录含义、作用与命名规范评估](2026-10/artifacts-directory-guide-and-naming-decision.md) | `artifacts-directory-guide-and-naming-decision.md` | `Implemented` |
| 48 | `2026-10-08` | `2026-10` | [扩展服务 (Extensions) 与 SaaS 函数微服务 (Workers & Daemons) 全功能真实演练、架构头脑风暴与深度自愈方案](2026-10/extensions-and-workers-comprehensive-verification-and-enhancement-plan.md) | `extensions-and-workers-comprehensive-verification-and-enhancement-plan.md` | `Implemented` |
| 49 | `2026-10-08` | `2026-10` | [动态脚本扩展与函数微服务：ZIP 导入、在线代码编辑与 MCP 运维开发工具集方案调研与设计](2026-10/extensions-and-workers-zip-online-edit-and-mcp-tools-plan.md) | `extensions-and-workers-zip-online-edit-and-mcp-tools-plan.md` | `Implemented` |
| 50 | `2026-10-08` | `2026-10` | [文件管理页面下载功能与文件夹打包下载调研及实施方案](2026-10/file-manager-download-and-folder-zip-plan.md) | `file-manager-download-and-folder-zip-plan.md` | `Implemented` |
| 51 | `2026-10-08` | `2026-10` | [前端空白页与主题样式问题调研、解决方案与端到端测试方案](2026-10/frontend-blank-page-and-cyber-theme-fix-plan.md) | `frontend-blank-page-and-cyber-theme-fix-plan.md` | `Implemented` |
| 52 | `2026-10-08` | `2026-10` | [前端原生 ESM 导入导出完整性故障分析与同类门禁防御方案](2026-10/frontend-esm-import-integrity-and-gate-plan.md) | `frontend-esm-import-integrity-and-gate-plan.md` | `Implemented` |
| 53 | `2026-10-08` | `2026-10` | [家庭网关 L4 端口转发失效根本原因深度调研与系统级重构方案](2026-10/gateway-l4-forward-failure-root-cause-and-remediation-plan.md) | `gateway-l4-forward-failure-root-cause-and-remediation-plan.md` | `Implemented` |
| 54 | `2026-10-08` | `2026-10` | [GitHub 冒烟性能指标实施方案](2026-10/github-smoke-performance-plan.md) | `github-smoke-performance-plan.md` | `Implemented` |
| 55 | `2026-10-08` | `2026-10` | [GitHub 白盒、黑盒与发布冒烟实施方案](2026-10/github-whitebox-blackbox-smoke-plan.md) | `github-whitebox-blackbox-smoke-plan.md` | `Implemented` |
| 56 | `2026-10-08` | `2026-10` | [JWT 有效期延长与滑动窗口自动续期方案](2026-10/jwt-expiration-and-auto-renewal-plan.md) | `jwt-expiration-and-auto-renewal-plan.md` | `Implemented` |
| 57 | `2026-10-08` | `2026-10` | [MCP 服务与核心功能工具封装技术调研与实施方案](2026-10/mcp-server-and-tool-encapsulation-plan.md) | `mcp-server-and-tool-encapsulation-plan.md` | `Implemented` |
| 58 | `2026-10-08` | `2026-10` | [MCP 服务引擎架构重构与模块化优化方案](2026-10/mcp-server-engine-architecture-refactoring-and-optimization-plan.md) | `mcp-server-engine-architecture-refactoring-and-optimization-plan.md` | `Implemented` |
| 59 | `2026-10-08` | `2026-10` | [MCP 工作流/指令/定时任务整合与二级树状权限架构调研与落地方案](2026-10/mcp-two-level-tree-and-workflow-command-dispatch-plan.md) | `mcp-two-level-tree-and-workflow-command-dispatch-plan.md` | `Implemented` |
| 60 | `2026-10-08` | `2026-10` | [挂载恢复失败提示“上次文件系统探针尚未退出”根因剖析与纵深防御方案](2026-10/mount-probe-guard-and-d-state-recovery-plan.md) | `mount-probe-guard-and-d-state-recovery-plan.md` | `Implemented` |
| 61 | `2026-10-08` | `2026-10` | [挂载状态机实施方案](2026-10/mount-state-machine-plan.md) | `mount-state-machine-plan.md` | `Implemented` |
| 62 | `2026-10-08` | `2026-10` | [导航栏下拉子菜单移动至主菜单项异常消失问题调研与修复方案](2026-10/nav-dropdown-submenu-hover-disappear-analysis-and-fix.md) | `nav-dropdown-submenu-hover-disappear-analysis-and-fix.md` | `Implemented` |
| 63 | `2026-10-08` | `2026-10` | [特权容器宿主穿透对象模型与服务平台单文件快速模式深度调研与架构方案](2026-10/privileged-container-host-target-and-quick-workers-plan.md) | `privileged-container-host-target-and-quick-workers-plan.md` | `Implemented` |
| 64 | `2026-10-08` | `2026-10` | [Python 驱动的 Linux Top 资源监控与进程治理扩展方案 (Process Top Monitor)](2026-10/python-top-process-management-extension-feasibility-plan.md) | `python-top-process-management-extension-feasibility-plan.md` | `Implemented` |
| 65 | `2026-10-08` | `2026-10` | [SFTP / S3 挂载配置](2026-10/rclone-sftp-s3.md) | `rclone-sftp-s3.md` | `Implemented` |
| 66 | `2026-10-08` | `2026-10` | [指令管理与定时任务跨主机调度（本地与远程主机）架构调研与落地方案](2026-10/remote-host-command-and-schedule-dispatch-plan.md) | `remote-host-command-and-schedule-dispatch-plan.md` | `Implemented` |
| 67 | `2026-10-08` | `2026-10` | [远程主机文件管理性能极致优化与全功能（上传/下载/复制/移动/编辑）架构落地方案](2026-10/remote-host-file-management-speedup-and-crud-plan.md) | `remote-host-file-management-speedup-and-crud-plan.md` | `Implemented` |
| 68 | `2026-10-08` | `2026-10` | [远程主机 SFTP 目录无限转圈挂起与密钥测试失败根因剖析及系统化治理方案](2026-10/remote-host-sftp-hang-and-key-test-failure-analysis-and-fix.md) | `remote-host-sftp-hang-and-key-test-failure-analysis-and-fix.md` | `Implemented` |
| 69 | `2026-10-08` | `2026-10` | [远程主机单指令执行异常与 SFTP 超时挂死深度调研与双轨文件系统落地方案](2026-10/remote-host-sftp-stream-poisoning-and-exec-fallback-plan.md) | `remote-host-sftp-stream-poisoning-and-exec-fallback-plan.md` | `Implemented` |
| 70 | `2026-10-08` | `2026-10` | [SaaS 函数式微服务与长时守护平台 (LWT Workers & Daemons) 独立架构与落地设计方案](2026-10/saas-functions-workers-and-daemons-platform-plan.md) | `saas-functions-workers-and-daemons-platform-plan.md` | `Implemented` |
| 71 | `2026-10-08` | `2026-10` | [系统服务 API 签名契约门禁与 .NET Worker 启动自愈方案](2026-10/services-api-signature-and-dotnet-worker-startup-fix-plan.md) | `services-api-signature-and-dotnet-worker-startup-fix-plan.md` | `Implemented` |
| 72 | `2026-10-08` | `2026-10` | [动态服务管理扩展进阶方案：SSH 访问卫士、Caddy 管理、环境自检引导与 GitHub 多模块导入](2026-10/services-dynamic-extensions-ssh-caddy-and-github-import-plan.md) | `services-dynamic-extensions-ssh-caddy-and-github-import-plan.md` | `Implemented` |
| 73 | `2026-10-08` | `2026-10` | [SMB 卸载阻塞与登录请求长期等待：调查和修复](2026-10/smb-d-state-login-hang-analysis-and-fix.md) | `smb-d-state-login-hang-analysis-and-fix.md` | `Implemented` |
| 74 | `2026-10-08` | `2026-10` | [SMB 生命周期与故障诊断方案](2026-10/smb-diagnostics-plan.md) | `smb-diagnostics-plan.md` | `Implemented` |
| 75 | `2026-10-08` | `2026-10` | [SMB 子目录读取阻塞隔离](2026-10/smb-directory-blocking.md) | `smb-directory-blocking.md` | `Implemented` |
| 76 | `2026-10-08` | `2026-10` | [SMB 实际状态复核与访问保护实施方案](2026-10/smb-recovery-reconciliation-plan.md) | `smb-recovery-reconciliation-plan.md` | `Implemented` |
| 77 | `2026-10-08` | `2026-10` | [Systemd 系统服务等长列表自动分页调研与架构方案](2026-10/systemd-and-long-lists-auto-pagination-plan.md) | `systemd-and-long-lists-auto-pagination-plan.md` | `Implemented` |
| 78 | `2026-10-08` | `2026-10` | [Systemd 服务智能分类（系统服务与用户服务区分）调研与架构方案](2026-10/systemd-services-classification-and-categorization-plan.md) | `systemd-services-classification-and-categorization-plan.md` | `Implemented` |
| 79 | `2026-10-08` | `2026-10` | [LinuxWebTool 软件测试框架与性能追踪指标建设方案](2026-10/testing-and-observability-governance-plan.md) | `testing-and-observability-governance-plan.md` | `Implemented` |
| 80 | `2026-10-08` | `2026-10` | [WebDAV 挂载支持方案](2026-10/webdav-mount-plan.md) | `webdav-mount-plan.md` | `Implemented` |
| 81 | `2026-10-08` | `2026-10` | [WebLinuxTool 全功能演进综合架构预览方案](2026-10/weblinuxtool-v2-comprehensive-architecture-preview.md) | `weblinuxtool-v2-comprehensive-architecture-preview.md` | `Implemented` |
| 82 | `2026-09-08` | `2026-09` | [挂载健康检测与自动恢复原则](2026-09/mount-health-principles.md) | `mount-health-principles.md` | `Implemented` |

<a id="插件与微服务"></a>
### 🧩 插件与微服务 (47 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-08` | `2026-11` | [LinuxWebTool 自动化部署、环境引导与插件系统架构优化方案](2026-11/deployment-and-plugin-architecture-optimization-plan.md) | `deployment-and-plugin-architecture-optimization-plan.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [LinuxWebTool 系统文件夹占用查询插件技术可行性调研与架构设计方案](2026-11/disk-usage-analyzer-plugin-feasibility-and-architecture-design.md) | `disk-usage-analyzer-plugin-feasibility-and-architecture-design.md` | `Implemented` |
| 3 | `2026-11-08` | `2026-11` | [LinuxWebTool 插件动态目录扫描、热注册引擎与导入优先开发流架构设计](2026-11/dynamic-folder-scan-and-import-first-plugin-architecture.md) | `dynamic-folder-scan-and-import-first-plugin-architecture.md` | `Implemented` |
| 4 | `2026-11-08` | `2026-11` | [现有 21+ 扩展插件全量升级至 Schema v3.1 规范与目录标准化方案](2026-11/existing-plugins-schema-v3-migration-and-directory-standardization-plan.md) | `existing-plugins-schema-v3-migration-and-directory-standardization-plan.md` | `Implemented` |
| 5 | `2026-11-08` | `2026-11` | [Go 运行引擎与 SOCKS5 代理微服务调研与架构设计方案](2026-11/go-runtime-engine-and-socks5-proxy-plugin-design.md) | `go-runtime-engine-and-socks5-proxy-plugin-design.md` | `Implemented` |
| 6 | `2026-11-08` | `2026-11` | [宿主侵入代码退役与插件可选依赖环境（Optional Dependencies）架构调研与设计方案](2026-11/host-decoupling-retirement-and-plugin-optional-dependencies-design.md) | `host-decoupling-retirement-and-plugin-optional-dependencies-design.md` | `Implemented` |
| 7 | `2026-11-08` | `2026-11` | [宿主元数据向下投影与插件上下文感知架构设计](2026-11/host-metadata-projection-and-plugin-context-architecture.md) | `host-metadata-projection-and-plugin-context-architecture.md` | `Implemented` |
| 8 | `2026-11-08` | `2026-11` | [LXC 插件宿主依赖安装与环境选择方案](2026-11/lxc-host-dependency-installation-feasibility.md) | `lxc-host-dependency-installation-feasibility.md` | `Implemented` |
| 9 | `2026-11-08` | `2026-11` | [LXC 管理插件可行性与设计](2026-11/lxc-management-plugin-feasibility-and-design.md) | `lxc-management-plugin-feasibility-and-design.md` | `Implemented` |
| 10 | `2026-11-08` | `2026-11` | [LXC 管理插件启用与创建预检闭环方案](2026-11/lxc-manager-activation-and-preflight-solution.md) | `lxc-manager-activation-and-preflight-solution.md` | `Implemented` |
| 11 | `2026-11-08` | `2026-11` | [LXC 管理插件实机验证与优化方案](2026-11/lxc-real-environment-validation-plan.md) | `lxc-real-environment-validation-plan.md` | `Implemented` |
| 12 | `2026-11-08` | `2026-11` | [原生插件二进制缺失根因剖析、生命周期自愈与端口防泄漏架构治理方案](2026-11/mcp-bridge-native-binary-resolution-and-lifecycle-safety-plan.md) | `mcp-bridge-native-binary-resolution-and-lifecycle-safety-plan.md` | `Implemented` |
| 13 | `2026-11-08` | `2026-11` | [MCP Bridge 配置持久化保证与脚本轻量扩展状态机生命周期治理调研与方案](2026-11/mcp-bridge-persistence-and-script-plugin-state-lifecycle-research.md) | `mcp-bridge-persistence-and-script-plugin-state-lifecycle-research.md` | `Implemented` |
| 14 | `2026-11-08` | `2026-11` | [函数服务托管的 ChatGPT Tunnel / MCP 聚合插件：可行性与设计](2026-11/mcp-tunnel-plugin-feasibility.md) | `mcp-tunnel-plugin-feasibility.md` | `Implemented` |
| 15 | `2026-11-08` | `2026-11` | [MCP Tunnel 插件：当前系统可复用设施与缺口核验](2026-11/mcp-tunnel-plugin-infrastructure.md) | `mcp-tunnel-plugin-infrastructure.md` | `Implemented` |
| 16 | `2026-11-08` | `2026-11` | [MCP Tunnel 插件：语言、现有库与行业方案比较](2026-11/mcp-tunnel-plugin-options.md) | `mcp-tunnel-plugin-options.md` | `Implemented` |
| 17 | `2026-11-08` | `2026-11` | [借鉴微服务与函数平台架构优化 LinuxWebTool 插件系统调研与落地方案](2026-11/microservices-faas-platform-research-and-plugin-system-optimization-plan.md) | `microservices-faas-platform-research-and-plugin-system-optimization-plan.md` | `Implemented` |
| 18 | `2026-11-08` | `2026-11` | [插件平台级基础设施通用化抽象：声明式 Ingress、双工流隧道、统一守卫与平滑排空深度调研与架构方案](2026-11/platform-level-generic-ingress-and-fallback-architecture.md) | `platform-level-generic-ingress-and-fallback-architecture.md` | `Implemented` |
| 19 | `2026-11-08` | `2026-11` | [宿主转发能力向插件平台级下沉与全生命周期扩展性深度调研与架构方案](2026-11/platform-level-plugin-ingress-and-tunnel-extensibility-research.md) | `platform-level-plugin-ingress-and-tunnel-extensibility-research.md` | `Implemented` |
| 20 | `2026-11-08` | `2026-11` | [统一插件平台自启动机制、生命周期记忆与依赖健康门禁架构方案](2026-11/plugin-auto-start-and-dependency-governance-plan.md) | `plugin-auto-start-and-dependency-governance-plan.md` | `Implemented` |
| 21 | `2026-11-08` | `2026-11` | [插件卡片“访问微服务网页/接口”无效根因剖析、统一 URL 解析器与 UI 模式契约治理方案](2026-11/plugin-card-gateway-url-resolution-and-ui-mode-audit-plan.md) | `plugin-card-gateway-url-resolution-and-ui-mode-audit-plan.md` | `Implemented` |
| 22 | `2026-11-08` | `2026-11` | [LinuxWebTool (LWT) 插件中心架构深度剖析、业界对标与商业价值白皮书](2026-11/plugin-center-architecture-industry-comparison-and-commercial-value.md) | `plugin-center-architecture-industry-comparison-and-commercial-value.md` | `Implemented` |
| 23 | `2026-11-08` | `2026-11` | [插件中心搜索输入框体验与架构治理调研与优化方案](2026-11/plugin-center-search-optimization-and-architecture-design.md) | `plugin-center-search-optimization-and-architecture-design.md` | `Implemented` |
| 24 | `2026-11-08` | `2026-11` | [插件中心标签过滤栏内容溢出问题调研与架构优化方案](2026-11/plugin-center-toolbar-overflow-research-and-optimization-plan.md) | `plugin-center-toolbar-overflow-research-and-optimization-plan.md` | `Implemented` |
| 25 | `2026-11-08` | `2026-11` | [统一插件分类体系重构与多维正交分类学调研与设计方案](2026-11/plugin-classification-and-archetype-orthogonal-taxonomy-plan.md) | `plugin-classification-and-archetype-orthogonal-taxonomy-plan.md` | `Implemented` |
| 26 | `2026-11-08` | `2026-11` | [插件中心代码编辑器文件树状显示与智能折叠架构设计方案](2026-11/plugin-file-tree-and-smart-folding-plan.md) | `plugin-file-tree-and-smart-folding-plan.md` | `Implemented` |
| 27 | `2026-11-08` | `2026-11` | [插件元数据与物理目录全量升级至最新 Schema v3.1 架构方案与深度调研报告](2026-11/plugin-full-metadata-and-directory-structure-upgrade-plan.md) | `plugin-full-metadata-and-directory-structure-upgrade-plan.md` | `Implemented` |
| 28 | `2026-11-08` | `2026-11` | [插件手动生命周期持久化、动态文件管理与 UI 紧凑响应式排版治理方案](2026-11/plugin-manual-lifecycle-persistence-and-dynamic-file-management-governance-plan.md) | `plugin-manual-lifecycle-persistence-and-dynamic-file-management-governance-plan.md` | `Implemented` |
| 29 | `2026-11-08` | `2026-11` | [插件基础设施：当前问题、可复用能力与验证清单](2026-11/plugin-platform-current-state-audit.md) | `plugin-platform-current-state-audit.md` | `Implemented` |
| 30 | `2026-11-08` | `2026-11` | [函数服务与脚本扩展：插件能力完善的可行性与设计](2026-11/plugin-platform-feasibility-and-design.md) | `plugin-platform-feasibility-and-design.md` | `Implemented` |
| 31 | `2026-11-08` | `2026-11` | [插件扩展能力：现有库、通信方式与业界方案比较](2026-11/plugin-platform-libraries-and-industry.md) | `plugin-platform-libraries-and-industry.md` | `Implemented` |
| 32 | `2026-11-08` | `2026-11` | [插件长时停留“探测中”根因深度剖析、自适应就绪探针方案与回归门禁设计](2026-11/plugin-probing-stuck-root-cause-and-adaptive-prober-plan.md) | `plugin-probing-stuck-root-cause-and-adaptive-prober-plan.md` | `Implemented` |
| 33 | `2026-11-08` | `2026-11` | [扩展中心长时 HTTP 微服务鉴权、网关安全防护与设计模式重构方案](2026-11/plugin-resident-microservice-auth-and-gateway-security-plan.md) | `plugin-resident-microservice-auth-and-gateway-security-plan.md` | `Implemented` |
| 34 | `2026-11-08` | `2026-11` | [LinuxWebTool 插件纯净排查模式 (Safe Mode) 与环境变量治理方案](2026-11/plugin-safe-mode-and-environment-variable-governance-plan.md) | `plugin-safe-mode-and-environment-variable-governance-plan.md` | `Implemented` |
| 35 | `2026-11-08` | `2026-11` | [插件源码类型识别、在线热编译命令规范与调试架构演进方案](2026-11/plugin-source-type-and-hot-compile-specification.md) | `plugin-source-type-and-hot-compile-specification.md` | `Implemented` |
| 36 | `2026-11-08` | `2026-11` | [LinuxWebTool 插件系统架构演化与技术债务清零方案](2026-11/plugin-system-architecture-evolution-and-legacy-cleanup-plan.md) | `plugin-system-architecture-evolution-and-legacy-cleanup-plan.md` | `Implemented` |
| 37 | `2026-11-08` | `2026-11` | [插件中心网关鉴权穿透、状态过滤置顶、脚本执行容错与降级治理研发方案](2026-11/plugin-system-auth-state-filter-and-degradation-governance-plan.md) | `plugin-system-auth-state-filter-and-degradation-governance-plan.md` | `Implemented` |
| 38 | `2026-11-08` | `2026-11` | [根目录 plugins 改造、示例扁平收敛与 MCP-Bridge 扩展中心集成调研与架构方案](2026-11/plugins-directory-refactoring-and-mcp-bridge-integration-research.md) | `plugins-directory-refactoring-and-mcp-bridge-integration-research.md` | `Implemented` |
| 39 | `2026-11-08` | `2026-11` | [插件中心长时 HTTP/TCP 服务快速换端与端口冲突检测避让方案调研](2026-11/plugins-http-service-port-switching-and-conflict-avoidance-plan.md) | `plugins-http-service-port-switching-and-conflict-avoidance-plan.md` | `Implemented` |
| 40 | `2026-11-08` | `2026-11` | [统一插件平台彻底重构与纯粹服务运维架构设计方案](2026-11/unification-of-plugins-and-workers-and-pure-services-architecture-design.md) | `unification-of-plugins-and-workers-and-pure-services-architecture-design.md` | `Implemented` |
| 41 | `2026-11-08` | `2026-11` | [统一插件运行时体系与三套 UI 注入模式演进调研与方案设计](2026-11/unified-plugin-runtime-and-three-ui-modes-research-and-design.md) | `unified-plugin-runtime-and-three-ui-modes-research-and-design.md` | `Implemented` |
| 42 | `2026-11-08` | `2026-11` | [通用插件多维本质抽象、动态服务发现与完全语言解耦架构规划](2026-11/universal-plugin-architecture-matrix-and-dynamic-discovery-plan.md) | `universal-plugin-architecture-matrix-and-dynamic-discovery-plan.md` | `Implemented` |
| 43 | `2026-11-08` | `2026-11` | [通用插件多维分类、显式热编译契约与元数据架构去隐式化方案](2026-11/universal-plugin-metadata-build-and-runtime-matrix-specification.md) | `universal-plugin-metadata-build-and-runtime-matrix-specification.md` | `Implemented` |
| 44 | `2026-11-08` | `2026-11` | [LinuxWebTool 插件通用持久化路径重构与现有插件全量升级方案](2026-11/universal-plugin-persistence-and-folder-refactoring-plan.md) | `universal-plugin-persistence-and-folder-refactoring-plan.md` | `Implemented` |
| 45 | `2026-11-08` | `2026-11` | [LinuxWebTool 通用跨语言插件持久化架构与存储状态机设计方案](2026-11/universal-plugin-persistence-architecture-and-state-machine-framework.md) | `universal-plugin-persistence-architecture-and-state-machine-framework.md` | `Implemented` |
| 46 | `2026-11-08` | `2026-11` | [LinuxWebTool 统一插件生态、正交分类拓扑与全栈交互完整优化方案 (Master Plan)](2026-11/universal-plugin-v3-full-stack-evolution-master-plan.md) | `universal-plugin-v3-full-stack-evolution-master-plan.md` | `Implemented` |
| 47 | `2026-11-08` | `2026-11` | [业界主流插件架构对比与第三方库选型评估](2026-11/worker-pure-architecture-industry-and-libraries.md) | `worker-pure-architecture-industry-and-libraries.md` | `Implemented` |

<a id="组网与网络穿透"></a>
### 🌐 组网与网络穿透 (36 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-08` | `2026-11` | [宿主 HTTP 路径封装代理隧道、独立客户端实例与抗封锁伪装性深度调研与架构方案](2026-11/advanced-http-encapsulated-proxy-tunnel-and-anti-censorship-research.md) | `advanced-http-encapsulated-proxy-tunnel-and-anti-censorship-research.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [复杂 SPA 视频流网页（Bilibili）滚动评论区在代理下加载缓慢深度调研与解决方案](2026-11/bilibili-comment-lazy-load-slowdown-proxy-research.md) | `bilibili-comment-lazy-load-slowdown-proxy-research.md` | `Implemented` |
| 3 | `2026-11-08` | `2026-11` | [SOCKS5 与 HTTP 代理多协议复用、数据面传输性能极致优化与架构重构调研报告](2026-11/http-proxy-multiplexing-and-transfer-performance-optimization-research.md) | `http-proxy-multiplexing-and-transfer-performance-optimization-research.md` | `Implemented` |
| 4 | `2026-11-08` | `2026-11` | [LXC/Incus 容器高级网络（IPv4/v6 NAT与物理网卡桥接）与动态硬件性能调优方案](2026-11/lxc-advanced-network-and-dynamic-resource-tuning-plan.md) | `lxc-advanced-network-and-dynamic-resource-tuning-plan.md` | `Implemented` |
| 5 | `2026-11-08` | `2026-11` | [LXC/Incus 容器实例网络 IP 重复问题深度调研与架构解决方案](2026-11/lxc-container-network-ip-duplicate-analysis-and-solution.md) | `lxc-container-network-ip-duplicate-analysis-and-solution.md` | `Implemented` |
| 6 | `2026-11-08` | `2026-11` | [LXC 容器硬件规格热调优与网络接口模式持久化方案调研与实践报告](2026-11/lxc-container-network-mode-persistence-and-hot-tuning-research.md) | `lxc-container-network-mode-persistence-and-hot-tuning-research.md` | `Implemented` |
| 7 | `2026-11-08` | `2026-11` | [LXC 容器导入端点缺失、实例彻底删除、细粒度硬件真实物理验证与网络接口热切换调研及解决方案](2026-11/lxc-import-endpoint-deletion-and-network-switching-report.md) | `lxc-import-endpoint-deletion-and-network-switching-report.md` | `Implemented` |
| 8 | `2026-11-08` | `2026-11` | [LXC 容器物理网络选项缺失、NAT 端口映射异常与导出故障深度调研与综合解决方案](2026-11/lxc-network-options-nat-fix-and-export-troubleshooting-report.md) | `lxc-network-options-nat-fix-and-export-troubleshooting-report.md` | `Implemented` |
| 9 | `2026-11-08` | `2026-11` | [OpenAI MCP Tunnel 渠道连通性异常、速率限制根因与完整日志观测性方案](2026-11/mcp-bridge-openai-tunnel-channel-diagnostics-and-rate-limit-resolution.md) | `mcp-bridge-openai-tunnel-channel-diagnostics-and-rate-limit-resolution.md` | `Implemented` |
| 10 | `2026-11-08` | `2026-11` | [OpenAI MCP Tunnel 长轮询超时根因分析与网络出口代理治理方案](2026-11/mcp-bridge-openai-tunnel-poll-timeout-root-cause-and-egress-governance.md) | `mcp-bridge-openai-tunnel-poll-timeout-root-cause-and-egress-governance.md` | `Implemented` |
| 11 | `2026-11-08` | `2026-11` | [网络代理性能测试方案、延迟与带宽基准及真实网页访问性能调优调研](2026-11/network-proxy-performance-testing-and-optimization-research.md) | `network-proxy-performance-testing-and-optimization-research.md` | `Implemented` |
| 12 | `2026-11-08` | `2026-11` | [12 种主流代理协议兼容性测试套件与全景验收方案](2026-11/poly-protocol-compatibility-test-suite-and-acceptance-plan.md) | `poly-protocol-compatibility-test-suite-and-acceptance-plan.md` | `Implemented` |
| 13 | `2026-11-08` | `2026-11` | [SOCKS5 边缘中继网关进阶演化：常规链接导入、分流模板、自适应链式代理、垂直切片架构与透明网关落地方案](2026-11/poly-protocol-forwarder-link-import-routing-templates-adaptive-chains-and-tun-architecture.md) | `poly-protocol-forwarder-link-import-routing-templates-adaptive-chains-and-tun-architecture.md` | `Implemented` |
| 14 | `2026-11-08` | `2026-11` | [12 种主流代理协议矩阵与进阶传输层深度调研与架构设计规范](2026-11/poly-protocol-proxy-matrix-and-advanced-transport-architecture.md) | `poly-protocol-proxy-matrix-and-advanced-transport-architecture.md` | `Implemented` |
| 15 | `2026-11-08` | `2026-11` | [SOCKS5 安全代理浏览器延迟审计与回归门禁](2026-11/socks5-browser-latency-audit-and-regression-gates.md) | `socks5-browser-latency-audit-and-regression-gates.md` | `Implemented` |
| 16 | `2026-11-08` | `2026-11` | [SOCKS5 客户端边缘中继网关：多上游协议、链式代理、分流路由与透明网关架构调研与深度设计](2026-11/socks5-forwarder-multi-upstream-chaining-routing-tproxy-architecture.md) | `socks5-forwarder-multi-upstream-chaining-routing-tproxy-architecture.md` | `Implemented` |
| 17 | `2026-11-08` | `2026-11` | [浏览器同时打开多个标签时的代理卡顿调查](2026-11/socks5-multitab-burst-stall-investigation.md) | `socks5-multitab-burst-stall-investigation.md` | `Implemented` |
| 18 | `2026-11-08` | `2026-11` | [SOCKS5 代理高并发容量阶梯与运行时观测方案](2026-11/socks5-proxy-capacity-ladder-and-runtime-observability-design.md) | `socks5-proxy-capacity-ladder-and-runtime-observability-design.md` | `Implemented` |
| 19 | `2026-11-08` | `2026-11` | [SOCKS5 代理全链路验证与主流优化方案复核](2026-11/socks5-proxy-full-flow-validation-and-mainstream-optimization-review.md) | `socks5-proxy-full-flow-validation-and-mainstream-optimization-review.md` | `Implemented` |
| 20 | `2026-11-08` | `2026-11` | [SOCKS5 安全代理优化路线与可行性评估](2026-11/socks5-proxy-optimization-roadmap-and-feasibility.md) | `socks5-proxy-optimization-roadmap-and-feasibility.md` | `Implemented` |
| 21 | `2026-11-08` | `2026-11` | [SOCKS5 代理微服务 `socks5://10.126.1.70:18080` 无法访问根因调研与解决方案报告](2026-11/socks5-service-10-126-1-70-inaccessibility-analysis-and-solution.md) | `socks5-service-10-126-1-70-inaccessibility-analysis-and-solution.md` | `Implemented` |
| 22 | `2026-11-08` | `2026-11` | [WebCodex 通过 OpenAI Secure MCP Tunnel 向 ChatGPT 提供服务：调研索引](2026-11/webcodex-tunnel-research.md) | `webcodex-tunnel-research.md` | `Implemented` |
| 23 | `2026-11-08` | `2026-11` | [WebCodex Tunnel：工具发现、执行与 LinuxWebTool 接入映射](2026-11/webcodex-tunnel-tools.md) | `webcodex-tunnel-tools.md` | `Implemented` |
| 24 | `2026-11-05` | `2026-11` | [SOCKS5 代理多连接并发与容量治理评审](2026-11/socks5-proxy-concurrency-and-capacity-review.md) | `socks5-proxy-concurrency-and-capacity-review.md` | `Implemented` |
| 25 | `2026-11-02` | `2026-11` | [SOCKS5 代理网段白名单控制与域名通配符/正则过滤架构设计方案](2026-11/socks5-proxy-subnet-whitelist-and-domain-filter-design.md) | `socks5-proxy-subnet-whitelist-and-domain-filter-design.md` | `Implemented` |
| 26 | `2026-10-08` | `2026-10` | [EasyTier 容器局域网路由冲突：原因与处理方案](2026-10/easytier-container-lan-route-conflict-analysis.md) | `easytier-container-lan-route-conflict-analysis.md` | `Implemented` |
| 27 | `2026-10-08` | `2026-10` | [EasyTier 节点编辑页面可扩展选项调研](2026-10/easytier-editor-options-research.md) | `easytier-editor-options-research.md` | `Implemented` |
| 28 | `2026-10-08` | `2026-10` | [EasyTier 多节点故障隔离与防回归方案](2026-10/easytier-multi-node-safety-plan.md) | `easytier-multi-node-safety-plan.md` | `Implemented` |
| 29 | `2026-10-08` | `2026-10` | [EasyTier 虚拟组网节点管理与双层热更新技术方案](2026-10/easytier-node-management-and-hot-update-plan.md) | `easytier-node-management-and-hot-update-plan.md` | `Implemented` |
| 30 | `2026-10-08` | `2026-10` | [EasyTier 静态 IP 被 DHCP 覆盖：原因、修复与验证](2026-10/easytier-static-ip-dhcp-analysis-and-fix.md) | `easytier-static-ip-dhcp-analysis-and-fix.md` | `Implemented` |
| 31 | `2026-10-08` | `2026-10` | [ProxyByCF FRP 内网穿透客户端：多线路配置、302 内网重定向代理与 HTTP/SOCKS 代理支持调研及方案](2026-10/frp-multi-line-and-302-redirect-proxy-plan.md) | `frp-multi-line-and-302-redirect-proxy-plan.md` | `Implemented` |
| 32 | `2026-10-08` | `2026-10` | [基于 ProxyByCF 工业级标准的 FRP 反向穿透优化可行性调研与预览方案](2026-10/frp-proxybycf-optimization-feasibility-and-preview-plan.md) | `frp-proxybycf-optimization-feasibility-and-preview-plan.md` | `Implemented` |
| 33 | `2026-10-08` | `2026-10` | [ProxyByCF FRP 客户端集成技术调研与实施方案](2026-10/frp-tunnel-client-integration-plan.md) | `frp-tunnel-client-integration-plan.md` | `Implemented` |
| 34 | `2026-10-08` | `2026-10` | [家庭网关与 FRP 反向穿透全流程代码审计与优化实施方案](2026-10/gateway-and-frp-tunnel-full-flow-audit-and-optimization-plan.md) | `gateway-and-frp-tunnel-full-flow-audit-and-optimization-plan.md` | `Implemented` |
| 35 | `2026-10-08` | `2026-10` | [借鉴 ProxyYARP 代理库与家庭网关融合技术方案](2026-10/proxyyarp-integration-and-home-gateway-plan.md) | `proxyyarp-integration-and-home-gateway-plan.md` | `Implemented` |
| 36 | `2026-10-07` | `2026-10` | [EasyTier 组网故障排查、权限模型与工程避坑实践指南](2026-10/easytier-troubleshooting-and-lessons-learned.md) | `easytier-troubleshooting-and-lessons-learned.md` | `Implemented` |

<a id="安全鉴权与凭据"></a>
### 🛡️ 安全鉴权与凭据 (15 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-11` | `2026-11` | [Samba (SMB) 宿主穿透架构治理、冷启动自愈与凭据管理方案](2026-11/samba-host-service-penetration-and-credential-governance-plan.md) | `samba-host-service-penetration-and-credential-governance-plan.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [API Key 统一安全策略层与防火墙模板化架构方案 (RFC v3.1 - 四层正交收敛版)](2026-11/apikey-security-policy-guardrails-and-firewall-template-plan.md) | `apikey-security-policy-guardrails-and-firewall-template-plan.md` | `Implemented` |
| 3 | `2026-11-08` | `2026-11` | [Go 动态增量编译包路径缺陷分析与 SOCKS5 代理三维安全防御方案](2026-11/go-runtime-package-build-and-socks5-auth-plan.md) | `go-runtime-package-build-and-socks5-auth-plan.md` | `Implemented` |
| 4 | `2026-11-08` | `2026-11` | [敏感凭据安全检测门禁与 Git 提交钩子架构设计方案](2026-11/secret-security-gate-and-git-hook-architecture-design.md) | `secret-security-gate-and-git-hook-architecture-design.md` | `Implemented` |
| 5 | `2026-11-08` | `2026-11` | [凭据防泄露门禁：多字符集自适应香农熵与脱敏规则合规校验调研与架构方案](2026-11/secret-security-gate-entropy-and-desensitization-governance-plan.md) | `secret-security-gate-entropy-and-desensitization-governance-plan.md` | `Implemented` |
| 6 | `2026-11-08` | `2026-11` | [WebCodex Tunnel：连接、凭据与传输核心代码](2026-11/webcodex-tunnel-connection.md) | `webcodex-tunnel-connection.md` | `Implemented` |
| 7 | `2026-10-26` | `2026-10` | [API Key 终端与文件夹访问路径防护可行性调研与设计方案](2026-10/apikey-path-scope-and-isolation-feasibility-plan.md) | `apikey-path-scope-and-isolation-feasibility-plan.md` | `Implemented` |
| 8 | `2026-10-09` | `2026-10` | [认证安全与权限控制全流程整改及防护门禁建设方案](2026-10/auth-and-permission-remediation-plan.md) | `auth-and-permission-remediation-plan.md` | `Implemented` |
| 9 | `2026-10-08` | `2026-10` | [APIKey 授权体系与细粒度权限矩阵方案](2026-10/apikey-authorization-and-permission-matrix-plan.md) | `apikey-authorization-and-permission-matrix-plan.md` | `Implemented` |
| 10 | `2026-10-08` | `2026-10` | [ApiKey 授权、终端页面与转码流程全链路审计与优化实施方案](2026-10/apikey-terminal-transcode-audit-and-optimization-plan.md) | `apikey-terminal-transcode-audit-and-optimization-plan.md` | `Implemented` |
| 11 | `2026-10-08` | `2026-10` | [用户登录认证与权限控制全流程代码逻辑深度审计与性能调研报告](2026-10/auth-and-permission-audit-report.md) | `auth-and-permission-audit-report.md` | `Implemented` |
| 12 | `2026-10-08` | `2026-10` | [MCP 二级工具权限勾选失效根因调研与 SSOT 契约统一方案](2026-10/mcp-catalog-provider-alias-and-permission-fix-plan.md) | `mcp-catalog-provider-alias-and-permission-fix-plan.md` | `Implemented` |
| 13 | `2026-10-08` | `2026-10` | [远程主机文件管理下载出现“需要授权”问题根因剖析与纵深防御方案](2026-10/remote-host-file-download-authorization-fix.md) | `remote-host-file-download-authorization-fix.md` | `Implemented` |
| 14 | `2026-10-08` | `2026-10` | [统一业务模块与 MCP 二级工具树状授权方案设计与可行性调研](2026-10/unified-permission-tree-matrix-plan.md) | `unified-permission-tree-matrix-plan.md` | `Implemented` |
| 15 | `2026-10-08` | `2026-10` | [扩展服务与函数服务混合单仓、MCP/APIKey调用、工作流编排与在线热更新架构方案](2026-10/unified-repo-mcp-workflow-and-hotupdate-plan.md) | `unified-repo-mcp-workflow-and-hotupdate-plan.md` | `Implemented` |

<a id="备份与存储同步"></a>
### 📦 备份与存储同步 (14 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-08` | `2026-11` | [前端 Schema v3.1 全面同步演进与微服务动态发现调用交互方案](2026-11/frontend-schema-v3-synchronization-and-interaction-evolution-plan.md) | `frontend-schema-v3-synchronization-and-interaction-evolution-plan.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [SOCKS5 安全代理微服务端口无法访问与 Webview 模态框端口脱节深度调研与治理方案](2026-11/socks5-proxy-port-inaccessibility-and-webview-port-desync-analysis-plan.md) | `socks5-proxy-port-inaccessibility-and-webview-port-desync-analysis-plan.md` | `Implemented` |
| 3 | `2026-10-27` | `2026-10` | [备份与同步任务“源/目标目录与关联挂载异常自动暂停”可行性调研与架构方案](2026-10/backup-sync-mount-health-guard.md) | `backup-sync-mount-health-guard.md` | `Implemented` |
| 4 | `2026-10-11` | `2026-10` | [文件夹备份与同步功能可行性调研与预览方案](2026-10/folder-backup-and-sync-feasibility-and-preview-plan.md) | `folder-backup-and-sync-feasibility-and-preview-plan.md` | `Implemented` |
| 5 | `2026-10-08` | `2026-10` | [备份与同步排除规则智能探测与推荐引擎设计方案 (Backup-Sync Exclude Detection & Recommendation Plan)](2026-10/backup-sync-exclude-detection-and-recommendation-plan.md) | `backup-sync-exclude-detection-and-recommendation-plan.md` | `Implemented` |
| 6 | `2026-10-08` | `2026-10` | [备份与同步任务实时执行进度（Execution Progress）调研与技术方案](2026-10/backup-sync-execution-progress-tracking-plan.md) | `backup-sync-execution-progress-tracking-plan.md` | `Implemented` |
| 7 | `2026-10-08` | `2026-10` | [备份同步任务无法完成深度根因分析与执行记录自动清理设计方案](2026-10/backup-sync-execution-records-and-pruning-design-plan.md) | `backup-sync-execution-records-and-pruning-design-plan.md` | `Implemented` |
| 8 | `2026-10-08` | `2026-10` | [备份与同步模块：路径快捷跳转文件管理页面方案](2026-10/backup-sync-file-manager-quick-jump-plan.md) | `backup-sync-file-manager-quick-jump-plan.md` | `Implemented` |
| 9 | `2026-10-08` | `2026-10` | [备份同步文件夹路径点选排除与交互体验升级设计方案 (Folder Picker & Visual Exclusion Plan)](2026-10/backup-sync-folder-picker-and-visual-exclusion-plan.md) | `backup-sync-folder-picker-and-visual-exclusion-plan.md` | `Implemented` |
| 10 | `2026-10-08` | `2026-10` | [备份与同步模块：实时进度交互美化与移动端响应式架构优化方案](2026-10/backup-sync-mobile-and-progress-ui-optimization-plan.md) | `backup-sync-mobile-and-progress-ui-optimization-plan.md` | `Implemented` |
| 11 | `2026-10-08` | `2026-10` | [备份同步智能排除规则与可视化交互全景开发方案 (Backup-Sync Smart Exclusions Master Implementation Plan)](2026-10/backup-sync-smart-exclusions-master-implementation-plan.md) | `backup-sync-smart-exclusions-master-implementation-plan.md` | `Implemented` |
| 12 | `2026-10-08` | `2026-10` | [备份同步执行中假死与 CIFS 深度韧性优化方案 (Backup-Sync Stuck-Running & CIFS Resilience Plan)](2026-10/backup-sync-stuck-running-and-cifs-resilience-plan.md) | `backup-sync-stuck-running-and-cifs-resilience-plan.md` | `Implemented` |
| 13 | `2026-10-08` | `2026-10` | [定时备份任务失败“Could not find file detect-libc”根因调研与全链路符号链接及容错防御方案](2026-10/backup-sync-symlink-and-vanished-files-fix-plan.md) | `backup-sync-symlink-and-vanished-files-fix-plan.md` | `Implemented` |
| 14 | `2026-10-08` | `2026-10` | [备份同步排除规则可视化操作与零黑盒逻辑深度设计方案 (Backup-Sync Visual Exclude Rules & Zero-Hidden-Logic Plan)](2026-10/backup-sync-visual-exclude-rules-and-zero-hidden-logic-plan.md) | `backup-sync-visual-exclude-rules-and-zero-hidden-logic-plan.md` | `Implemented` |

<a id="审计与复盘报告"></a>
### 📊 审计与复盘报告 (9 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-11` | `2026-11` | [Samba (SMB) 局域网共享管理 深度增删查改 (CRUD) 补齐与连接会话审计治理方案](2026-11/samba-share-advanced-crud-and-audit-governance-plan.md) | `samba-share-advanced-crud-and-audit-governance-plan.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [MCP Bridge 隧道状态固化回填、配置生效容错降级与界面协同调研报告](2026-11/mcp-bridge-persistence-apply-degradation-and-ui-refresh-research.md) | `mcp-bridge-persistence-apply-degradation-and-ui-refresh-research.md` | `Implemented` |
| 3 | `2026-11-08` | `2026-11` | [MCP 智能网关持久化深度复盘、设计模式演进与存储状态机架构方案](2026-11/mcp-bridge-persistence-deep-analysis-and-storage-state-machine-redesign.md) | `mcp-bridge-persistence-deep-analysis-and-storage-state-machine-redesign.md` | `Implemented` |
| 4 | `2026-11-08` | `2026-11` | [MCP Bridge 真实工具链式调用与 Native AOT 零反射闭环研发与实测报告](2026-11/mcp-bridge-real-tool-call-and-native-aot-integration-report.md) | `mcp-bridge-real-tool-call-and-native-aot-integration-report.md` | `Implemented` |
| 5 | `2026-11-08` | `2026-11` | [SOCKS5h 协议支持、网页访问延迟根因剖析与热更新优化调研报告](2026-11/socks5h-remote-dns-latency-optimization-and-hotupdate-research.md) | `socks5h-remote-dns-latency-optimization-and-hotupdate-research.md` | `Implemented` |
| 6 | `2026-11-08` | `2026-11` | [纯粹 Worker 架构现状差距审计与解耦实施计划](2026-11/worker-pure-architecture-gap-audit-and-plan.md) | `worker-pure-architecture-gap-audit-and-plan.md` | `Implemented` |
| 7 | `2026-11-02` | `2026-11` | [开发环境验证流程深度优化与容器内在途热重载调研报告](2026-11/dev-workflow-optimization-and-in-container-fast-reload.md) | `dev-workflow-optimization-and-in-container-fast-reload.md` | `Implemented` |
| 8 | `2026-10-08` | `2026-10` | [前端页面内容溢出剪切与对齐居中全面审计与优化方案](2026-10/frontend-layout-overflow-alignment-audit-and-optimization-plan.md) | `frontend-layout-overflow-alignment-audit-and-optimization-plan.md` | `Implemented` |
| 9 | `2026-10-08` | `2026-10` | [剩余核心模块全流程审计与优化方案](2026-10/remaining-modules-audit-and-optimization-plan.md) | `remaining-modules-audit-and-optimization-plan.md` | `Implemented` |

<a id="终端与PTY引擎"></a>
### 💻 终端与 PTY 引擎 (9 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-08` | `2026-11` | [宿主终端 CWD 孤岛根治与执行目标描述统一方案](2026-11/host-terminal-cwd-dangling-fix-and-target-ui-unification-plan.md) | `host-terminal-cwd-dangling-fix-and-target-ui-unification-plan.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [宿主终端 Locale 与路径净化、函数微服务安装引导跨宿主边界与定时任务宿主穿透调研及方案](2026-11/host-terminal-locale-worker-guides-and-schedules-target-plan.md) | `host-terminal-locale-worker-guides-and-schedules-target-plan.md` | `Implemented` |
| 3 | `2026-10-08` | `2026-10` | [远程主机多协议纳管、Web 终端与文件系统集成方案](2026-10/remote-host-management-and-multi-protocol-feasibility-plan.md) | `remote-host-management-and-multi-protocol-feasibility-plan.md` | `Implemented` |
| 4 | `2026-10-08` | `2026-10` | [终端清屏后顶部残留](2026-10/terminal-clear-screen-residue.md) | `terminal-clear-screen-residue.md` | `Implemented` |
| 5 | `2026-10-08` | `2026-10` | [终端连接、指令与定时任务执行问题分析及修复方案](2026-10/terminal-command-schedule-execution-fix-plan.md) | `terminal-command-schedule-execution-fix-plan.md` | `Implemented` |
| 6 | `2026-10-08` | `2026-10` | [终端与文件目录联动、掉线后继续运行方案](2026-10/terminal-directory-and-detached-session-plan.md) | `terminal-directory-and-detached-session-plan.md` | `Implemented` |
| 7 | `2026-10-08` | `2026-10` | [终端方案实施记录](2026-10/terminal-implementation-progress.md) | `terminal-implementation-progress.md` | `Implemented` |
| 8 | `2026-10-08` | `2026-10` | [tmux 备用终端方案](2026-10/terminal-tmux-fallback-option.md) | `terminal-tmux-fallback-option.md` | `Implemented` |
| 9 | `2026-10-08` | `2026-10` | [Windows 终端与文件目录联动实施方案](2026-10/windows-terminal-and-file-integration-plan.md) | `windows-terminal-and-file-integration-plan.md` | `Implemented` |

<a id="容器与部署架构"></a>
### 🚀 容器与部署架构 (8 篇)

| 序号 | 制定日期 | 年月 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :---: | :--- | :--- | :---: |
| 1 | `2026-11-08` | `2026-11` | [LinuxWebTool 自动化发布、Linux 部署与内置浏览器验收全流程规范](2026-11/automated-deployment-and-browser-acceptance-workflow.md) | `automated-deployment-and-browser-acceptance-workflow.md` | `Implemented` |
| 2 | `2026-11-08` | `2026-11` | [Docker 构建第 13 阶段（dotnet publish）耗时超 100 秒深度剖析与极速优化方案](2026-11/docker-build-stage13-latency-analysis-and-optimization-plan.md) | `docker-build-stage13-latency-analysis-and-optimization-plan.md` | `Implemented` |
| 3 | `2026-11-08` | `2026-11` | [Docker 构建 73 秒二次瓶颈深度剖析与极速方案设计（设计模式、架构分层与性能极限突破）](2026-11/docker-build-sub-15s-deep-optimization-and-design-patterns-plan.md) | `docker-build-sub-15s-deep-optimization-and-design-patterns-plan.md` | `Implemented` |
| 4 | `2026-11-08` | `2026-11` | [LXC 宿主依赖与 Docker 共存方案](2026-11/lxc-host-dependencies-docker-coexistence-plan.md) | `lxc-host-dependencies-docker-coexistence-plan.md` | `Implemented` |
| 5 | `2026-11-08` | `2026-11` | [LXC 宿主依赖自动化配置、声明式契约与快速部署方案](2026-11/lxc-host-dependency-automated-provisioning-and-deployment-plan.md) | `lxc-host-dependency-automated-provisioning-and-deployment-plan.md` | `Implemented` |
| 6 | `2026-10-08` | `2026-10` | [GitHub Actions Docker 多架构构建矩阵收敛优化 (方案 1)](2026-10/docker-multi-arch-matrix-optimization-plan.md) | `docker-multi-arch-matrix-optimization-plan.md` | `Implemented` |
| 7 | `2026-10-08` | `2026-10` | [GitHub Actions CI/CD 分工解耦与双重构建优化方案 (方案 1)](2026-10/github-actions-ci-cd-optimization-plan.md) | `github-actions-ci-cd-optimization-plan.md` | `Implemented` |
| 8 | `2026-10-08` | `2026-10` | [系统与服务管理中枢 (Services Hub) 可行性与深度架构方案](2026-10/system-services-and-docker-management-hub-feasibility-plan.md) | `system-services-and-docker-management-hub-feasibility-plan.md` | `Implemented` |

---

## 4. 视图 B：按年月时间线明细表 (Chronological Timeline)

<a id="202611"></a>
### 📅 2026-11 时间线汇总 (139 篇)

| 序号 | 制定日期 | 分类 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :--- | :--- | :--- | :---: |
| 1 | `2026-11-11` | 🛡️ 安全鉴权与凭据 | [Samba (SMB) 宿主穿透架构治理、冷启动自愈与凭据管理方案](2026-11/samba-host-service-penetration-and-credential-governance-plan.md) | `samba-host-service-penetration-and-credential-governance-plan.md` | `Implemented` |
| 2 | `2026-11-11` | 📊 审计与复盘报告 | [Samba (SMB) 局域网共享管理 深度增删查改 (CRUD) 补齐与连接会话审计治理方案](2026-11/samba-share-advanced-crud-and-audit-governance-plan.md) | `samba-share-advanced-crud-and-audit-governance-plan.md` | `Implemented` |
| 3 | `2026-11-08` | 🌐 组网与网络穿透 | [宿主 HTTP 路径封装代理隧道、独立客户端实例与抗封锁伪装性深度调研与架构方案](2026-11/advanced-http-encapsulated-proxy-tunnel-and-anti-censorship-research.md) | `advanced-http-encapsulated-proxy-tunnel-and-anti-censorship-research.md` | `Implemented` |
| 4 | `2026-11-08` | 📝 架构方案与决议 | [AI 辅助系统级复杂工程落地可行性与能力边界深度剖析](2026-11/ai-assisted-engineering-boundary-and-system-feasibility-analysis.md) | `ai-assisted-engineering-boundary-and-system-feasibility-analysis.md` | `Implemented` |
| 5 | `2026-11-08` | 🛡️ 安全鉴权与凭据 | [API Key 统一安全策略层与防火墙模板化架构方案 (RFC v3.1 - 四层正交收敛版)](2026-11/apikey-security-policy-guardrails-and-firewall-template-plan.md) | `apikey-security-policy-guardrails-and-firewall-template-plan.md` | `Implemented` |
| 6 | `2026-11-08` | 🚀 容器与部署架构 | [LinuxWebTool 自动化发布、Linux 部署与内置浏览器验收全流程规范](2026-11/automated-deployment-and-browser-acceptance-workflow.md) | `automated-deployment-and-browser-acceptance-workflow.md` | `Implemented` |
| 7 | `2026-11-08` | 🌐 组网与网络穿透 | [复杂 SPA 视频流网页（Bilibili）滚动评论区在代理下加载缓慢深度调研与解决方案](2026-11/bilibili-comment-lazy-load-slowdown-proxy-research.md) | `bilibili-comment-lazy-load-slowdown-proxy-research.md` | `Implemented` |
| 8 | `2026-11-08` | 📝 架构方案与决议 | [ChatGPT 渠道常驻服务方案复核](2026-11/chatgpt-channel-service-reassessment.md) | `chatgpt-channel-service-reassessment.md` | `Implemented` |
| 9 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 自动化部署、环境引导与插件系统架构优化方案](2026-11/deployment-and-plugin-architecture-optimization-plan.md) | `deployment-and-plugin-architecture-optimization-plan.md` | `Implemented` |
| 10 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 系统文件夹占用查询插件技术可行性调研与架构设计方案](2026-11/disk-usage-analyzer-plugin-feasibility-and-architecture-design.md) | `disk-usage-analyzer-plugin-feasibility-and-architecture-design.md` | `Implemented` |
| 11 | `2026-11-08` | 🚀 容器与部署架构 | [Docker 构建第 13 阶段（dotnet publish）耗时超 100 秒深度剖析与极速优化方案](2026-11/docker-build-stage13-latency-analysis-and-optimization-plan.md) | `docker-build-stage13-latency-analysis-and-optimization-plan.md` | `Implemented` |
| 12 | `2026-11-08` | 🚀 容器与部署架构 | [Docker 构建 73 秒二次瓶颈深度剖析与极速方案设计（设计模式、架构分层与性能极限突破）](2026-11/docker-build-sub-15s-deep-optimization-and-design-patterns-plan.md) | `docker-build-sub-15s-deep-optimization-and-design-patterns-plan.md` | `Implemented` |
| 13 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 插件动态目录扫描、热注册引擎与导入优先开发流架构设计](2026-11/dynamic-folder-scan-and-import-first-plugin-architecture.md) | `dynamic-folder-scan-and-import-first-plugin-architecture.md` | `Implemented` |
| 14 | `2026-11-08` | 🧩 插件与微服务 | [现有 21+ 扩展插件全量升级至 Schema v3.1 规范与目录标准化方案](2026-11/existing-plugins-schema-v3-migration-and-directory-standardization-plan.md) | `existing-plugins-schema-v3-migration-and-directory-standardization-plan.md` | `Implemented` |
| 15 | `2026-11-08` | 📝 架构方案与决议 | [Linux 极速开发与快速测试工作流架构设计与落地方案](2026-11/fast-linux-dev-pipeline-and-architecture-design.md) | `fast-linux-dev-pipeline-and-architecture-design.md` | `Implemented` |
| 16 | `2026-11-08` | 📝 架构方案与决议 | [LinuxWebTool 文件管理器目标源切换（容器 vs 宿主穿透）技术可行性调研与架构设计方案](2026-11/file-manager-host-target-switching-feasibility-and-architecture-design.md) | `file-manager-host-target-switching-feasibility-and-architecture-design.md` | `Implemented` |
| 17 | `2026-11-08` | 📝 架构方案与决议 | [前端列表相关容器显示门禁与逻辑高度限制调研及架构设计方案](2026-11/frontend-list-container-height-guard-and-patterns-plan.md) | `frontend-list-container-height-guard-and-patterns-plan.md` | `Implemented` |
| 18 | `2026-11-08` | 📦 备份与存储同步 | [前端 Schema v3.1 全面同步演进与微服务动态发现调用交互方案](2026-11/frontend-schema-v3-synchronization-and-interaction-evolution-plan.md) | `frontend-schema-v3-synchronization-and-interaction-evolution-plan.md` | `Implemented` |
| 19 | `2026-11-08` | 🧩 插件与微服务 | [Go 运行引擎与 SOCKS5 代理微服务调研与架构设计方案](2026-11/go-runtime-engine-and-socks5-proxy-plugin-design.md) | `go-runtime-engine-and-socks5-proxy-plugin-design.md` | `Implemented` |
| 20 | `2026-11-08` | 🛡️ 安全鉴权与凭据 | [Go 动态增量编译包路径缺陷分析与 SOCKS5 代理三维安全防御方案](2026-11/go-runtime-package-build-and-socks5-auth-plan.md) | `go-runtime-package-build-and-socks5-auth-plan.md` | `Implemented` |
| 21 | `2026-11-08` | 🧩 插件与微服务 | [宿主侵入代码退役与插件可选依赖环境（Optional Dependencies）架构调研与设计方案](2026-11/host-decoupling-retirement-and-plugin-optional-dependencies-design.md) | `host-decoupling-retirement-and-plugin-optional-dependencies-design.md` | `Implemented` |
| 22 | `2026-11-08` | 🧩 插件与微服务 | [宿主元数据向下投影与插件上下文感知架构设计](2026-11/host-metadata-projection-and-plugin-context-architecture.md) | `host-metadata-projection-and-plugin-context-architecture.md` | `Implemented` |
| 23 | `2026-11-08` | 💻 终端与 PTY 引擎 | [宿主终端 CWD 孤岛根治与执行目标描述统一方案](2026-11/host-terminal-cwd-dangling-fix-and-target-ui-unification-plan.md) | `host-terminal-cwd-dangling-fix-and-target-ui-unification-plan.md` | `Implemented` |
| 24 | `2026-11-08` | 💻 终端与 PTY 引擎 | [宿主终端 Locale 与路径净化、函数微服务安装引导跨宿主边界与定时任务宿主穿透调研及方案](2026-11/host-terminal-locale-worker-guides-and-schedules-target-plan.md) | `host-terminal-locale-worker-guides-and-schedules-target-plan.md` | `Implemented` |
| 25 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 与 HTTP 代理多协议复用、数据面传输性能极致优化与架构重构调研报告](2026-11/http-proxy-multiplexing-and-transfer-performance-optimization-research.md) | `http-proxy-multiplexing-and-transfer-performance-optimization-research.md` | `Implemented` |
| 26 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 边缘中继网关：三阶分层解耦架构与交互重塑方案 (Tripartite Architecture)](2026-11/inbound-outbound-routing-tripartite-architecture-and-ux-refactor.md) | `inbound-outbound-routing-tripartite-architecture-and-ux-refactor.md` | `Implemented` |
| 27 | `2026-11-08` | 📝 架构方案与决议 | [Incus 宿主安装影响评估](2026-11/incus-host-install-impact-assessment.md) | `incus-host-install-impact-assessment.md` | `Implemented` |
| 28 | `2026-11-08` | 📝 架构方案与决议 | [平台级 Ingress 动态路径别名、WebSocket 主动探测深度分析与多层防特征伪装流架构调研](2026-11/ingress-path-aliases-and-anti-probing-camouflaged-duplex-stream-research.md) | `ingress-path-aliases-and-anti-probing-camouflaged-duplex-stream-research.md` | `Implemented` |
| 29 | `2026-11-08` | 📝 架构方案与决议 | [宿主中转路径范围治理、路由冲突仲裁与双轨制命名空间（Dual-Track Namespacing）架构调研与优化方案](2026-11/ingress-path-scope-conflict-arbitration-and-dual-track-namespacing-plan.md) | `ingress-path-scope-conflict-arbitration-and-dual-track-namespacing-plan.md` | `Implemented` |
| 30 | `2026-11-08` | 🌐 组网与网络穿透 | [LXC/Incus 容器高级网络（IPv4/v6 NAT与物理网卡桥接）与动态硬件性能调优方案](2026-11/lxc-advanced-network-and-dynamic-resource-tuning-plan.md) | `lxc-advanced-network-and-dynamic-resource-tuning-plan.md` | `Implemented` |
| 31 | `2026-11-08` | 📝 架构方案与决议 | [LXC 系统容器管理架构调研与演进方案](2026-11/lxc-container-management-architecture-and-evolution-plan.md) | `lxc-container-management-architecture-and-evolution-plan.md` | `Implemented` |
| 32 | `2026-11-08` | 🌐 组网与网络穿透 | [LXC/Incus 容器实例网络 IP 重复问题深度调研与架构解决方案](2026-11/lxc-container-network-ip-duplicate-analysis-and-solution.md) | `lxc-container-network-ip-duplicate-analysis-and-solution.md` | `Implemented` |
| 33 | `2026-11-08` | 🌐 组网与网络穿透 | [LXC 容器硬件规格热调优与网络接口模式持久化方案调研与实践报告](2026-11/lxc-container-network-mode-persistence-and-hot-tuning-research.md) | `lxc-container-network-mode-persistence-and-hot-tuning-research.md` | `Implemented` |
| 34 | `2026-11-08` | 📝 架构方案与决议 | [LXC/Incus 容器细粒度硬件配额治理（CPU占用比、磁盘IO速率/IOPS、防Fork炸弹与Swap策略）深度方案](2026-11/lxc-fine-grained-hardware-limits-and-cgroupv2-tuning-plan.md) | `lxc-fine-grained-hardware-limits-and-cgroupv2-tuning-plan.md` | `Implemented` |
| 35 | `2026-11-08` | 🚀 容器与部署架构 | [LXC 宿主依赖与 Docker 共存方案](2026-11/lxc-host-dependencies-docker-coexistence-plan.md) | `lxc-host-dependencies-docker-coexistence-plan.md` | `Implemented` |
| 36 | `2026-11-08` | 🚀 容器与部署架构 | [LXC 宿主依赖自动化配置、声明式契约与快速部署方案](2026-11/lxc-host-dependency-automated-provisioning-and-deployment-plan.md) | `lxc-host-dependency-automated-provisioning-and-deployment-plan.md` | `Implemented` |
| 37 | `2026-11-08` | 🧩 插件与微服务 | [LXC 插件宿主依赖安装与环境选择方案](2026-11/lxc-host-dependency-installation-feasibility.md) | `lxc-host-dependency-installation-feasibility.md` | `Implemented` |
| 38 | `2026-11-08` | 🌐 组网与网络穿透 | [LXC 容器导入端点缺失、实例彻底删除、细粒度硬件真实物理验证与网络接口热切换调研及解决方案](2026-11/lxc-import-endpoint-deletion-and-network-switching-report.md) | `lxc-import-endpoint-deletion-and-network-switching-report.md` | `Implemented` |
| 39 | `2026-11-08` | 📝 架构方案与决议 | [LXC/Incus 容器实例导入导出与冷热迁移架构设计方案](2026-11/lxc-instance-import-export-and-migration-plan.md) | `lxc-instance-import-export-and-migration-plan.md` | `Implemented` |
| 40 | `2026-11-08` | 🧩 插件与微服务 | [LXC 管理插件可行性与设计](2026-11/lxc-management-plugin-feasibility-and-design.md) | `lxc-management-plugin-feasibility-and-design.md` | `Implemented` |
| 41 | `2026-11-08` | 🧩 插件与微服务 | [LXC 管理插件启用与创建预检闭环方案](2026-11/lxc-manager-activation-and-preflight-solution.md) | `lxc-manager-activation-and-preflight-solution.md` | `Implemented` |
| 42 | `2026-11-08` | 🌐 组网与网络穿透 | [LXC 容器物理网络选项缺失、NAT 端口映射异常与导出故障深度调研与综合解决方案](2026-11/lxc-network-options-nat-fix-and-export-troubleshooting-report.md) | `lxc-network-options-nat-fix-and-export-troubleshooting-report.md` | `Implemented` |
| 43 | `2026-11-08` | 🧩 插件与微服务 | [LXC 管理插件实机验证与优化方案](2026-11/lxc-real-environment-validation-plan.md) | `lxc-real-environment-validation-plan.md` | `Implemented` |
| 44 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 自动连接与看门狗自动守护架构方案设计](2026-11/mcp-bridge-auto-connect-and-watchdog-supervisor-design.md) | `mcp-bridge-auto-connect-and-watchdog-supervisor-design.md` | `Implemented` |
| 45 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 原生二进制跨平台启动异常 (Exec format error) 调查分析与全链路二进制治理方案](2026-11/mcp-bridge-exec-format-error-investigation-and-binary-governance-plan.md) | `mcp-bridge-exec-format-error-investigation-and-binary-governance-plan.md` | `Implemented` |
| 46 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 修改文件范围与验收标准](2026-11/mcp-bridge-file-scope-and-acceptance.md) | `mcp-bridge-file-scope-and-acceptance.md` | `Implemented` |
| 47 | `2026-11-08` | 📝 架构方案与决议 | [Go 单进程 MCP Bridge 实施计划](2026-11/mcp-bridge-implementation-plan.md) | `mcp-bridge-implementation-plan.md` | `Implemented` |
| 48 | `2026-11-08` | 📝 架构方案与决议 | [OpenAI MCP 隧道长轮询超时、空闲断开与弹性自愈治理方案](2026-11/mcp-bridge-long-polling-timeout-idle-disconnect-and-resilience-governance.md) | `mcp-bridge-long-polling-timeout-idle-disconnect-and-resilience-governance.md` | `Implemented` |
| 49 | `2026-11-08` | 🧩 插件与微服务 | [原生插件二进制缺失根因剖析、生命周期自愈与端口防泄漏架构治理方案](2026-11/mcp-bridge-native-binary-resolution-and-lifecycle-safety-plan.md) | `mcp-bridge-native-binary-resolution-and-lifecycle-safety-plan.md` | `Implemented` |
| 50 | `2026-11-08` | 🌐 组网与网络穿透 | [OpenAI MCP Tunnel 渠道连通性异常、速率限制根因与完整日志观测性方案](2026-11/mcp-bridge-openai-tunnel-channel-diagnostics-and-rate-limit-resolution.md) | `mcp-bridge-openai-tunnel-channel-diagnostics-and-rate-limit-resolution.md` | `Implemented` |
| 51 | `2026-11-08` | 🌐 组网与网络穿透 | [OpenAI MCP Tunnel 长轮询超时根因分析与网络出口代理治理方案](2026-11/mcp-bridge-openai-tunnel-poll-timeout-root-cause-and-egress-governance.md) | `mcp-bridge-openai-tunnel-poll-timeout-root-cause-and-egress-governance.md` | `Implemented` |
| 52 | `2026-11-08` | 🧩 插件与微服务 | [MCP Bridge 配置持久化保证与脚本轻量扩展状态机生命周期治理调研与方案](2026-11/mcp-bridge-persistence-and-script-plugin-state-lifecycle-research.md) | `mcp-bridge-persistence-and-script-plugin-state-lifecycle-research.md` | `Implemented` |
| 53 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 控制台持久化生效机制、渠道自启与日志管理深度治理方案](2026-11/mcp-bridge-persistence-and-wire-logs-governance-plan.md) | `mcp-bridge-persistence-and-wire-logs-governance-plan.md` | `Implemented` |
| 54 | `2026-11-08` | 📊 审计与复盘报告 | [MCP Bridge 隧道状态固化回填、配置生效容错降级与界面协同调研报告](2026-11/mcp-bridge-persistence-apply-degradation-and-ui-refresh-research.md) | `mcp-bridge-persistence-apply-degradation-and-ui-refresh-research.md` | `Implemented` |
| 55 | `2026-11-08` | 📊 审计与复盘报告 | [MCP 智能网关持久化深度复盘、设计模式演进与存储状态机架构方案](2026-11/mcp-bridge-persistence-deep-analysis-and-storage-state-machine-redesign.md) | `mcp-bridge-persistence-deep-analysis-and-storage-state-machine-redesign.md` | `Implemented` |
| 56 | `2026-11-08` | 📝 架构方案与决议 | [MCP 智能安全网关桥接服务配置持久化丢失根本原因剖析、架构缺欠头脑风暴与设计模式重构方案](2026-11/mcp-bridge-persistence-loss-root-cause-and-storage-state-machine-architecture-plan.md) | `mcp-bridge-persistence-loss-root-cause-and-storage-state-machine-architecture-plan.md` | `Implemented` |
| 57 | `2026-11-08` | 📊 审计与复盘报告 | [MCP Bridge 真实工具链式调用与 Native AOT 零反射闭环研发与实测报告](2026-11/mcp-bridge-real-tool-call-and-native-aot-integration-report.md) | `mcp-bridge-real-tool-call-and-native-aot-integration-report.md` | `Implemented` |
| 58 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 敏感字段变活动作与前后端契约容错方案](2026-11/mcp-bridge-secret-mutation-action-compatibility-plan.md) | `mcp-bridge-secret-mutation-action-compatibility-plan.md` | `Implemented` |
| 59 | `2026-11-08` | 📝 架构方案与决议 | [MCP 智能安全网关桥接服务：支持标准 MCP JSON 格式直接导入调研与落地实施方案](2026-11/mcp-bridge-standard-json-import-and-adapter-plan.md) | `mcp-bridge-standard-json-import-and-adapter-plan.md` | `Implemented` |
| 60 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 状态投影、服务降级治理与工具激活生命周期研发方案](2026-11/mcp-bridge-status-projection-degradation-governance-and-tool-activation-plan.md) | `mcp-bridge-status-projection-degradation-governance-and-tool-activation-plan.md` | `Implemented` |
| 61 | `2026-11-08` | 📝 架构方案与决议 | [MCP Bridge 工具自动激活与轻量脚本自适应执行深度治理方案](2026-11/mcp-bridge-tool-activation-and-oneshot-execution-governance-plan.md) | `mcp-bridge-tool-activation-and-oneshot-execution-governance-plan.md` | `Implemented` |
| 62 | `2026-11-08` | 🧩 插件与微服务 | [函数服务托管的 ChatGPT Tunnel / MCP 聚合插件：可行性与设计](2026-11/mcp-tunnel-plugin-feasibility.md) | `mcp-tunnel-plugin-feasibility.md` | `Implemented` |
| 63 | `2026-11-08` | 🧩 插件与微服务 | [MCP Tunnel 插件：当前系统可复用设施与缺口核验](2026-11/mcp-tunnel-plugin-infrastructure.md) | `mcp-tunnel-plugin-infrastructure.md` | `Implemented` |
| 64 | `2026-11-08` | 🧩 插件与微服务 | [MCP Tunnel 插件：语言、现有库与行业方案比较](2026-11/mcp-tunnel-plugin-options.md) | `mcp-tunnel-plugin-options.md` | `Implemented` |
| 65 | `2026-11-08` | 🧩 插件与微服务 | [借鉴微服务与函数平台架构优化 LinuxWebTool 插件系统调研与落地方案](2026-11/microservices-faas-platform-research-and-plugin-system-optimization-plan.md) | `microservices-faas-platform-research-and-plugin-system-optimization-plan.md` | `Implemented` |
| 66 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 中继多端口多线路入站架构设计与远端节点离线深度排障](2026-11/multi-port-inbound-routing-and-upstream-offline-diagnosis.md) | `multi-port-inbound-routing-and-upstream-offline-diagnosis.md` | `Implemented` |
| 67 | `2026-11-08` | 📝 架构方案与决议 | [原生工具服务化实施计划](2026-11/native-tool-service-implementation-plan.md) | `native-tool-service-implementation-plan.md` | `Implemented` |
| 68 | `2026-11-08` | 📝 架构方案与决议 | [原生工具服务化：修改范围、示例与验收标准](2026-11/native-tool-service-scope-and-acceptance.md) | `native-tool-service-scope-and-acceptance.md` | `Implemented` |
| 69 | `2026-11-08` | 📝 架构方案与决议 | [NativeAOT-LLVM Wasm 架构剖析、在线源码编辑编译可行性调研与系统方案](2026-11/nativeaot-llvm-wasm-architecture-and-online-compilation-plan.md) | `nativeaot-llvm-wasm-architecture-and-online-compilation-plan.md` | `Implemented` |
| 70 | `2026-11-08` | 🌐 组网与网络穿透 | [网络代理性能测试方案、延迟与带宽基准及真实网页访问性能调优调研](2026-11/network-proxy-performance-testing-and-optimization-research.md) | `network-proxy-performance-testing-and-optimization-research.md` | `Implemented` |
| 71 | `2026-11-08` | 📝 架构方案与决议 | [次世代全协议多跳路由边缘网关（NextGen Poly-Protocol Edge Gateway）架构演进调研与深度设计](2026-11/nextgen-forwarder-chained-multi-protocol-routing-and-tun-architecture.md) | `nextgen-forwarder-chained-multi-protocol-routing-and-tun-architecture.md` | `Implemented` |
| 72 | `2026-11-08` | 📝 架构方案与决议 | [非 HTTP 扩展的前端注入与界面呈现架构调研与设计](2026-11/non-http-extension-frontend-injection-research-and-design.md) | `non-http-extension-frontend-injection-research-and-design.md` | `Implemented` |
| 73 | `2026-11-08` | 📝 架构方案与决议 | [页面加载抖动与滚动条闪现根因深度调研、业界方案对比与抗抖动治理方案](2026-11/page-jitter-scrollbar-flicker-root-cause-and-solution.md) | `page-jitter-scrollbar-flicker-root-cause-and-solution.md` | `Implemented` |
| 74 | `2026-11-08` | 🧩 插件与微服务 | [插件平台级基础设施通用化抽象：声明式 Ingress、双工流隧道、统一守卫与平滑排空深度调研与架构方案](2026-11/platform-level-generic-ingress-and-fallback-architecture.md) | `platform-level-generic-ingress-and-fallback-architecture.md` | `Implemented` |
| 75 | `2026-11-08` | 🧩 插件与微服务 | [宿主转发能力向插件平台级下沉与全生命周期扩展性深度调研与架构方案](2026-11/platform-level-plugin-ingress-and-tunnel-extensibility-research.md) | `platform-level-plugin-ingress-and-tunnel-extensibility-research.md` | `Implemented` |
| 76 | `2026-11-08` | 🧩 插件与微服务 | [统一插件平台自启动机制、生命周期记忆与依赖健康门禁架构方案](2026-11/plugin-auto-start-and-dependency-governance-plan.md) | `plugin-auto-start-and-dependency-governance-plan.md` | `Implemented` |
| 77 | `2026-11-08` | 🧩 插件与微服务 | [插件卡片“访问微服务网页/接口”无效根因剖析、统一 URL 解析器与 UI 模式契约治理方案](2026-11/plugin-card-gateway-url-resolution-and-ui-mode-audit-plan.md) | `plugin-card-gateway-url-resolution-and-ui-mode-audit-plan.md` | `Implemented` |
| 78 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool (LWT) 插件中心架构深度剖析、业界对标与商业价值白皮书](2026-11/plugin-center-architecture-industry-comparison-and-commercial-value.md) | `plugin-center-architecture-industry-comparison-and-commercial-value.md` | `Implemented` |
| 79 | `2026-11-08` | 🧩 插件与微服务 | [插件中心搜索输入框体验与架构治理调研与优化方案](2026-11/plugin-center-search-optimization-and-architecture-design.md) | `plugin-center-search-optimization-and-architecture-design.md` | `Implemented` |
| 80 | `2026-11-08` | 🧩 插件与微服务 | [插件中心标签过滤栏内容溢出问题调研与架构优化方案](2026-11/plugin-center-toolbar-overflow-research-and-optimization-plan.md) | `plugin-center-toolbar-overflow-research-and-optimization-plan.md` | `Implemented` |
| 81 | `2026-11-08` | 🧩 插件与微服务 | [统一插件分类体系重构与多维正交分类学调研与设计方案](2026-11/plugin-classification-and-archetype-orthogonal-taxonomy-plan.md) | `plugin-classification-and-archetype-orthogonal-taxonomy-plan.md` | `Implemented` |
| 82 | `2026-11-08` | 🧩 插件与微服务 | [插件中心代码编辑器文件树状显示与智能折叠架构设计方案](2026-11/plugin-file-tree-and-smart-folding-plan.md) | `plugin-file-tree-and-smart-folding-plan.md` | `Implemented` |
| 83 | `2026-11-08` | 🧩 插件与微服务 | [插件元数据与物理目录全量升级至最新 Schema v3.1 架构方案与深度调研报告](2026-11/plugin-full-metadata-and-directory-structure-upgrade-plan.md) | `plugin-full-metadata-and-directory-structure-upgrade-plan.md` | `Implemented` |
| 84 | `2026-11-08` | 🧩 插件与微服务 | [插件手动生命周期持久化、动态文件管理与 UI 紧凑响应式排版治理方案](2026-11/plugin-manual-lifecycle-persistence-and-dynamic-file-management-governance-plan.md) | `plugin-manual-lifecycle-persistence-and-dynamic-file-management-governance-plan.md` | `Implemented` |
| 85 | `2026-11-08` | 🧩 插件与微服务 | [插件基础设施：当前问题、可复用能力与验证清单](2026-11/plugin-platform-current-state-audit.md) | `plugin-platform-current-state-audit.md` | `Implemented` |
| 86 | `2026-11-08` | 🧩 插件与微服务 | [函数服务与脚本扩展：插件能力完善的可行性与设计](2026-11/plugin-platform-feasibility-and-design.md) | `plugin-platform-feasibility-and-design.md` | `Implemented` |
| 87 | `2026-11-08` | 🧩 插件与微服务 | [插件扩展能力：现有库、通信方式与业界方案比较](2026-11/plugin-platform-libraries-and-industry.md) | `plugin-platform-libraries-and-industry.md` | `Implemented` |
| 88 | `2026-11-08` | 🧩 插件与微服务 | [插件长时停留“探测中”根因深度剖析、自适应就绪探针方案与回归门禁设计](2026-11/plugin-probing-stuck-root-cause-and-adaptive-prober-plan.md) | `plugin-probing-stuck-root-cause-and-adaptive-prober-plan.md` | `Implemented` |
| 89 | `2026-11-08` | 🧩 插件与微服务 | [扩展中心长时 HTTP 微服务鉴权、网关安全防护与设计模式重构方案](2026-11/plugin-resident-microservice-auth-and-gateway-security-plan.md) | `plugin-resident-microservice-auth-and-gateway-security-plan.md` | `Implemented` |
| 90 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 插件纯净排查模式 (Safe Mode) 与环境变量治理方案](2026-11/plugin-safe-mode-and-environment-variable-governance-plan.md) | `plugin-safe-mode-and-environment-variable-governance-plan.md` | `Implemented` |
| 91 | `2026-11-08` | 🧩 插件与微服务 | [插件源码类型识别、在线热编译命令规范与调试架构演进方案](2026-11/plugin-source-type-and-hot-compile-specification.md) | `plugin-source-type-and-hot-compile-specification.md` | `Implemented` |
| 92 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 插件系统架构演化与技术债务清零方案](2026-11/plugin-system-architecture-evolution-and-legacy-cleanup-plan.md) | `plugin-system-architecture-evolution-and-legacy-cleanup-plan.md` | `Implemented` |
| 93 | `2026-11-08` | 🧩 插件与微服务 | [插件中心网关鉴权穿透、状态过滤置顶、脚本执行容错与降级治理研发方案](2026-11/plugin-system-auth-state-filter-and-degradation-governance-plan.md) | `plugin-system-auth-state-filter-and-degradation-governance-plan.md` | `Implemented` |
| 94 | `2026-11-08` | 🧩 插件与微服务 | [根目录 plugins 改造、示例扁平收敛与 MCP-Bridge 扩展中心集成调研与架构方案](2026-11/plugins-directory-refactoring-and-mcp-bridge-integration-research.md) | `plugins-directory-refactoring-and-mcp-bridge-integration-research.md` | `Implemented` |
| 95 | `2026-11-08` | 🧩 插件与微服务 | [插件中心长时 HTTP/TCP 服务快速换端与端口冲突检测避让方案调研](2026-11/plugins-http-service-port-switching-and-conflict-avoidance-plan.md) | `plugins-http-service-port-switching-and-conflict-avoidance-plan.md` | `Implemented` |
| 96 | `2026-11-08` | 🌐 组网与网络穿透 | [12 种主流代理协议兼容性测试套件与全景验收方案](2026-11/poly-protocol-compatibility-test-suite-and-acceptance-plan.md) | `poly-protocol-compatibility-test-suite-and-acceptance-plan.md` | `Implemented` |
| 97 | `2026-11-08` | 📝 架构方案与决议 | [多协议智能分流中继网关全生命周期开发流程与实施路线图 (Development & Implementation Workflow)](2026-11/poly-protocol-forwarder-development-and-implementation-workflow.md) | `poly-protocol-forwarder-development-and-implementation-workflow.md` | `Implemented` |
| 98 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 边缘中继网关进阶演化：常规链接导入、分流模板、自适应链式代理、垂直切片架构与透明网关落地方案](2026-11/poly-protocol-forwarder-link-import-routing-templates-adaptive-chains-and-tun-architecture.md) | `poly-protocol-forwarder-link-import-routing-templates-adaptive-chains-and-tun-architecture.md` | `Implemented` |
| 99 | `2026-11-08` | 🌐 组网与网络穿透 | [12 种主流代理协议矩阵与进阶传输层深度调研与架构设计规范](2026-11/poly-protocol-proxy-matrix-and-advanced-transport-architecture.md) | `poly-protocol-proxy-matrix-and-advanced-transport-architecture.md` | `Implemented` |
| 100 | `2026-11-08` | 📝 架构方案与决议 | [脚本扩展、ChatGPT 渠道 Worker、无 HTTP 前端扩展与指令 API Key 细粒度授权调研与设计方案](2026-11/script-extension-and-command-scoping-feasibility-and-design.md) | `script-extension-and-command-scoping-feasibility-and-design.md` | `Implemented` |
| 101 | `2026-11-08` | 🛡️ 安全鉴权与凭据 | [敏感凭据安全检测门禁与 Git 提交钩子架构设计方案](2026-11/secret-security-gate-and-git-hook-architecture-design.md) | `secret-security-gate-and-git-hook-architecture-design.md` | `Implemented` |
| 102 | `2026-11-08` | 🛡️ 安全鉴权与凭据 | [凭据防泄露门禁：多字符集自适应香农熵与脱敏规则合规校验调研与架构方案](2026-11/secret-security-gate-entropy-and-desensitization-governance-plan.md) | `secret-security-gate-entropy-and-desensitization-governance-plan.md` | `Implemented` |
| 103 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 基准现状、可行性与设计审查](2026-11/socks5-benchmark-feasibility-and-design-review.md) | `socks5-benchmark-feasibility-and-design-review.md` | `Implemented` |
| 104 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 安全代理浏览器延迟审计与回归门禁](2026-11/socks5-browser-latency-audit-and-regression-gates.md) | `socks5-browser-latency-audit-and-regression-gates.md` | `Implemented` |
| 105 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 客户端中继转发器（SOCKS5 Forwarder）与多入站多节点边缘网关架构调研与深度设计](2026-11/socks5-client-forwarder-and-multi-inbound-edge-relay-architecture.md) | `socks5-client-forwarder-and-multi-inbound-edge-relay-architecture.md` | `Implemented` |
| 106 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 伪装封装客户端生态权威盘点与多节点边缘中继网关（Edge Relay Gateway）架构深度调研](2026-11/socks5-encapsulation-clients-and-multi-node-edge-relay-research.md) | `socks5-encapsulation-clients-and-multi-node-edge-relay-research.md` | `Implemented` |
| 107 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 客户端边缘中继网关：CRUD 闭环审计、拓扑端口解绑与全生命周期治理方案](2026-11/socks5-forwarder-crud-integrity-and-topology-unbind-governance.md) | `socks5-forwarder-crud-integrity-and-topology-unbind-governance.md` | `Implemented` |
| 108 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 客户端边缘中继网关：多上游协议、链式代理、分流路由与透明网关架构调研与深度设计](2026-11/socks5-forwarder-multi-upstream-chaining-routing-tproxy-architecture.md) | `socks5-forwarder-multi-upstream-chaining-routing-tproxy-architecture.md` | `Implemented` |
| 109 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 客户端边缘中继网关：下一代现代化 Webview 拓扑控制台架构与无模态交互设计方案](2026-11/socks5-forwarder-nextgen-webview-topology-and-modeless-ux-design.md) | `socks5-forwarder-nextgen-webview-topology-and-modeless-ux-design.md` | `Implemented` |
| 110 | `2026-11-08` | 📝 架构方案与决议 | [SOCKS5 边缘中继网关节点“离线”根因排查与两级分层健康探针架构方案](2026-11/socks5-forwarder-node-offline-root-cause-and-two-tier-health-probe-architecture.md) | `socks5-forwarder-node-offline-root-cause-and-two-tier-health-probe-architecture.md` | `Implemented` |
| 111 | `2026-11-08` | 🌐 组网与网络穿透 | [浏览器同时打开多个标签时的代理卡顿调查](2026-11/socks5-multitab-burst-stall-investigation.md) | `socks5-multitab-burst-stall-investigation.md` | `Implemented` |
| 112 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 代理高并发容量阶梯与运行时观测方案](2026-11/socks5-proxy-capacity-ladder-and-runtime-observability-design.md) | `socks5-proxy-capacity-ladder-and-runtime-observability-design.md` | `Implemented` |
| 113 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 代理全链路验证与主流优化方案复核](2026-11/socks5-proxy-full-flow-validation-and-mainstream-optimization-review.md) | `socks5-proxy-full-flow-validation-and-mainstream-optimization-review.md` | `Implemented` |
| 114 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 安全代理优化路线与可行性评估](2026-11/socks5-proxy-optimization-roadmap-and-feasibility.md) | `socks5-proxy-optimization-roadmap-and-feasibility.md` | `Implemented` |
| 115 | `2026-11-08` | 📦 备份与存储同步 | [SOCKS5 安全代理微服务端口无法访问与 Webview 模态框端口脱节深度调研与治理方案](2026-11/socks5-proxy-port-inaccessibility-and-webview-port-desync-analysis-plan.md) | `socks5-proxy-port-inaccessibility-and-webview-port-desync-analysis-plan.md` | `Implemented` |
| 116 | `2026-11-08` | 🌐 组网与网络穿透 | [SOCKS5 代理微服务 `socks5://10.126.1.70:18080` 无法访问根因调研与解决方案报告](2026-11/socks5-service-10-126-1-70-inaccessibility-analysis-and-solution.md) | `socks5-service-10-126-1-70-inaccessibility-analysis-and-solution.md` | `Implemented` |
| 117 | `2026-11-08` | 📊 审计与复盘报告 | [SOCKS5h 协议支持、网页访问延迟根因剖析与热更新优化调研报告](2026-11/socks5h-remote-dns-latency-optimization-and-hotupdate-research.md) | `socks5h-remote-dns-latency-optimization-and-hotupdate-research.md` | `Implemented` |
| 118 | `2026-11-08` | 📝 架构方案与决议 | [SSH 访问安全守卫宿主穿透管理、管理对象动态切换与架构重构调研方案](2026-11/ssh-guard-host-piercing-and-target-switching-plan.md) | `ssh-guard-host-piercing-and-target-switching-plan.md` | `Implemented` |
| 119 | `2026-11-08` | 📝 架构方案与决议 | [无兼容包袱的终极纯粹网关架构：控制面/数据面解耦、多维路由矩阵与 UDS 零端口流式内核调研](2026-11/ultimate-pure-gateway-architecture-and-multi-dimensional-routing-research.md) | `ultimate-pure-gateway-architecture-and-multi-dimensional-routing-research.md` | `Implemented` |
| 120 | `2026-11-08` | 🧩 插件与微服务 | [统一插件平台彻底重构与纯粹服务运维架构设计方案](2026-11/unification-of-plugins-and-workers-and-pure-services-architecture-design.md) | `unification-of-plugins-and-workers-and-pure-services-architecture-design.md` | `Implemented` |
| 121 | `2026-11-08` | 🧩 插件与微服务 | [统一插件运行时体系与三套 UI 注入模式演进调研与方案设计](2026-11/unified-plugin-runtime-and-three-ui-modes-research-and-design.md) | `unified-plugin-runtime-and-three-ui-modes-research-and-design.md` | `Implemented` |
| 122 | `2026-11-08` | 🧩 插件与微服务 | [通用插件多维本质抽象、动态服务发现与完全语言解耦架构规划](2026-11/universal-plugin-architecture-matrix-and-dynamic-discovery-plan.md) | `universal-plugin-architecture-matrix-and-dynamic-discovery-plan.md` | `Implemented` |
| 123 | `2026-11-08` | 🧩 插件与微服务 | [通用插件多维分类、显式热编译契约与元数据架构去隐式化方案](2026-11/universal-plugin-metadata-build-and-runtime-matrix-specification.md) | `universal-plugin-metadata-build-and-runtime-matrix-specification.md` | `Implemented` |
| 124 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 插件通用持久化路径重构与现有插件全量升级方案](2026-11/universal-plugin-persistence-and-folder-refactoring-plan.md) | `universal-plugin-persistence-and-folder-refactoring-plan.md` | `Implemented` |
| 125 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 通用跨语言插件持久化架构与存储状态机设计方案](2026-11/universal-plugin-persistence-architecture-and-state-machine-framework.md) | `universal-plugin-persistence-architecture-and-state-machine-framework.md` | `Implemented` |
| 126 | `2026-11-08` | 🧩 插件与微服务 | [LinuxWebTool 统一插件生态、正交分类拓扑与全栈交互完整优化方案 (Master Plan)](2026-11/universal-plugin-v3-full-stack-evolution-master-plan.md) | `universal-plugin-v3-full-stack-evolution-master-plan.md` | `Implemented` |
| 127 | `2026-11-08` | 📝 架构方案与决议 | [上游节点与入站隧道延迟分析与高并发零开销路由架构设计](2026-11/upstream-ingress-latency-diagnosis-and-optimization-architecture.md) | `upstream-ingress-latency-diagnosis-and-optimization-architecture.md` | `Implemented` |
| 128 | `2026-11-08` | 📝 架构方案与决议 | [VLESS 与 XTLS-Vision 协议兼容排障与深度适配治理方案](2026-11/vless-xtls-vision-compatibility-and-protocol-governance.md) | `vless-xtls-vision-compatibility-and-protocol-governance.md` | `Implemented` |
| 129 | `2026-11-08` | 📝 架构方案与决议 | [Wasm 与多语言脚本微函数架构对比、可行性调研与系统演进方案](2026-11/wasm-vs-script-microfunctions-architecture-and-feasibility-study.md) | `wasm-vs-script-microfunctions-architecture-and-feasibility-study.md` | `Implemented` |
| 130 | `2026-11-08` | 🛡️ 安全鉴权与凭据 | [WebCodex Tunnel：连接、凭据与传输核心代码](2026-11/webcodex-tunnel-connection.md) | `webcodex-tunnel-connection.md` | `Implemented` |
| 131 | `2026-11-08` | 🌐 组网与网络穿透 | [WebCodex 通过 OpenAI Secure MCP Tunnel 向 ChatGPT 提供服务：调研索引](2026-11/webcodex-tunnel-research.md) | `webcodex-tunnel-research.md` | `Implemented` |
| 132 | `2026-11-08` | 🌐 组网与网络穿透 | [WebCodex Tunnel：工具发现、执行与 LinuxWebTool 接入映射](2026-11/webcodex-tunnel-tools.md) | `webcodex-tunnel-tools.md` | `Implemented` |
| 133 | `2026-11-08` | 📊 审计与复盘报告 | [纯粹 Worker 架构现状差距审计与解耦实施计划](2026-11/worker-pure-architecture-gap-audit-and-plan.md) | `worker-pure-architecture-gap-audit-and-plan.md` | `Implemented` |
| 134 | `2026-11-08` | 🧩 插件与微服务 | [业界主流插件架构对比与第三方库选型评估](2026-11/worker-pure-architecture-industry-and-libraries.md) | `worker-pure-architecture-industry-and-libraries.md` | `Implemented` |
| 135 | `2026-11-08` | 📝 架构方案与决议 | [纯粹 Worker 扩展服务架构调研与总体设计方案](2026-11/worker-pure-architecture-research-and-design.md) | `worker-pure-architecture-research-and-design.md` | `Implemented` |
| 136 | `2026-11-05` | 🌐 组网与网络穿透 | [SOCKS5 代理多连接并发与容量治理评审](2026-11/socks5-proxy-concurrency-and-capacity-review.md) | `socks5-proxy-concurrency-and-capacity-review.md` | `Implemented` |
| 137 | `2026-11-02` | 📊 审计与复盘报告 | [开发环境验证流程深度优化与容器内在途热重载调研报告](2026-11/dev-workflow-optimization-and-in-container-fast-reload.md) | `dev-workflow-optimization-and-in-container-fast-reload.md` | `Implemented` |
| 138 | `2026-11-02` | 📝 架构方案与决议 | [MCP Bridge 控制台刷新异常与添加服务点击无效根因分析、业界方案调研与加固落地方案](2026-11/mcp-bridge-web-console-path-resolution-and-modal-dialog-plan.md) | `mcp-bridge-web-console-path-resolution-and-modal-dialog-plan.md` | `Implemented` |
| 139 | `2026-11-02` | 🌐 组网与网络穿透 | [SOCKS5 代理网段白名单控制与域名通配符/正则过滤架构设计方案](2026-11/socks5-proxy-subnet-whitelist-and-domain-filter-design.md) | `socks5-proxy-subnet-whitelist-and-domain-filter-design.md` | `Implemented` |

<a id="202610"></a>
### 📅 2026-10 时间线汇总 (80 篇)

| 序号 | 制定日期 | 分类 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :--- | :--- | :--- | :---: |
| 1 | `2026-10-27` | 📦 备份与存储同步 | [备份与同步任务“源/目标目录与关联挂载异常自动暂停”可行性调研与架构方案](2026-10/backup-sync-mount-health-guard.md) | `backup-sync-mount-health-guard.md` | `Implemented` |
| 2 | `2026-10-26` | 🛡️ 安全鉴权与凭据 | [API Key 终端与文件夹访问路径防护可行性调研与设计方案](2026-10/apikey-path-scope-and-isolation-feasibility-plan.md) | `apikey-path-scope-and-isolation-feasibility-plan.md` | `Implemented` |
| 3 | `2026-10-11` | 📦 备份与存储同步 | [文件夹备份与同步功能可行性调研与预览方案](2026-10/folder-backup-and-sync-feasibility-and-preview-plan.md) | `folder-backup-and-sync-feasibility-and-preview-plan.md` | `Implemented` |
| 4 | `2026-10-11` | 📝 架构方案与决议 | [磁盘挂载管理：新增与完善 SSH (SFTP) 后端挂载功能方案](2026-10/ssh-backend-mount-plan.md) | `ssh-backend-mount-plan.md` | `Implemented` |
| 5 | `2026-10-09` | 🛡️ 安全鉴权与凭据 | [认证安全与权限控制全流程整改及防护门禁建设方案](2026-10/auth-and-permission-remediation-plan.md) | `auth-and-permission-remediation-plan.md` | `Implemented` |
| 6 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [APIKey 授权体系与细粒度权限矩阵方案](2026-10/apikey-authorization-and-permission-matrix-plan.md) | `apikey-authorization-and-permission-matrix-plan.md` | `Implemented` |
| 7 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [ApiKey 授权、终端页面与转码流程全链路审计与优化实施方案](2026-10/apikey-terminal-transcode-audit-and-optimization-plan.md) | `apikey-terminal-transcode-audit-and-optimization-plan.md` | `Implemented` |
| 8 | `2026-10-08` | 📝 架构方案与决议 | [`artifacts` 目录含义、作用与命名规范评估](2026-10/artifacts-directory-guide-and-naming-decision.md) | `artifacts-directory-guide-and-naming-decision.md` | `Implemented` |
| 9 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [用户登录认证与权限控制全流程代码逻辑深度审计与性能调研报告](2026-10/auth-and-permission-audit-report.md) | `auth-and-permission-audit-report.md` | `Implemented` |
| 10 | `2026-10-08` | 📦 备份与存储同步 | [备份与同步排除规则智能探测与推荐引擎设计方案 (Backup-Sync Exclude Detection & Recommendation Plan)](2026-10/backup-sync-exclude-detection-and-recommendation-plan.md) | `backup-sync-exclude-detection-and-recommendation-plan.md` | `Implemented` |
| 11 | `2026-10-08` | 📦 备份与存储同步 | [备份与同步任务实时执行进度（Execution Progress）调研与技术方案](2026-10/backup-sync-execution-progress-tracking-plan.md) | `backup-sync-execution-progress-tracking-plan.md` | `Implemented` |
| 12 | `2026-10-08` | 📦 备份与存储同步 | [备份同步任务无法完成深度根因分析与执行记录自动清理设计方案](2026-10/backup-sync-execution-records-and-pruning-design-plan.md) | `backup-sync-execution-records-and-pruning-design-plan.md` | `Implemented` |
| 13 | `2026-10-08` | 📦 备份与存储同步 | [备份与同步模块：路径快捷跳转文件管理页面方案](2026-10/backup-sync-file-manager-quick-jump-plan.md) | `backup-sync-file-manager-quick-jump-plan.md` | `Implemented` |
| 14 | `2026-10-08` | 📦 备份与存储同步 | [备份同步文件夹路径点选排除与交互体验升级设计方案 (Folder Picker & Visual Exclusion Plan)](2026-10/backup-sync-folder-picker-and-visual-exclusion-plan.md) | `backup-sync-folder-picker-and-visual-exclusion-plan.md` | `Implemented` |
| 15 | `2026-10-08` | 📦 备份与存储同步 | [备份与同步模块：实时进度交互美化与移动端响应式架构优化方案](2026-10/backup-sync-mobile-and-progress-ui-optimization-plan.md) | `backup-sync-mobile-and-progress-ui-optimization-plan.md` | `Implemented` |
| 16 | `2026-10-08` | 📦 备份与存储同步 | [备份同步智能排除规则与可视化交互全景开发方案 (Backup-Sync Smart Exclusions Master Implementation Plan)](2026-10/backup-sync-smart-exclusions-master-implementation-plan.md) | `backup-sync-smart-exclusions-master-implementation-plan.md` | `Implemented` |
| 17 | `2026-10-08` | 📦 备份与存储同步 | [备份同步执行中假死与 CIFS 深度韧性优化方案 (Backup-Sync Stuck-Running & CIFS Resilience Plan)](2026-10/backup-sync-stuck-running-and-cifs-resilience-plan.md) | `backup-sync-stuck-running-and-cifs-resilience-plan.md` | `Implemented` |
| 18 | `2026-10-08` | 📦 备份与存储同步 | [定时备份任务失败“Could not find file detect-libc”根因调研与全链路符号链接及容错防御方案](2026-10/backup-sync-symlink-and-vanished-files-fix-plan.md) | `backup-sync-symlink-and-vanished-files-fix-plan.md` | `Implemented` |
| 19 | `2026-10-08` | 📦 备份与存储同步 | [备份同步排除规则可视化操作与零黑盒逻辑深度设计方案 (Backup-Sync Visual Exclude Rules & Zero-Hidden-Logic Plan)](2026-10/backup-sync-visual-exclude-rules-and-zero-hidden-logic-plan.md) | `backup-sync-visual-exclude-rules-and-zero-hidden-logic-plan.md` | `Implemented` |
| 20 | `2026-10-08` | 🚀 容器与部署架构 | [GitHub Actions Docker 多架构构建矩阵收敛优化 (方案 1)](2026-10/docker-multi-arch-matrix-optimization-plan.md) | `docker-multi-arch-matrix-optimization-plan.md` | `Implemented` |
| 21 | `2026-10-08` | 🌐 组网与网络穿透 | [EasyTier 容器局域网路由冲突：原因与处理方案](2026-10/easytier-container-lan-route-conflict-analysis.md) | `easytier-container-lan-route-conflict-analysis.md` | `Implemented` |
| 22 | `2026-10-08` | 🌐 组网与网络穿透 | [EasyTier 节点编辑页面可扩展选项调研](2026-10/easytier-editor-options-research.md) | `easytier-editor-options-research.md` | `Implemented` |
| 23 | `2026-10-08` | 🌐 组网与网络穿透 | [EasyTier 多节点故障隔离与防回归方案](2026-10/easytier-multi-node-safety-plan.md) | `easytier-multi-node-safety-plan.md` | `Implemented` |
| 24 | `2026-10-08` | 🌐 组网与网络穿透 | [EasyTier 虚拟组网节点管理与双层热更新技术方案](2026-10/easytier-node-management-and-hot-update-plan.md) | `easytier-node-management-and-hot-update-plan.md` | `Implemented` |
| 25 | `2026-10-08` | 🌐 组网与网络穿透 | [EasyTier 静态 IP 被 DHCP 覆盖：原因、修复与验证](2026-10/easytier-static-ip-dhcp-analysis-and-fix.md) | `easytier-static-ip-dhcp-analysis-and-fix.md` | `Implemented` |
| 26 | `2026-10-08` | 📝 架构方案与决议 | [扩展服务 (Extensions) 与 SaaS 函数微服务 (Workers & Daemons) 全功能真实演练、架构头脑风暴与深度自愈方案](2026-10/extensions-and-workers-comprehensive-verification-and-enhancement-plan.md) | `extensions-and-workers-comprehensive-verification-and-enhancement-plan.md` | `Implemented` |
| 27 | `2026-10-08` | 📝 架构方案与决议 | [动态脚本扩展与函数微服务：ZIP 导入、在线代码编辑与 MCP 运维开发工具集方案调研与设计](2026-10/extensions-and-workers-zip-online-edit-and-mcp-tools-plan.md) | `extensions-and-workers-zip-online-edit-and-mcp-tools-plan.md` | `Implemented` |
| 28 | `2026-10-08` | 📝 架构方案与决议 | [文件管理页面下载功能与文件夹打包下载调研及实施方案](2026-10/file-manager-download-and-folder-zip-plan.md) | `file-manager-download-and-folder-zip-plan.md` | `Implemented` |
| 29 | `2026-10-08` | 📝 架构方案与决议 | [前端空白页与主题样式问题调研、解决方案与端到端测试方案](2026-10/frontend-blank-page-and-cyber-theme-fix-plan.md) | `frontend-blank-page-and-cyber-theme-fix-plan.md` | `Implemented` |
| 30 | `2026-10-08` | 📝 架构方案与决议 | [前端原生 ESM 导入导出完整性故障分析与同类门禁防御方案](2026-10/frontend-esm-import-integrity-and-gate-plan.md) | `frontend-esm-import-integrity-and-gate-plan.md` | `Implemented` |
| 31 | `2026-10-08` | 📊 审计与复盘报告 | [前端页面内容溢出剪切与对齐居中全面审计与优化方案](2026-10/frontend-layout-overflow-alignment-audit-and-optimization-plan.md) | `frontend-layout-overflow-alignment-audit-and-optimization-plan.md` | `Implemented` |
| 32 | `2026-10-08` | 🌐 组网与网络穿透 | [ProxyByCF FRP 内网穿透客户端：多线路配置、302 内网重定向代理与 HTTP/SOCKS 代理支持调研及方案](2026-10/frp-multi-line-and-302-redirect-proxy-plan.md) | `frp-multi-line-and-302-redirect-proxy-plan.md` | `Implemented` |
| 33 | `2026-10-08` | 🌐 组网与网络穿透 | [基于 ProxyByCF 工业级标准的 FRP 反向穿透优化可行性调研与预览方案](2026-10/frp-proxybycf-optimization-feasibility-and-preview-plan.md) | `frp-proxybycf-optimization-feasibility-and-preview-plan.md` | `Implemented` |
| 34 | `2026-10-08` | 🌐 组网与网络穿透 | [ProxyByCF FRP 客户端集成技术调研与实施方案](2026-10/frp-tunnel-client-integration-plan.md) | `frp-tunnel-client-integration-plan.md` | `Implemented` |
| 35 | `2026-10-08` | 🌐 组网与网络穿透 | [家庭网关与 FRP 反向穿透全流程代码审计与优化实施方案](2026-10/gateway-and-frp-tunnel-full-flow-audit-and-optimization-plan.md) | `gateway-and-frp-tunnel-full-flow-audit-and-optimization-plan.md` | `Implemented` |
| 36 | `2026-10-08` | 📝 架构方案与决议 | [家庭网关 L4 端口转发失效根本原因深度调研与系统级重构方案](2026-10/gateway-l4-forward-failure-root-cause-and-remediation-plan.md) | `gateway-l4-forward-failure-root-cause-and-remediation-plan.md` | `Implemented` |
| 37 | `2026-10-08` | 🚀 容器与部署架构 | [GitHub Actions CI/CD 分工解耦与双重构建优化方案 (方案 1)](2026-10/github-actions-ci-cd-optimization-plan.md) | `github-actions-ci-cd-optimization-plan.md` | `Implemented` |
| 38 | `2026-10-08` | 📝 架构方案与决议 | [GitHub 冒烟性能指标实施方案](2026-10/github-smoke-performance-plan.md) | `github-smoke-performance-plan.md` | `Implemented` |
| 39 | `2026-10-08` | 📝 架构方案与决议 | [GitHub 白盒、黑盒与发布冒烟实施方案](2026-10/github-whitebox-blackbox-smoke-plan.md) | `github-whitebox-blackbox-smoke-plan.md` | `Implemented` |
| 40 | `2026-10-08` | 📝 架构方案与决议 | [JWT 有效期延长与滑动窗口自动续期方案](2026-10/jwt-expiration-and-auto-renewal-plan.md) | `jwt-expiration-and-auto-renewal-plan.md` | `Implemented` |
| 41 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [MCP 二级工具权限勾选失效根因调研与 SSOT 契约统一方案](2026-10/mcp-catalog-provider-alias-and-permission-fix-plan.md) | `mcp-catalog-provider-alias-and-permission-fix-plan.md` | `Implemented` |
| 42 | `2026-10-08` | 📝 架构方案与决议 | [MCP 服务与核心功能工具封装技术调研与实施方案](2026-10/mcp-server-and-tool-encapsulation-plan.md) | `mcp-server-and-tool-encapsulation-plan.md` | `Implemented` |
| 43 | `2026-10-08` | 📝 架构方案与决议 | [MCP 服务引擎架构重构与模块化优化方案](2026-10/mcp-server-engine-architecture-refactoring-and-optimization-plan.md) | `mcp-server-engine-architecture-refactoring-and-optimization-plan.md` | `Implemented` |
| 44 | `2026-10-08` | 📝 架构方案与决议 | [MCP 工作流/指令/定时任务整合与二级树状权限架构调研与落地方案](2026-10/mcp-two-level-tree-and-workflow-command-dispatch-plan.md) | `mcp-two-level-tree-and-workflow-command-dispatch-plan.md` | `Implemented` |
| 45 | `2026-10-08` | 📝 架构方案与决议 | [挂载恢复失败提示“上次文件系统探针尚未退出”根因剖析与纵深防御方案](2026-10/mount-probe-guard-and-d-state-recovery-plan.md) | `mount-probe-guard-and-d-state-recovery-plan.md` | `Implemented` |
| 46 | `2026-10-08` | 📝 架构方案与决议 | [挂载状态机实施方案](2026-10/mount-state-machine-plan.md) | `mount-state-machine-plan.md` | `Implemented` |
| 47 | `2026-10-08` | 📝 架构方案与决议 | [导航栏下拉子菜单移动至主菜单项异常消失问题调研与修复方案](2026-10/nav-dropdown-submenu-hover-disappear-analysis-and-fix.md) | `nav-dropdown-submenu-hover-disappear-analysis-and-fix.md` | `Implemented` |
| 48 | `2026-10-08` | 📝 架构方案与决议 | [特权容器宿主穿透对象模型与服务平台单文件快速模式深度调研与架构方案](2026-10/privileged-container-host-target-and-quick-workers-plan.md) | `privileged-container-host-target-and-quick-workers-plan.md` | `Implemented` |
| 49 | `2026-10-08` | 🌐 组网与网络穿透 | [借鉴 ProxyYARP 代理库与家庭网关融合技术方案](2026-10/proxyyarp-integration-and-home-gateway-plan.md) | `proxyyarp-integration-and-home-gateway-plan.md` | `Implemented` |
| 50 | `2026-10-08` | 📝 架构方案与决议 | [Python 驱动的 Linux Top 资源监控与进程治理扩展方案 (Process Top Monitor)](2026-10/python-top-process-management-extension-feasibility-plan.md) | `python-top-process-management-extension-feasibility-plan.md` | `Implemented` |
| 51 | `2026-10-08` | 📝 架构方案与决议 | [SFTP / S3 挂载配置](2026-10/rclone-sftp-s3.md) | `rclone-sftp-s3.md` | `Implemented` |
| 52 | `2026-10-08` | 📊 审计与复盘报告 | [剩余核心模块全流程审计与优化方案](2026-10/remaining-modules-audit-and-optimization-plan.md) | `remaining-modules-audit-and-optimization-plan.md` | `Implemented` |
| 53 | `2026-10-08` | 📝 架构方案与决议 | [指令管理与定时任务跨主机调度（本地与远程主机）架构调研与落地方案](2026-10/remote-host-command-and-schedule-dispatch-plan.md) | `remote-host-command-and-schedule-dispatch-plan.md` | `Implemented` |
| 54 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [远程主机文件管理下载出现“需要授权”问题根因剖析与纵深防御方案](2026-10/remote-host-file-download-authorization-fix.md) | `remote-host-file-download-authorization-fix.md` | `Implemented` |
| 55 | `2026-10-08` | 📝 架构方案与决议 | [远程主机文件管理性能极致优化与全功能（上传/下载/复制/移动/编辑）架构落地方案](2026-10/remote-host-file-management-speedup-and-crud-plan.md) | `remote-host-file-management-speedup-and-crud-plan.md` | `Implemented` |
| 56 | `2026-10-08` | 💻 终端与 PTY 引擎 | [远程主机多协议纳管、Web 终端与文件系统集成方案](2026-10/remote-host-management-and-multi-protocol-feasibility-plan.md) | `remote-host-management-and-multi-protocol-feasibility-plan.md` | `Implemented` |
| 57 | `2026-10-08` | 📝 架构方案与决议 | [远程主机 SFTP 目录无限转圈挂起与密钥测试失败根因剖析及系统化治理方案](2026-10/remote-host-sftp-hang-and-key-test-failure-analysis-and-fix.md) | `remote-host-sftp-hang-and-key-test-failure-analysis-and-fix.md` | `Implemented` |
| 58 | `2026-10-08` | 📝 架构方案与决议 | [远程主机单指令执行异常与 SFTP 超时挂死深度调研与双轨文件系统落地方案](2026-10/remote-host-sftp-stream-poisoning-and-exec-fallback-plan.md) | `remote-host-sftp-stream-poisoning-and-exec-fallback-plan.md` | `Implemented` |
| 59 | `2026-10-08` | 📝 架构方案与决议 | [SaaS 函数式微服务与长时守护平台 (LWT Workers & Daemons) 独立架构与落地设计方案](2026-10/saas-functions-workers-and-daemons-platform-plan.md) | `saas-functions-workers-and-daemons-platform-plan.md` | `Implemented` |
| 60 | `2026-10-08` | 📝 架构方案与决议 | [系统服务 API 签名契约门禁与 .NET Worker 启动自愈方案](2026-10/services-api-signature-and-dotnet-worker-startup-fix-plan.md) | `services-api-signature-and-dotnet-worker-startup-fix-plan.md` | `Implemented` |
| 61 | `2026-10-08` | 📝 架构方案与决议 | [动态服务管理扩展进阶方案：SSH 访问卫士、Caddy 管理、环境自检引导与 GitHub 多模块导入](2026-10/services-dynamic-extensions-ssh-caddy-and-github-import-plan.md) | `services-dynamic-extensions-ssh-caddy-and-github-import-plan.md` | `Implemented` |
| 62 | `2026-10-08` | 📝 架构方案与决议 | [SMB 卸载阻塞与登录请求长期等待：调查和修复](2026-10/smb-d-state-login-hang-analysis-and-fix.md) | `smb-d-state-login-hang-analysis-and-fix.md` | `Implemented` |
| 63 | `2026-10-08` | 📝 架构方案与决议 | [SMB 生命周期与故障诊断方案](2026-10/smb-diagnostics-plan.md) | `smb-diagnostics-plan.md` | `Implemented` |
| 64 | `2026-10-08` | 📝 架构方案与决议 | [SMB 子目录读取阻塞隔离](2026-10/smb-directory-blocking.md) | `smb-directory-blocking.md` | `Implemented` |
| 65 | `2026-10-08` | 📝 架构方案与决议 | [SMB 实际状态复核与访问保护实施方案](2026-10/smb-recovery-reconciliation-plan.md) | `smb-recovery-reconciliation-plan.md` | `Implemented` |
| 66 | `2026-10-08` | 🚀 容器与部署架构 | [系统与服务管理中枢 (Services Hub) 可行性与深度架构方案](2026-10/system-services-and-docker-management-hub-feasibility-plan.md) | `system-services-and-docker-management-hub-feasibility-plan.md` | `Implemented` |
| 67 | `2026-10-08` | 📝 架构方案与决议 | [Systemd 系统服务等长列表自动分页调研与架构方案](2026-10/systemd-and-long-lists-auto-pagination-plan.md) | `systemd-and-long-lists-auto-pagination-plan.md` | `Implemented` |
| 68 | `2026-10-08` | 📝 架构方案与决议 | [Systemd 服务智能分类（系统服务与用户服务区分）调研与架构方案](2026-10/systemd-services-classification-and-categorization-plan.md) | `systemd-services-classification-and-categorization-plan.md` | `Implemented` |
| 69 | `2026-10-08` | 💻 终端与 PTY 引擎 | [终端清屏后顶部残留](2026-10/terminal-clear-screen-residue.md) | `terminal-clear-screen-residue.md` | `Implemented` |
| 70 | `2026-10-08` | 💻 终端与 PTY 引擎 | [终端连接、指令与定时任务执行问题分析及修复方案](2026-10/terminal-command-schedule-execution-fix-plan.md) | `terminal-command-schedule-execution-fix-plan.md` | `Implemented` |
| 71 | `2026-10-08` | 💻 终端与 PTY 引擎 | [终端与文件目录联动、掉线后继续运行方案](2026-10/terminal-directory-and-detached-session-plan.md) | `terminal-directory-and-detached-session-plan.md` | `Implemented` |
| 72 | `2026-10-08` | 💻 终端与 PTY 引擎 | [终端方案实施记录](2026-10/terminal-implementation-progress.md) | `terminal-implementation-progress.md` | `Implemented` |
| 73 | `2026-10-08` | 💻 终端与 PTY 引擎 | [tmux 备用终端方案](2026-10/terminal-tmux-fallback-option.md) | `terminal-tmux-fallback-option.md` | `Implemented` |
| 74 | `2026-10-08` | 📝 架构方案与决议 | [LinuxWebTool 软件测试框架与性能追踪指标建设方案](2026-10/testing-and-observability-governance-plan.md) | `testing-and-observability-governance-plan.md` | `Implemented` |
| 75 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [统一业务模块与 MCP 二级工具树状授权方案设计与可行性调研](2026-10/unified-permission-tree-matrix-plan.md) | `unified-permission-tree-matrix-plan.md` | `Implemented` |
| 76 | `2026-10-08` | 🛡️ 安全鉴权与凭据 | [扩展服务与函数服务混合单仓、MCP/APIKey调用、工作流编排与在线热更新架构方案](2026-10/unified-repo-mcp-workflow-and-hotupdate-plan.md) | `unified-repo-mcp-workflow-and-hotupdate-plan.md` | `Implemented` |
| 77 | `2026-10-08` | 📝 架构方案与决议 | [WebDAV 挂载支持方案](2026-10/webdav-mount-plan.md) | `webdav-mount-plan.md` | `Implemented` |
| 78 | `2026-10-08` | 📝 架构方案与决议 | [WebLinuxTool 全功能演进综合架构预览方案](2026-10/weblinuxtool-v2-comprehensive-architecture-preview.md) | `weblinuxtool-v2-comprehensive-architecture-preview.md` | `Implemented` |
| 79 | `2026-10-08` | 💻 终端与 PTY 引擎 | [Windows 终端与文件目录联动实施方案](2026-10/windows-terminal-and-file-integration-plan.md) | `windows-terminal-and-file-integration-plan.md` | `Implemented` |
| 80 | `2026-10-07` | 🌐 组网与网络穿透 | [EasyTier 组网故障排查、权限模型与工程避坑实践指南](2026-10/easytier-troubleshooting-and-lessons-learned.md) | `easytier-troubleshooting-and-lessons-learned.md` | `Implemented` |

<a id="202609"></a>
### 📅 2026-09 时间线汇总 (1 篇)

| 序号 | 制定日期 | 分类 | 文档标题 | 归档文件路径 | 状态 |
| :---: | :---: | :--- | :--- | :--- | :---: |
| 1 | `2026-09-08` | 📝 架构方案与决议 | [挂载健康检测与自动恢复原则](2026-09/mount-health-principles.md) | `mount-health-principles.md` | `Implemented` |

---

> *本索引由 `scripts/docs-archiver.cjs` 自动生成，双重视图构建时间：2026-11-11*。
