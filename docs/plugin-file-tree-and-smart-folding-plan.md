# 插件中心代码编辑器文件树状显示与智能折叠架构设计方案

> **文档标识**：`docs/plugin-file-tree-and-smart-folding-plan.md`  
> **关联模块**：[`src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/PluginCodeModal.js`](../src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/PluginCodeModal.js)  
> **核心工具**：[`src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js`](../src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js)  
> **核心目标**：将插件中心代码编辑器左侧原本生硬平铺的 20~50+ 文件扁平列表，升级为现代化、层次分明、支持目录折叠/展开与当前活动文件智能聚焦的**树状文件管理器（File Tree View）**；结合适配器模式、策略模式与状态机模式，确保零构建下的高性能与低维护成本。

---

## 1. 背景与问题定义

在 LinuxWebTool 插件中心（Plugin Platform）中，每个插件由独立的目录包承载，包含前后端代码、清单配置、图标资源与工具声明（典型如 20~50 个文件）：
- 根目录文件：`manifest.json`, `package.json`, `README.md`, `icon.png`；
- 后端脚本目录：`src/index.js`, `src/handlers/action.js`, `src/handlers/event.js`, `src/utils/format.js`；
- 前端 Web 视图目录：`web/dist/index.html`, `web/dist/assets/index.css`, `web/dist/assets/index.js`。

### 1.1 现状缺陷：平铺展示（Flat Listing）的四大痛点
1. **视觉信息杂乱，深度上下文丢失**：所有深层嵌套文件（如 `src/handlers/action.js`）与根目录文件同级排列，无法直观反映代码包的模块划分；
2. **长路径截断与辨识困难**：左侧侧边栏宽度通常有限（`w-56` / 14rem），长路径被强制 `truncate` 后，开发者只能看到 `.../index.js`，无法分辨其是根入口、工具入口还是前端入口；
3. **滚动开销剧增**：27+ 个文件全部平铺展示，占用大量纵向高度，查找定位文件依赖视线全局扫视；
4. **缺乏聚焦机制**：用户切换活动文件时，列表没有任何折叠聚焦状态，干扰当前编码视线。

因此，**将扁平文件列表改造为支持自动折叠、按层级缩进、图标语义化与当前文件智能聚焦的树状视图**，是提升插件开发与调试体验的关键环节。

---

## 2. 深度头脑风暴：可行性、逻辑欠缺与性能瓶颈

### 2.1 递归组件 vs 扁平 DFS 遍历树（架构选型对比）

在 Vue 3 中实现树状视图，业界通常有两种主流模型：

| 选型维度 | 方案 A：递归组件模型 (`<FileTreeItem>`) | 方案 B：扁平深度优先遍历树 (`Flattened DFS Tree`) |
| :--- | :--- | :--- |
| **实现原理** | 每个目录项递归挂载自身组件 `<FileTreeItem v-for="child in node.children" />` | 将整棵多叉树在内存中按 DFS 顺序拍平为带 `depth` 属性的一维数组，根据目录折叠状态动态过滤得到 `visibleNodes`，外层单个 `v-for` 渲染 |
| **DOM 嵌套深度** | 随目录深度线性加深（多层嵌套 `<ul><li><ul><li>...`） | 恒定为 1 层扁平列表（极度干净） |
| **组件开销** | 27 个节点可能创建数十个子组件实例，产生额外的响应式开销 | **0 个子组件**，纯函数式计算与单层虚拟映射 |
| **门禁契合度** | 容易触发 `FE-FILE-SIZE-LIMIT` 与组件深层事件冒泡链条复杂性 | **完美契合**，纯逻辑沉淀在工具库，Modal 组件只增几十行代码 |
| **动画与样式** | 嵌套内边距累加，复杂 CSS 继承 | 简单的 `style="{ paddingLeft: node.depth * 12 + 'px' }"` |

> **选型决策**：  
> 坚定采纳 **方案 B（扁平深度优先遍历树）**。在零构建（Zero-Build）架构下，扁平 DFS 模型兼具极致渲染性能（$< 0.5\text{ms}$ 响应）与简练的单层模板。

---

### 2.2 潜在逻辑欠缺与边界防御（Edge Cases）

在设计文件树算法与交互逻辑时，必须防御以下典型边界陷阱：

