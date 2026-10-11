# 导航栏下拉子菜单移动至主菜单项异常消失问题调研与修复方案

## 1. 问题背景与现象描述 (Problem)

### 1.1 触发场景
在桌面端顶部导航栏中，包含二级子项的逻辑分组菜单（如“安全与审计”、“运维控制”、“存储与媒体”、“网络与穿透”）：
- 当鼠标悬浮在主菜单项上或其下拉浮层上时，下拉菜单正常展示。
- 当鼠标光标位于下拉子菜单（如“系统日志”、“执行历史”等子菜单项）中，随后**缓慢向斜上方或垂直上方移动**回到对应的主菜单项（如 `[安全与审计 ▾]`）时，子菜单会在光标移至主菜单项表面或过渡途中**瞬间消失/闪烁关闭**。

### 1.2 用户体验痛点
- 用户本意是在主菜单项与子菜单项之间自如游走（例如再次点击主菜单项收起、查看主菜单状态、或平移至相邻导航栏），但由于子菜单在光标回移到主菜单项时非预期关闭，造成交互断裂和界面闪烁。
- 违反了现代桌面端 Web 界面悬浮桥接（Hover Bridge / Safe Triangle）的平滑过渡预期。

---

## 2. 根因剖析 (Root Cause)

经过对 `src/LinuxWebTool.WebHost/wwwroot/app/components/AppLayout.js` 的事件生命周期与 DOM 层次结构深入调研，确认该问题由以下两个主要缺陷复合导致：

### 2.1 根因一：子浮层冗余的 `mouseleave` 触发了无法被取消的关闭计时器 (Event Leakage)

原模板代码结构如下：
```html
<div
  class="relative shrink-0"
  @mouseenter="openDropdown(group.key)"
  @mouseleave="closeDropdown()"
>
  <button
    type="button"
    class="nav-link flex items-center gap-1 shrink-0 whitespace-nowrap cursor-pointer select-none"
    :class="{ active: isGroupActive(group) || activeMenu === group.key }"
    @click.stop="toggleMenu(group.key)"
  >
    <span>{{ group.label }}</span>
    <span ...>▾</span>
  </button>

  <!-- 下拉浮层容器 -->
  <div
    v-if="activeMenu === group.key"
    class="absolute top-full left-0 pt-1.5 z-50"
    @mouseenter="openDropdown(group.key)"
    @mouseleave="closeDropdown()"
  >
    <!-- 子项链接列表 -->
  </div>
</div>
```

**事件执行时序问题分析：**
1. 下拉浮层容器 `<div class="absolute ...">` 自身绑定了 `@mouseleave="closeDropdown()"`。
2. 当鼠标光标从浮层向上移动并跨越边界进入主菜单 `<button>` 时：
   - 鼠标离开了下拉浮层元素，触发该子元素的 `mouseleave` 事件。
   - `closeDropdown()` 被调用，启动了 200ms 的延时关闭计时器：
     ```js
     closeTimer = setTimeout(() => {
       activeMenu.value = null;
       closeTimer = null;
     }, 200);
     ```
3. 鼠标随后进入 `<button>` 元素：
   - 根据 **W3C DOM Level 3 UI Events 规范**，指针在同一父节点（`<div class="relative shrink-0">`）的两个子元素（浮层 `div` 与 `<button>`）之间转移时，**父级容器绝不会重复触发 `mouseenter` 事件**（因为光标一直在父级容器的子树内部）。
   - 同时，**`<button>` 元素自身并未绑定 `@mouseenter` 事件处理函数**。
4. 致命后果：
   - 进入 `<button>` 后，没有任何逻辑调用 `clearTimeout(closeTimer)` 或 `openDropdown(group.key)`。
   - 200ms 计时器照常到期，执行 `activeMenu.value = null`，导致下拉子菜单直接销毁。
   - 最终呈现给用户的现象就是：**鼠标明明正停留在主菜单项按钮正上方，子菜单却在眼皮底下自动消失了**！

### 2.2 根因二：组件几何非对称性与延迟窗口过短 (Asymmetric Geometry & Insufficient Buffer)

1. **几何不对称**：
   - 主菜单按钮宽度仅约 `80px ~ 100px`；
   - 下拉浮层的宽度为 `w-56`（`14rem = 224px`），左侧与按钮对齐，右侧产生超出按钮右边缘约 `120px+` 的悬空区。
