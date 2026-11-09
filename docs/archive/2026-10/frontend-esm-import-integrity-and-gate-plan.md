# 前端原生 ESM 导入导出完整性故障分析与同类门禁防御方案

## 1. 问题现场与故障原因

### 1.1 报错日志
```
vue-router.esm-browser.js:3561 SyntaxError: The requested module '../../api/client.js' does not provide an export named 'httpGet' (at ExtensionCodeModal.js:3:10)
vue-router.esm-browser.js:3561 SyntaxError: The requested module '../../api/client.js' does not provide an export named 'httpGet' (at WorkerCodeModal.js:3:10)
```

### 1.2 故障现象
用户在访问“扩展服务”或“函数服务”页面时，Vue Router 执行路由懒加载对应的组件模块（`ExtensionCodeModal.js` 与 `WorkerCodeModal.js`），浏览器原生 ES 模块加载器静态解析其依赖树时抛出 `SyntaxError`，导致路由跳转被未捕获异常中断，页面白屏/无法访问。

### 1.3 根因分析 (Root Cause)
1. **代码层面**：
   在实现扩展服务与 Worker 在线编辑组件时，文件头部误写了 `import { httpGet, httpPost } from '../../api/client.js'`，而底层网络客户端 `client.js` 只导出了统一入口函数 `export async function http(url, { method = 'GET', ... })`、`httpUpload`、`httpDownload`，并未导出 `httpGet` 和 `httpPost`。
2. **门禁与测试层面（漏洞本质）**：
   - 现有的 `frontend-gate.cjs` 包含 15+ 项规则（检查原生 fetch、localStorage 越权、Tailwind 类名、标签平衡等），但全部基于正则表达式与模板文本检查，**缺乏 ES Module 符号导出与导入的双向一致性校验**。
   - 现有的 `tests/frontend/` 单元测试仅针对特定 store 和 EasyTier 逻辑，**未对全量前端 JS 文件树执行 ESM 加载冒烟测试**。
   - 因此，当组件使用了不存在的导出方法时，后端编译 0 警告、前端架构门禁 100% 绿灯，严重漏洞逃逸至生产环境。

---

## 2. 业界主流方案对比与借鉴

| 方案 | 原理 | 优缺点 | LinuxWebTool 适用性评估 |
| :--- | :--- | :--- | :--- |
| **Vite / Rollup 静态打包** | 在打包编译时解析 AST，若发现未导出符号直接报错 `is not exported by` | 依赖 Node 编译打包工具链，产物非原生 ESM，违背本项目“零打包构建、原生轻量部署”设计原则 | ❌ 不采用（保留原生 ESM 零构建优势） |
| **Node.js 动态 ESM Dry-run 遍历导入** | 使用 Node.js 18+ 原生 ESM 动态 `import()` 递归遍历所有前端 JS 文件，模拟浏览器加载依赖树 | **极简、零依赖、100% 复刻浏览器原生报错行为**，单次运行仅耗时 100ms 左右 | ✅ **强烈推荐，作为自动化测试第一防线** |
| **AST / 静态符号提取交叉校验门禁** | 在 `frontend-gate.cjs` 中解析 `import { x } from 'y'` 与目标文件的 `export ...` 符号集合做集合求差 | 运行在快速门禁前置，不依赖 Node.js 执行环境的 mock，语法检查覆盖度高 | ✅ **强烈推荐，作为前置架构门禁第二防线** |

---

## 3. 具体落地解决方案

### 3.1 客户端层防御（增强 API 韧性）
在 `api/client.js` 中正规补齐 `httpGet` 与 `httpPost` 导出：
```javascript
export async function httpGet(url, options = {}) {
  return http(url, { ...options, method: 'GET' });
}

export async function httpPost(url, body = undefined, options = {}) {
  return http(url, { ...options, method: 'POST', body });
}
```
同时在 `ExtensionCodeModal.js` 与 `WorkerCodeModal.js` 中严格规范调用。

### 3.2 静态门禁规则：`FE-IMPORT-EXPORT`
在 `frontend-gate.cjs` 中增加静态导入导出一致性门禁：
1. 扫描 `src/LinuxWebTool.WebHost/wwwroot/app/**/*.js`；
2. 提取所有形如 `import { a, b, c } from '...'` 的解构导入；
3. 解析对应的本地相对模块文件，提取所有 `export (function|const|let|var|class|async function)` 以及 `export { ... }` 符号；
4. 校验每一个导入符号是否均存在于目标模块导出集中，若不存在立即阻断构建并输出精准文件名与行号。

### 3.3 白盒测试：`tests/frontend/esm-integrity.test.mjs`
新增自动化单元测试：
1. 递归扫描 `wwwroot/app` 下的所有 `.js` 业务组件、视图、工具库；
2. Mock 浏览器基础环境（`window`, `localStorage`, `sessionStorage`, `document`）；
3. 顺序执行动态 `await import(fileUrl)`；
4. 保证未来任何人新增或重构组件时，若有拼写错误、路径错误或未导出符号，`npm test` / CI 门禁直接在 1 秒内爆红拦截。
