# 前端页面内容溢出剪切与对齐居中全面审计与优化方案

## 1. 审核背景与综述

在 LinuxWebTool（轻量自托管 Linux 运维指令与网络控制台）前端界面的实际使用与视觉审核中（基于用户反馈的真实截图及全页面走查），发现若干由于原子样式类缺失、弹性布局截断机制不完整、表格无最小宽度保护、以及组件基线不一致导致的内容溢出剪切、贴边贴底及对齐错位问题。

本方案针对用户提供的当前 FRP 穿透卡片问题以及全站 14 个核心视图、公共组件和公共 CSS 进行彻底排查，制定系统性的修复与规范化标准，并直接实施执行。

---

## 2. 核心问题诊断与根因分析

### 2.1 当前页面问题（FRP 穿透线路卡片 `FrpView.js`，对照用户截图）

1. **原子类 `p-4.5` 不存在导致卡片边距归零、内容贴边剪切（核心 Bug）**：
   - **现象**：在卡片中，左侧标题“默认穿透线路”距离左外边框仅 2~3px；右上角“● 运行中”徽章紧贴右上圆角；底部的“日志/编辑/删除”与右下角“■ 断开”按钮完全挨着卡片底边和侧边；中间统计数字“↑ 60 B”、“↓ 261 B”在右侧发生严重的视觉挤压与边缘贴紧。
   - **根因**：`FrpView.js` 第 413 行使用了 `class="panel p-4.5 ..."`。Tailwind CSS 官方 spacing 标尺中仅包含 `4` (16px) 和 `5` (20px)，**根本不存在 `4.5`**。Tailwind 无法生成对应的 CSS 规则，导致卡片 padding 为 0，所有内容直接贴在 1px 边框上。
   - **修复**：规范统一使用 `p-5`（20px），为卡片内所有元素提供标准、宽裕的呼吸间距，消除圆角边界剪切。

2. **弹性布局中 `truncate` 不规范导致公网链接与外跳图标溢出截断**：
   - **现象**：`subdomainUrl` 和 `publicUrl` 的超链接使用了 `class="... flex items-center gap-1 truncate"`。
   - **根因**：根据 CSS 规范，`truncate`（`overflow: hidden; text-overflow: ellipsis; white-space: nowrap;`）作用在 `display: flex` 容器上时，其弹性子文本节点不会自动触发省略号，且外跳箭头 `↗` 也会随之变形或被截断。
   - **修复**：外层保留 `flex items-center min-w-0`，链接文字由 `<span class="truncate">{{ url }}</span>` 包裹，外跳箭头抽离为 `<span class="shrink-0">↗</span>`，同时加上完整的 `:title="url"` 悬浮气泡。

3. **卡片头部容器缺少 `min-w-0`**：
   - **现象**：线路名称或 Host 域名过长时，会强制推挤右侧的 `stateBadge` 徽章，导致徽章折行或卡片横向撑爆。
   - **修复**：卡片头部左侧文本包裹块添加 `min-w-0`，配合 `truncate` 保证弹性收缩安全。

4. **流量与心跳统计数据对齐与留白优化**：
   - **现象**：右侧上行流量与下行流量文本使用 `text-right` 紧贴右边缘。
   - **修复**：在标准 `p-5` 边距下，将运行统计重构为紧凑工整的网格卡片背景块，指标标签与数值左右对齐，字体基线对其一致。

---

### 2.2 全站其他前端页面全面走查与问题清单

