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
| `FE-HTML-INLINE` | index.html 内联脚本（importmap 除外）/ 内联事件 | frontend-gate.cjs |
| `FE-API-OWNERSHIP` | API 路径字符串出现在 config.js 之外 | frontend-gate.cjs |
| `FE-NO-FETCH` | fetch() 出现在 api/client.js 之外 | frontend-gate.cjs |
| `FE-STORAGE` | Web Storage 出现在 client.js / auth.js 之外 | frontend-gate.cjs |
| `FE-IMPORT-BOUNDARY` | 跨层 import（store→views 等） | frontend-gate.cjs |
| `FE-TEMPLATE-REF` | 模板事件绑定使用裸标识符（Vue 运行时编译会错误提升导致 handler 丢失，必须 `method()`） | frontend-gate.cjs |

## 约定要点

- **实体放 Infrastructure**：SqlSugar 特性属于持久化细节，Contracts 只保留 DTO 与接口（本工具对参考项目六层结构的简化，语义一致）。
- **Controller 不触碰 SqlSugar**：数据访问一律经 `*Store` 仓储。
- **前端零构建**：Vue3 ESM + importmap + 本地 vendor 自托管（内网零外网依赖），`vendor/` 目录不参与门禁扫描。
- **事件绑定必须 `method()`**：Vue 完整版运行时编译会把裸标识符处理器误提升到 `with(_ctx)` 作用域之外，导致 handler 为 undefined（详见 FE-TEMPLATE-REF 规则说明）。

## 基线纪律

`wwwroot/frontend-gate-baseline.json` 只用于冻结存量债务：基线总量只能下降；新代码触发门禁时修复依赖，不得扩充基线；门禁发现失效条目时会提示删除。

## 扩展门禁

新增规则遵循测试先行：先写一个会失败的最小样例（xUnit 用例或 frontend-gate 规则 + 临时违规文件），再实现规则，最后补充无违规反例。
