# 架构门禁（Architecture Gates）

本门禁把分层约定从人工约定变为可执行规则。快速门禁入口：仓库根目录运行 `./scripts/verify-fast.ps1`（编译 + 架构测试 + 前端门禁）。
完整架构演进方案、五大核心视图与 L0~L5 门禁治理体系详见 [docs/architecture/](../architecture/README.md)。

## 允许的依赖方向

```text
LinuxWebTool.Contracts          ← 零依赖（DTO / 枚举 / 业务与技术接口，纯 BCL）
        ↑             ↑
LinuxWebTool.Application     LinuxWebTool.Infrastructure  ← Dapper.AOT / SQLite / Quartz / Shell / PTY 原生库
(业务用例/状态机编排)           (纯技术适配/持久化)
        ↑             ↑
LinuxWebTool.WebHost            ← ASP.NET Core 组合根 + Routes + MCP 端点 + wwwroot 前端 (显式直接引用)
```

| 调用方 | 允许直接引用 |
|---|---|
| Contracts | 无（仅 BCL 原生类型） |
| Application | Contracts、BCL、Microsoft.Extensions.Logging.Abstractions |
| Infrastructure | Contracts、BCL、技术 NuGet（Dapper/Dapper.AOT/Microsoft.Data.Sqlite/Quartz/Microsoft.IdentityModel/Polly） |
| WebHost | Contracts（显式直接引用）、Application、Infrastructure、ASP.NET Core 框架 |

## 诊断编号

| 编号 | 含义 | 强制方式 |
|---|---|---|
| `LinuxArch001` | Contracts 引用项目或第三方包 | xUnit（程序集引用检查） |
| `LinuxArch002` | Infrastructure 禁止引用 WebHost 或 Application | xUnit |
| `LinuxArch003` | Contracts 出现 ORM / 调度 / 认证框架类型 | xUnit |
| `LinuxArch004` | 下层禁止反向引用上层（严格验证 Contracts / Application / Infrastructure / WebHost 单向拓扑） | xUnit |
| `LinuxArch005` | Routes(Controller) 接触底层持久化细节或仓储 | xUnit（源码扫描） |
| `LinuxArch007` | 命名空间与物理路径不一致 | xUnit（源码扫描） |
| `LinuxArch008` | AOT 响应类型必须为显式 DTO（禁止匿名对象 new { ... }） | xUnit（源码扫描） |
| `LinuxArch009` | 关键控制器接口必须存在 Minimal API 生成映射 | xUnit（源码契约检查） |
| `LinuxArch010` | 发布脚本必须启用 Native AOT 且关闭反射 JSON | xUnit（文件配置断言） |
| `LinuxArch011` | 文件删除请求必须使用 DELETE 方法 | xUnit（前端视图检查） |
| `LinuxArch012` | Controller `Http*` 特性与 `EndpointsMapper.g.cs` 方法/路径不一致 | xUnit（源码契约检查） |
| `LinuxArch013` | SQLite 仓储层 Guid/Id 字段比较必须声明 `COLLATE NOCASE`（防止 .NET Guid 大写参数匹配失败） | xUnit（源码扫描） |
| `LinuxArch014` | SQLite DDL 表主键定义必须声明 `COLLATE NOCASE` | xUnit（源码扫描） |
| `LinuxArch015` | Minimal API GET 查询模型属性禁止使用非空值类型（必须为可空或引用类型，防止无参请求 400/500） | xUnit（反射契约检查） |
| `LinuxArch016` | SQLite 实体按 Guid 查询支持大小写混合真实数据库验证 | xUnit（SQLite 动态集成测试） |
| `LinuxArch017` | Minimal API 可选 `[FromBody]` 参数禁止直接声明为委托参数（须用 `ctx.Request.HasJsonContentType()` 动态解析，防止空 Body 或无 Content-Type 报 404） | xUnit（源码契约检查） |
| `LinuxArch018` | Native AOT 环境下禁止调用无显式源生成上下文的 `JsonSerializer` / `WriteAsJsonAsync` / `ReadFromJsonAsync` 反射重载 | xUnit（源码 AST/语法检查） |
| `LinuxArch019` | WebHost 项目必须显式直接引用 Contracts 项目，禁止依赖隐式传递 | xUnit（csproj 依赖检查） |
| `LinuxArch020` | 后端精确基线引擎健康检测与失效（Stale）条目防假绿拦截 | xUnit（基线引擎自检） |
| `LinuxArch021` | 所有生产项目直接引用拓扑图无环检测（Acyclic DAG） | xUnit（拓扑排序检查） |
| `LinuxArch022` | IHostedService 双重注册守卫（防止后台单例服务解析失败） | xUnit（源码注册断言） |
| `LinuxArch023` | 生产项目根目录必须遵循物理架构规范白名单（仅允许 Features/、Composition/、Shared/ 等） | xUnit（物理目录扫描） |
| `LinuxArch024` | 所有控制器必须在 ApiKeyMiddleware 中显式声明权限归属（拒绝或绑定模块，严禁默认漏控） | xUnit（控制器路由与中间件匹配断言） |
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

