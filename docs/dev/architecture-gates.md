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
| `LinuxArch025` | L4 端口转发服务必须支持双栈监听、真实运行时状态暴露与端口冲突自愈 | xUnit（套接字行为与契约验证） |
| `LinuxArch026` | 单源文件代码行数与单一职责门禁（通用 <= 700 行，MCP <= 400 行，Contracts <= 300 行；存量 4 个适配器冻结硬顶基线防假绿） | xUnit（全源码扫描与行数基线硬顶断言） |
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
| `FE-FILE-SIZE-LIMIT` | 前端单文件代码行数门禁（视图 <= 2000 行，非视图模块 <= 400 行） | frontend-gate.cjs |
| `FE-CONTAINER-HEIGHT-LIMIT` | 禁止前端列表容器无限长度延伸，包含动态长列表（v-for）的容器必须具备逻辑高度约束（分页适配器 usePagination、max-h-* 或 flex-1 min-h-0 + overflow-y-auto） | frontend-gate.cjs |
| `FE-THEME-FORM-CONTROL-DARK` | 所有表单输入控件（input/select/textarea）必须显式具备暗黑主题类名（.input 等）或暗色底色，严禁回退至浏览器默认白底 | frontend-gate.cjs |
| `FE-PLUGIN-DATA-PURITY` | PluginsView 全局数据加载函数禁止传递响应式过滤参数，防止增删改查刷新后冲垮宏观统计卡片与分类树事实源 | frontend-gate.cjs |
| `FE-PLUGIN-FILTER-FSM` | 插件中心多维策略过滤、有限状态机生命周期守卫与暗黑搜索框契约行为门禁 | frontend-gate.cjs / node --test |
| `FE-PLUGIN-FILE-TREE` | 插件中心代码编辑器文件树适配器与折叠状态机行为门禁（树状多叉构建、Folders-First 排序与祖先自动聚焦） | frontend-gate.cjs / node --test |
| `LinuxArch036` | 跨平台 Shell 脚本行尾换行符 (LF) 约束与 PTY 终端自愈门禁（断言严禁 CRLF，强制 .gitattributes 与构建清洗） | xUnit（全量脚本与配置 AST/字节扫描） |
| `LinuxArch037` | 插件运行时生态一致性与多发行版包管理器适配门禁（统一 TargetFramework net10.0、Alpine 禁 apt、Debian 禁 apk） | xUnit（动态反射与指引矩阵验证） |
| `FE-CRLF-FORBIDDEN` | 前端源码严禁包含 `\r` (CRLF)，防止 Windows 编辑器污染跨平台静态资产 | frontend-gate.cjs |
| `FE-DISTRO-CMD-INTEGRITY` | 插件默认安装指引必须严格区分发行版包管理器（Alpine 禁 apt、Debian 禁 apk），且 .NET 必须与 net10 对齐 | frontend-gate.cjs / node --test |
| `LinuxArch068` | 凭据安全防御：未脱敏明文密码、真实网络地址、隧道 ID、私钥、多字符集香农熵与脱敏合规防伪门禁（结合 .githooks 提交钩子） | xUnit / .githooks/security-gate.cjs |
| `LinuxArch069` | 微服务 Webview 控制台端口契约对齐与抗覆盖门禁（动态分配端口、防止 18001 硬编码、保留网络配置切面） | xUnit / AST & 文件扫描 |
| `LinuxArch070` | OpenAI MCP Tunnel 长轮询弹性超时预算（>= 15s）与出站代理规范（socks5h 远程解析与全量环境注入）门禁 | xUnit / Go 源码与契约扫描 |


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

### 单源文件代码行数与单一职责门禁（巨石文件防御）

- `LinuxArch026`：严格防止上帝类（God Class）与巨型代码单文件回潮：
  - **通用 C# 源文件**：上限 700 行（杜绝代码逻辑过度堆砌，强制采用策略模式、外观模式或扩展方法拆分）；
  - **契约层（Contracts）**：上限 300 行（契约仅包含轻量 DTO 与接口，严禁混入重型逻辑）；
  - **MCP 模块（Features/Mcp）**：上限 400 行（彻底杜绝 `McpServerEngine` 重新膨胀为千行巨石，强制遵循 `IMcpToolProvider` 领域提供者分拆规范）；
  - **存量底层适配器白名单硬顶**：仅冻结 4 个历史高复杂度底层进程/硬件适配文件（`EasyTierNodeManager` <= 1450、`FrpTunnelInstance` <= 1100、`FfmpegArgsBuilder` <= 950、`SystemStatusProvider` <= 750）。存量只降不增，超标立毙；若文件重构降至 700 行以内，门禁会断言其必须从白名单剔除（防假绿）。