| 页面 / 组件 | 发现的溢出 / 对齐 / 截断缺陷 | 根因与优化方案 |
| :--- | :--- | :--- |
| **家庭网关** (`GatewayView.js`) | 1. 4 大 Tab 的表格（Websites / Routes / Clusters / TCP）无 `min-w-[...]`，窄屏时列被严重挤压变形折行；<br>2. 直通入口长 URL 无最大宽度控制；<br>3. 表头 `th` 缺少 `whitespace-nowrap`，表头文字碎词折行。 | 为各个表格添加 `min-w-[48rem] ~ min-w-[56rem]` 水平滚动保护；URL 加 `truncate max-w-[16rem]` 与 `min-w-0`；表头加 `whitespace-nowrap`。 |
| **API 密钥** (`ApiKeysView.js`) | 1. 模板中直接访问 `window.location.origin`，Vue 3 模板沙箱无全局 window，引发渲染异常；<br>2. 密钥列表表格无 `min-w-[...]` 导致窄屏挤压；<br>3. 新建模态框缺少 `max-h-[90vh] overflow-y-auto`，小屏幕或软键盘遮挡保存按钮；<br>4. 使用未定义的 `btn-secondary`。 | 在 setup 显式导出 `originUrl`；表格加上 `min-w-[54rem]`；模态框加视口高度约束与滚动；补充全局 `.btn-secondary` 样式。 |
| **Web 终端** (`TerminalView.js`) | 1. 顶部标签栏无 `min-w-0`，标签过多时将右侧操作按钮组挤压拆分多行；<br>2. 后台会话列表项目内元素高低不齐、混排凌乱。 | 顶部标签页容器加上 `min-w-0 flex-1 overflow-x-auto`；后台列表统一对齐与排版。 |
| **定时任务** (`SchedulesView.js`) | 1. 状态启用按钮使用 `<button class="badge">`，与操作列的 `btn-xs` 高度（18px vs 24px）与圆角完全不一致；<br>2. 收藏星号 `★` 与任务标题基线不平；<br>3. ESLint 存在 `\$` 无效转义语法告警。 | 状态列使用统一规范的按钮与指示灯；星号按钮垂直居中微调；清理无用转义符。 |
| **指令管理** (`CommandsView.js`) | 1. 执行结果弹窗在指令名过长时挤压关闭按钮 `✕`；<br>2. 指令卡片星号位置微小偏移；<br>3. ESLint 存在 `\$` 无效转义语法告警。 | 弹窗头部标题加 `min-w-0 truncate`；星号按钮基线垂直居中微调；清理无用转义符。 |
| **媒体转码** (`TranscodeView.js`) | 1. 监听规则中操作列将 `badge` 与 `btn-xs` 混用导致高低错位；<br>2. 预设卡片标题与内置标签行高不齐；<br>3. ESLint 存在无用转义与未使用变量警告。 | 统一按钮系统高度与交互；规范预设卡片标题结构；修复 ESLint 问题。 |
| **系统监控** (`SystemStatusView.js`) | 1. 网卡实时列表每一项单行不折行，窄屏横向溢出；<br>2. 硬件设备长描述在 flex 下无 `min-w-0` 导致溢出截断失效。 | 网卡列表在窄屏下使用自适应响应式网格布局；硬件条目容器增加 `min-w-0`。 |
| **文件管理** (`FilesView.js`) | 面包屑容器同时应用 `truncate` 与 `overflow-x-auto` 样式冲突。 | 分离面包屑容器，横向滚动与单项省略互不干扰。 |
| **文件选择器** (`FilePicker.js`) | 模板末尾存在多余多写的闭合 `</div>` 标签。 | 清理多余闭合标签，消除潜在的 DOM 解析混乱。 |
| **执行历史** (`HistoryView.js`) | 1. 分页器在模板中错误编写 `totalPages.value`（Vue 3 模板已自动解包 ref）；<br>2. 执行详情弹窗无最大高度限制，长日志溢出屏幕。 | 纠正为 `totalPages`；弹窗增加 `max-h-[90vh] flex flex-col` 与滚动。 |
| **系统日志** (`LogsView.js`) | 1. 输出块既有 `output-block`（max-h 22rem）又设 `min-h-[24rem]` 冲突；<br>2. ESLint 未使用变量警告。 | 明确指定高度与滚动行为；清理未使用变量。 |
| **登录界面** (`LoginView.js`) | 内部 `min-h-screen` 叠加外部布局 padding 与 footer 导致必定出现双滚动条。 | 调整为自适应视口高度，消除非必要滚动条。 |
| **全局公共样式** (`style.css`) | 1. 缺少 `.btn-secondary` 类定义；<br>2. `.badge` 作为交互元素缺少 `cursor: pointer` 和 hover 反馈；<br>3. 表格单元格垂直对齐方式统一为 `vertical-align: middle`。 | 在 `style.css` 补充完整规范体系。 |

---

## 3. 规范化改造方案与实现细节

### 3.1 间距与卡片规范 (Card Spacing & Padding)
- **卡片内边距标准**：所有独立功能卡片（如 FRP 线路卡片、预设卡片、指令卡片）统一使用 Tailwind 标尺中的标准类名 `p-5`（20px）或 `p-4`（16px）。杜绝任何非法非标类名（如 `p-4.5`）。
- **留白防线**：所有卡片顶部、侧边、底部均保持至少 16px 以上内缩，确保内部的按钮、输入框、徽章与外层卡片 border-radius（0.75rem）形成至少 8px 以上的清晰视觉安全通道。

### 3.2 文本截断与单行省略规范 (Text Truncation)
- **Flex 容器截断准则**：在 flex 容器中，必须给截断容器或其父级声明 `min-w-0`，否则 flex item 的默认 `min-width: auto` 将阻止其缩小，导致 `truncate` 失效并把兄弟元素挤出容器。
- **图标跟随规范**：对于包含外跳箭头 `↗` 或复制按钮的 URL，链接统一采用：
  ```html
  <a :href="url" target="_blank" :title="url" class="inline-flex items-center gap-1 min-w-0 font-mono hover:underline">
    <span class="truncate">{{ url }}</span>
    <span class="shrink-0 text-[10px]">↗</span>
  </a>
  ```

