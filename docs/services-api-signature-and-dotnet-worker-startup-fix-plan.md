# 系统服务 API 签名契约门禁与 .NET Worker 启动自愈方案

> 状态：设计与实施完成  
> 责任模块：前端 API 路由规范 (`config.js`)、前端架构门禁 (`frontend-gate.cjs`)、函数服务运行时 (`WorkersManager.Defaults.cs` / `WorkersView.js`)

---

## 1. 故障现象与根因调查 (Root Cause Analysis)

### 现象 1：Systemd 与 Docker 服务子模块接口调用抛出 TypeError
- **报错信息**：
  - `请求失败: API.services.dockerContainers is not a function`
  - `网络请求失败: API.services.systemdUnits is not a function`
- **代码根因**：
  在 `src/LinuxWebTool.WebHost/wwwroot/app/config.js` 中，服务中枢子模块的相关端点被错误定义成了静态纯字符串：
  ```javascript
  // 错误的原定义（静态字符串）
  services: {
    systemdUnits: '/Services/Systemd/Units',
    systemdJournal: '/Services/Systemd/Journal',
    systemdUnitFile: '/Services/Systemd/UnitFile',
    dockerContainers: '/Services/Docker/Containers',
    dockerLogs: '/Services/Docker/Logs',
  }
  ```
  而在消费端组件中：
  - `DockerSubmodule.js` 调用了 `API.services.dockerContainers(true)` 以及 `API.services.dockerLogs(containerId, 200)`；
  - `SystemdSubmodule.js` 调用了 `API.services.systemdUnits(keyword.value, stateFilter.value, typeFilter.value)`、`API.services.systemdJournal(unitName, 150)` 以及 `API.services.systemdUnitFile(unitName)`。
  由于配置项为字符串而非函数，运行时直接触发 JavaScript `TypeError: ... is not a function` 异常导致页面渲染失败。
- **门禁漏洞分析**：
  现有 `frontend-gate.cjs` 中的 `FE-API-METHOD` 门禁仅提取 `http(API.x.y)` 调用的顶层标识符并在 VM 沙盒中验证该路径是否在后端 `EndpointsMapper.g.cs` 存在，但未对消费端 AST 调用形态（带括号的 `CallExpression` vs 不带括号的 `MemberExpression` 引用）与 `config.js` 中对应条目的真实数据类型（`function` vs `string`）做一一对应断言，导致这一类纯静态类型错误逃逸。

---

### 现象 2：Worker 'demo-dotnet-service' 启动失败并闪退
- **报错信息**：
  - Web 界面提示：`启动 Worker 'demo-dotnet-service' 失败，请检查日志。`
  - 终端流日志捕获：`Unhandled exception. System.Net.HttpListenerException (5): 拒绝访问。 at System.Net.HttpListener.AddPrefixCore(String registeredPrefix) at System.Net.HttpListener.Start()`
- **代码根因**：
  在 `WorkersManager.Defaults.cs` 中为 `demo-dotnet-service` 生成的 `Program.cs` 脚本中使用了通配监听前缀：
  ```csharp
  var listener = new HttpListener();
  listener.Prefixes.Add($"http://*:{port}/"); // 根因所在：通配符前缀
  listener.Start();
  ```
  在 Windows 操作系统中，底层的 HTTP Server API (`http.sys`) 实施了严格的访问控制列表（URL ACL）。非管理员权限（Non-Admin）的普通用户进程尝试注册 `http://*:{port}/` 或 `http://+:{port}/` 时，操作系统安全核心将直接拒绝并返回 Win32 错误代码 `5 (ERROR_ACCESS_DENIED)`，进而导致 `HttpListener.Start()` 抛出异常并使顶层单文件进程闪退退出。
  此外，在前端新建向导 `WorkersView.js` 的 C# 模板生成器中也存在相同的不可降级通配硬编码。

---

## 2. 业界主流方案与调研对比