- `FE-FILE-SIZE-LIMIT`：前端 ESM 单文件门禁：
  - **视图组件（views/*.js）**：上限 2000 行（含内联响应式模板与弹窗定义）；
  - **非视图模块（api/、components/、store/、utils/）**：上限 400 行（强制组件、工具库与状态管理遵循单一职责）。

### 插件微服务与原生调度自愈门禁

- `LinuxArch038`：`ResidentExecutionStrategy` 原生微服务启动生命周期异常保护与端口防泄漏门禁：
  - 启动规格解析必须置于 try/catch 块保护之下，发生任何 `FileNotFoundException` 或启动异常时，必须由状态机标记为 `MissingRuntime` 或 `Faulted`，严禁抛出未捕获 500；
  - 发生启动异常时必须显式调用 `portManager.ReleasePort` 释放已分配端口，杜绝端口悬挂泄漏；
  - 运行时适配器必须具备三级探查自愈能力（工作目录 -> 系统 PATH -> `/app/plugins` 静态模板）。
- `LinuxArch039`：极速开发镜像闭环构建与预编译产物优先门禁：
  - `Dockerfile.dev` 构建层必须包含 `build-base` 与 `go`；
  - 优先识别预编译的 Linux x64 纯静态二进制并复用，支持多环境跨平台构建与开发提速；
  - 容器发布产物必须复制至 `/usr/local/bin` 并赋予可执行权限 (`chmod +x`)。
- `LinuxArch040`：原生插件参数解析与悬挂宏防错门禁：
  - `NativeRuntimeAdapter` 必须支持动态回环端口 `{port}`、监听地址 `{listenAddr}` 与数据目录 `{stateDir}` 的动态求值；
  - 必须成对清洗悬挂的未绑定标志（如 `--control-token-file {controlTokenFile}`），防止微服务因无法解析占位符直接崩溃。
- `LinuxArch041`：原生微服务二进制智能同步与版本防腐门禁：
  - `EnsureMcpBridgeBinaryPresent` 必须比对文件时间戳与字节长度，禁止因数据卷内旧二进制存在而放弃升级同步；
  - 包含 `/app` 与 `BaseDirectory` 根路径探查，杜绝由于外部卷挂载遮蔽导致微服务文件丢失。
- `LinuxArch042`：Go 运行时引擎与 JIT 增量构建缓存门禁：
  - `PluginRuntime.Go = 6` 枚举声明与注册必须完备；
  - `GoRuntimeAdapter` 必须使用编译缓存（JIT 增量构建，`.cache/bin/app` 与源文件时间戳指纹检测），严禁使用导致子进程游离孤儿和端口泄漏的 `go run`；
  - 必须支持宏变量 `{port}`、`{listenAddr}`、`{stateDir}` 替换，单文件扩展解析器识别 `.go` 扩展与 `go`/`golang` 标识。
- `LinuxArch043`：SOCKS5 代理微服务规范与零外部依赖门禁：
  - `plugins/socks5-proxy/plugin.json` 必须声明常驻微服务规范、Go 运行时、入口为 `cmd/socks5-proxy/main.go`、预设回环端口 18001；
  - `go.mod` 严禁引入任何第三方依赖库（保持零 require），保证纯离线开箱即用与 200ms 极速构建；
  - 完整兼容 RFC 1928 / RFC 1929 规范（无密码/用户名密码双模认证，CONNECT 隧道代理）；
  - `PluginManager.Defaults.cs` 自动同步与初始化校验。
- `LinuxArch045`：插件 Webview 沙箱权限与原生页内模态交互门禁：
  - `PluginWebviewModal.js` 宿主 iframe 必须显式声明 `allow-modals` 沙箱权限；
  - 常驻型微服务 Webview 必须直连微服务网关反向代理 (`API.plugins.gatewayUrl`)，静态 UI 资产必须使用规范资产端点；
  - `mcp-bridge` 控制台前端 HTML 严禁使用阻断式原生弹窗 (`window.prompt` / `window.confirm`)，必须采用原生页内模态框 (`#addServerModal`, `#tokenModal`, `#confirmModal`)；
  - `mcp-bridge` Go 控制面路由必须具备 SPA Fallback 守卫（非 GET 请求或未匹配 control 路由返回 404 JSON 而非 200 HTML），并支持 `/control/invoke/` 路径前缀自愈容错与网关信任头 `X-LWT-Plugin-Id`。
- `LinuxArch046`：SOCKS5 代理网段白名单与域名过滤安全门禁：
  - `plugins/socks5-proxy/cmd/socks5-proxy/acl.go` 必须支持网段前缀（`10.`、`127.`、`192.168.`）、标准 CIDR、通配符域名后缀（`*.internal`）与 RE2 正则过滤；
  - `main.go` 必须实现 RFC 1928 `repRuleViolation (0x02)` 拦截状态码；
  - 必须具备出站二次校验（Anti-DNS-Rebinding Defense）与客户端来源 IP 校验；
  - 清单文件 `plugin.json` 与 Webview 界面必须支持快捷网段配置与预设模式（`lan`, `loopback`, `all`）。
- `LinuxArch047`：Go 动态增量构建包路径解析与 SOCKS5 三维安全防御门禁：
  - `GoRuntimeAdapter.cs` 严禁将单个 `.go` 文件绝对路径直接传递给 `go build`（防止 `cmd/go` 单文件模式 `# command-line-arguments` 导致兄弟源文件符号丢失），必须解析为规范相对包目录（如 `./cmd/socks5-proxy` 或 `./.`）；
  - SOCKS5 微服务必须全面实施三维纵深防御：
    1. 维度一：标准目标 CIDR 位掩码校验与输入自动归一化（消除 `172.16.` 等文本前缀歧义）；
    2. 维度二：入站客户端 IP 访问限制（在 TCP 握手前直接短路拒绝未授权客户端，零资源占用防护网络扫描嗅探，记录 `blockedClients` 统计指标）；
    3. 维度三：用户名与密码认证（RFC 1929 规范，基于 `crypto/subtle.ConstantTimeCompare` 恒定时间防时序攻击比对，记录 `authFailures` 统计指标）；
  - 插件清单与 Webview 界面必须完整提供三维安全防御配置项（入站客户端 CIDR、目标标准 CIDR、认证模式切换与高强度随机密码生成器）。
- `LinuxArch048`：原生二进制跨平台格式校验与镜像构建防投毒门禁：
  - `Dockerfile` 与 `Dockerfile.dev` 中复用预置原生可执行文件时，必须通过 `head -c 4` 与 `printf '\177ELF'` 校验 ELF 标头魔数，禁止盲目复用异构 PE 产物；
  - `scripts/deploy-remote.ps1` 部署脚本必须检验待打包二进制前 4 字节（`0x7F 0x45 0x4C 0x46`），非 ELF 时强制调用 Linux 交叉编译或移除毒化文件；
  - `NativeBinaryValidator.cs` 校验探针必须存在于基础设施层，零分配识别 Shebang 脚本、Windows PE、Linux ELF 及 CPU 架构（AMD64 / ARM64）；
  - `NativeRuntimeAdapter` 与 `PluginManager.Defaults` 必须集成 `NativeBinaryValidator`，对数据卷历史残留毒化二进制执行自愈清理与原地重新编译。
- `LinuxArch049`：MCP 网关敏感字段变活动作与前后端契约容错门禁：
  - Go 微服务敏感字段校验器必须支持 `replace` 与 `update` 动作归一化（遵循宽进严出 Postel 法则）；
  - `SaveTunnelRequestDTO` 必须支持 `tunnelKey` 与 `presharedKey` 别名映射及 `expectedRevision` 乐观锁透传；
  - Webview 控制台前端与 C# 契约模型保持强类型字段别名一致。
- `LinuxArch050`：插件长时服务快速换端与双栈冲突避让门禁：
  - `PluginPortManager.cs` 必须支持完整双栈（IPv4 与 IPv6）探测与内核网络表（`IPGlobalProperties`）全系统端口监听检测，杜绝 `127.0.0.1` 探针盲区；
  - 端口管理器必须提供 `AllocatePortWithDetails` 方法并支持 `avoid`（自动避让并记录原因）与 `strict`（严格拒绝报错）策略；
  - `ResidentExecutionStrategy.cs` 启动长时微服务前必须通过 `.runtime/process.pid` 文件自动检测并自愈回收历史遗留孤儿进程，彻底消除僵尸进程霸占端口；
  - 控制器 `PluginsController.cs` 必须提供 `/Port/Check` 与 `/Port/Switch` 接口，支持平滑热重载；
  - 前端必须提供 `PluginPortSwitchModal.js` 组件，卡片端口徽章支持交互呼出换端浮层并提供空闲端口智能推荐。
- `LinuxArch051`：MCP 标准 JSON 配置直接导入与多格式适配门禁：
  - `internal/config/importer.go` 必须支持标准 MCP JSON 格式多态适配（Claude Desktop `mcpServers`、Cursor `mcp.json`、Zed `context_servers`、单服务定义、数组形式）；
  - 具备非法 ID 标称化（`SlugifyServerID`）与协议类型自动推导（`DetectTransport`），保留原始名称作为显示名；
  - Go 控制面提供 `servers.import` 动作并具备 `ImportServersRequestDTO` 与单次原子事务 `SaveDraft` 落盘，支持 `overwrite`、`skip`、`rename` 冲突策略；
  - Webview 控制台必须提供“📥 导入标准 MCP 配置”入口与实时解析预览模态框；
  - C# 契约层与控制器必须提供 `ImportServersRequest`、`McpBridgeImportResult` 及 `POST /api/McpBridge/Servers/Import` 映射。
- `LinuxArch052`：MCP 桥接服务状态固化回填、容错降级生效与界面协同门禁：
  - Go `StatusDTO` 必须包含 `TunnelID` 与 `HasTunnelKey` 用于前端安全回填（绝不明文泄露密钥）；
  - `gateway.go` `Apply` 方法具备容错降级机制，单个后端不可达或缺失运行时（如缺少 uvx/npx）不中断全量可用服务生效，记录 `DegradedServers`；
  - Go 控制面支持 `servers.toggle` 原子启停后端，以及 Webview 控制台加载并发拉取 `servers.list` 呈现纳管服务与隧道状态；
  - C# 契约层与控制器同步声明并对齐降级列表与启停接口。
- `LinuxArch053`：MCP 工具实时调用与全链路闭环门禁：
  - Go 控制面 DTO 必须声明 `CallToolRequestDTO` 与 `CallToolResultDTO`；
  - Go `handler.go` 必须路由 `tools.call` 并在 Gateway 层支持双模调用（指定 Server 直调与未指定 Server 时网关全量路由）；
  - Web 控制台必须提供“🧪 测试调用”交互与参数录入模态框（`#testModal`、`submitTestCall`）；
  - C# 宿主网关必须对齐 `CallToolRequest` 与 `McpBridgeToolCallResult`，并在 `IMcpBridgeOrchestrator` 与 `/api/McpBridge/Tools/Call` 端点提供全链路转发，彻底杜绝 Native AOT 反射异常；
  - 仓库内常驻零依赖真实 MCP 工具服务（`plugins/mcp-bridge/tools/system-tools.js`），提供真实系统度量与计算工具。
- `LinuxArch054`：MCP 工具激活状态投影与服务降级治理门禁：
  - Go `StatusDTO` 必须强类型声明 `ActiveToolCount`，并在 `actionStatusGet` 中从活跃快照动态投影，彻底杜绝前端刷新被 `undefined` 冲刷为 0；
  - C# 契约 `McpBridgeStatusDto` 必须包含 `ActiveToolCount`；
  - Webview 控制台 `index.html` 必须具备完整工具管理模态框（`#toolSelectModal`、`openToolSelectModal`、`submitToolSelection`、`selectAllTools`），打通从工具发现到勾选审核落盘的显式白名单链路；
  - Webview 控制台具备降级服务一键批量临时禁用能力（`disableDegradedServers` 与 `#degradedAlertBanner`），让管理人员一键清洗不支持的环境报错。
- `LinuxArch055`：Dockerfile 与 Dockerfile.dev 还原层必须包含全部生产项目文件：
  - 断言 `Dockerfile` 与 `Dockerfile.dev` 的预拷贝层必须包含 4 个核心工程文件，避免依赖还原缓存击穿。
- `LinuxArch056`：AppJsonSerializerContext 禁止包含重复类型注册：
  - 杜绝重复的 `JsonSerializable` 特性导致 Roslyn 语法分析与源生成器产生重复代码。
- `LinuxArch057`：开发容器发布指令必须显式携带 `--no-restore`：
  - 防止发布阶段触发重复探测与还原，节约数秒开销。
- `LinuxArch058`：WebHost 项目文件必须支持根据 PublishAot 解耦 AOT 与 RDG 开关：
  - 非 AOT 模式下自动关闭重型源生成器。
- `LinuxArch059`：宿主微内核纯化与插件独立自包含门禁：
  - 确保宿主不侵入插件私有端点与特定逻辑。
- `LinuxArch060`：插件依赖声明契约与安装向导聚合门禁：
  - 确保插件依赖契约统一并在安装引导中聚合。
- `LinuxArch061`：Dockerfile.dev 分层编译状态机顺序守卫：
  - 断言遵循 `Copy Infra` -> `Build Infra` -> `Copy WebHost` 分层缓存状态机顺序，保障上游构建缓存高频命中。
- `LinuxArch062`：非 AOT 构建应禁用前端嵌入资源：
  - 断言 `LinuxWebTool.WebHost.csproj` 的 `EmbeddedResource` 严格受限于 `PublishAot != 'false'`，消除开发期静态资源打包浪费。
- `LinuxArch063`：开发容器应预载 mimalloc 高性能分配器：
  - 断言 `Dockerfile.dev` 预装 `mimalloc` 并配置 `LD_PRELOAD`，解除 Alpine musl libc 的全局堆锁。
- `LinuxArch064`：MCP 网关渠道观测性与 ChatGPT 就绪门禁：
  - Go 配置模型与 DTO 必须支持出站代理 `ProxyURL`、ChatGPT 真实就绪标记 `ReadyForChatGPT`、首包耗时 `InitialPollMs`、最近轮询错误 `LastPollError` 与生效代理 `EffectiveProxy`；
  - DTO 必须声明结构化事件 `ChannelLogEntryDTO` 与网络探针结果 `DiagnosticProbeResultDTO`；
  - 隧道控制器必须监听 `client.Ready()` 异步握手通道并注入 `ChannelLogBuffer`，严禁在收到首个成功轮询前虚标 `ReadyForChatGPT = true`；
  - 提供 DNS 解析、TCP 握手与 TLS 证书链路分层探针（`DiagnoseControlPlane`）；
  - Resident Control 必须暴露 `tunnel.logs` 与 `tunnel.diagnose` 端点；
  - 控制台 Webview 界面必须协同提供代理输入框、ChatGPT 就绪徽章、实时 Wire Logs 抽屉与网络诊断弹窗。
- `FE-WEBVIEW-SANDBOX-MODALS`：
  - 前端门禁扫描宿主中所有嵌入插件的 iframe 组件，强制要求声明 `allow-modals`，防止子页面弹窗被浏览器沙箱静默吞没。
- `LinuxArch065`：插件鉴权 Cookie 下发与状态机降级停止门禁：
  - `AuthController` 登录与续期接口必须向客户端下发 `lwt_token` Cookie；
  - `PluginAuthGatewayEvaluator` 必须双轨支持从 Query 参数与 Cookie 提取 Token；
  - 前端状态机 `pluginStateMachine.js` 的 `canStop` 必须允许降级、崩溃、退避状态手动停止；
  - `PluginCard.js` 必须透出降级原因与重新拉起/手动停止按钮；
  - Shell 运行时适配器必须在启动脚本前就地清洗 Windows CRLF；
  - `PluginsController` 执行动作必须返回结构化结果杜绝 400 吞噬错误。
- `LinuxArch066`：轻量脚本自适应执行与 MCP 工具自动发现门禁：
  - `PluginsController.SetEnabled` 必须允许 `bool? enabled = null` 支持缺省 Toggle 彻底杜绝 400；
  - `PluginsController.Start` 必须对 OneShot 轻量脚本自适应执行首个 Action 或默认入口；
  - `NativeRuntimeAdapter` 必须包含非空 Arguments 判空保护杜绝 NullReferenceException；
  - `PluginActionRequest` 必须支持 Action 缺省（默认为空字符串自动 fallback）；
  - 前端 `config.js` 的 `setEnabled` 必须支持传入 `enabled` 动态拼接 query 参数；
  - 前端 `PluginCard.js` 必须为 OneShot 脚本提供专属运行按钮 `handleExecute`；
  - MCP Bridge 编译器 `policy.go` 必须支持 Opt-Out 自动选择全部工具；
  - MCP Bridge backend `manager.go` 必须具备代理解析与出站注入能力。
- `LinuxArch067`：插件手动停止状态持久化与文件动态管理门禁：
  - `PluginManager.StopAsync` 必须区分人工操作（`isManual: true`），在插件目录下持久化 `.manual_stop` 标记文件；
  - `PluginManager.StartAsync` 必须清除 `.manual_stop` 标记；
  - `ExecuteStartupAutoStartAsync` 开机自启流程若检测到 `.manual_stop` 必须跳过自启；
  - `PluginsController` 必须暴露 `Upload`、`Download`、`Delete` 路由且遵循 `PluginSecurityGuard`；
  - 静态资源与 HTML 资产首字节严禁包含 ANSI Escape 字符（`\x1b`）与 UTF-8 BOM 乱码。
