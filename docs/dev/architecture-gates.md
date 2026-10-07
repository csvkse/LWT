# 架构门禁（Architecture Gates）

本门禁把分层约定从人工约定变为可执行规则。快速门禁入口：仓库根目录运行 `./scripts/verify-fast.ps1`（编译 + 架构测试 + 前端门禁）。

## 允许的依赖方向

```text
LinuxWebTool.Contracts          ← 零依赖（DTO / 枚举 / 技术接口）
        ↑
LinuxWebTool.Infrastructure     ← SqlSugar / Quartz / Shell / 文件日志 / JWT
        ↑
LinuxWebTool.WebHost            ← ASP.NET Core 组合根 + Routes + wwwroot 前端
```

| 调用方 | 允许直接引用 |
|---|---|
| Contracts | 无（仅 BCL） |
| Infrastructure | Contracts、BCL、技术 NuGet（SqlSugar/Quartz/IdentityModel/Microsoft.Extensions.*） |
| WebHost | Contracts、Infrastructure、ASP.NET Core 框架 |

## 诊断编号

| 编号 | 含义 | 强制方式 |
|---|---|---|
| `LinuxArch001` | Contracts 引用项目或第三方包 | xUnit（程序集引用检查） |
| `LinuxArch002` | Infrastructure 引用 WebHost | xUnit |
| `LinuxArch003` | Contracts 出现 ORM / 调度 / 认证框架类型 | xUnit |
| `LinuxArch004` | 下层反向引用上层 | xUnit |
| `LinuxArch005` | Routes(Controller) 直接 using SqlSugar | xUnit（源码扫描） |
| `LinuxArch007` | 命名空间与物理路径不一致 | xUnit（源码扫描） |
| `LinuxArch012` | Controller `Http*` 特性与 `EndpointsMapper.g.cs` 方法/路径不一致 | xUnit（源码契约检查） |
| `LinuxArch013` | SQLite 仓储层 Guid/Id 字段比较必须声明 `COLLATE NOCASE`（防止 .NET Guid 大写参数匹配失败） | xUnit（源码扫描） |
| `LinuxArch014` | SQLite DDL 表主键定义必须声明 `COLLATE NOCASE` | xUnit（源码扫描） |
| `LinuxArch015` | Minimal API GET 查询模型属性禁止使用非空值类型（必须为可空或引用类型，防止无参请求 400/500） | xUnit（反射契约检查） |
| `LinuxArch016` | SQLite 实体按 Guid 查询支持大小写混合真实数据库验证 | xUnit（SQLite 动态集成测试） |
| `LinuxArch017` | Minimal API 可选 `[FromBody]` 参数禁止直接声明为委托参数（须用 `ctx.Request.HasJsonContentType()` 动态解析，防止空 Body 或无 Content-Type 报 404） | xUnit（源码契约检查） |
| `LinuxArch018` | Native AOT 环境下禁止调用无显式源生成上下文的 `JsonSerializer` / `WriteAsJsonAsync` / `ReadFromJsonAsync` 反射重载 | xUnit（源码 AST/语法检查） |
| `FE-HTML-INLINE` | index.html 内联脚本（importmap 除外）/ 内联事件 | frontend-gate.cjs |
| `FE-API-OWNERSHIP` | API 路径字符串出现在 config.js 之外 | frontend-gate.cjs |
| `FE-NO-FETCH` | fetch() 出现在 api/client.js 之外 | frontend-gate.cjs |
| `FE-STORAGE` | Web Storage 出现在 client.js / auth.js 之外 | frontend-gate.cjs |
| `FE-IMPORT-BOUNDARY` | 跨层 import（store→views 等） | frontend-gate.cjs |
| `FE-TEMPLATE-REF` | 模板事件绑定使用裸标识符（Vue 运行时编译会错误提升导致 handler 丢失，必须 `method()`） | frontend-gate.cjs |
| `FE-API-METHOD` | 前端 `http/httpUpload/httpDownload` 动词与后端 mapper 契约不一致 | frontend-gate.cjs |
| `FE-TAILWIND-CLASS` | 禁止使用非标 Tailwind 尺寸/边距类（如 p-4.5，避免 padding 归零导致圆角内容剪切与贴边） | frontend-gate.cjs |
| `FE-OVERFLOW-CONFLICT` | 同一元素禁止混用 truncate 与 overflow-(auto/scroll)，防止横向滚动失效导致子项省略/截断冲突 | frontend-gate.cjs |
| `FE-TEMPLATE-WINDOW` | Vue 模板禁止直接访问全局 window.*，必须通过 setup 显式 return 暴露 | frontend-gate.cjs |
| `FE-TEMPLATE-VALUE-REF` | Vue 模板禁止访问 setup 暴露变量的 .value（模板自动解包，写 .value 为反模式） | frontend-gate.cjs |
| `FE-TEMPLATE-TAG-BALANCE` | Vue 组件模板关键结构标签（div/table/thead/tbody/section/aside）必须严格开闭平衡 | frontend-gate.cjs |
| `FE-TERMINAL-SESSION-HEAL` | TerminalView 必须具备失效会话自动重置与新建降级机制（防止服务重启或会话过期后前端卡死） | frontend-gate.cjs |

## 约定要点

- **实体放 Infrastructure**：SqlSugar 特性属于持久化细节，Contracts 只保留 DTO 与接口（本工具对参考项目六层结构的简化，语义一致）。
- **Controller 不触碰 SqlSugar**：数据访问一律经 `*Store` 仓储。
- **前端零构建**：Vue3 ESM + importmap + 本地 vendor 自托管（内网零外网依赖），`vendor/` 目录不参与门禁扫描。
- **事件绑定必须 `method()`**：Vue 完整版运行时编译会把裸标识符处理器误提升到 `with(_ctx)` 作用域之外，导致 handler 为 undefined（详见 FE-TEMPLATE-REF 规则说明）。

## 基线纪律

`wwwroot/frontend-gate-baseline.json` 只用于冻结存量债务：基线总量只能下降；新代码触发门禁时修复依赖，不得扩充基线；门禁发现失效条目时会提示删除。

## 扩展门禁

新增规则遵循测试先行：先写一个会失败的最小样例（xUnit 用例或 frontend-gate 规则 + 临时违规文件），再实现规则，最后补充无违规反例。
