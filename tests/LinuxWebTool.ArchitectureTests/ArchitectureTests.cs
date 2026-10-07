using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

/// <summary>
/// 后端架构门禁（对应 docs/dev/architecture-gates.md）：
/// - LinuxArch001 Contracts 零项目引用、禁第三方包；
/// - LinuxArch002/004 分层依赖方向（Infrastructure 仅 Contracts；WebHost 不被下层引用）；
/// - LinuxArch005 Routes(Controller) 禁直接触碰 SqlSugar；
/// - LinuxArch007 命名空间与物理路径一致。
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly ContractsAssembly = typeof(LinuxWebTool.Contracts.Shared.Contracts.SaveCommandRequest).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(LinuxWebTool.Application.Features.Security.ApiKeyPermissionMatrix).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(LinuxWebTool.Infrastructure.Shared.Persistence.DbSetup).Assembly;
    private static readonly Assembly WebHostAssembly = typeof(LinuxWebTool.WebHost.Program).Assembly;

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LinuxWebTool.slnx")))
            {
                dir = dir.Parent!;
            }
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    [Fact]
    public void LinuxArch001_Contracts_禁止引用任何项目与第三方包()
    {
        var references = ContractsAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        var violations = references
            .Where(name => name is not null
                && !name.StartsWith("System", StringComparison.Ordinal)
                && name != "netstandard"
                && name != "mscorlib"
                && name != "WindowsBase")
            .ToList();
        Assert.True(violations.Count == 0,
            $"Contracts 必须零依赖（仅 BCL），发现违规引用：{string.Join(", ", violations)}");
    }

    [Fact]
    public void LinuxArch002_Infrastructure_禁止引用上层项目()
    {
        var references = InfrastructureAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, name => name.Contains("WebHost") || name.Contains("Application"));
    }

    [Fact]
    public void LinuxArch004_下层禁止反向引用上层()
    {
        var infraRefs = InfrastructureAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(infraRefs, name => name.Contains("WebHost") || name.Contains("Application"));

        var appRefs = ApplicationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(appRefs, name => name.Contains("WebHost") || name.Contains("Infrastructure"));

        var contractsRefs = ContractsAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(contractsRefs, name => name.Contains("Application") || name.Contains("Infrastructure") || name.Contains("WebHost"));
    }

    [Fact]
    public void LinuxArch003_Contracts_不得引用持久化调度与认证框架()
    {
        var references = ContractsAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        var forbidden = new[] { "SqlSugar", "Quartz", "IdentityModel", "Microsoft.AspNetCore" };
        var violations = references.Where(name => forbidden.Any(f => name.Contains(f))).ToList();
        Assert.True(violations.Count == 0,
            $"Contracts 出现框架/持久化类型引用：{string.Join(", ", violations)}");
    }

    [Fact]
    public void LinuxArch005_Routes_控制器_禁止直接使用_SqlSugar()
    {
        var webHostDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost");
        var controllerFiles = Directory.EnumerateFiles(webHostDir, "*Controller.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(controllerFiles);
        var violations = controllerFiles
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bSqlSugar\b"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(violations.Count == 0,
            $"Controller 必须经仓储/服务访问数据，禁止直接 using SqlSugar：{string.Join(", ", violations)}");
    }

    [Fact]
    public void LinuxArch007_命名空间必须与物理路径一致()
    {
        var srcDir = Path.Combine(RepoRoot, "src");
        var violations = new List<string>();
        foreach (var projectDir in Directory.EnumerateDirectories(srcDir))
        {
            var projectFile = Path.GetFileName(projectDir);
            foreach (var source in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(source);
                var match = Regex.Match(content, @"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);
                if (!match.Success)
                {
                    continue; // GlobalUsing 等无命名空间文件
                }
                var relative = Path.GetRelativePath(projectDir, Path.GetDirectoryName(source)!);
                var expected = relative == "."
                    ? projectFile
                    : $"{projectFile}.{relative.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.')}";
                if (match.Groups[1].Value != expected)
                {
                    violations.Add($"{Path.GetRelativePath(RepoRoot, source)}：命名空间 {match.Groups[1].Value} ≠ 期望 {expected}");
                }
            }
        }
        Assert.True(violations.Count == 0, "命名空间与物理路径不一致：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void LinuxArch008_AOT关键响应类型必须为显式DTO()
    {
        var webHostDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost");
        var controllerFiles = Directory.EnumerateFiles(webHostDir, "*Controller.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(controllerFiles);
        var violations = controllerFiles
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"new\s*\{"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(violations.Count == 0, "Routes 禁止匿名对象响应/投影：" + string.Join(", ", violations));
        foreach (var file in new[] { "CommandsController.cs", "SmbMountsController.cs", "SystemStatusController.cs", "FilesController.cs", "TranscodeController.cs" })
        {
            var matchedFile = controllerFiles.FirstOrDefault(f => Path.GetFileName(f) == file);
            Assert.NotNull(matchedFile);
            var source = File.ReadAllText(matchedFile);
            Assert.DoesNotContain("List<object>", source);
        }
    }

    [Fact]
    public void LinuxArch009_关键控制器接口必须存在生成映射()
    {
        var mapper = File.ReadAllText(Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs"));
        foreach (var route in new[] { "MapGet(\"Check\"", "group_FilesController.MapGet(\"\"", "MapGet(\"Files\"", "MapGet(\"Files/{name}\"", "group_SmbMountsController.MapGet(\"Support\"" })
        {
            Assert.Contains(route, mapper);
        }
    }

    [Fact]
    public void LinuxArch010_发布脚本必须启用NativeAOT且关闭反射JSON()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "LinuxWebTool.WebHost.csproj"));
        var publish = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "publish.ps1"));
        Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", project);
        Assert.Contains("<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>", project);
        Assert.Contains("-p:PublishAot=true", publish);
        var verifier = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "verify-aot.ps1"));
        Assert.Contains("docker build", verifier);
        Assert.Contains("PublishAot", File.ReadAllText(Path.Combine(RepoRoot, "Dockerfile")));
    }

    [Fact]
    public void LinuxArch011_文件删除请求必须使用DELETE方法()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "views", "FilesView.js"));
        var deleteCalls = Regex.Matches(
            source,
            @"http\(API\.files\.remove\(\),\s*\{\s*method:\s*'DELETE'");
        Assert.Equal(2, deleteCalls.Count);
    }

    [Fact]
    public void LinuxArch012_ControllerHttp特性和生成映射必须一致()
    {
        var webHostDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost");
        var mapper = File.ReadAllText(Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs"));
        var mapperRoutes = ParseMapperRoutes(mapper);
        var controllerRoutes = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(webHostDir, "*Controller.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var className = Regex.Match(source, @"\bclass\s+(\w+Controller)\b").Groups[1].Value;
            Assert.Contains("[Route(\"api/[controller]\")]", source);
            var baseRoute = $"/api/{className[..^"Controller".Length]}";
            foreach (Match endpoint in Regex.Matches(source, @"\[Http(Get|Post|Put|Delete)(?:\(""([^""]*)""\))?\]"))
                controllerRoutes.Add($"{className}|{endpoint.Groups[1].Value.ToUpperInvariant()}|{NormalizeRoute(baseRoute, endpoint.Groups[2].Value)}");
        }
        var missingMapper = controllerRoutes.Except(mapperRoutes).ToList();
        var missingController = mapperRoutes.Except(controllerRoutes).ToList();
        Assert.True(missingMapper.Count == 0 && missingController.Count == 0,
            $"Http 特性与 mapper 不一致：缺映射 {string.Join(", ", missingMapper)}；缺控制器 {string.Join(", ", missingController)}");
    }

    private static HashSet<string> ParseMapperRoutes(string mapper)
    {
        var routes = new HashSet<string>();
        var controller = string.Empty;
        foreach (var line in mapper.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var group = Regex.Match(line, @"var group_(\w+) = app\.MapGroup\(""([^""]+)""\)");
            if (group.Success) controller = group.Groups[1].Value;
            var endpoint = Regex.Match(line, @"\.Map(Get|Post|Put|Delete)\(""([^""]*)""" );
            if (endpoint.Success && controller.Length > 0)
            {
                var baseRoute = $"/api/{controller[..^"Controller".Length]}";
                routes.Add($"{controller}|{endpoint.Groups[1].Value.ToUpperInvariant()}|{NormalizeRoute(baseRoute, endpoint.Groups[2].Value)}");
            }
        }
        return routes;
    }

    private static string NormalizeRoute(string basePath, string route)
    {
        var segments = $"{basePath.Trim('/')}/{route.Trim('/')}".Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments.Select(s => s.StartsWith('{') ? "*" : s));
    }

    [Fact]
    public void LinuxArch013_SQLite_仓储层Guid主外键比较必须指定_COLLATE_NOCASE()
    {
        var infraDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure");
        var storeFiles = Directory.EnumerateFiles(infraDir, "*Store.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(storeFiles);

        var pattern = new Regex(@"(?i)\b(WHERE|AND|OR)\b[^;""\r\n]*?\b(\w*Id)\s*(=|!=|<>)\s*@(\w*Id)\b(?!\s*COLLATE\s+NOCASE)", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in storeFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (pattern.IsMatch(line))
                {
                    violations.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "SQLite Guid/Id 字段比对必须声明 COLLATE NOCASE（避免 .NET 大写 Guid 传参时比较失败导致 404）：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void LinuxArch014_SQLite_DDL主键定义必须声明_COLLATE_NOCASE()
    {
        var infraDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure");
        var dbSetupFiles = Directory.EnumerateFiles(infraDir, "DbSetup.cs", SearchOption.AllDirectories).ToList();
        Assert.Single(dbSetupFiles);
        var dbSetupFile = dbSetupFiles[0];

        var lines = File.ReadAllLines(dbSetupFile);
        var pattern = new Regex(@"(?i)\bId\s+TEXT\s+PRIMARY\s+KEY(?!\s+COLLATE\s+NOCASE)", RegexOptions.Compiled);
        var violations = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (pattern.IsMatch(line))
            {
                violations.Add($"DbSetup.cs:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "DbSetup.cs 中所有主键 DDL 必须包含 COLLATE NOCASE：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void LinuxArch015_MinimalApi_GET查询模型属性必须为可空或引用类型()
    {
        var queryTypes = ContractsAssembly.GetTypes()
            .Where(t => t.IsClass && t.Name.EndsWith("Query", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(queryTypes);
        var violations = new List<string>();

        foreach (var type in queryTypes)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // 值类型且非 Nullable<T>，在 Minimal API [AsParameters] 绑定时若 query string 未传会抛 BadHttpRequestException
                if (prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) == null)
                {
                    violations.Add($"{type.Name}.{prop.Name} ({prop.PropertyType.Name})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "GET 查询模型属性禁止使用非空值类型（必须为 string 等引用类型或 int?、bool? 等可空类型，防止无参请求报 400/500）：\n" + string.Join("\n", violations));
    }

    [Fact]
    public async Task LinuxArch016_SQLite_实体按Guid查询支持大小写混合真实数据库验证()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"lwt_test_arch_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={tempDbPath};Mode=ReadWriteCreate;Cache=Shared";
        var factory = DbSetup.CreateFactory(connectionString);
        DbSetup.Initialize(factory);

        try
        {
            const string lowercaseCmdId = "00000000-0000-0000-0000-000000000001";
            const string lowercaseTaskId = "00000000-0000-0000-0000-000000000002";

            using (var conn = factory.CreateConnection())
            {
                conn.Open();
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = $@"
                    INSERT INTO linux_command (Id, Name, CommandText, ScriptType, Description, GroupId, IsPinned, SortOrder, TimeoutSeconds, LastExecTime, CreateTime, UpdateTime)
                    VALUES ('{lowercaseCmdId}', 'arch-test-cmd', 'echo 1', 0, '', NULL, 0, 0, 30, NULL, '2026-01-01 00:00:00', '2026-01-01 00:00:00');

                    INSERT INTO schedule_task (Id, Name, CommandId, CronExpression, Enabled, GroupId, IsPinned, SortOrder, TimeoutSeconds, Arguments, LastRunTime, NextRunTime, CreateTime, UpdateTime)
                    VALUES ('{lowercaseTaskId}', 'arch-test-task', '{lowercaseCmdId}', '0 */5 * * * ?', 1, NULL, 0, 0, 60, NULL, NULL, NULL, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
                ";
                await insertCmd.ExecuteNonQueryAsync();
            }

            // 使用 Store 按 Guid 参数查询（.NET 驱动会向 SQLite 传入全大写字符串）
            var commandStore = new CommandStore(factory);
            var cmd = await commandStore.GetByIdAsync(Guid.Parse(lowercaseCmdId));
            Assert.NotNull(cmd);
            Assert.Equal("arch-test-cmd", cmd.Name);

            var scheduleStore = new ScheduleStore(factory);
            var task = await scheduleStore.GetByIdAsync(Guid.Parse(lowercaseTaskId));
            Assert.NotNull(task);
            Assert.Equal("arch-test-task", task.Name);
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                try { File.Delete(tempDbPath); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void LinuxArch017_可空FromBody参数在MinimalApi映射中禁止直接声明以防无Body请求报404()
    {
        var webHostDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost");
        var mapper = File.ReadAllText(Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs"));

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(webHostDir, "*Controller.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            var className = Regex.Match(content, @"\bclass\s+(\w+Controller)\b").Groups[1].Value;
            var matches = Regex.Matches(content, @"\[Http(Post|Put)[^\]]*\][\s\r\n]*public\s+[^\(]+\b(\w+)\s*\(([^)]*\[FromBody\]\s*[^,\)]+\?[^)]*)\)", RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                var actionName = match.Groups[2].Value;
                var mapperGroupPattern = $@"group_{className}\.Map(Post|Put)\([^;]+ctrl\.{actionName}\(";
                var mapperMatch = Regex.Match(mapper, mapperGroupPattern, RegexOptions.Singleline);
                if (mapperMatch.Success)
                {
                    var lambdaHeader = mapperMatch.Value[..mapperMatch.Value.IndexOf("=>", StringComparison.Ordinal)];
                    if (lambdaHeader.Contains("[FromBody]"))
                    {
                        violations.Add($"{className}.{actionName}: Minimal API 委托参数包含 [FromBody]，会导致 Content-Length: 0 或无 Content-Type 的请求 404/415");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "检测到控制器存在可选 [FromBody] 参数，但在 EndpointsMapper.g.cs 注册中直接使用了 [FromBody] 委托参数。\n" +
            "Minimal API 要求此类可选 Body 必须通过 HttpContext 动态判断 (ctx.Request.HasJsonContentType())：\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void LinuxArch018_NativeAOT下禁止调用无显式源生成上下文的JsonSerializer重载()
    {
        var srcDir = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(srcDir), "src 目录不存在");

        var methodRegex = new Regex(
            @"\b(?:JsonSerializer\s*\.\s*(?:Serialize|Deserialize|SerializeAsync|DeserializeAsync|SerializeToElement|SerializeToNode|SerializeToUtf8Bytes)|WriteAsJsonAsync|ReadFromJsonAsync)\b",
            RegexOptions.Compiled);
        var safeContextRegex = new Regex(
            @"(\b\w*Json(?:Serializer)?Context\b|\bJsonTypeInfo\b|\bDefault\b|\bTypeInfoResolver\b)",
            RegexOptions.Compiled);

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var normalizedPath = file.Replace('\\', '/');
            if (normalizedPath.Contains("/bin/") || normalizedPath.Contains("/obj/"))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var matches = methodRegex.Matches(content);
            foreach (Match match in matches)
            {
                var start = match.Index + match.Length;

                // 跳过泛型参数 <...>，如 Deserialize<List<string>> 或 ReadFromJsonAsync<T>
                if (start < content.Length && content[start] == '<')
                {
                    var angleDepth = 1;
                    start++;
                    while (start < content.Length && angleDepth > 0)
                    {
                        if (content[start] == '<') angleDepth++;
                        else if (content[start] == '>') angleDepth--;
                        start++;
                    }
                }

                while (start < content.Length && char.IsWhiteSpace(content[start]))
                {
                    start++;
                }

                // 寻找对应的方法参数圆括号
                if (start < content.Length && content[start] == '(')
                {
                    var parenDepth = 1;
                    var end = start + 1;
                    while (end < content.Length && parenDepth > 0)
                    {
                        if (content[end] == '(') parenDepth++;
                        else if (content[end] == ')') parenDepth--;
                        end++;
                    }

                    var argsText = content.Substring(start + 1, end - start - 2);
                    if (!safeContextRegex.IsMatch(argsText))
                    {
                        var lineNumber = content.AsSpan(0, match.Index).Count('\n') + 1;
                        var relativePath = Path.GetRelativePath(RepoRoot, file);
                        violations.Add($"{relativePath}:{lineNumber}: {match.Value}({argsText.Trim()})");
                    }
                }
            }
        }


        Assert.True(violations.Count == 0,
            "检测到调用了无显式源生成参数的 JsonSerializer / JSON I/O 反射重载！\n" +
            "由于项目配置了 <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>，\n" +
            "未传递 JsonTypeInfo 或 JsonSerializerContext 的调用将在运行时抛出 InvalidOperationException。\n" +
            "请传入 AppJsonSerializerContext.Default.* 或自定义 *JsonContext.Default.*。\n" +
            "违规代码：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void LinuxArch019_WebHost必须显式直接引用Contracts项目()
    {
        var webHostProj = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "LinuxWebTool.WebHost.csproj");
        Assert.True(File.Exists(webHostProj), "WebHost csproj 不存在");
        var content = File.ReadAllText(webHostProj).Replace('\\', '/');
        Assert.True(content.Contains("LinuxWebTool.Contracts/LinuxWebTool.Contracts.csproj"),
            "WebHost 消费了 Contracts 中的大量契约，必须显式直接引用 Contracts 项目，禁止依赖隐式传递。");
    }

    [Fact]
    public void LinuxArch020_基线引擎正常工作且无失效残留条目()
    {
        var baselinePath = Path.Combine(RepoRoot, "tests", "LinuxWebTool.ArchitectureTests", "backend-baseline.json");
        Assert.True(File.Exists(baselinePath), "backend-baseline.json 必须存在于 ArchitectureTests 根目录下");
        var engine = new LinuxWebTool.ArchitectureTests.Support.BaselineEngine(baselinePath);
        engine.AssertNoStaleEntries();
    }

    [Fact]
    public void LinuxArch021_所有生产项目直接引用图无环()
    {
        var srcDir = Path.Combine(RepoRoot, "src");
        var projectFiles = Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories).ToList();
        LinuxWebTool.ArchitectureTests.Support.BaselineEngine.AssertNonEmptyScope(projectFiles.Count, "生产 csproj 项目集合");

        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var proj in projectFiles)
        {
            var projName = Path.GetFileNameWithoutExtension(proj);
            if (!graph.ContainsKey(projName)) graph[projName] = new();
            var content = File.ReadAllText(proj);
            var matches = Regex.Matches(content, @"<ProjectReference\s+Include=""([^""]+)""");
            foreach (Match m in matches)
            {
                var refPath = m.Groups[1].Value;
                var refName = Path.GetFileNameWithoutExtension(refPath);
                graph[projName].Add(refName);
            }
        }

        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cyclePath = new List<string>();

        bool HasCycle(string node)
        {
            visited[node] = 1;
            cyclePath.Add(node);
            if (graph.TryGetValue(node, out var neighbors))
            {
                foreach (var next in neighbors)
                {
                    if (!visited.TryGetValue(next, out var state) || state == 0)
                    {
                        if (HasCycle(next)) return true;
                    }
                    else if (state == 1)
                    {
                        cyclePath.Add(next);
                        return true;
                    }
                }
            }
            visited[node] = 2;
            cyclePath.RemoveAt(cyclePath.Count - 1);
            return false;
        }

        foreach (var node in graph.Keys)
        {
            if (!visited.TryGetValue(node, out var state) || state == 0)
            {
                if (HasCycle(node))
                {
                    Assert.Fail($"检测到生产项目间直接引用存在循环依赖闭环：{string.Join(" -> ", cyclePath)}");
                }
            }
        }
    }

    [Fact]
    public void LinuxArch022_HostedService双重注册守卫()
    {
        var compositionFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Composition", "ServiceCollectionExtensions.cs");
        Assert.True(File.Exists(compositionFile), "ServiceCollectionExtensions.cs 不存在");
        var content = File.ReadAllText(compositionFile);

        var criticalTypes = new[] { "TranscodeQueueService", "MountStateMachineService", "FrpTunnelManager", "EasyTierNodeManager", "EasyTierHostSupervisor" };
        foreach (var typeName in criticalTypes)
        {
            Assert.True(content.Contains($"builder.Services.AddSingleton<{typeName}>()"),
                $"服务 {typeName} 既是后台任务又被其他单例/控制器直接注入，必须显式调用 AddSingleton<{typeName}>()，杜绝运行时解析失败。");
        }
    }

    [Fact]
    public void LinuxArch023_生产项目根目录必须遵循物理架构规范白名单()
    {
        var srcDir = Path.Combine(RepoRoot, "src");
        var globalAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Features", "Composition", "Shared", "Platform", "Serialization", "bin", "obj"
        };
        var webHostSpecificAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MinimalApi", "wwwroot", "Deploy", "data", "logs", "Properties"
        };

        var violations = new List<string>();

        foreach (var projectDir in Directory.EnumerateDirectories(srcDir))
        {
            var projectName = Path.GetFileName(projectDir);
            var isWebHost = projectName.EndsWith("WebHost", StringComparison.OrdinalIgnoreCase);

            foreach (var subDir in Directory.EnumerateDirectories(projectDir))
            {
                var dirName = Path.GetFileName(subDir);
                if (globalAllowed.Contains(dirName)) continue;
                if (isWebHost && webHostSpecificAllowed.Contains(dirName)) continue;

                violations.Add($"{projectName}/{dirName}");
            }
        }

        Assert.True(violations.Count == 0,
            $"生产项目根目录检测到违规目录（禁止在根目录平铺业务名或职责名，必须统一收敛至 Features/、Shared/、Composition/）：\n{string.Join("\n", violations)}");
    }
}