1. **路径分隔符混乱（Windows `\` vs Linux `/`）**：
   - 插件打包上传可能来自 Windows 本地开发环境，路径可能混用 `src\utils/format.js`。
   - *防御对策*：适配器输入层强制经过 `normalizeSlashes`，统一归一化为 POSIX `/` 分隔符。
2. **单分支深层目录（Single-child Directory Chain）**：
   - 如 `src/handlers/custom/`，如果用户需要一层层点击展开极其繁琐。
   - *防御对策*：默认“智能展开”当前 `activeFile` 的**整条祖先链路**；用户点击该文件时，祖先目录全自动保持展开状态。
3. **空路径与根文件混杂**：
   - 根目录下有普通文件（如 `manifest.json`），也有多级目录。
   - *防御对策*：严格遵循 **“目录优先（Folders First）+ 字母升序”** 的业界标准排序规范（VS Code / WebStorm 一致习惯），避免目录与文件犬牙交错。
4. **单文件代码行数硬顶（`FE-FILE-SIZE-LIMIT`）**：
   - `PluginCodeModal.js` 属于非视图组件，代码行数上限严格为 **400 行**（当前已占用 244 行）。若直接在组件内写完树构建、折叠状态、排序算法，将极易突破 400 行导致 CI 门禁失败！
   - *防御对策*：严格践行单一职责原则，将树的构建、展开状态机、图标映射全部下沉到独立纯函数工具模块 [`src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js`](../src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js) 中，由该工具输出响应式状态与纯函数，`PluginCodeModal.js` 仅做简明调用，控制在 290 行以内。

---

## 3. 设计模式优化与架构设计

```mermaid
flowchart TD
    subgraph Input [异构平铺数据源]
        RawList["string[] 原始文件路径 (如: ['src/index.js', 'manifest.json', ...])"]
    end

    subgraph AdapterLayer [文件树适配器 (Adapter Pattern)]
        BFT["buildFileTree(files) 适配器函数"]
        NodeTree["多叉树结构 (DirectoryNode / FileNode)"]
        SortStrategy["FoldersFirst 排序策略 (Strategy Pattern)"]
    end

    subgraph StateMachineLayer [折叠与聚焦状态机 (State Machine Pattern)]
        FSM["expandedFolders (Set<string>) 折叠状态机"]
        AutoExpand["智能聚焦: EXPAND_ANCESTORS(activeFile)"]
        Toggle["快捷流转: TOGGLE / EXPAND_ALL / COLLAPSE_ALL"]
    end

    subgraph OutputView [视图渲染控制]
        VisibleNodes["visibleTreeNodes 扁平可视节点集合"]
        UI["PluginCodeModal 左侧树状侧边栏 (带语义图标与缩进)"]
    end

    RawList --> BFT
    BFT --> SortStrategy --> NodeTree
    NodeTree --> FSM
    FSM --> AutoExpand
    FSM --> Toggle
    AutoExpand --> VisibleNodes
    Toggle --> VisibleNodes
    VisibleNodes --> UI
```

### 3.1 适配器模式（Adapter Pattern）
- **职责**：将后端返回的简单字符串数组 `string[]` 适配为统一的、高语义的 `TreeNode` 结构：
  ```javascript
  {
    name: 'action.js',              // 显示名称
    fullPath: 'src/handlers/action.js', // 完整相对路径
    isDir: false,                    // 是否为目录
    depth: 2,                        // 缩进深度
    parentPath: 'src/handlers',      // 父目录路径
    extension: 'js',                 // 扩展名
    icon: '🟨'                       // 语义化图标
  }
  ```

### 3.2 策略模式（Strategy Pattern）
- **折叠展开策略（Folding Strategy）**：
  - `ActivePathFocusedStrategy`（默认策略）：仅保留活动文件所在的祖先链条处于展开状态，其他深层目录默认闭合，保证视图视觉噪音最低；
  - `ExpandAllStrategy`：一键全展开，适合快速全局浏览；
  - `CollapseAllStrategy`：一键全收拢，仅展示根层级。
- **图标渲染策略（Icon Strategy）**：
  - 针对文件后缀动态指派开发友好图标：
    - `js` / `ts` / `mjs`: 🟨
    - `json`: 🟩
    - `html`: 🟧
    - `css` / `scss`: 🟦
    - `md` / `txt`: 📝
    - `bak`: 📦
    - `sh` / `py`: ⚡
    - 文件夹（折叠 `▸ 📁`，展开 `▾ 📂`）。

### 3.3 状态机模式（State Machine Pattern）
- **状态集合**：`expandedFolders: Set<string>` 记录所有当前处于展开态的目录绝对路径。
- **状态动作（Actions）**：
  - `TOGGLE(dirPath)`：反转目标目录的展开状态；
  - `ENSURE_PATH(filePath)`：拆解文件父路径链条，确保其所有层级目录全部加入展开集；
  - `COLLAPSE_ALL()`：重置展开集合为空；
  - `EXPAND_ALL()`：将整棵树的所有目录全量加入展开集。

---

## 4. 实施清单与代码落地方案

1. **创建独立工具模块**：[`src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js`](../src/LinuxWebTool.WebHost/wwwroot/app/utils/fileTree.js)
   - 纯函数式树构建、排序、拍平与图标解析；
   - 暴露 `useFileTree(fileListRef, activeFileRef)` 响应式管理钩子。
2. **重构代码编辑弹窗**：[`src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/PluginCodeModal.js`](../src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/PluginCodeModal.js)
   - 接入 `useFileTree`；
   - 头部增加快捷展开/收拢操作组；
   - 模板替换平铺按钮为带缩进、箭头与状态高亮的树节点列表。
3. **编写单元与集成测试**：[`tests/frontend/file-tree-adapter.test.mjs`](../tests/frontend/file-tree-adapter.test.mjs)
   - 验证路径切分、目录优先排序、活动文件自动展开链路与快捷收折。
4. **验证门禁与回归**：
   - 确保 `frontend-gate.cjs` 扫描通过，且满足 `FE-FILE-SIZE-LIMIT`（$\le 400$ 行）。
