# LinuxWebTool Native AOT 迁移经验

## 目标与结论

本项目从常规 .NET Web 应用迁移到 Native AOT 后，必须把运行时反射、动态代码生成和不明确的序列化类型改造成编译期可分析的代码。最终验证方式不是“能启动”即可，而是同时通过构建、测试、容器启动和接口冒烟。

## 主要问题与处理方式

### System.Text.Json

Native AOT 下不能依赖运行时反射补齐 JSON metadata。所有可能作为根类型返回、接收或嵌套出现的 DTO 都要加入 `JsonSerializable`，并统一使用项目的 `AppJsonSerializerContext`。

特别注意：LINQ 查询结果不能直接把匿名类型或 `IEnumerable<T>` 交给响应序列化器。应先投影为明确的 record/DTO，并在必要时 materialize 为 `List<T>`。否则会出现 `JsonTypeInfo metadata ... was not provided`。

### Minimal API

自动生成的 endpoint mapper 仍可能通过 `Delegate` 绑定参数，产生 `IL2026`/`IL3050` 警告。路由处理器应使用显式参数类型、明确返回模型，并检查生成代码是否包含反射绑定路径。警告不应被简单压制；要确认相关参数和响应类型已进入 source generation。

### Dapper 与数据库

避免 `dynamic`、运行时生成参数对象和不明确的查询结果。SQL 查询使用静态匿名参数模型或明确类型，查询结果立即映射到 DTO。数据库 provider 版本要与 .NET/Native AOT 支持矩阵保持一致，并在容器环境实际运行集成测试。

### 配置绑定

`IConfiguration.Get<T>()`、`GetValue<T>()` 对复杂类型可能触发 trimming/AOT 警告。配置对象应尽量使用显式读取和默认值；如果必须绑定，应采用框架支持的 source-generated configuration binding，并验证发布产物中的运行时路径。

### 文件上传与表单

文件上传需要同时验证前端事件、`multipart/form-data` 字段名和后端 `IFormFile` 参数。前端 file input 的 change handler 必须传递 `$event`，否则选择文件后不会进入上传逻辑。请求层不要手工设置 multipart 的 `Content-Type`，让浏览器生成 boundary。

## 门禁与测试分层

- 架构测试：扫描动态序列化、匿名类型、危险依赖和 AOT 配置，补充核心服务单元测试。
- 单元测试：密码哈希、路径解析、参数过滤等无外部依赖逻辑。
- 集成测试：使用真实 WebHost、SQLite 和 HTTP 客户端验证认证、CRUD、文件、计划任务、预设和 WatchRule。
- 接口冒烟：在 Native AOT Docker 容器中登录后遍历后端路由，检查状态码、JSON 响应和 AOT 动态代码错误日志。
- Docker 验证：先构建 AOT 镜像，再启动临时容器执行冒烟；测试结束清理临时容器。

## 常见失败模式

1. 本地 Windows 通过、Linux CI 失败：路径测试不能写死 `C:/...`，应使用当前平台生成的 rooted path。
2. 接口返回 HTML：SPA fallback 接管了未知 API；请求层必须检查 JSON content type。
3. 只验证镜像构建：构建成功不代表 endpoint metadata 完整，必须继续执行容器接口冒烟。
4. 只看测试数量：还要检查 401/403、404 JSON、异常路径、空文件、重名文件和容器日志。
5. 忽略 IL2026/IL3050：警告可能意味着发布后路径依赖反射或动态代码，不能用“能编译”代替确认。

## 推荐执行顺序

```powershell
dotnet test LinuxWebTool.slnx -c Release --nologo
./scripts/verify-fast.ps1
./scripts/verify-aot.ps1 -ImageTag linuxwebtool:aot-verify
```

CI 中应保留架构测试、集成测试、前端门禁、AOT Docker 构建和容器接口冒烟，避免迁移回归只在开发机暴露。
