# Systemd 系统服务等长列表自动分页调研与架构方案

## 1. 背景与问题定义

在 Linux 环境下（如 Ubuntu、Debian、CentOS、Alpine 容器），Systemd 管理的系统服务单元（Units）数量通常在 **300 到 1000+** 之间。
当前 `SystemdSubmodule.js` 一次性拉取全量单元列表，并直接在 Vue 3 模板中使用 `v-for="u in units"` 渲染为单页大表格：
1. **DOM 节点爆炸**：每个单元行包含 5 列、多状态指示灯、徽章、4~5 个控制按钮，800 个单元即生成 **4,000+ 个活跃 DOM 节点**；
2. **渲染与交互卡顿**：初次加载与状态切换时产生明显的主线程阻塞（TBT）；每次输入关键字过滤时全量 DOM 重新计算；
3. **浏览体验劣质**：页面需要滚屏数十屏，难以快速定位首尾与查看统计区间；在移动端或工控平板等低配设备极易卡顿或内存膨胀。

因此，为 Systemd 系统服务及类似长列表（Docker 容器、进程列表等）引入**自动分页与切片保全机制**是提升性能与用户体验的核心刚需。

---

## 2. 深度头脑风暴：可行性、逻辑欠缺与性能瓶颈

### 2.1 性能与架构选型对比
| 模式 | 运行机制 | 性能特征 | 适用场景分析 |
| :--- | :--- | :--- | :--- |
| **纯服务端物理分页** (Server-side Pagination) | 每次切页调用后端 API 传递 `page/pageSize`，由服务端只返回切片 | 后端没有原生索引游标，每次仍需执行完整 `systemctl list-units`（50~150ms），切页延迟大，频繁 fork 进程增加服务器负担 | 适合磁盘持久化日志（如 Logs/History），不适合 Systemd 内存表 |
| **客户端内存切片分页** (In-Memory Slicing) | 后端单次执行拉取全量 Unit（JSON 仅 20~40KB），前端内存中维护切片与即时模糊过滤 | **毫秒级切页响应**，网络往返 0 次，无服务端子进程颠簸，DOM 节点恒定为 15~100 个 | **最佳契合方案**（Systemd/Docker/扩展服务首选） |
| **虚拟滚动** (Virtual Scrolling) | 仅渲染可视窗口区域的 DOM，滚动时动态计算偏移 | 复杂表格高度不一时易产生跳动，且破坏浏览器原生 Ctrl+F 搜索与无障碍交互 | 适合数十万条数据，几百条至千条场景分页体验更直观可预期 |

### 2.2 潜在逻辑欠缺与边界防御 (Edge-case Defenses)
1. **过滤搜索导致的页码越界**：
   用户停留在第 8 页，此时输入关键字搜索，符合条件的记录骤降为 10 条（仅 1 页）。若页码未自愈重置，页面将显示“空白无数据”。
   *防御策略*：在状态机中监听数据源总数与每页尺寸，自动执行越界校准 `currentPage = min(currentPage, totalPages)`。
2. **生命周期操作后状态丢失**：
   用户在第 4 页对某个服务执行“启动/停止/重启”，操作完成后刷新列表。若粗暴重置到第 1 页，会打断用户当前运维视线。
   *防御策略*：后台刷新采用静默更新模式，维持当前所处页码。
3. **每页尺寸（Page Size）切换平滑过渡**：
   支持 15 / 25 / 50 / 100 条自由切换，切换时根据第一条项索引自动折算至对应新页码，防止视图突变。

---

## 3. 设计模式优化与架构设计

```mermaid
flowchart TD
    subgraph DataSource [异构数据源输入]
        S[Systemd Units 列表]
        D[Docker 容器列表]
        W[Worker 微服务列表]
    end

    subgraph AdapterLayer [长列表分页适配器 (Adapter Pattern)]
        PA["usePagination() 响应式适配器"]
        F["过滤与排序策略 (Strategy Pattern)"]
        SM["状态机自校准 (State Machine Pattern)"]
    end

    subgraph OutputView [输出与渲染控制]
        PI["pagedItems (当前页切片 15~100 个)"]
        UI["PaginationBar 通用分页组件 (Tailwind Cyber Dark)"]
    end

    DataSource --> PA
    PA --> F --> SM
    SM --> PI
    SM --> UI
```

### 3.1 适配器模式 (Adapter Pattern)
抽取 `usePagination` 统一适配器，将异构数组数据结构适配为标准化分页接口：
- 输入：`itemsRef`, `options { defaultPageSize, pageSizeOptions, filterFn }`
- 输出：`pagedItems`, `currentPage`, `pageSize`, `totalItems`, `totalPages`, `startIndex`, `endIndex`, `setPage`, `nextPage`, `prevPage`, `resetPage`。

### 3.2 策略模式 (Strategy Pattern)
将数据过滤解耦为可插拔策略：
- 支持客户端多字段模糊匹配策略；
- 支持未来切换为服务端流式分页策略。

### 3.3 状态机模式 (State Machine Pattern)
状态流转：`IDLE` $\rightarrow$ `FILTERING` $\rightarrow$ `PAGING` $\rightarrow$ `RESETTING`。
任何导致数据集变动的事件均受到边界约束保护，保证无论外界如何快速输入，当前页码永远满足 $1 \le currentPage \le totalPages$。

---

## 4. 业界主流方案对比与借鉴

1. **Cockpit Project (Linux 官方控制台)**：
   Cockpit 的 Systemd 模块采用客户端内存过滤 + 20/50 条分页展示，状态更新时就地保留视口。
2. **Portainer (容器云平台)**：
   列表提供统一的底栏分页控制器，支持每页条数切换与快速上一页/下一页。
3. **VueUse (`useOffsetPagination`)**：
   借鉴其响应式切片解耦思想，但本项目坚持零外部依赖原则，使用原生 Vue 3 Composition API 构建轻量级实现。

---

## 5. 具体实施清单

1. **创建通用适配器**：[`src/LinuxWebTool.WebHost/wwwroot/app/utils/pagination.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/wwwroot/app/utils/pagination.js)
   - 纯函数式响应式钩子，管理切片、状态机与越界自愈。
2. **创建通用分页组件**：[`src/LinuxWebTool.WebHost/wwwroot/app/components/PaginationBar.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/wwwroot/app/components/PaginationBar.js)
   - 极简 Cyber Dark 风格，提供首页/上一页/下一页/末页、页码选择、条数切换、项区间汇总。
3. **改造 `SystemdSubmodule.js`**：
   - 引入 `usePagination` 与 `PaginationBar`；
   - 表格只渲染当前页切片 `pagedUnits`；
   - 底部插入分页导航栏；
   - 保持单文件代码行数严格在 400 行架构门禁之内。
4. **同类长列表扩展复用**：
   - 验证对 `DockerSubmodule.js` 等长列表模块的即插即用性。
5. **门禁与自动化测试验证**：
   - 编写前端自动化测试 `tests/frontend/pagination-adapter.test.mjs`，覆盖边界自愈、过滤重置与切片计算。