2. **慢速斜向轨迹穿空**：
   - 当用户从子菜单较宽的右半部（如子项描述文字处）向左上方的主菜单按钮缓慢移动时，鼠标会穿过浮层右上角与主按钮右侧之间的空白导航栏区域（属于 `<nav>` 背景）。
   - 在这片区域中，鼠标短暂离开了整个分组的 DOM 树，触发了父级容器的 `mouseleave`。
3. **200ms 延迟过短**：
   - 用户慢速移动鼠标时，斜向跨越这段距离通常耗时 `250ms ~ 400ms`，原先设定的 200ms 防抖延迟在鼠标抵达主菜单按钮前就已过期触发关闭。
   - 当鼠标最终抵达主菜单按钮时，由于 `<button>` 缺乏 `@mouseenter`，已关闭的菜单无法被重新唤醒。

---

## 3. 修复方案 (Solution)

为了从根源上彻底解决此问题，并符合现代前端悬浮菜单的交互规范，实施以下四项协同优化：

```
                    ┌──────────────────────────────────────────────┐
                    │  父容器: div.relative.shrink-0               │
                    │  @mouseenter="openDropdown(group.key)"       │
                    │  @mouseleave="closeDropdown()" (延时 300ms)  │
                    └───────┬──────────────────────────────┬───────┘
                            │                              │
                            ▼                              ▼
             ┌─────────────────────────────┐ ┌─────────────────────────────┐
             │ 主菜单按钮: button.nav-link │ │ 下拉浮层: div.absolute      │
             │ + @mouseenter="openDropdown"│ │ - 移除冗余 @mouseleave      │
             │   (进入即时清除关闭计时器)   │ │ + 仅保留 @mouseenter        │
             └─────────────────────────────┘ └─────────────────────────────┘
                            ▲                              ▲
                            └──────── 自由双向平滑切换 ─────┘
                                  (无泄漏关闭计时器)
```

### 3.1 核心修复 1：为主菜单 `<button>` 补充 `@mouseenter="openDropdown(group.key)"`
- **目的**：打通进入主菜单按钮的唤醒与保活通道。
- **效果**：无论光标是从子菜单垂直上移，还是从外部斜向掠过后进入主按钮，只要光标接触到 `<button>`，立即调用 `openDropdown(group.key)`，即时取消任何排队中的 `closeTimer`，确保下拉菜单稳定展开不消失。

### 3.2 核心修复 2：移除子浮层冗余的 `@mouseleave="closeDropdown()"`
- **目的**：理清 DOM 事件层级职责，消除组件内部移动导致的错误关闭事件。
- **效果**：子浮层与 `<button>` 都在外层 `<div class="relative shrink-0">` 内，两者之间的移动纯属组件内部状态转移。移除子浮层的 `mouseleave` 后，光标在子菜单和主菜单按钮之间移动时不再触发任何错误的关闭计时器。移出整个组件区域（向左、向右或向下）时，由外层容器的 `@mouseleave` 统筹关闭。

### 3.3 核心修复 3：防抖延迟时间调优为 300ms（原 200ms）
- **目的**：为用户的慢速和斜向移动提供安全平滑的过渡容错窗口。
- **效果**：300ms 是业界各大成熟组件库（如 Element Plus、Radix UI、Bootstrap）推荐的最佳悬浮容错时间，既能杜绝慢速移动中途误关闭，又不会让用户感到菜单粘滞不闭合。

### 3.4 辅助优化 4：顶级直接跳转链接补充 `@mouseenter="closeDropdownImmediate()"`
- **目的**：避免悬浮至无下拉菜单的直达项（如“概览”）时残留 300ms 悬浮菜单。
- **效果**：光标移入直达单项链接时立即收起展开的下拉框，提供干脆利落的同级切换响应。

---

## 4. 影响面与架构合规性评估

1. **影响范围**：
   - 仅涉及全局导航布局组件 `src/LinuxWebTool.WebHost/wwwroot/app/components/AppLayout.js`。
   - 不改变任何路由结构、数据接口或移动端抽屉导航逻辑。
2. **前端架构门禁合规性**：
   - 符合 `FE-TEMPLATE-REF`：所有模板事件调用均显式使用括号（`openDropdown(group.key)`、`closeDropdown()`、`closeDropdownImmediate()`）。
   - 符合 `FE-TAILWIND-CLASS`：不引入非标 Tailwind 边距类。
   - 符合所有快速门禁检查规则。
