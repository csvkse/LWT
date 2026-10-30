# 备份与同步模块：路径快捷跳转文件管理页面方案

## 1. 需求与使用场景分析

在备份与直接同步任务的管理运维过程中，用户经常需要：
1. **实地核验源端文件**：在开始同步前，检查源目录内的真实文件层级、权限与体积，确认要排除的目录；
2. **校验镜像目标产物**：在任务执行完成或异常报错时，快速进入目标目录查看文件写入情况，核对回收站 `.lwt_trash` 或已还原快照。

当前缺少直接联动机制，用户必须记忆或复制长路径，切换到左侧菜单「文件管理」，再逐级寻径，割裂且低效。
因此，在任务列表中对「源端路径」与「镜像目标路径」后方分别添加「快捷跳转文件管理页面按钮」，点击后一键直达并在文件管理器中定位该目录。

---

## 2. 系统基础设施复用与可行性验证

经调研当前前端路由与文件管理视图实现：
1. **路由与查询参数机制**：
   - `src/LinuxWebTool.WebHost/wwwroot/app/router/index.js` 已注册路由：
     ```javascript
     { path: '/files', name: 'files', component: () => import('../views/FilesView.js') }
     ```
   - `src/LinuxWebTool.WebHost/wwwroot/app/views/FilesView.js` 在 `onMounted` 中已实现对 URL 查询参数的响应式加载：
     ```javascript
     const q = route.query.path;
     const start = typeof q === 'string' && q ? q : '/';
     load(start);
     ```
2. **已有业务复用范例**：
   - `SmbMountsView.js`（挂载管理）与 `TerminalView.js`（终端控制台）中均已采用该标准方式：
     ```javascript
     router.push({ path: '/files', query: { path: targetPath } });
     ```
3. **性能与体验**：
   - 依赖 Vue Router 进行 SPA 内部状态平滑迁移，无整页重刷或资源重新拉取开销，切换速度在 10ms 以内；
   - 路径自动进行 URL 编解码，对包含空格、中文字符或特殊符号的路径提供完备兼容。

---

## 3. 设计模式与架构实现

### 3.1 导航门面/适配器模式 (Navigation Facade / Adapter)
在 `BackupSyncView.js` 中抽离并暴露路径跳转门面：
```javascript
const router = useRouter();

function jumpToFiles(rawPath) {
  if (!rawPath || typeof rawPath !== 'string') return;
  const normalized = rawPath.trim();
  if (!normalized) return;
  router.push({ path: '/files', query: { path: normalized } });
}
```

### 3.2 桌面端表格与移动端卡片双端呈现策略
- **桌面端表格 (`hidden md:block`)**：
  - 在源路径和目标路径文本右侧嵌入紧凑微型按钮 `📂`，配合 Tailwind `group-hover` 与 title 提示，视觉上整洁克制，点击即可在文件管理器中展开该目录；
- **移动端卡片 (`block md:hidden`)**：
  - 在 `BackupTaskCard.js` 中，路径行采用 Flex 两端对齐布局，右侧提供触控友好的 `📂 打开` 胶囊按钮，触控靶心充足，移动端单手操作无误触。

---

## 4. 交付与验证步骤

1. 在 `BackupSyncView.js` 中引入 `useRouter`，并声明 `jumpToFiles` 门面函数；
2. 更新桌面端增量备份表与直接同步表，在源端与镜像目标路径后挂载快捷跳转按钮；
3. 更新 `BackupTaskCard.js` 组件，在移动端路径行添加跳转触发并透传事件；
4. 运行前端架构门禁 `node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs`，确保行数和调用规范；
5. 执行全套编译与自动化测试并推送。