### 3.3 表格自适应与防挤压规范 (Responsive Tables)
- **最小宽度保护**：在所有 `<div class="overflow-x-auto">` 内的 `table` 必须声明明确的 `min-w-[48rem]`（或对应列数权重的宽度），确保桌面和移动设备上即使视口只有 360px 宽度，也是平滑横向滚动，决不允许列宽被挤压坍塌。
- **表头对齐规范**：表头 `th` 必须全部声明 `whitespace-nowrap`，并且操作列统一采用 `text-right`，内容列垂直对齐一律对齐到中线或顶部基线。

### 3.4 按钮与交互元素对齐体系 (Buttons & Alignment)
- **统一高度体系**：
  - 默认按钮 `.btn`：高度约 34px (`py-2 px-3.5`)。
  - 微型按钮 `.btn-xs`：高度约 24px (`py-1 px-2.5 text-xs`)。
  - 状态操作按钮一律使用 `.btn.btn-xs`（可搭配指示灯圆点），不直接把展示型 `.badge` 作为主要操作按钮。
- **公共样式补充**：在 `style.css` 中增加标准 `.btn-secondary` 样式，以及改善 `.badge` 的交互状态。

### 3.5 弹窗与视口约束规范 (Modal & Viewport Constraints)
- 所有弹窗内容层统一遵守安全视口约束：
  ```html
  <div class="panel w-full max-w-lg p-5 max-h-[90vh] flex flex-col overflow-y-auto">
  ```
  保证在任意高宽屏幕（尤其小屏笔记本、手机端）上内容均在视口内可滚动浏览，底部取消/保存操作栏永远触手可及。

---

## 4. 实施清单与文件变更

1. **`src/LinuxWebTool.WebHost/wwwroot/app/style.css`**：
   - 增加 `.btn-secondary` 样式；
   - 增强可点击 `.badge` 悬停反馈与基线对齐；
   - 规范 `.data-table th` 与 `td` 对齐。
2. **`src/LinuxWebTool.WebHost/wwwroot/app/views/FrpView.js`**：
   - 修正卡片外层 `p-4.5` 为标准 `p-5`；
   - 修复公网链接截断与外跳图标跟随；
   - 头部容器加 `min-w-0` 防挤压；
   - 优化实时统计与卡片底部操作按钮排版。
3. **`src/LinuxWebTool.WebHost/wwwroot/app/views/GatewayView.js`**：
   - 为 4 个 Tab 的数据表统一添加 `min-w-[...]` 水平滚动保护；
   - 表头增加 `whitespace-nowrap`，限制直通 URL 最大宽度。
4. **`src/LinuxWebTool.WebHost/wwwroot/app/views/ApiKeysView.js`**：
   - 暴露 `originUrl` 解决模板 window 引用；
   - 数据表增加 `min-w-[54rem]` 与 `whitespace-nowrap`；
   - 弹窗增加最大高度限制与纵向滚动。
5. **`src/LinuxWebTool.WebHost/wwwroot/app/views/TerminalView.js`**：
   - 顶部控制栏与标签栏响应式防挤压优化；
   - 后台会话列表对齐优化。
6. **`src/LinuxWebTool.WebHost/wwwroot/app/views/SchedulesView.js`**：
   - 修复星号基线对齐，统一表格启用切换按钮，消除 ESLint 转义告警。
7. **`src/LinuxWebTool.WebHost/wwwroot/app/views/CommandsView.js`**：
   - 修复执行结果弹窗长名称挤压，星号基线对齐，消除 ESLint 转义告警。
8. **`src/LinuxWebTool.WebHost/wwwroot/app/views/TranscodeView.js`**：
   - 统一规则表格状态按钮样式，消除 ESLint 告警。
9. **`src/LinuxWebTool.WebHost/wwwroot/app/views/SystemStatusView.js`**：
   - 优化网卡列表窄屏响应式折行与硬件项截断。
10. **`src/LinuxWebTool.WebHost/wwwroot/app/views/FilesView.js`** 与 **`FilePicker.js`**：
    - 修复面包屑滚动与截断冲突；移除 FilePicker 多余闭合标签。
11. **`src/LinuxWebTool.WebHost/wwwroot/app/views/HistoryView.js`**、**`LogsView.js`**、**`LoginView.js`**：
    - 修复分页器属性引用与弹窗高度约束；消除双滚动条。

---

## 5. 验证与门禁检查标准

- 前端架构门禁：执行 `node frontend-gate.cjs`，确保契约与架构规则 100% 通过；
- ESLint 规范：执行 `npm run lint`，确保所有转义与无用变量警告清理归零；
- 视觉与交互测试：确保在桌面端（1920x1080）、平板端（768x1024）、手机端（375x667）无边界剪切、无非预期溢出、文字省略号正常、居中基线协调。
