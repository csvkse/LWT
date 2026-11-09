# `artifacts` 目录含义、作用与命名规范评估

## 结论与决策

**最终决策：保持现有 `artifacts` 文件夹名称不变，不改为 `.artifacts`。**

原因总结：
1. **符合 .NET 与 GitHub Actions 社区主流规范**：.NET 8+ 官方统一构建输出布局（`UseArtifactsOutput`）及 `dotnet` 官方仓库均默认使用 `artifacts`（不带点）。
2. **工具链零额外负担**：GitHub Actions 官方插件 `actions/upload-artifact@v4` 和 Release 发布插件 `softprops/action-gh-release@v2` 默认忽略以 `.` 开头的隐藏目录；保持 `artifacts` 可避免所有上传步骤额外维护 `include-hidden-files: true` 或通配符漏传风险。
3. **本地可随时清理**：该目录已被 Git 和 Docker 忽略，仅在运行冒烟测试或带报告输出的测试命令时自动生成，平时在本地可直接删除，不影响日常编译与运行。

---

## 一、`artifacts` 的含义与行业背景

在软件工程、CI/CD（持续集成/持续交付）以及 .NET 生态中，**`artifacts`** 通常译为**“构建产物 / 构件 / 归档证据”**。
它的核心定义是：**由源代码通过编译、测试、打包或冒烟运行后生成的“副产品”，而不是源代码本身。**

1. **GitHub Actions 官方概念**：GitHub Actions 中跨 Job 传递或供开发者下载的测试报告、日志、构建包统称为 **Workflow Artifacts**（配套官方插件 `actions/upload-artifact` 与 `actions/download-artifact`）。将本地待上传的汇总目录命名为 `artifacts/` 是最直观的对应方式。
2. **.NET 8+ 官方构建布局**：.NET SDK 引入的 Artifacts Output Layout 默认在仓库根目录使用 `artifacts/` 统一收纳构建产物，官方 `.gitignore` 模板也默认内置 `[Aa]rtifacts/`。
3. **隔离与清理便利性**：将所有测试报告、冒烟日志、性能快照收敛在单一顶层目录下，只需在 `.gitignore` 和 `.dockerignore` 中配置一行 `artifacts/`，即可防止测试残留或临时凭据被误提交至代码库或混入 Docker 镜像构建上下文。

---

## 二、`artifacts` 在本项目中的具体作用

在本仓库中，`artifacts/` 是**被 Git 忽略的临时输出与测试证据汇总目录**，各子目录职责如下：

| 子目录路径 | 产生来源 | 存放内容与用途 |
| :--- | :--- | :--- |
| `artifacts/architecture`<br>`artifacts/unittests`<br>`artifacts/integration` | `.github/workflows/ci.yml` 中的 `dotnet test` | 存放架构测试、白盒单元测试、API 集成测试的 `.trx` 结果报告及 `XPlat Code Coverage` 覆盖率文件，供 CI 汇总上传为 `whitebox-reports`。 |
| `artifacts/resilience` | `.github/workflows/resilience.yml` | 存放并发与故障注入回归测试（10 轮循环）生成的 `round-*.trx` 报告。 |
| `artifacts/smoke` | `scripts/smoke-aot.ps1` & `scripts/smoke-http.mjs` | 存放 Docker Native AOT 容器黑盒冒烟证据：<br>• `container.log`（脱敏后的容器标准输出日志）<br>• `performance.json`（启动就绪耗时与核心接口 P50/P95/P99 延迟）<br>• `resources.json`（容器运行前后 CPU/内存/PIDs 快照） |
| `artifacts/desktop-smoke` | `scripts/smoke-published.ps1` | 存放桌面端自包含发布包冒烟测试证据（每次运行生成独立 GUID 子目录）：<br>• `stdout.log` / `stderr.log`（脱敏后的进程日志）<br>• `performance.json` / `resources.json`（性能与进程资源快照）<br>• `data-*`（隔离测试用临时数据目录，仅留存本地，CI 上传白名单已排除） |
| `artifacts`（Release 阶段） | `.github/workflows/desktop-release.yml` | 在 `release` 作业中临时汇总下载各平台构建好的压缩包（`linuxwebtool-*.tar.gz` / `.zip`），供 `softprops/action-gh-release@v2` 一次性挂载到 GitHub Release。 |

---

## 三、`artifacts` vs `.artifacts` 对比评估

| 评估维度 | `artifacts`（当前采用） | `.artifacts`（隐藏目录风格） |
| :--- | :--- | :--- |
| **CI 上传兼容性** | **原生支持**。`actions/upload-artifact@v4` 与 `action-gh-release@v2` 默认直接扫描匹配。 | **默认被忽略**。`actions/upload-artifact@v4` 默认 `include-hidden-files: false`，未显式开启时会报 `No files were found` 并跳过上传；Release glob 匹配也默认忽略点号目录。 |
| **本地排查体验** | 冒烟或测试失败后，在资源管理器或终端 `ls` 中可直接看到并打开日志与 JSON 报告。 | Linux/macOS 下 `ls` 默认隐藏，Windows 未开启“显示隐藏的项目”时不可见，增加排查路径成本。 |
| **根目录视觉整洁度** | 作为普通文件夹显示（在 IDE 中因 `.gitignore` 呈灰色显示）。 | 与 `.git`、`.github`、`.vs` 排列在一起，视觉上归类为隐藏工作区。 |

---

## 四、关联脚本与配置文件索引（备查）

若未来确需调整 `artifacts` 目录路径或排查相关流水线，涉及以下 **10 个关联文件**：

### 1. 忽略规则文件（2 个）
* `.gitignore`（第 17 行）：`artifacts/`
* `.dockerignore`（第 24 行）：`artifacts/`

### 2. 本地与 CI 冒烟脚本（3 个）
* `scripts/smoke-aot.ps1`（第 6 行）：默认参数 `[string]$Artifacts = 'artifacts/smoke'`
* `scripts/smoke-published.ps1`（第 5 行）：默认参数 `[string]$Artifacts = 'artifacts/desktop-smoke'`
* `scripts/smoke-http.mjs`（第 106 行）：默认性能报告保存路径 `'artifacts/smoke/performance.json'`

### 3. GitHub Actions 工作流（4 个）
* `.github/workflows/ci.yml`：
  * 第 51、54、57 行：`dotnet test` 的 `--results-directory artifacts/...`
  * 第 70-74 行：上传 `whitebox-reports`（`path: artifacts/`）
  * 第 197-204 行：上传 `blackbox-${{ matrix.vendor }}-${{ matrix.arch }}`（`path: artifacts/smoke/`）
* `.github/workflows/aot.yml`：
  * 第 29-32 行：上传 `aot-smoke-logs`（`path: artifacts/smoke/`）
* `.github/workflows/resilience.yml`：
  * 第 24 行：`dotnet test` 的 `--results-directory artifacts/resilience`
  * 第 26-30 行：上传 `resilience-reports`（`path: artifacts/resilience/`）
* `.github/workflows/desktop-release.yml`：
  * 第 98-105 行：上传 `smoke-logs-${{ matrix.rid }}`（`artifacts/desktop-smoke/**/*`）
  * 第 145-150、219 行：下载各平台构建产物至 `artifacts` 并通过 `files: artifacts/**/*` 发布 Release

### 4. 文档引用（1 个）
* `docs/github-smoke-performance-plan.md`（第 42、59 行）：本地验证示例命令与报告输出路径说明
