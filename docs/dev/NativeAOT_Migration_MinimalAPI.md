# Native AOT 与 Minimal API 迁移方案记录

## 1. 迁移背景
由于 ASP.NET Core MVC (基于 [ApiController]) 强依赖运行时反射和动态代码生成 (Reflection.Emit) 来进行路由注册和模型绑定，导致开启 Native AOT 编译后，所有接口在运行时均报 404 Not Found 错误。
为支持 Native AOT 部署，必须将所有 MVC Controller 改为 Minimal API。鉴于项目已有十余个 Controller 和数百个接口，手动重写工作量巨大且易错。

## 2. 封装转换器方案 (最少代码修改)
为了最大限度复用现有的 MVC 代码，我们采用了一种“封装器 + 自动生成”的策略：

1. **实现伪装基类 (MinimalApi.ControllerBase)**：
   - 移除原来的 Microsoft.AspNetCore.Mvc.ControllerBase 继承。
   - 自定义基类提供与 MVC 签名相同的 Ok()、BadRequest()、NotFound() 等方法，但其返回值改为 IResult（代替 IActionResult）。
   - 将原来 Controller 中的 Task<IActionResult> 签名替换为 Task<IResult>。

2. **路由映射自动生成 (gen_minimal_api.py)**：
   - 编写 Python 脚本解析源代码中的 [Route]、[HttpGet]、[HttpPost] 等特性。
   - 生成 EndpointsMapper.g.cs 文件，使用 pp.MapGroup() 以及 MapGet() / MapPost() / MapDelete() 对原有的 Controller 方法进行 Minimal API 注册。
   - **参数注入**：自动在生成的 Lambda 表达式中加入 [FromServices] 以从 DI 容器中解析 Controller 实例。

## 3. AOT 深度适配的关键技术点

### 3.1 复杂查询对象的 [FromQuery] 替换
在 Minimal API 中，[FromQuery] 不能直接用于修饰复杂对象（会导致 CS0029 编译错误）。
- **解决方案**：在自动生成映射代码时，检测到复杂模型作为查询参数时，将其修饰符自动转换为 [AsParameters]。

### 3.2 匿名对象的剔除与 JSON 强类型注册
AOT 模式下 System.Text.Json 无法在运行时序列化未注册的动态类型和匿名对象 (
ew { message = "..." })，否则会抛出 NotSupportedException。
- **解决方案**：
  1. 彻底清除了代码库中所有的匿名对象返回，统一提取为 MessageResponse、EnqueueResponse、ImportResponse、IdResponse 等显式的 ecord。
  2. 维护 AppJsonSerializerContext.cs，使用 [JsonSerializable] 显式注册了项目中所有的出入参实体类，并为 System.Text.Json 开启 Source Generator。

### 3.3 暂时剥离 OpenAPI (Swagger)
uilder.Services.AddOpenApi() 中的类型推断器在处理复杂嵌套泛型（如 PagedResponse<T>）或 List<T> 时，如果缺少对应元数据，会在应用程序启动 (MapPost 阶段) 时抛出严重异常导致崩溃。
- **解决方案**：在 AOT 模式下，目前暂时将 OpenAPI 的相关配置（AddOpenApi、MapOpenApi 以及 Scalar UI）从服务注册和管道中移除，以保证服务的成功启动。

## 4. 最终成效
- 绝大部分核心业务逻辑文件完全无需改动，通过自动生成的 Wrapper 文件无缝切换到了 Minimal API。
- 彻底消除了反射依赖。
- 项目在基于 Alpine Linux (linux-musl-x64) 的 Docker 容器中成功完成 Native AOT 编译。
- 服务实现秒级启动（不到 100ms），内存占用极小，路由和 JSON 反序列化运转正常。