- **实体与数据库适配放 Infrastructure**：Dapper.AOT 与 SQLite 表特性属于持久化实现细节，Contracts 保持纯净。
- **业务用例下沉 Application**：编排、状态机与参数决策集中在 `LinuxWebTool.Application`。
- **Controller 不触碰底层仓储**：数据访问与流程编排经由 Application 用例。
- **前端零构建**：Vue3 ESM + importmap + 本地 vendor 自托管（内网零外网依赖），`vendor/` 目录不参与门禁扫描。
- **事件绑定必须 `method()`**：Vue 完整版运行时编译会把裸标识符处理器误提升到 `with(_ctx)` 作用域之外，导致 handler 为 undefined（详见 FE-TEMPLATE-REF 规则说明）。

## 基线纪律

- 前端基线：`wwwroot/frontend-gate-baseline.json`；
- 后端基线：`tests/LinuxWebTool.ArchitectureTests/backend-baseline.json`。
- **基线铁律**：只用于冻结存量技术债务，总量只能下降；新代码触发门禁时就地修复，不得扩充基线；任何条目修复后若未从基线删除，门禁将作为失效（Stale）条目报错失败（防假绿）。

## 扩展门禁

### EasyTier 地址模式门禁

- `FE-EASYTIER-IP-MODE`：`frontend-gate.cjs` 自动执行 `tests/frontend/easytier-static-ip.test.mjs`。加载实际 EasyTier 组件及 Vue 响应式实现，隔离网络和生命周期，调用新建/编辑的保存事件并检查提交请求；静态 IPv4 必须关闭 DHCP，空地址必须保留用户的 DHCP 选择，原始 TOML 不被改写。
- 包含旧错误写法的内存回归样例，验证断言确实能够识别“静态 IP + DHCP=true”。只改变测试内存中的模块，不修改产品文件。
- `EasyTierStaticIpTests` 保护请求与持久化实体两条 TOML 生成路径；`EasyTierFunctionalTests` 的创建、更新流程检查 API 返回的有效 TOML，防止后端遗漏静态地址优先规则。
- 已接入现有 CI 和 `scripts/verify-fast.ps1`，无需新增工作流。前端行为门禁不可通过静态违规基线豁免，测试缺失、执行错误、超时或断言失败都会返回非零退出码。
- 单独运行前端规则：`node --experimental-vm-modules --test tests/frontend/easytier-static-ip.test.mjs`；完整前端门禁：`node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs`。不需要安装新依赖，也不连接 EasyTier 服务器或创建 TUN 网卡。

新增规则遵循测试先行：先写一个会失败的最小样例（xUnit 用例或 frontend-gate 规则 + 临时违规文件），再实现规则，最后补充无违规反例。

### EasyTier 多节点与故障隔离门禁

- `LinuxArch020`：`EasyTierSafetyTests` 执行网段检查行为，保护重叠/包含关系、本机网段、裸 IP、Raw TOML 覆盖及未知 DHCP 网段，检查不同网段的通过反例。
- `LinuxArch021`：原生调用超时仍保留唯一槽位，后续调用不得新建工作线程；仅模拟阻塞，不调用真实 FFI 或更改网络。
- `LinuxArch022`：`EasyTierLifecycleSafetyTests` 使用真实节点仓储，保护并发启停串行边界、重复启动复用、启动前拒绝冲突、改名停止旧名称及停止失败禁止重启。
- `FE-EASYTIER-REFRESH`：前端门禁加载真实 EasyTier/Vue 组件，模拟迟到请求与时钟；上一轮未完成时不得重复发起节点查询，失败后忙碌状态必须恢复并允许下一轮。查询必须指定超时。
- 后端用例自动纳入既有架构/集成测试；前端用例直接由 `frontend-gate.cjs` 执行，缺失、失败或超时均返回非零退出码，不允许基线豁免。方案及边界见 `docs/easytier-multi-node-safety-plan.md`。
