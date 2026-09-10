# 迁移与构建辅助脚本 (Migration Scripts)

这里存档了在将 LinuxWebTool 项目从 ASP.NET Core MVC 迁移到 Native AOT (Minimal API) 时使用的自动化脚本。这些脚本极大地减少了重构的手工工作量。如果未来有新的 Controller 被添加进来，可以参考或重新运行这些脚本来进行转换。

## 脚本列表与功能说明

### 1. gen_minimal_api.py
**核心脚本**。用于读取 src/LinuxWebTool.WebHost/Routes/ 目录下的所有原 MVC Controller (.cs 文件)，通过正则表达式解析其中的 [Route], [HttpGet], [HttpPost] 等 HTTP 动词，并抽取所有方法签名和参数。
最后它会生成一个 EndpointsMapper.g.cs 文件，里面包含 AddAutoControllers() (注入依赖) 和 MapAutoControllers() (Minimal API 的 MapGroup 路由注册逻辑)。
**注意**：在参数解析时，会自动将复杂对象的 [FromQuery] 转换为 [AsParameters]，并将服务对象放入 [FromServices] 注入参数列表中，以便通过 AOT 编译。

### 2. convert_controllers.py
**辅助脚本**。用于批量扫描 Routes 文件夹下的 Controller，将其继承的 ControllerBase 替换为自定义的封装基类 MinimalApi.ControllerBase，从而实现 Ok()、BadRequest() 的兼容。

### 3. ix.py
**辅助脚本**。用于批量扫描 Routes 文件夹下的 Controller，将所有方法的返回值从原有的 MVC 类型 Task<IActionResult> 替换为 Minimal API 的 Task<IResult>。

### 4. dd_types.py
**序列化辅助脚本**。扫描 src/LinuxWebTool.Contracts/Models/ 下的各类传输模型和响应类，提取类名，并自动向 AppJsonSerializerContext.cs 文件中追加 [JsonSerializable(typeof(TypeName))] 的声明。这是 AOT 模式下 System.Text.Json 的 Source Generator 所必须的强类型注册。

### 5. dd_list.py
**序列化辅助脚本**。与 dd_types.py 类似，专门用于为 AppJsonSerializerContext.cs 追加上述所有类型的列表泛型支持，即 [JsonSerializable(typeof(List<TypeName>))] 和 [JsonSerializable(typeof(IEnumerable<TypeName>))]，以确保 AOT 时数组或列表序列化不报错。