| 架构维度 | 业界方案 A (TypeScript/OpenAPI/tRPC) | 业界方案 B (微内核轻量门禁 - 本系统采用) | 差异与优势 |
| :--- | :--- | :--- | :--- |
| **API 调用类型校验** | 引入 `tsc` 编译链路、生成 TypeScript 声明文件与 OpenAPI CodeGen（如 Orval / Aspida） | 在零构建原生 ESM 架构下，通过静态代码分析门禁 (`FE-API-SIGNATURE`) 扫描代码调用签名 | 保持纯前端 0 npm 依赖、0 打包编译极速加载，同时达到等同于 TS 编译期的类型契约校验强度 |
| **Windows HTTP 监听权限** | 强制要求以管理员运行或预先执行 `netsh http add urlacl` | 运行时**双轨弹性降级模式**：优先尝试全网卡监听，若遇 `HttpListenerException (5)` 自动安全降级至 `http://localhost:{port}/` 与 `http://127.0.0.1:{port}/` | 真正实现开箱即用，无需任何提权与管理员操作；且反向代理本来就是转发至 `127.0.0.1`，完全无缝兼容 |

---

## 3. 具体修复与技术实施

### 1. 修复 `config.js` 服务中枢端点签名
将服务中枢的相关端点重构为支持查询参数与动态路径的函数：
- `systemdUnits(keyword, state, type)`：支持通过 `URLSearchParams` 组装 `?keyword=...&state=...&type=...`；
- `systemdJournal(unit, lines = 100)`：支持动态构建带 unit 与 lines 的日志查询串；
- `systemdUnitFile(unit)`：构建指定 unit 的配置文件读取路径；
- `dockerContainers(all = true)`：支持布尔过滤查询；
- `dockerLogs(containerId, tail = 200)`：支持带容器 ID 和保留行数的日志路径；
- `extensionLogs(id, lines = 150)`：统一函数化与向后兼容。

### 2. 增强前端架构门禁 (`frontend-gate.cjs`)
1. **新增 `FE-API-SIGNATURE` 规则**：
   - 提取代码中所有对 `API.<module>.<property>` 的访问；
   - 区分 `CallExpression`（代码中带调用括号 `(...)`）与纯值传递（传给 `http(...)`）；
   - 断言：若代码作为函数调用，`config.js` 中必须为 `function`；若作为纯字符串传递，`config.js` 中必须为 `string`；
   - 在沙盒中执行所有函数型端点，验证其返回值为有效以 `/` 开头的非空路径。
2. **修正 `normalizeRoute` 查询参数剔除**：
   - 将 `pathValue.split('?')[0]` 加入 `normalizeRoute`，确保带 query 字符串的动态端点能够精准匹配后端 C# 路由表。

### 3. C# 极简服务运行时韧性降级
在 `WorkersManager.Defaults.cs` 与 `WorkersView.js` 中更新 C# 启动引导逻辑，引入自愈双轨降级：
```csharp
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var listener = new HttpListener();
try
{
    listener.Prefixes.Add($"http://*:{port}/");
    listener.Start();
}
catch (HttpListenerException)
{
    // 非管理员模式下无权限监听 http://*，安全降级监听 localhost 与 127.0.0.1
    listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
}
```
并在 `WorkersManager.Defaults.cs` 初始化时增加历史存量自动检测修复（若是旧版生成且包含通配符前缀，自动原地升级自愈）。

---

## 4. 门禁与自动化验证

1. **前端自动化行为测试**：
   新增 `tests/frontend/api-signatures-and-callability.test.mjs`，对 `config.js` 所有的 API 模块进行 100% 签名遍历，确保 0 个非函数误调。
2. **前端架构门禁**：
   运行 `node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs`，确保 `FE-API-SIGNATURE` 与 `FE-API-METHOD` 全部通过。
3. **C# 单元与集成测试**：
   运行 `dotnet test tests/LinuxWebTool.UnitTests` 与 `dotnet test tests/LinuxWebTool.ArchitectureTests`，验证 Native AOT 零破坏。
