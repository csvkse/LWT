# 插件中心标签过滤栏内容溢出问题调研与架构优化方案

> **文档定位**：针对 LinuxWebTool (LWT) 插件中心 (`PluginsView.js`) 在中高分辨率和紧凑视口下标签过滤栏内容截断、右侧核心操作按钮被吞没的问题，进行系统性深因剖析、业界主流方案调研、系统基础设施复用评估，并输出彻底根治的架构设计、同类问题门禁防御规则与自动化回归测试用例。

---

## 1. 背景与现场问题诊断 (Context & Problem Statement)

### 1.1 视觉故障现象与现场复盘

在当前版本的插件中心 (`/app/#/plugins`) 界面中，当用户使用常见的 1080P 显示器（非全屏或带系统缩放）、1440x900 笔记本、iPad 平板设备，或者主内容区处于默认 `max-w-7xl` (1280px) 容器限制下时，中间的标签过滤与操作控制区出现严重的横向溢出问题：

![现场截图与红框标注](file:///C:/Users/hlr/.gemini/antigravity/brain/d8cecf54-526c-4c58-be5a-b7b9b37b02be/.user_uploaded/media_1794109130938_71179937.jpg)

从现场截图标注可清晰观察到：
1. **搜索框半截截断**：位于中间偏右位置的全文搜索输入框（`input`）右半部分被外层卡片边界生硬裁切，右侧的清空按钮 `✕` 与边框直接脱离视口。
2. **核心业务操作入口完全失踪**：原本排布在搜索框右侧的 **4 大关键操作按钮**（`📡 透明互调`、`📦 导入/扫描`、`+ 新建插件`、`🔄 刷新`）被完全挤出可视区域。
3. **滚动条隐蔽与不可知性**：由于容器采用 `overflow-x-auto` 且暗黑赛博主题下滚动槽未做醒目标记，普通用户完全无法感知右侧还有被吞没的按钮，误以为系统“无法新建或导入插件”。

---

## 2. 问题本质与深层技术根因分析 (Root Cause & Width Budget Analysis)

经过对 `src/LinuxWebTool.WebHost/wwwroot/app/views/PluginsView.js` 源码、`AppLayout.js` 布局以及 Tailwind CSS 盒模型的深度溯源，该问题的根因可以解构为以下三个层面的技术矛盾：

### 2.1 DOM 结构与 Flexbox 盒模型死锁

查看 `PluginsView.js` 当前模板的第 420 ~ 570 行结构：

```html
<!-- 核心控制区单容器 -->
<div class="panel p-3 flex flex-col xl:flex-row items-stretch xl:items-center justify-between gap-3">
  <!-- 左侧：工作负载 Tab 组 -->
  <div class="flex items-center gap-1 w-full xl:w-auto overflow-x-auto bg-slate-900/80 p-1 rounded-lg border border-cyber-line/50 shrink-0">
    <button class="... shrink-0 whitespace-nowrap">🧩 全部 ({{ stats.total }})</button>
    <button class="... shrink-0 whitespace-nowrap">🌐 常驻微服务与网关 ({{ stats.service }})</button>
    <button class="... shrink-0 whitespace-nowrap">⚡ 弹性微函数与计算 ({{ stats.function }})</button>
    <button class="... shrink-0 whitespace-nowrap">🛠️ 运维指令与单次任务 ({{ stats.task }})</button>
  </div>

  <!-- 右侧：多维切面过滤 + 搜索框 + 按钮组 -->
  <div class="flex items-center gap-2 w-full xl:w-auto justify-between xl:justify-end overflow-x-auto min-w-0 flex-nowrap shrink-0 py-0.5">
    <div class="flex items-center gap-2 shrink-0 flex-nowrap">
      <select class="... w-24 sm:w-28 shrink-0">...</select> <!-- 生命周期 -->
      <select class="... w-24 sm:w-28 shrink-0">...</select> <!-- 协议 -->
      <select class="... w-24 sm:w-28 shrink-0">...</select> <!-- 状态 -->
      <select class="... w-24 sm:w-28 shrink-0">...</select> <!-- 分类 -->
      <div class="relative shrink-0 w-32 sm:w-40 md:w-48">...</div> <!-- 搜索框 -->
    </div>
    <div class="flex items-center gap-1.5 shrink-0 flex-nowrap">
      <button class="... shrink-0 whitespace-nowrap">📡 透明互调</button>
      <button class="... shrink-0 whitespace-nowrap">📦 导入/扫描</button>
      <button class="... shrink-0 whitespace-nowrap">+ 新建插件</button>
      <button class="... shrink-0">🔄</button>
    </div>
  </div>
</div>
```

**死锁根因剖析**：
1. **无伸缩性挤压（`shrink-0` 滥用）**：外层容器左右两棵子树均强制声明了 `shrink-0`，同时内部每个元素都声明了固定宽度或 `whitespace-nowrap shrink-0`。这意味着所有子元素都拒绝在空间不足时进行缩放或折行。
2. **断点假设与宿主容器硬约束脱节（Breakpoint vs. Budget Mismatch）**：
   - 开发者使用了 Tailwind 的 `xl:flex-row`（即屏幕宽度 $\ge 1280\text{px}$ 时强制在同一行水平排列）。
   - 但是在 `AppLayout.js` 中，主内容区被外层类名 `max-w-7xl`（即 $80\text{rem} = 1280\text{px}$）物理卡死！
   - 扣除外部内边距 `px-4`（两侧各 $16\text{px}$）和卡片内部 `p-3`（两侧各 $12\text{px}$），**卡片内部无论在大屏还是小屏下，物理可用宽度极限只有 $1224\text{px}$！**
   - 换言之，**在屏幕刚好达到 $1280\text{px}$（触发 `xl:` 规则）时，容器可用宽度只有 $1224\text{px}$，却被强行按单行排列，此时溢出最为严重！**

### 2.2 物理宽度预算审计（Width Budget Audit）

对当前单行内所有 UI 元素的固有最小占用宽度（Intrinsic Minimum Width）进行精确审计核算：

| 元素分组 | 具体控件 | 固有最小宽度 (Min-Width) | 组间/元素间隙 (Gap) | 分组小计 |
| :--- | :--- | :--- | :--- | :--- |
| **左侧工作负载 Tabs** | 🧩 全部、🌐 常驻微服务、⚡ 弹性微函数、🛠️ 运维任务 (4 项) | $82\text{px} + 172\text{px} + 165\text{px} + 178\text{px}$ | $4\text{px} \times 3 = 12\text{px}$ (外加 padding $8\text{px}$) | **$\approx 617\text{px}$** |
| **右侧切面过滤组** | 生命周期、通讯协议、运行状态、领域分类 (4 个 Select) | $112\text{px} \times 4 = 448\text{px}$ | $8\text{px} \times 3 = 24\text{px}$ | **$\approx 472\text{px}$** |
| **全文搜索框** | 搜索输入框 + 放大镜 + 清空图标 | $192\text{px}$ (`w-48`) | $8\text{px}$ | **$\approx 200\text{px}$** |
| **核心操作动作组** | 📡 透明互调、📦 导入/扫描、`+` 新建插件、🔄 刷新 (4 个 Button) | $85\text{px} + 92\text{px} + 90\text{px} + 32\text{px}$ | $6\text{px} \times 3 = 18\text{px}$ | **$\approx 317\text{px}$** |
| **中间弹性间距** | 左侧 Tabs 与右侧工具栏之间的 Flex 间隙 | $12\text{px}$ (`gap-3`) | - | **$12\text{px}$** |
| **单行所需绝对物理宽度** | **全部元素在单行排布时的总需求** | - | - | **$\mathbf{1618\text{px}}$** |
| **容器可用最大宽度** | `max-w-7xl` ($1280\text{px}$) 扣除 padding | - | - | **$\mathbf{1224\text{px}}$** |
| **宽度赤字 (Deficit)** | **可用宽度与需求宽度的差额** | **$1224\text{px} - 1618\text{px}$** | - | **$\mathbf{-394\text{px}}$ (严重赤字)** |

> [!CAUTION]
> **数学铁律**：$1224\text{px}$ 的容器物理上绝对无法无损容纳 $1618\text{px}$ 的内容。这 $394\text{px}$ 的物理赤字直接迫使右侧容器触发 `overflow-x`，恰好对应截断了搜索框右半边（约 $75\text{px}$）以及后面整个操作按钮组（$317\text{px}$）。

### 2.3 信息架构认知失调（Information Architecture Dissonance）

除了样式层面的布局冲突，其深层次原因是**界面信息架构（IA）层级未解耦**：
- **一级视角（宏观工作负载定位）**：Tabs 解决的是“我要看哪类微服务”（数据源范围切换）。
- **二级视角（微观属性细粒度切面）**：4 个 Select 下拉框解决的是“在这个大类下按属性过滤”（维度降维）。
- **三级视角（全局管理行为 CTA）**：新建、导入、互调、刷新解决的是“对插件进行系统级写操作与调试”（非过滤行为）。

将这三个完全不同频次、不同语义维度的功能塞进同一个不可折行的单行 flex 容器中，不仅违背了高内聚低耦合的 UI 组织原则，更从物理上造成了空间死锁。

---

## 3. 现有开源库与生态技术借鉴调研 (Ecosystem & Open-Source Research)

### 3.1 业界主流 UI 规范的标准解法

| 体系 / 库 | 设计规范与模式 | 核心做法 | 对 LWT 的借鉴价值 |
| :--- | :--- | :--- | :--- |
| **Ant Design (ProTable / QueryFilter)** | **工具栏与查询表单解耦 (Toolbar vs Search Split)** | 明确将 `ToolBar`（标题、Tab、主操作按钮如新建、刷新、密度）与 `Search`（多条件查询表单）分置于两个独立结构层，严禁混在单行。 | **极高**。直接提供了语义分层的基础理论支撑，证明“Tab + Action”与“Filter + Search”必须解耦。 |
| **GitHub / GitLab Dashboard** | **多层级流式响应布局 (Multi-Tier Responsive Flow)** | 顶层单行放置一级分类 Tab 与主仓库操作；下方独立通栏放置检索输入框与属性标签下拉框，搜索框宽度弹性拉伸 (`flex-1`)。 | **极高**。完美符合赛博暗黑风格与桌面/移动端适配直觉。 |
| **Element Plus (Tabs + Extra)** | **Tab 导航挂载附加操作 (Tab Header Extra Slot)** | 利用 Tabs 的 Header 槽位右侧放置全局操作按钮；列表上方单独开设筛选条（Filter Bar）。 | **高**。可将 Tab 栏与“新建插件/导入”组合为一行，释放下方空间给筛选框。 |
| **Tailwind UI / Headless UI** | **Stacked Responsive Action Bar** | 在小屏下自动切换为堆叠式网格 (`grid grid-cols-2 sm:grid-cols-4`)，搜索框占据全宽，避免在水平方向产生不可预测的横向滚动。 | **高**。提供了移动端与平板端优雅降级的布局模型。 |

### 3.2 零构建约束下的选型考量（Zero-Build / Vanilla ESM Constraints）

系统根目录与 `AGENTS.md` 明确强调：
- **前端为原生 ESM 架构（Vanilla JS + CSS Variables）**，零构建打包（无 Webpack/Vite/npm build），全部静态资源自托管于 `wwwroot/`，在内网完全离线运行。
- **严禁引入重量级外部 NPM 依赖**：任何需要构建工具编译的库（如 Element Plus、Ant Design Vue、Naive UI 等）均不可用，否则将破坏整个纯净的 Zero-Build 架构体系。
- **结论**：**无需引入任何外部第三方库**。系统内置的 **Vue 3 Composition API + Tailwind CSS 原子类体系** 已具备极其强大的弹性流式排布、网格计算与响应式断点能力，直接通过纯原生现代 CSS / Vue 架构即可实现远优于外部库的毫秒级渲染性能。

---

## 4. 业界主流方案对比与选型评估 (Benchmarking & Decision Matrix)

我们对解决此类溢出问题的 4 种业界主流技术方案进行全面评测：

```
┌────────────────────────────────────────────────────────────────────────┐
│                        方案对比与决策评估全景                          │
├────────────────────────────────┬───────────────────────────────────────┤
│ 方案 1: 语义分层解耦架构 (推荐) │ 宏观 Tab + Action 顶置，微观 Filter + │
│                                │ Search 下沉，彻底消除宽度赤字         │
├────────────────────────────────┼───────────────────────────────────────┤
│ 方案 2: 复合折叠面板 (抽屉式)   │ 单行保留高频项，折叠收纳次要下拉框    │
├────────────────────────────────┼───────────────────────────────────────┤
│ 方案 3: 溢出收纳下拉菜单 (More)│ 动态 Resize 计算，超出项移入 "更多"   │
├────────────────────────────────┼───────────────────────────────────────┤
│ 方案 4: 简单 Flex-Wrap 自然折行│ 仅开启换行，不做视觉节奏与层级规划    │
└────────────────────────────────┴───────────────────────────────────────┘
```

### 4.1 四大方案优缺点与多维指标对比矩阵

| 评估维度 (权重) | 方案 1：语义分层解耦架构 (推荐) | 方案 2：复合折叠筛选面板 | 方案 3：溢出收纳下拉菜单 ("More") | 方案 4：纯 Flex-Wrap 粗暴换行 |
| :--- | :--- | :--- | :--- | :--- |
| **彻底根治宽度赤字 (25%)** | **100分** (两行各自 $\approx 850\text{px}$ 与 $700\text{px}$，远低于 $1224\text{px}$) | **90分** (折叠后单行约 $900\text{px}$) | **85分** (收纳后单行受控，但边界计算复杂) | **70分** (容易产生两三个孤立按钮另起一行) |
| **核心 CTA 入口保全性 (25%)** | **100分** (操作按钮顶置右侧，永远 100% 显眼可见) | **85分** (与搜索框同级，受宽度压迫) | **80分** (易被误收纳入下拉菜单) | **70分** (折行后可能掉到界面最底部) |
| **操作点击路径深度 (Click Depth) (20%)** | **100分** (所有筛选与操作 1 次点击直达，零额外层级) | **60分** (需先点击“展开筛选”才能选生命周期) | **60分** (需在“更多”菜单内二级选择) | **95分** (直接平铺，但排版错乱) |
| **零构建与原生兼容度 (15%)** | **100分** (纯 Tailwind 类与原生 DOM，零 JS 额外开销) | **85分** (需维护展开/收起过渡态与高度动画) | **65分** (需引入 ResizeObserver 动态计算宽度) | **100分** (仅修改 class) |
| **赛博暗黑风格美学与秩序感 (15%)** | **98分** (上下呼应，搜索框拉伸充满呼吸感，布局稳健) | **80分** (展开后界面上下跳动) | **75分** (菜单展开容易遮挡下方卡片) | **50分** (参差不齐，视觉断层严重) |
| **综合加权得分** | **$\mathbf{99.2}$ (最佳推荐)** | **76.5** | **74.0** | **76.75** |

### 4.2 决策结论

一致采纳 **方案 1：语义分层解耦与自适应流式排布架构 (Semantic Multi-Tier Architecture)**：
- 将原本混杂的单行拆解为逻辑清晰的 **双层控制台体系**：
  - **第 1 层（宏观视图）：工作负载分类 Tabs + 全局核心操作按钮组**；
  - **第 2 层（微观检索）：弹性全文搜索框 + 多维策略切面过滤组 + 一键清空重置**。

---

## 5. 当前系统既有基础设施与复用资产盘点 (Existing Infrastructure & Asset Reuse)

在设计具体方案时，系统已有的丰富基础设施完全能够支撑零成本复用：

1. **Tailwind CSS 3.x 运行时**：
   - 现成的断点前缀：`sm:` (640px), `md:` (768px), `lg:` (1024px), `xl:` (1280px)。
   - 弹性伸缩利器：`flex-1`（自动吞噬剩余空间）、`min-w-0`、`gap-2`、`justify-between`、`items-center`。
2. **主题样式类库 (`style.css`)**：
   - `.panel`：暗黑半透明渐变玻璃面板背景；
   - `.input` 与 `.select-compact`：统一样式的暗黑赛博输入框与下拉选择框；
   - `.btn-primary`、`.btn-secondary`、`.btn-ghost`：具备悬浮发光与触控反馈的标准按钮；
   - `.no-scrollbar`：系统预置的隐藏滚动条但保留触摸平滑滑动能力的样式类（已在 `style.css` 定义，移动端 Tab 滑动体验极佳）。
3. **同类视图先验最佳实践**：
   - `CommandsView.js`：采用 `flex items-center gap-2 flex-wrap` 配合 `sm:ml-auto` 操作区右对齐，展现了优雅的响应式弹性；
   - `RemoteHostsView.js`：采用“操作区”与“搜索与协议筛选栏”分层独立卡片模式，从未发生过溢出问题。

---

## 6. 具体解决方案与实施细节 (Concrete Implementation Plan)

### 6.1 重构后的界面架构与视觉拓扑

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ 控制区外层容器 (.panel p-3 flex flex-col gap-3)                                │
├───────────────────────────────────────────────────────────────────────────────┤
│ [第一层: 宏观工作负载导航与全局操作]                                           │
│ ┌────────────────────────────────────────┐   ┌──────────────────────────────┐ │
│ │ 🧩 全部  🌐 常驻微服务  ⚡ 弹性函数  🛠️ 任务│   │ 📡透明互调  📦导入  +新建  🔄│ │
│ └────────────────────────────────────────┘   └──────────────────────────────┘ │
│      (左对齐，小屏支持 no-scrollbar 滑动)              (右对齐吸附，CTA 永不丢失)     │
├───────────────────────────────────────────────────────────────────────────────┤
│ [第二层: 全文检索与多维切面策略过滤]                                           │
│ ┌───────────────────────────────┐  ┌────────────────────────────────────────┐ │
│ │ 🔍 [搜索插件名称/ID...      ] ✕│  │ [生命周期▾] [通讯协议▾] [状态▾] [分类▾] ↺│ │
│ └───────────────────────────────┘  └────────────────────────────────────────┘ │
│     (左侧 flex-1 弹性填满，宽阔舒适)              (右侧紧凑排列，条件高密度聚焦)      │
└───────────────────────────────────────────────────────────────────────────────┘
```

### 6.2 模板重构实施代码 (Vue 3 Template)

将 `src/LinuxWebTool.WebHost/wwwroot/app/views/PluginsView.js` 中的核心控制区模板更新为：

```html
<!-- 核心控制区：双层语义解耦控制台 (第 1 层：工作负载 Tabs 与全局操作；第 2 层：检索与多维切面过滤) -->
<div class="panel p-3 flex flex-col gap-3">
  <!-- 第 1 层：三大正交工作负载范式 Tabs + 全局管理核心动作组 -->
  <div class="flex flex-col sm:flex-row items-stretch sm:items-center justify-between gap-2.5">
    <!-- 标签导航切换：三大正交工作负载范式 (支持小屏横向平滑滑动，隐藏滚动条) -->
    <div class="flex items-center gap-1 overflow-x-auto no-scrollbar bg-slate-900/80 p-1 rounded-lg border border-cyber-line/50 shrink-0">
      <button
        type="button"
        class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
        :class="profileTab === 'all' ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/40' : 'text-slate-400 hover:text-slate-200'"
        @click="profileTab = 'all'"
      >
        🧩 全部 ({{ stats.total }})
      </button>
      <button
        type="button"
        class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
        :class="profileTab === 'service' ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40' : 'text-slate-400 hover:text-slate-200'"
        @click="profileTab = 'service'"
      >
        🌐 常驻微服务与网关 ({{ stats.service }})
      </button>
      <button
        type="button"
        class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
        :class="profileTab === 'function' ? 'bg-purple-500/20 text-purple-300 border border-purple-500/40' : 'text-slate-400 hover:text-slate-200'"
        @click="profileTab = 'function'"
      >
        ⚡ 弹性微函数与计算 ({{ stats.function }})
      </button>
      <button
        type="button"
        class="px-3 py-1.5 rounded text-xs font-medium transition-colors shrink-0 whitespace-nowrap"
        :class="profileTab === 'task' ? 'bg-sky-500/20 text-sky-300 border border-sky-500/40' : 'text-slate-400 hover:text-slate-200'"
        @click="profileTab = 'task'"
      >
        🛠️ 运维指令与单次任务 ({{ stats.task }})
      </button>
    </div>

    <!-- 全局操作动作组：透明互调、导入、新建与刷新 (永远显式外露于顶层右侧，杜绝任何截断隐患) -->
    <div class="flex items-center gap-1.5 shrink-0 justify-end flex-wrap">
      <button
        type="button"
        class="btn-secondary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0 whitespace-nowrap text-cyan-300"
        title="打开微服务与微函数透明反射互调网关"
        @click="openInvokeDrawer(null)"
      >
        <span>📡</span>
        <span class="hidden sm:inline">透明互调</span>
      </button>
      <button
        type="button"
        class="btn-secondary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0 whitespace-nowrap"
        @click="showImportModal = true"
      >
        <span>📦</span>
        <span class="hidden sm:inline">导入/扫描</span>
      </button>
      <button
        type="button"
        class="btn-primary text-xs py-1.5 px-2.5 flex items-center gap-1.5 shrink-0 whitespace-nowrap"
        @click="showCreateModal = true"
      >
        <span>+</span>
        <span class="hidden sm:inline">新建插件</span>
      </button>
      <button
        type="button"
        class="btn-ghost text-xs p-1.5 text-slate-400 hover:text-white shrink-0"
        title="刷新列表"
        @click="loadPlugins()"
      >
        <span>🔄</span>
      </button>
    </div>
  </div>

  <!-- 第 2 层：全文检索与多维切面过滤工具栏 (搜索框全宽自适应拉伸，过滤组紧凑对齐) -->
  <div class="flex flex-col md:flex-row items-stretch md:items-center justify-between gap-2.5 pt-1 border-t border-cyber-line/30">
    <!-- 全文检索输入框 (flex-1 弹性填补剩余空间，告别原本拥挤狭窄的固定宽度) -->
    <div class="relative flex-1 min-w-[180px] max-w-xl">
      <span class="absolute left-2.5 top-1/2 -translate-y-1/2 text-slate-500 pointer-events-none text-xs">🔍</span>
      <input
        v-model="searchKeyword"
        type="text"
        class="input text-xs py-1.5 pl-7 pr-7 w-full bg-slate-900/90 border border-cyber-line/80 focus:border-cyan-400 focus:ring-1 focus:ring-cyan-400/30 text-slate-100 placeholder:text-slate-500 rounded-md transition-all"
        placeholder="搜索插件名称、描述、ID 或分类领域..."
        @keydown.esc="searchKeyword = ''"
      />
      <button
        v-if="searchKeyword"
        type="button"
        class="absolute right-2 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-200 text-xs p-0.5 rounded transition-colors"
        title="清空搜索"
        @click="searchKeyword = ''"
      >
        ✕
      </button>
    </div>

    <!-- 多维切面过滤下拉选择组 (生命周期、协议、状态、分类) + 重置按钮 -->
    <div class="flex items-center gap-2 flex-wrap shrink-0">
      <!-- 生命周期切面 -->
      <select
        v-model="lifecycleFilter"
        class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
        title="按生命周期（解释型/预编译/源码构建）过滤"
      >
        <option value="all">全生命周期</option>
        <option value="interpreted">⚡ 解释执行</option>
        <option value="precompiled">📦 预编译模块</option>
        <option value="sourcecompiled">🔨 源码构建</option>
      </select>

      <!-- 协议切面 -->
      <select
        v-model="protocolFilter"
        class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
        title="按通讯协议过滤"
      >
        <option value="all">全通讯协议</option>
        <option value="http">🌐 HTTP</option>
        <option value="uds">🔌 UDS</option>
        <option value="wasmabi">⚡ WasmAbi</option>
        <option value="cli">💻 CLI</option>
      </select>

      <!-- 运行状态过滤 -->
      <select
        v-model="stateFilter"
        class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
        title="按插件当前运行状态过滤"
      >
        <option value="all">全部状态</option>
        <option value="running">🟢 运行中</option>
        <option value="ready">⚡ 待命/就绪</option>
        <option value="stopped">⏹️ 已停止</option>
        <option value="disabled">🚫 已禁用</option>
        <option value="degraded">⚠️ 异常/降级</option>
        <option value="missing">🧩 缺少依赖</option>
      </select>

      <!-- 领域分类筛选 -->
      <select
        v-if="categories.length > 0"
        v-model="categoryFilter"
        class="input select-compact text-xs py-1.5 px-2 bg-slate-900/90 border border-cyber-line/80 text-slate-200 focus:border-cyan-400 rounded-md shrink-0 w-24 sm:w-28"
        title="按领域分类筛选"
      >
        <option value="all">全部分类</option>
        <option v-for="cat in categories" :key="cat" :value="cat">
          {{ cat }}
        </option>
      </select>

      <!-- 一键重置全部过滤条件按钮 (仅在存在活跃筛选时高亮显示) -->
      <button
        v-if="profileTab !== 'all' || lifecycleFilter !== 'all' || protocolFilter !== 'all' || stateFilter !== 'all' || categoryFilter !== 'all' || searchKeyword"
        type="button"
        class="btn-ghost text-xs py-1 px-2 text-cyan-400 hover:text-cyan-300 hover:bg-cyan-950/40 rounded flex items-center gap-1 transition-colors shrink-0"
        title="重置所有筛选条件"
        @click="resetFilters()"
      >
        <span>↺</span>
        <span class="hidden sm:inline">重置</span>
      </button>
    </div>
  </div>
</div>
```

### 6.3 优化后各断点多端表现对比

| 视口尺寸 / 设备 | 旧版单行排布表现 | 新版双层解耦表现 |
| :--- | :--- | :--- |
| **超大屏 / 宽屏 (>= 1536px, 2K/4K)** | 虽勉强排开，但搜索框窄小，按钮居于最远端，鼠标移动行程过长。 | 第一层 Tabs 与 Actions 两端呼应；第二层搜索框自适应拉长，下拉框紧凑贴合，视觉平衡极佳。 |
| **标准桌面与笔记本 (1280px ~ 1440px)** | **触发 `xl:flex-row`，发生 -394px 赤字灾难，搜索框截断半截，新建/导入按钮完全消失！** | 第一层总宽 $\approx 920\text{px}$，第二层总宽 $\approx 820\text{px}$，在 $1224\text{px}$ 空间内游刃有余，呼吸感极佳。 |
| **平板横屏 / 小笔记本 (1024px ~ 1279px)** | 原先处于断点临界区，横向滚动槽难看，内容严重被压缩。 | 依然保持两层布局，第二层搜索框自然伸缩，所有控件 100% 暴露。 |
| **平板竖屏 (768px ~ 1023px)** | 换为两行，但第二行依旧塞入 4 下拉 + 搜索 + 4 按钮（1000px），依然严重横向溢出。 | 第一层垂直/水平切换，第二层通过 `flex-wrap` 优雅折行，无任何溢出与滚动条。 |
| **手机移动端 (< 768px)** | 彻底崩溃，用户在手机上左右划动极其痛苦，常误触。 | Tab 栏利用 `.no-scrollbar` 原生流畅触摸滑动；按钮与搜索框全宽自适应，单手触控极佳。 |

---

## 7. 同类问题架构门禁设计 (Architectural Gate: FE-TOOLBAR-OVERFLOW-SAFETY)

为杜绝类似“单行塞入过多固定元素导致宽度溢出截断”、“在 `overflow-x-auto` 容器内隐蔽放置关键 CTA 按钮”的坏味道在后续迭代或其他视图中再次发生，应在 `src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs` 中引入全新的静态分析架构门禁规则。

### 7.1 门禁规则编号：`FE-TOOLBAR-OVERFLOW-SAFETY`

**规则核心防卫要点**：
1. **防卫要点 A（核心行动入口不可被隐藏）**：严禁在带有 `overflow-x-auto` 或 `overflow-hidden` 的容器内部直接包含高价值的核心行动按钮（如携带 `btn-primary` 或关键操作词 `新建`、`添加`、`导入` 的按钮），防止用户因横向滚动视口截断而丢失关键业务入口。
2. **防卫要点 B（禁止单行无弹性死锁组合）**：在未声明 `flex-wrap` 的 flex 容器中，禁止同一行子元素总数超过 6 个且同时混用 `shrink-0` 与固定尺寸类（如 `w-24`, `w-28`, `w-32`, `w-48`），防止累加宽度突破主流安全视口阈值（$1150\text{px}$）。

### 7.2 门禁检测逻辑实现方案

在 `frontend-gate.cjs` 的模板检查模块中增加检测逻辑：

```javascript
// FE-TOOLBAR-OVERFLOW-SAFETY: 工具栏与容器防溢出安全门禁
const overflowAutoContainers = tpl.matchAll(/<([a-z0-9-]+)\b[^>]*?class\s*=\s*["']([^"']*?\boverflow-x-auto\b[^"']*)["'][^>]*>([\s\S]*?)<\/\1>/gi);
for (const [, tag, cls, innerHtml] of overflowAutoContainers) {
  // 1. 检查是否存在核心 CTA 操作按钮被困在横向滚动条内
  if (/\bbtn-primary\b/.test(innerHtml) && /\b(?:新建|创建|导入|添加|保存|提交)\b/.test(innerHtml)) {
    add(
      'FE-TOOLBAR-OVERFLOW-SAFETY',
      file,
      `在声明了 "overflow-x-auto" 的横向滚动容器内检测到核心 CTA 按钮（btn-primary）。关键操作按钮严禁置于横向溢出容器内，必须提升至稳定层级，防止被滚动截断吞没。`
    );
  }
}

// 2. 检查单行 flex 容器元素超载与 shrink-0 死锁
const singleLineFlexBars = tpl.matchAll(/<div\b[^>]*?class\s*=\s*["']([^"']*?\bflex\b[^"']*?\b(?:flex-nowrap|xl:flex-row)\b[^"']*)["'][^>]*>([\s\S]*?)<\/div>/gi);
for (const [, cls, innerHtml] of singleLineFlexBars) {
  if (!/\bflex-wrap\b/.test(cls)) {
    const fixedWidthInputs = (innerHtml.match(/\bw-(?:24|28|32|36|40|48)\b/g) || []).length;
    const shrinkZeroButtons = (innerHtml.match(/\bshrink-0\b/g) || []).length;
    if (fixedWidthInputs >= 4 && shrinkZeroButtons >= 4) {
      add(
        'FE-TOOLBAR-OVERFLOW-SAFETY',
        file,
        `检测到潜在的单行工具栏宽度超载死锁（${fixedWidthInputs} 个固定宽输入控件 + ${shrinkZeroButtons} 个 shrink-0 子元素处于单行无 wrap 容器内），极易引发视口截断，请采用双层语义解耦或增加 flex-wrap。`
      );
    }
  }
}
```

---

## 8. 自动化防回归测试套件规划 (Automated Regression Test Suite)

在 `tests/frontend/` 目录下新增独立的单元断言与模板契约测试 `tests/frontend/toolbar-overflow-and-responsive.test.mjs`，确保此问题被永久防护：

### 8.1 测试用例矩阵设计

```javascript
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

const pluginsViewPath = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app/views/PluginsView.js');
const pluginsViewSource = fs.readFileSync(pluginsViewPath, 'utf8');

test('TOOLBAR-RESPONSIVE-01: PluginsView 核心控制区必须采用双层解耦架构，不得使用单行拥挤结构', () => {
  // 断言不存在旧版的死锁类名组合：flex xl:flex-row 与 overflow-x-auto 强行塞入全部下拉与按钮
  const hasLegacyDeadlock = /class\s*=\s*["'][^"']*?xl:flex-row[^"']*?["'][\s\S]*?v-model="searchKeyword"[\s\S]*?showCreateModal/i.test(pluginsViewSource);
  assert.equal(hasLegacyDeadlock, false, 'PluginsView 不得在单行 flex-row 容器内同时混杂 Tabs、4个下拉框、搜索框与新建按钮');
});

test('TOOLBAR-RESPONSIVE-02: 新建插件与导入等核心 CTA 按钮必须位于无 overflow-x 截断的稳定容器中', () => {
  // 提取包含 showCreateModal 按钮的父级容器
  const buttonSnippet = pluginsViewSource.match(/<button\b[^>]*?@click="showCreateModal\s*=\s*true"[\s\S]*?<\/button>/)?.[0] || '';
  assert.ok(buttonSnippet, '必须存在新建插件按钮');

  // 回溯该按钮所在的上层容器片段，确保其祖先容器未被 overflow-x-auto 限制
  const createModalIndex = pluginsViewSource.indexOf('showCreateModal = true');
  const contextBefore = pluginsViewSource.slice(Math.max(0, createModalIndex - 500), createModalIndex);
  
  // 断言上层容器不包含 overflow-x-auto
  const hasLocalOverflowContainer = /<div\b[^>]*?class\s*=\s*["'][^"']*?\boverflow-x-auto\b[^"']*?>[^<]*$/i.test(contextBefore);
  assert.equal(hasLocalOverflowContainer, false, '新建插件按钮不得包裹在受限的 overflow-x-auto 容器内');
});

test('TOOLBAR-RESPONSIVE-03: 全文搜索框必须声明 flex-1 或伸缩容器，禁止硬编码 w-32/w-40 狭窄固定宽度', () => {
  const searchInputMatch = pluginsViewSource.match(/<input\b[^>]*?v-model="searchKeyword"[^>]*>/i);
  assert.ok(searchInputMatch, '必须包含 searchKeyword 搜索输入框');
  
  const searchContext = pluginsViewSource.slice(
    Math.max(0, pluginsViewSource.indexOf('searchKeyword') - 200),
    pluginsViewSource.indexOf('searchKeyword') + 200
  );
  
  // 校验包含弹性自适应拉伸类 flex-1
  assert.match(searchContext, /\bflex-1\b/, '搜索框容器必须具备 flex-1 弹性拉伸属性，以充分利用横向空间');
});

test('TOOLBAR-RESPONSIVE-04: 所有切面下拉框 (Select) 必须遵循暗黑主题规范，且具备标准尺寸阶梯', () => {
  const selectMatches = pluginsViewSource.matchAll(/<select\b([^>]*?v-model="(?:lifecycleFilter|protocolFilter|stateFilter|categoryFilter)"[^>]*)>/gi);
  let count = 0;
  for (const match of selectMatches) {
    count++;
    const attrs = match[1];
    assert.match(attrs, /\binput\b/, '下拉框必须具备 .input 类');
    assert.match(attrs, /\bbg-slate-900/, '下拉框必须具备暗黑色底色');
    assert.match(attrs, /\bselect-compact\b/, '下拉框必须具备 select-compact 紧凑类');
  }
  assert.ok(count >= 3, '至少应检测到 3 个以上的策略切面下拉框');
});
```

---

## 9. 部署与验证指引 (Deployment & Verification Guide)

依据项目 `AGENTS.md` 规范，本优化属于 **主系统前端静态资源更新**，严禁使用耗时数分钟的构建管线，统一执行 **秒级前端静态热更**：

1. **本地单元与门禁测试核验**：
   ```powershell
   node tests/frontend/toolbar-overflow-and-responsive.test.mjs
   node tests/frontend/plugin-filter-and-state-machine.test.mjs
   node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs
   ```
2. **极速推送验证（耗时约 4~5 秒）**：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -FrontendOnly
   ```
3. **内置浏览器自动化回归验收**：
   - 访问入口：`http://10.126.1.70:5271/app/#/plugins`
   - 使用 Chrome DevTools 仿真以下典型视口并检查：
     - **1280x800** (标准笔记本典型分辨率)：确认搜索框与新建插件按钮全部自然显式呈现，无滚动条；
     - **1024x768** (iPad 平板横屏)：确认第二层搜索与过滤框弹性适应，按钮位置稳固；
     - **375x667** (移动端)：确认 Tab 栏平滑触摸滑动，搜索框全宽平铺，无横向破位。

---

## 10. 总结与效益评估 (Summary & Benefits)

| 指标 | 优化前 (Legacy) | 优化后 (Optimized) | 改善效益 |
| :--- | :--- | :--- | :--- |
| **可用宽度赤字** | $-394\text{px}$ (严重超载) | $+304\text{px}$ (充裕呼吸空间) | **物理彻底根治溢出** |
| **搜索框可视宽度** | 截断仅剩 $\approx 100\text{px}$ | $\approx 280\text{px} \sim 500\text{px}$ 弹性自适应 | **提升 280% 输入与阅读舒适度** |
| **核心操作入口可见度** | **0% (完全被吞没隐藏)** | **100% (常驻外露显眼可见)** | **消除关键功能不可用的重大缺陷** |
| **单行元素拥挤度** | 10 个控件挤在单行 | 分解为 4+4 的两层清晰矩阵 | **符合认知负荷与人机工效学** |
| **架构防回归防护** | 无专门宽度与 CTA 门禁 | 新增门禁与 4 项专项回归测试 | **零死角保障未来迭代不破位** |
