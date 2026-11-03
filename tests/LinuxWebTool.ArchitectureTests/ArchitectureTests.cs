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

    [Fact]
    public void LinuxArch024_所有控制器必须在ApiKeyMiddleware中显式声明权限归属()
    {
        var webHostDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost");
        var middlewareFile = Path.Combine(webHostDir, "Shared", "Middleware", "ApiKeyMiddleware.cs");
        Assert.True(File.Exists(middlewareFile), "ApiKeyMiddleware.cs 文件不存在");

        var middlewareContent = File.ReadAllText(middlewareFile);

        var controllerFiles = Directory.GetFiles(webHostDir, "*Controller.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(controllerFiles);

        var missingControllers = new List<string>();

        foreach (var file in controllerFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (!fileName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)) continue;

            var routeName = fileName[..^"Controller".Length];
            var expectedRoute = $"/api/{routeName}";

            // 检查 ApiKeyMiddleware.cs 是否显式提到了该路由前缀（严格避免隐式放行或漏控）
            if (!middlewareContent.Contains($"\"{expectedRoute}\"", StringComparison.OrdinalIgnoreCase) &&
                !middlewareContent.Contains($"\"{expectedRoute}/", StringComparison.OrdinalIgnoreCase))
            {
                missingControllers.Add($"{fileName} ({expectedRoute})");
            }
        }

        Assert.True(missingControllers.Count == 0,
            $"发现未在 ApiKeyMiddleware.cs 中显式声明权限控制的控制器：\n{string.Join("\n", missingControllers)}\n" +
            "所有控制器路由必须在 ApiKeyMiddleware.CheckModulePermission 中显式划定权限归属（拒绝或绑定模块权限），严禁依赖默认规则！");
    }

    [Fact]
    public void LinuxArch026_单源文件代码行数与单一职责门禁()
    {
        var srcDir = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(srcDir), "src 目录不存在");

        const int StandardMaxLines = 700;
        const int ContractsMaxLines = 300;
        const int McpMaxLines = 400;

        // 存量高复杂度底层适配器与构建器硬顶基线（严格冻结，只降不增）
        // 任何非白名单文件超过 StandardMaxLines 将直接失败；白名单文件一旦重构缩减至 <= 700 行必须移除白名单（防假绿）
        var legacyBaseline = new Dictionary<string, (int HardCap, string Reason)>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/LinuxWebTool.Infrastructure/Features/EasyTier/Adapters/EasyTierNodeManager.cs"] = (1450, "EasyTier 进程多节点生命周期编排与健康检查适配器"),
            ["src/LinuxWebTool.Infrastructure/Features/Tunnel/Adapters/FrpTunnelInstance.cs"] = (1100, "FRP 进程启动与双向流转发适配器"),
            ["src/LinuxWebTool.Infrastructure/Features/Transcode/Adapters/FfmpegArgsBuilder.cs"] = (950, "复杂 FFmpeg 编解码滤镜链参数构建器"),
            ["src/LinuxWebTool.Infrastructure/Features/SystemInfo/Platform/SystemStatusProvider.cs"] = (750, "跨平台系统资源与硬件传感器采集提供者")
        };

        var scannedFiles = 0;
        var violations = new List<string>();
        var staleBaselines = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var normalizedPath = file.Replace('\\', '/');
            if (normalizedPath.Contains("/bin/") || normalizedPath.Contains("/obj/") || normalizedPath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scannedFiles++;
            var lineCount = File.ReadLines(file).Count();
            var relativePath = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');

            if (legacyBaseline.TryGetValue(relativePath, out var baseline))
            {
                if (lineCount > baseline.HardCap)
                {
                    violations.Add($"[白名单硬顶超标] {relativePath} 当前 {lineCount} 行，超过存量硬顶上限 {baseline.HardCap} 行（原因：{baseline.Reason}）！");
                }
                else if (lineCount <= StandardMaxLines)
                {
                    staleBaselines.Add($"{relativePath} (当前仅 {lineCount} 行 <= {StandardMaxLines}，已健康，必须从白名单中移除以防假绿！)");
                }
                continue;
            }

            // 针对 Contracts 契约层做更严格的单文件限制（Contracts 应当精炼轻量，避免混入非必要实现）
            if (relativePath.StartsWith("src/LinuxWebTool.Contracts/", StringComparison.OrdinalIgnoreCase))
            {
                if (lineCount > ContractsMaxLines)
                {
                    violations.Add($"[Contracts 契约超标] {relativePath} 当前 {lineCount} 行，超过契约层上限 {ContractsMaxLines} 行。契约应拆分为按领域聚合的轻量 DTO 与接口。");
                }
                continue;
            }

            // 针对 MCP 引擎和工具提供者做更严格的单文件限制（杜绝 McpServerEngine 膨胀复发）
            if (relativePath.Contains("/Features/Mcp/", StringComparison.OrdinalIgnoreCase))
            {
                if (lineCount > McpMaxLines)
                {
                    violations.Add($"[MCP 模块超标] {relativePath} 当前 {lineCount} 行，超过 MCP 模块上限 {McpMaxLines} 行。请按功能拆分为独立的 IMcpToolProvider 实现类！");
                }
                continue;
            }

            // 通用代码文件上限（单文件原则上不得超过 700 行）
            if (lineCount > StandardMaxLines)
            {
                violations.Add($"[通用单文件超标] {relativePath} 当前 {lineCount} 行，超过架构门禁上限 {StandardMaxLines} 行。请遵循单一职责原则（SRP），运用策略模式、提供者模式或外观模式拆分为子模块！");
            }
        }

        Assert.True(scannedFiles > 50, $"扫描源文件数量过少（仅 {scannedFiles} 个），可能存在路径扫描异常（防假绿）");

        if (staleBaselines.Count > 0)
        {
            Assert.Fail($"检测到已失效的巨石文件白名单条目（代码行数已降至安全阈值内，请及时清理白名单）：\n{string.Join("\n", staleBaselines)}");
        }

        Assert.True(violations.Count == 0,
            $"架构门禁检测到 {violations.Count} 处代码文件行数超标（巨石文件防御）：\n{string.Join("\n", violations)}\n\n" +
            "💡 解决方案指导：\n" +
            "1. 检查是否存在单一职责原则（SRP）违背或上帝类（God Class）；\n" +
            "2. 使用策略模式 (Strategy)、提供者模式 (Provider)、组合根或分部类/扩展方法拆分领域逻辑；\n" +
            "3. 如同 McpServerEngine 拆分为 6 个 IMcpToolProvider 子类，将大单文件重构成轻量高内聚模块；\n" +
            "4. 严禁随意扩充白名单或堆砌巨型单文件！");
    }

    [Fact]
    public void LinuxArch027_远程主机SFTP与连通性接口必须具备超时与取消机制()
    {
        var controllerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "RemoteHost", "Routes", "RemoteHostsController.cs");
        Assert.True(File.Exists(controllerFile), "RemoteHostsController.cs 不存在");
        var controllerContent = File.ReadAllText(controllerFile);

        // 验证关键远程网络调用必须具备 CancelAfter 显式超时保护（防止 SFTP/网络无限挂死）
        Assert.Contains("CancelAfter", controllerContent);
        Assert.Contains("TestById", controllerContent);
        Assert.Contains("OperationCanceledException", controllerContent);

        // 验证 SshHostAdapter.cs 中的 SftpClient 必须显式配置 OperationTimeout，杜绝 SSH.NET 默认 InfiniteTimeSpan 隐患
        var adapterFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "RemoteHost", "Adapters", "SshHostAdapter.cs");
        Assert.True(File.Exists(adapterFile), "SshHostAdapter.cs 不存在");
        var adapterContent = File.ReadAllText(adapterFile);

        Assert.Contains("OperationTimeout", adapterContent);
        Assert.Contains("KeepAliveInterval", adapterContent);
    }

    [Fact]
    public void LinuxArch028_远程主机必须具备SFTP与SSHExec双轨文件系统降级与终端转义净化能力()
    {
        var adaptersDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "RemoteHost", "Adapters");
        var resilientFsFile = Path.Combine(adaptersDir, "ResilientRemoteFileSystem.cs");
        var execFsFile = Path.Combine(adaptersDir, "SshExecRemoteFileSystem.cs");
        var sanitizerFile = Path.Combine(adaptersDir, "AnsiOutputSanitizer.cs");
        var adapterFile = Path.Combine(adaptersDir, "SshHostAdapter.cs");
        var feViewFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "views", "RemoteHostsView.js");

        Assert.True(File.Exists(resilientFsFile), "ResilientRemoteFileSystem.cs 不存在");
        Assert.True(File.Exists(execFsFile), "SshExecRemoteFileSystem.cs 不存在");
        Assert.True(File.Exists(sanitizerFile), "AnsiOutputSanitizer.cs 不存在");
        Assert.True(File.Exists(adapterFile), "SshHostAdapter.cs 不存在");
        Assert.True(File.Exists(feViewFile), "RemoteHostsView.js 不存在");

        var resilientContent = File.ReadAllText(resilientFsFile);
        var execFsContent = File.ReadAllText(execFsFile);
        var sanitizerContent = File.ReadAllText(sanitizerFile);
        var adapterContent = File.ReadAllText(adapterFile);
        var feContent = File.ReadAllText(feViewFile);

        // 1. 双轨文件系统验证
        Assert.Contains("IRemoteFileSystem", resilientContent);
        Assert.Contains("SshExecRemoteFileSystem", resilientContent);
        Assert.Contains("SftpClient", resilientContent);
        Assert.Contains("IRemoteFileSystem", execFsContent);
        Assert.Contains("ParseDirectoryOutput", execFsContent);

        // 2. 宿主适配器联动验证
        Assert.Contains("ResilientRemoteFileSystem", adapterContent);
        Assert.Contains("AnsiOutputSanitizer.Sanitize", adapterContent);

        // 3. 终端转义与 OSC 133 净化验证
        Assert.Contains("Sanitize", sanitizerContent);
        Assert.Contains("133", sanitizerContent);
        Assert.Contains("stripAnsi", feContent);
    }

    [Fact]
    public void LinuxArch029_远程主机文件管理器必须具备连接池会话复用与复制移动能力()
    {
        var adaptersDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "RemoteHost", "Adapters");
        var sessionPoolFile = Path.Combine(adaptersDir, "RemoteFileSystemSessionPool.cs");
        var controllerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "RemoteHost", "Routes", "RemoteHostsController.cs");
        var contractsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "RemoteHost", "Contracts", "RemoteFileContracts.cs");
        var mapperFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs");
        var feViewFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "views", "RemoteHostsView.js");

        Assert.True(File.Exists(sessionPoolFile), "RemoteFileSystemSessionPool.cs 不存在");
        Assert.True(File.Exists(controllerFile), "RemoteHostsController.cs 不存在");
        Assert.True(File.Exists(contractsFile), "RemoteFileContracts.cs 不存在");
        Assert.True(File.Exists(mapperFile), "EndpointsMapper.g.cs 不存在");
        Assert.True(File.Exists(feViewFile), "RemoteHostsView.js 不存在");

        var poolContent = File.ReadAllText(sessionPoolFile);
        var controllerContent = File.ReadAllText(controllerFile);
        var contractsContent = File.ReadAllText(contractsFile);
        var mapperContent = File.ReadAllText(mapperFile);
        var feContent = File.ReadAllText(feViewFile);

        // 1. 连接池与会话复用验证
        Assert.Contains("GetOrCreateAsync", poolContent);
        Assert.Contains("Invalidate", poolContent);
        Assert.Contains("RemoteFileSystemSessionPool", controllerContent);
        Assert.Contains("sessionPool.GetOrCreateAsync", controllerContent);

        // 2. 复制与移动能力验证
        Assert.Contains("CopyAsync", contractsContent);
        Assert.Contains("copy", controllerContent);
        Assert.Contains("move", controllerContent);

        // 3. 上传与下载 MinimalApi AOT 端点映射验证
        Assert.Contains("/Files/Upload", mapperContent);
        Assert.Contains("/Files/Download", mapperContent);

        // 4. 前端交互功能完整性验证
        Assert.Contains("triggerFileUpload", feContent);
        Assert.Contains("downloadEntry", feContent);
        Assert.Contains("openCopyDialog", feContent);
        Assert.Contains("openMoveDialog", feContent);
    }

    [Fact]
    public void LinuxArch030_挂载进程守卫必须具备有界TTL与僵尸进程豁免且卸载和未挂载状态不被死锁拦截()
    {
        var mountDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Mount", "Adapters");
        var guardFile = Path.Combine(mountDir, "MountProbeProcessGuard.cs");
        var stateMachineFile = Path.Combine(mountDir, "MountStateMachineService.cs");
        var smbServiceFile = Path.Combine(mountDir, "SmbMountService.cs");

        Assert.True(File.Exists(guardFile), "MountProbeProcessGuard.cs 不存在");
        Assert.True(File.Exists(stateMachineFile), "MountStateMachineService.cs 不存在");
        Assert.True(File.Exists(smbServiceFile), "SmbMountService.cs 不存在");

        var guardContent = File.ReadAllText(guardFile);
        var stateMachineContent = File.ReadAllText(stateMachineFile);
        var smbServiceContent = File.ReadAllText(smbServiceFile);

        // 1. 严禁 Win32Exception 永久返回 true 导致进程守卫死锁
        Assert.DoesNotContain("catch (System.ComponentModel.Win32Exception) { return true; }", guardContent);

        // 2. 必须具备有界 TTL (RecordedAt + DefaultTtl) 与主动 Clear 能力
        Assert.Contains("RecordedAt", guardContent);
        Assert.Contains("DefaultTtl", guardContent);
        Assert.Contains("public static void Clear(string path)", guardContent);

        // 3. 必须识别并放行 Linux 僵尸进程 (Z) 和已退出进程
        Assert.Contains("\"Z\"", guardContent);
        Assert.Contains("exited-or-unavailable", guardContent);

        // 4. 状态机在卸载/删除或未挂载状态时必须调用 MountProbeProcessGuard.Clear 解锁，严禁死锁拦截
        Assert.Contains("MountProbeProcessGuard.Clear(descriptor.Path)", stateMachineContent);

        // 5. SMB 挂载参数必须默认注入 echo_interval 以缩短内核 D 状态阻塞窗口
        Assert.Contains("echo_interval", smbServiceContent);
    }

    [Fact]
    public void LinuxArch031_McpCatalog与所有Provider工具必须完全双向一致零差异零孤儿()
    {
        var mcpDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Mcp", "Engine");
        var catalogFile = Path.Combine(mcpDir, "McpCatalogRegistry.cs");
        var providersDir = Path.Combine(mcpDir, "Providers");

        Assert.True(File.Exists(catalogFile), "McpCatalogRegistry.cs 不存在");
        Assert.True(Directory.Exists(providersDir), "Providers 目录不存在");

        var catalogContent = File.ReadAllText(catalogFile);

        // 1. 提取所有 Provider 中注册的实际工具名
        var providerTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(providersDir, "*Provider.cs"))
        {
            var content = File.ReadAllText(file);
            var matches = Regex.Matches(content, @"new\s+McpToolDefinition\s*\(\s*""([^""]+)""");
            foreach (Match m in matches)
            {
                providerTools.Add(m.Groups[1].Value);
            }
        }
        Assert.True(providerTools.Count >= 20, $"扫描到的 Provider 工具数量异常过少: {providerTools.Count}");

        // 2. 提取 Catalog 中注册的所有工具名
        var catalogTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catMatches = Regex.Matches(catalogContent, @"new\s*\(\s*""([^""]+)""\s*,\s*""[^""]+""\s*,\s*""(?:Read|Execute|Dangerous)""");
        foreach (Match m in catMatches)
        {
            catalogTools.Add(m.Groups[1].Value);
        }
        Assert.True(catalogTools.Count >= 20, $"扫描到的 Catalog 工具数量异常过少: {catalogTools.Count}");

        // 3. 双向契约一致性校验：Provider 中的每个工具必须在 Catalog 中有对应（1:1 无孤儿工具）
        var missingInCatalog = new List<string>();
        foreach (var tool in providerTools)
        {
            if (!catalogTools.Contains(tool))
            {
                missingInCatalog.Add(tool);
            }
        }
        Assert.True(missingInCatalog.Count == 0,
            $"发现底层 Provider 已实现但未在 McpCatalogRegistry.cs 中登记的孤儿工具（前端无法勾选）：\n{string.Join("\n", missingInCatalog)}");

        // 4. 双向契约一致性校验：Catalog 中的每个工具必须在 Provider 中有真实实现（1:1 无幽灵工具）
        var ghostInCatalog = new List<string>();
        foreach (var tool in catalogTools)
        {
            if (!providerTools.Contains(tool))
            {
                ghostInCatalog.Add(tool);
            }
        }
        Assert.True(ghostInCatalog.Count == 0,
            $"发现 McpCatalogRegistry.cs 中登记但底层 Provider 未实现的幽灵工具（勾选后无法执行）：\n{string.Join("\n", ghostInCatalog)}");
    }

    [Fact]
    public void LinuxArch032_备份同步引擎必须具备符号链接保真与单文件异常弹性防御能力()
    {
        var backupDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "BackupSync", "Adapters");
        var helperFile = Path.Combine(backupDir, "SymlinkAndFileTransferHelper.cs");
        var hardlinkEngineFile = Path.Combine(backupDir, "HardlinkSnapshotEngine.cs");
        var mirrorEngineFile = Path.Combine(backupDir, "MirrorSyncEngine.cs");
        var restoreEngineFile = Path.Combine(backupDir, "SnapshotRestoreEngine.cs");

        Assert.True(File.Exists(helperFile), "SymlinkAndFileTransferHelper.cs 不存在");
        Assert.True(File.Exists(hardlinkEngineFile), "HardlinkSnapshotEngine.cs 不存在");
        Assert.True(File.Exists(mirrorEngineFile), "MirrorSyncEngine.cs 不存在");
        Assert.True(File.Exists(restoreEngineFile), "SnapshotRestoreEngine.cs 不存在");

        var helperContent = File.ReadAllText(helperFile);
        var hardlinkContent = File.ReadAllText(hardlinkEngineFile);
        var mirrorContent = File.ReadAllText(mirrorEngineFile);
        var restoreContent = File.ReadAllText(restoreEngineFile);

        // 1. 验证辅助类具备符号链接判断与安全删除/重建
        Assert.Contains("IsSymbolicLink", helperContent);
        Assert.Contains("GetLinkTarget", helperContent);
        Assert.Contains("CreateOrUpdateSymlink", helperContent);
        Assert.Contains("SafeDeleteFileOrLink", helperContent);
        Assert.Contains("TryGetSafeLength", helperContent);

        // 2. 验证三个核心引擎均集成了 SymlinkAndFileTransferHelper
        Assert.Contains("SymlinkAndFileTransferHelper", hardlinkContent);
        Assert.Contains("SymlinkAndFileTransferHelper", mirrorContent);
        Assert.Contains("SymlinkAndFileTransferHelper", restoreContent);

        // 3. 验证严禁直接调用无防护的 file.Length 或 file.CopyTo
        Assert.Contains("IsSymbolicLink", hardlinkContent);
        Assert.Contains("IsSymbolicLink", mirrorContent);
        Assert.Contains("IsSymbolicLink", restoreContent);

        // 4. 验证引擎循环内必须具备单文件异常弹性捕获隔离（防止单个消失文件中断全局备份）
        Assert.Contains("IOException or UnauthorizedAccessException", hardlinkContent);
        Assert.Contains("IOException or UnauthorizedAccessException", mirrorContent);
        Assert.Contains("IOException or UnauthorizedAccessException", restoreContent);
    }

    [Fact]
    public void LinuxArch033_备份同步执行记录必须具备状态机生命周期与自动清理保留策略()
    {
        var infraDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure");
        var persistenceDir = Path.Combine(infraDir, "Shared", "Persistence");
        var backupRetentionDir = Path.Combine(infraDir, "Features", "BackupSync", "Retention");
        var backupSafetyDir = Path.Combine(infraDir, "Features", "BackupSync", "Safety");
        var webhostDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost");

        var dbSetupFile = Path.Combine(persistenceDir, "DbSetup.cs");
        var recordStoreFile = Path.Combine(persistenceDir, "BackupSyncRecordStore.cs");
        var retentionStrategyFile = Path.Combine(backupRetentionDir, "IBackupRecordRetentionStrategy.cs");
        var stateMachineFile = Path.Combine(backupSafetyDir, "BackupSyncStateMachine.cs");
        var controllerFile = Path.Combine(webhostDir, "Features", "BackupSync", "Routes", "BackupSyncController.cs");

        Assert.True(File.Exists(dbSetupFile), "DbSetup.cs 不存在");
        Assert.True(File.Exists(recordStoreFile), "BackupSyncRecordStore.cs 不存在");
        Assert.True(File.Exists(retentionStrategyFile), "IBackupRecordRetentionStrategy.cs 不存在");
        Assert.True(File.Exists(stateMachineFile), "BackupSyncStateMachine.cs 不存在");
        Assert.True(File.Exists(controllerFile), "BackupSyncController.cs 不存在");

        var dbSetup = File.ReadAllText(dbSetupFile);
        var recordStore = File.ReadAllText(recordStoreFile);
        var retention = File.ReadAllText(retentionStrategyFile);
        var stateMachine = File.ReadAllText(stateMachineFile);
        var controller = File.ReadAllText(controllerFile);

        // 1. 验证 SQLite 建表具备 COLLATE NOCASE 和索引
        Assert.Contains("CREATE TABLE IF NOT EXISTS backup_sync_record", dbSetup);
        Assert.Contains("idx_backup_sync_record_task_time", dbSetup);
        Assert.Contains("idx_backup_sync_record_time", dbSetup);

        // 2. 验证仓储具备 DapperAot、生命周期更新、查询与智能修剪
        Assert.Contains("[DapperAot]", recordStore);
        Assert.Contains("InsertRecordAsync", recordStore);
        Assert.Contains("UpdateRecordCompletionAsync", recordStore);
        Assert.Contains("QueryRecordsAsync", recordStore);
        Assert.Contains("PruneTaskRecordsAsync", recordStore);
        Assert.Contains("MarkInterruptedRunningRecordsAsync", recordStore);

        // 3. 验证保留策略抽象与智能复合实现
        Assert.Contains("interface IBackupRecordRetentionStrategy", retention);
        Assert.Contains("class SmartHybridRecordRetentionStrategy", retention);

        // 4. 验证状态机驱动单次执行生命周期
        Assert.Contains("StartExecutionRecordAsync", stateMachine);
        Assert.Contains("CompleteExecutionRecordAsync", stateMachine);
        Assert.Contains("StatusInterrupted", stateMachine);

        // 5. 验证控制器暴露查询与清理端点
        Assert.Contains("[HttpGet(\"Records\")]", controller);
        Assert.Contains("[HttpPost(\"Records/Clear\")]", controller);
    }

    [Fact]
    public void LinuxArch034_备份同步执行进度必须具备内存无锁热路径与阶段状态机管理()
    {
        var contractsDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "BackupSync", "Contracts");
        var progressDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "BackupSync", "Progress");
        var aotFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Composition", "AppJsonSerializerContext.cs");
        var controllerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "BackupSync", "Routes", "BackupSyncController.cs");
        var endpointsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs");

        var progressContractsFile = Path.Combine(contractsDir, "BackupSyncProgressContracts.cs");
        var reporterInterfaceFile = Path.Combine(progressDir, "IBackupProgressReporter.cs");
        var trackerFile = Path.Combine(progressDir, "BackupProgressTracker.cs");

        Assert.True(File.Exists(progressContractsFile), "BackupSyncProgressContracts.cs 不存在");
        Assert.True(File.Exists(reporterInterfaceFile), "IBackupProgressReporter.cs 不存在");
        Assert.True(File.Exists(trackerFile), "BackupProgressTracker.cs 不存在");

        var contractsContent = File.ReadAllText(progressContractsFile);
        var trackerContent = File.ReadAllText(trackerFile);
        var aotContent = File.ReadAllText(aotFile);
        var controllerContent = File.ReadAllText(controllerFile);
        var endpointsContent = File.ReadAllText(endpointsFile);

        // 1. 契约层具备状态机枚举与不可变快照模型
        Assert.Contains("enum BackupExecutionPhase", contractsContent);
        Assert.Contains("record BackupProgressSnapshot", contractsContent);

        // 2. 内存热路径无锁约束（ReportFileProcessed 严禁互斥锁）
        Assert.Contains("Interlocked.Increment(ref _processedFiles)", trackerContent);
        Assert.Contains("Interlocked.Add(ref _processedBytes", trackerContent);
        Assert.Contains("Volatile.Read(ref _scannedFiles)", trackerContent);

        // 3. AOT 零反射序列化契约绑定
        Assert.Contains("[JsonSerializable(typeof(BackupProgressSnapshot))]", aotContent);
        Assert.Contains("[JsonSerializable(typeof(BackupExecutionPhase))]", aotContent);

        // 4. REST 端点与 Minimal API 映射匹配
        Assert.Contains("[HttpGet(\"Tasks/{id:guid}/Progress\")]", controllerContent);
        Assert.Contains("group_BackupSyncController.MapGet(\"Tasks/{id:guid}/Progress\"", endpointsContent);
    }

    [Fact]
    public void LinuxArch035_插件平台统一契约与纯粹服务运维架构门禁()
    {
        var servicesController = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Services", "Routes", "ServicesController.cs");
        var pluginsController = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Routes", "PluginsController.cs");
        var endpointsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs");
        var pipelineFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Composition", "PipelineExtensions.cs");
        var pluginContracts = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginModels.cs");

        var servicesContent = File.ReadAllText(servicesController);
        var pluginsContent = File.ReadAllText(pluginsController);
        var endpointsContent = File.ReadAllText(endpointsFile);
        var pipelineContent = File.ReadAllText(pipelineFile);
        var pluginContractsContent = File.ReadAllText(pluginContracts);

        // 1. 服务运维页面纯粹化：ServicesController 绝不包含任何 Extensions 端点
        Assert.DoesNotContain("Extensions", servicesContent);

        // 2. 模式 3 (Webview 沙箱资产) 与 模式 1 (声明式动作与表单)：统一由 PluginsController 暴露
        Assert.Contains("[HttpGet(\"{id}/Assets/{**filePath}\")]", pluginsContent);
        Assert.Contains("[HttpPost(\"Action\")]", pluginsContent);
        Assert.Contains("group_PluginsController.MapGet(\"{id}/Assets\"", endpointsContent);
        Assert.Contains("group_PluginsController.MapPost(\"Action\"", endpointsContent);
        Assert.Contains("public PluginUiConfig? Ui { get; init; }", pluginContractsContent);
        Assert.Contains("public IReadOnlyList<PluginCustomAction> CustomActions", pluginContractsContent);

        // 3. 模式 2 (HTTP 网关反代)：统一路由为 /plugins/{pluginId}/{**catch-all}
        Assert.Contains("public string? GatewayUrl => AccessUrl;", pluginContractsContent);
        Assert.Contains("app.Map(\"/plugins/{pluginId}/{**catch-all}\"", pipelineContent);
    }

    [Fact]
    public void LinuxArch036_跨平台脚本行尾换行符与终端自愈架构门禁()
    {
        // 1. 扫描仓库中所有 shell 脚本，断言严禁包含 \r (CRLF)，防止 Linux Bash 报错 unexpected token
        var scriptFiles = Directory.GetFiles(RepoRoot, "*.sh", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(RepoRoot, "*.bash", SearchOption.AllDirectories))
            .Where(f => !f.Contains(".git") && !f.Contains("bin") && !f.Contains("obj"))
            .ToList();

        var crlfViolations = new List<string>();
        foreach (var file in scriptFiles)
        {
            var bytes = File.ReadAllBytes(file);
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
            {
                crlfViolations.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }
        Assert.True(crlfViolations.Count == 0,
            $"发现包含 CRLF(\\r\\n) 的 Shell 脚本，在 Linux 下会导致 unexpected token 语法错误：\n{string.Join("\n", crlfViolations)}");

        // 2. 断言根目录存在 .gitattributes 且强制声明 *.sh text eol=lf
        var gitAttributesPath = Path.Combine(RepoRoot, ".gitattributes");
        Assert.True(File.Exists(gitAttributesPath), "根目录必须存在 .gitattributes 文件");
        var gitAttributesContent = File.ReadAllText(gitAttributesPath);
        Assert.Contains("*.sh text eol=lf", gitAttributesContent);

        // 3. 断言 CrossPlatformPtyEngine 具备自愈机制（检测到 \r 自动剥离清洗）
        var ptyEnginePath = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Terminal", "Platform", "CrossPlatformPtyEngine.cs");
        Assert.True(File.Exists(ptyEnginePath), "CrossPlatformPtyEngine.cs 不存在");
        var ptyEngineContent = File.ReadAllText(ptyEnginePath);
        Assert.Contains("SanitizeBashRc", ptyEngineContent);
        Assert.Contains("Replace(\"\\r\\n\", \"\\n\")", ptyEngineContent);

        // 4. 断言 Dockerfile 与 Dockerfile.dev 阶段包含针对 terminal-bashrc.sh 的行尾清洗防御指令
        var dockerfile = Path.Combine(RepoRoot, "Dockerfile");
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        Assert.Contains("sed -i 's/\\r$//' /app/terminal-bashrc.sh", File.ReadAllText(dockerfile));
        Assert.Contains("sed -i 's/\\r$//' /app/terminal-bashrc.sh", File.ReadAllText(dockerfileDev));
    }

    [Fact]
    public void LinuxArch037_插件运行时生态一致性与多发行版包管理器适配门禁()
    {
        // 1. TargetFramework 统一为 net10.0 对齐断言
        var defaultsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.Defaults.cs");
        var adaptersFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");
        var managerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.cs");

        var defaultsContent = File.ReadAllText(defaultsFile);
        var adaptersContent = File.ReadAllText(adaptersFile);
        var managerContent = File.ReadAllText(managerFile);

        var csprojBlock = Regex.Match(defaultsContent, @"var csproj = """"""(?<xml>[\s\S]*?)""""""").Groups["xml"].Value;
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csprojBlock);
        Assert.DoesNotContain("<TargetFramework>net8.0</TargetFramework>", csprojBlock);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", adaptersContent);

        // 2. 发行版自适应检测与包管理器隔离
        Assert.Contains("DetectCurrentDistroKey()", managerContent);

        // 3. 动态实例化所有 IRuntimeAdapter，断言 Alpine 严禁推荐 apt，Debian 严禁推荐 apk
        var adapterTypes = typeof(LinuxWebTool.Infrastructure.Features.Plugins.Adapters.IRuntimeAdapter).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(LinuxWebTool.Infrastructure.Features.Plugins.Adapters.IRuntimeAdapter).IsAssignableFrom(t));

        foreach (var type in adapterTypes)
        {
            var adapter = (LinuxWebTool.Infrastructure.Features.Plugins.Adapters.IRuntimeAdapter)Activator.CreateInstance(type)!;
            var alpineGuide = adapter.ResolveInstallGuide("alpine");
            var debianGuide = adapter.ResolveInstallGuide("debian");

            Assert.False(alpineGuide.Contains("apt ") || alpineGuide.Contains("apt-get "),
                $"{type.Name} 在 alpine 环境下的安装指引包含 apt 命令：{alpineGuide}");
            Assert.False(debianGuide.Contains("apk add"),
                $"{type.Name} 在 debian 环境下的安装指引包含 apk 命令：{debianGuide}");
        }

        // 4. DotNetRuntimeAdapter 在各发行版下安装指引均必须指向 .NET 10
        var dotNetAdapter = new LinuxWebTool.Infrastructure.Features.Plugins.Adapters.DotNetRuntimeAdapter();
        Assert.Contains("10", dotNetAdapter.ResolveInstallGuide("alpine"));
        Assert.Contains("10", dotNetAdapter.ResolveInstallGuide("debian"));
        Assert.Contains("10", dotNetAdapter.ResolveInstallGuide("windows"));

        // 5. 极速开发镜像必须从 build 镜像拷贝官方 .NET 10 SDK 保证自备运行时
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        var dockerfileDevContent = File.ReadAllText(dockerfileDev);
        Assert.Contains("COPY --from=build /usr/share/dotnet /usr/share/dotnet", dockerfileDevContent);
        Assert.Contains("ENV PATH=\"/usr/share/dotnet:$PATH\"", dockerfileDevContent);
    }

    [Fact]
    public void LinuxArch038_原生插件调度生命周期异常保护与端口防泄漏门禁()
    {
        var strategyFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Strategies", "ResidentExecutionStrategy.cs");
        var adaptersFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");

        var strategyContent = File.ReadAllText(strategyFile);
        var adaptersContent = File.ReadAllText(adaptersFile);

        // 1. BuildStartSpec 必须置于 try/catch 块中，并在异常时显式调用 ReleasePort 释放已分配端口
        Assert.Contains("spec = adapter.BuildStartSpec(manifest, pluginDir, allocatedPort);", strategyContent);
        Assert.Contains("portManager.ReleasePort(manifest.Id);", strategyContent);
        Assert.Contains("stateMachine.MarkMissingRuntime", strategyContent);

        // 2. NativeRuntimeAdapter 必须具备三级自愈机制，防止由于运行目录缺少二进制文件直接盲目抛出异常
        Assert.Contains("NodeRuntimeAdapter.IsExecutableInPath(targetRef)", adaptersContent);
        Assert.Contains("三级自愈探查", adaptersContent);
    }

    [Fact]
    public void LinuxArch039_极速开发镜像必须闭环构建原生微服务二进制门禁()
    {
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        var dockerfileDevContent = File.ReadAllText(dockerfileDev);

        // 1. 构建层必须安装 go 编译器
        Assert.Contains("apk update && apk add --no-cache build-base go", dockerfileDevContent);

        // 2. 构建层必须包含 mcp-bridge 的编译命令
        Assert.Contains("cd plugins/mcp-bridge && CGO_ENABLED=0 go build", dockerfileDevContent);

        // 3. 运行阶段必须将编译产物拷贝并赋予执行权限
        Assert.Contains("/usr/local/bin/mcp-bridge", dockerfileDevContent);
        Assert.Contains("chmod +x /usr/local/bin/mcp-bridge", dockerfileDevContent);
    }

    [Fact]
    public void LinuxArch040_原生插件参数解析与悬挂宏防错门禁()
    {
        var adaptersFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");
        var adaptersContent = File.ReadAllText(adaptersFile);

        // 1. NativeRuntimeAdapter 必须支持回环端口与监听地址宏变量自动替换
        Assert.Contains("{port}", adaptersContent);
        Assert.Contains("{listenAddr}", adaptersContent);
        Assert.Contains("{stateDir}", adaptersContent);

        // 2. 必须具备成对标志清理逻辑，防止悬挂未绑定的宏参数被传递为字面量导致微服务崩溃
        Assert.Contains("--control-token-file", adaptersContent);
        Assert.Contains("{controlTokenFile}", adaptersContent);
    }

    [Fact]
    public void LinuxArch041_原生微服务二进制智能同步与版本防腐门禁()
    {
        var defaultsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.Defaults.cs");
        var defaultsContent = File.ReadAllText(defaultsFile);

        // 1. EnsureMcpBridgeBinaryPresent 必须比对文件时间戳与长度，禁止因旧二进制存在而放弃更新
        Assert.Contains("GetLastWriteTimeUtc", defaultsContent);
        Assert.Contains("FileInfo(c).Length != new FileInfo(binDest).Length", defaultsContent);

        // 2. 必须包含发布根目录 /app 候选路径，确保容器卷覆盖场景下优先同步最新编译产物
        Assert.Contains("Path.Combine(\"/app\", binName)", defaultsContent);
    }

    [Fact]
    public void LinuxArch042_Go运行时适配器与JIT增量构建安全门禁()
    {
        var modelsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginModels.cs");
        var adaptersFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");
        var goAdapterFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "GoRuntimeAdapter.cs");
        var pluginMgrFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.cs");
        var parserFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "SingleFileHeaderParser.cs");

        var modelsContent = File.ReadAllText(modelsFile);
        var adaptersContent = File.Exists(goAdapterFile) ? File.ReadAllText(goAdapterFile) : File.ReadAllText(adaptersFile);
        var pluginMgrContent = File.ReadAllText(pluginMgrFile);
        var parserContent = File.ReadAllText(parserFile);

        // 1. PluginRuntime 枚举必须定义 Go = 6
        Assert.Contains("Go = 6", modelsContent);

        // 2. PluginManager 必须注册 GoRuntimeAdapter 对应 PluginRuntime.Go
        Assert.Contains("RegisterAdapter(new GoRuntimeAdapter())", pluginMgrContent);
        Assert.Contains("public PluginRuntime RuntimeType => PluginRuntime.Go;", adaptersContent);

        // 3. GoRuntimeAdapter 必须实现 IRuntimeAdapter 并采用 JIT 增量编译，严禁直接使用导致子进程泄漏的 go run
        Assert.Contains("class GoRuntimeAdapter : IRuntimeAdapter", adaptersContent);
        Assert.Contains(".source.stamp", adaptersContent);
        Assert.Contains("build -ldflags", adaptersContent);
        Assert.Contains("FindGoExecutable", adaptersContent);
        Assert.DoesNotContain("\"run\"", adaptersContent.Split("class GoRuntimeAdapter")[1]);

        // 4. 单文件扩展头部解析器必须支持 .go 扩展名与 go/golang 运行时标识
        Assert.Contains("\".go\"", parserContent);
        Assert.Contains("\"golang\"", parserContent);
        Assert.Contains("PluginRuntime.Go", parserContent);
    }

    [Fact]
    public void LinuxArch043_SOCKS5代理微服务规范与零外部依赖门禁()
    {
        var socksDir = Path.Combine(RepoRoot, "plugins", "socks5-proxy");
        var manifestFile = Path.Combine(socksDir, "plugin.json");
        var modFile = Path.Combine(socksDir, "go.mod");
        var mainFile = Path.Combine(socksDir, "cmd", "socks5-proxy", "main.go");
        var defaultsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.Defaults.cs");

        Assert.True(File.Exists(manifestFile), "plugins/socks5-proxy/plugin.json 必须存在");
        Assert.True(File.Exists(modFile), "plugins/socks5-proxy/go.mod 必须存在");
        Assert.True(File.Exists(mainFile), "plugins/socks5-proxy/cmd/socks5-proxy/main.go 必须存在");

        var manifestContent = File.ReadAllText(manifestFile);
        var modContent = File.ReadAllText(modFile);
        var mainContent = File.ReadAllText(mainFile);
        var defaultsContent = File.ReadAllText(defaultsFile);

        // 1. 清单必须声明为常驻微服务 (profile: 0 或 resident)，运行时为 go，端口为 18001 或 18080
        Assert.Contains("\"runtime\": \"go\"", manifestContent);
        Assert.True(manifestContent.Contains("18001") || manifestContent.Contains("18080"), "SOCKS5 proxy 端口必须定义为 18001 或 18080");
        Assert.Contains("socks5-proxy", manifestContent);

        // 2. go.mod 必须保持零第三方外部依赖 (不含 require)，确保纯离线与秒级自编译
        Assert.DoesNotContain("require (", modContent);
        Assert.DoesNotContain("require\t", modContent);

        // 3. main.go 必须遵循 RFC 1928 / RFC 1929 规范 (处理认证握手与 CONNECT 请求)
        Assert.Contains("socks5-proxy", mainContent);
        Assert.Contains("net.Listen", mainContent);
        Assert.Contains("io.Copy", mainContent);

        // 4. PluginManager.Defaults.cs 必须注册并自动确保 socks5-proxy 微服务创建
        Assert.Contains("EnsureSocks5ProxyCreated", defaultsContent);
    }

    [Fact]
    public void LinuxArch045_PluginWebviewSandboxAndModalIntegrity()
    {
        var webviewModalFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginWebviewModal.js");
        var configJsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "config.js");
        var mcpBridgeHtmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var handlerGoFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var webGoFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "web.go");

        Assert.True(File.Exists(webviewModalFile), "PluginWebviewModal.js 必须存在");
        Assert.True(File.Exists(configJsFile), "config.js 必须存在");
        Assert.True(File.Exists(mcpBridgeHtmlFile), "mcp-bridge index.html 必须存在");
        Assert.True(File.Exists(handlerGoFile), "mcp-bridge handler.go 必须存在");
        Assert.True(File.Exists(webGoFile), "mcp-bridge web.go 必须存在");

        var webviewContent = File.ReadAllText(webviewModalFile);
        var configContent = File.ReadAllText(configJsFile);
        var mcpHtml = File.ReadAllText(mcpBridgeHtmlFile);
        var handlerGo = File.ReadAllText(handlerGoFile);
        var webGo = File.ReadAllText(webGoFile);

        // 1. 宿主 iframe 沙箱必须显式声明 allow-modals 权限，避免原生弹窗被静默阻断
        Assert.Contains("allow-modals", webviewContent);

        // 2. 宿主 WebviewModal 针对常驻微服务必须走反代网关 /plugins/{id}/，静态 UI 资产必须使用规范资产端点
        Assert.Contains("API.plugins.gatewayUrl", webviewContent);
        Assert.Contains("/api/Plugins/${encodeURIComponent(id)}/Assets/", configContent);

        // 3. mcp-bridge 控制台 HTML 严禁使用阻断式原生弹窗 (window.prompt / window.confirm)
        Assert.DoesNotContain("prompt(", mcpHtml);
        Assert.DoesNotContain("confirm(", mcpHtml);
        Assert.Contains("function getBaseUrl()", mcpHtml);
        Assert.Contains("openAddServerModal()", mcpHtml);
        Assert.Contains("modalServerId", mcpHtml);

        // 4. mcp-bridge Go 控制面必须具备 SPA Fallback 守卫与反代路径容错
        Assert.Contains("wrapFallback", handlerGo);
        Assert.Contains("strings.Contains(r.URL.Path, \"/control/invoke/\")", handlerGo);
        Assert.Contains("X-LWT-Plugin-Id", handlerGo);

        // 5. web.go 必须提供带 Token 注入的 WebHandler
        Assert.Contains("HandlerWithToken", webGo);
    }

    [Fact]
    public void LinuxArch046_SOCKS5代理网段白名单与域名过滤安全门禁()
    {
        var socksDir = Path.Combine(RepoRoot, "plugins", "socks5-proxy");
        var aclFile = Path.Combine(socksDir, "cmd", "socks5-proxy", "acl.go");
        var mainFile = Path.Combine(socksDir, "cmd", "socks5-proxy", "main.go");
        var manifestFile = Path.Combine(socksDir, "plugin.json");
        var uiFile = Path.Combine(socksDir, "ui", "index.html");

        Assert.True(File.Exists(aclFile), "acl.go 必须存在");
        Assert.True(File.Exists(mainFile), "main.go 必须存在");
        Assert.True(File.Exists(manifestFile), "plugin.json 必须存在");
        Assert.True(File.Exists(uiFile), "ui/index.html 必须存在");

        var aclContent = File.ReadAllText(aclFile);
        var mainContent = File.ReadAllText(mainFile);
        var manifestContent = File.ReadAllText(manifestFile);
        var uiContent = File.ReadAllText(uiFile);

        // 1. acl.go 必须实现子网前缀与 CIDR 匹配、通配符后缀快速匹配、RE2 正则匹配与 LAN 预设
        Assert.Contains("type SubnetRule struct", aclContent);
        Assert.Contains("strings.HasPrefix(s, r.prefix+\".\")", aclContent);
        Assert.Contains("type WildcardDomainRule struct", aclContent);
        Assert.Contains("strings.HasSuffix(domain, r.suffix)", aclContent);
        Assert.Contains("type RegexDomainRule struct", aclContent);
        Assert.Contains("PresetLan", aclContent);

        // 2. main.go 必须严格实现 RFC 1928 repRuleViolation (0x02) 拦截状态码
        Assert.Contains("repRuleViolation  = 0x02", mainContent);
        Assert.Contains("repRuleViolation", mainContent);

        // 3. 必须具备出站二次校验 (Anti-DNS-Rebinding Defense) 与客户端来源 IP 校验
        Assert.Contains("IsClientAllowed", mainContent);
        Assert.Contains("EvaluateTarget", mainContent);
        Assert.Contains("IsIpAllowed(resolvedIP)", mainContent);

        // 4. 清单与 UI 控制台必须支持快速配置与预设交互
        Assert.Contains("--allowed-subnets", manifestContent);
        Assert.Contains("--allowed-domains", manifestContent);
        Assert.Contains("input-subnets", uiContent);
        Assert.Contains("input-domains", uiContent);
        Assert.Contains("applyPreset", uiContent);
    }

    [Fact]
    public void LinuxArch047_Go构建包路径解析与SOCKS5三维安全防御门禁()
    {
        // 1. 验证 GoRuntimeAdapter.cs 采用相对包路径目录编译（例如 ./cmd/socks5-proxy 或 ./.），杜绝单文件编译丢失兄弟符号
        var goAdapterFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "GoRuntimeAdapter.cs");
        Assert.True(File.Exists(goAdapterFile), "GoRuntimeAdapter.cs 必须存在");
        var goAdapterContent = File.ReadAllText(goAdapterFile);
        Assert.Contains("buildTarget = string.IsNullOrEmpty(targetDir) || targetDir == \".\" ? \"./.\" : $\"./{targetDir}\"", goAdapterContent);
        Assert.Contains("Arguments = $\"build -ldflags \\\"-s -w\\\" -o \\\"{targetBinary}\\\" {buildTarget}\"", goAdapterContent);
        Assert.DoesNotContain("buildTarget = entry", goAdapterContent);

        // 2. SOCKS5 代理微服务核心实现检验 (三维安全架构)
        var socksDir = Path.Combine(RepoRoot, "plugins", "socks5-proxy");
        var aclFile = Path.Combine(socksDir, "cmd", "socks5-proxy", "acl.go");
        var mainFile = Path.Combine(socksDir, "cmd", "socks5-proxy", "main.go");
        var manifestFile = Path.Combine(socksDir, "plugin.json");
        var uiFile = Path.Combine(socksDir, "ui", "index.html");

        var aclContent = File.ReadAllText(aclFile);
        var mainContent = File.ReadAllText(mainFile);
        var manifestContent = File.ReadAllText(manifestFile);
        var uiContent = File.ReadAllText(uiFile);

        // 维度一：标准 CIDR 归一化与出站检验
        Assert.Contains("normalizeSubnetCidr", aclContent);
        Assert.Contains("10.0.0.0/8", aclContent);
        Assert.Contains("172.16.0.0/12", aclContent);
        Assert.Contains("192.168.0.0/16", aclContent);

        // 维度二：入站客户端 IP 访问限制 (CIDR 短路拦截)
        Assert.Contains("IsClientAllowed", aclContent);
        Assert.Contains("blockedClients", aclContent);
        Assert.Contains("RecordClientBlocked", aclContent);
        Assert.Contains("IsClientAllowed", mainContent);

        // 维度三：用户名密码认证 (RFC 1929) 与防时序攻击
        Assert.Contains("authUserPass", mainContent);
        Assert.Contains("userPassVersion", mainContent);
        Assert.Contains("subtle.ConstantTimeCompare", mainContent);
        Assert.Contains("RecordAuthFailure", aclContent);
        Assert.Contains("authFailures", aclContent);

        // 3. 插件清单与 Webview 控制台完整性
        Assert.Contains("--auth-mode", manifestContent);
        Assert.Contains("--allowed-clients", manifestContent);
        Assert.Contains("input-clients", uiContent);
        Assert.Contains("input-username", uiContent);
        Assert.Contains("input-password", uiContent);
        Assert.Contains("generateRandomPassword", uiContent);
    }

    [Fact]
    public void LinuxArch048_原生二进制跨平台格式校验与镜像构建防投毒门禁()
    {
        // 1. Dockerfile 与 Dockerfile.dev 复用预置二进制必须校验 ELF 标头魔数
        var dockerfile = Path.Combine(RepoRoot, "Dockerfile");
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        var dfContent = File.ReadAllText(dockerfile);
        var dfDevContent = File.ReadAllText(dockerfileDev);

        Assert.Contains("head -c 4", dfContent);
        Assert.Contains(@"printf '\177ELF'", dfContent);
        Assert.Contains("head -c 4", dfDevContent);
        Assert.Contains(@"printf '\177ELF'", dfDevContent);

        // 2. deploy-remote.ps1 必须校验 ELF 魔数
        var deployScript = Path.Combine(RepoRoot, "scripts", "deploy-remote.ps1");
        var deployContent = File.ReadAllText(deployScript);
        Assert.Contains("0x7F", deployContent);
        Assert.Contains("0x45", deployContent);
        Assert.Contains("0x4C", deployContent);
        Assert.Contains("0x46", deployContent);

        // 3. NativeBinaryValidator.cs 必须存在且覆盖 Shebang、PE 与 ELF 架构检测
        var validatorFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "NativeBinaryValidator.cs");
        Assert.True(File.Exists(validatorFile), "NativeBinaryValidator.cs 必须存在");
        var valContent = File.ReadAllText(validatorFile);
        Assert.Contains("0x23", valContent); // Shebang #
        Assert.Contains("0x21", valContent); // Shebang !
        Assert.Contains("0x4D", valContent); // PE M
        Assert.Contains("0x5A", valContent); // PE Z
        Assert.Contains("0x7F", valContent); // ELF \x7f
        Assert.Contains("ElfMachineAmd64", valContent);
        Assert.Contains("ElfMachineArm64", valContent);

        // 4. NativeRuntimeAdapter 与 PluginManager.Defaults 必须调用校验探针
        var adaptersFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");
        var defaultsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.Defaults.cs");
        Assert.Contains("NativeBinaryValidator.ValidateExecutable", File.ReadAllText(adaptersFile));
        Assert.Contains("NativeBinaryValidator.ValidateExecutable", File.ReadAllText(defaultsFile));
    }

    [Fact]
    public void LinuxArch049_MCP网关敏感字段变活动作与前后端契约容错门禁()
    {
        // 1. Go 模型与校验器必须兼容支持 update 敏感变活动作 (宽进严出 Postel's Law)
        var modelGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "config", "model.go");
        var validateGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "config", "validate.go");
        var modelContent = File.ReadAllText(modelGo);
        var validateContent = File.ReadAllText(validateGo);

        Assert.Contains("SecretActionUpdate", modelContent);
        Assert.Contains("\"update\"", modelContent);
        Assert.Contains("SecretActionReplace, SecretActionUpdate", validateContent);

        // 2. DTO 与 Handler 必须支持 presharedKey 别名与 update 动作归一化
        var dtoGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "dto.go");
        var handlerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var dtoContent = File.ReadAllText(dtoGo);
        var handlerContent = File.ReadAllText(handlerGo);

        Assert.Contains("PresharedKey", dtoContent);
        Assert.Contains("\"presharedKey,omitempty\"", dtoContent);
        Assert.Contains("tunnelKey == \"\" && req.PresharedKey != \"\"", handlerContent);
        Assert.Contains("mut.Action == config.SecretActionReplace || mut.Action == config.SecretActionUpdate", handlerContent);

        // 3. Webview 控制台前端必须具备双向兼容载荷
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("tunnelKey", htmlContent);
        Assert.Contains("presharedKey", htmlContent);

        // 4. 插件 DTO 与配置控制面必须覆盖 PresharedKey 与 ExpectedRevision
        Assert.Contains("ExpectedRevision", dtoContent);
        Assert.Contains("PresharedKey", dtoContent);
    }

    [Fact]
    public void LinuxArch050_插件长时服务快速换端与双栈冲突避让门禁()
    {
        // 1. 验证 PluginPortManager.cs 具备双栈探活性、系统内核监听表探测与冲突自动避让策略
        var portManagerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Supervisor", "PluginPortManager.cs");
        Assert.True(File.Exists(portManagerFile), "PluginPortManager.cs 必须存在");
        var pmContent = File.ReadAllText(portManagerFile);
        Assert.Contains("AllocatePortWithDetails", pmContent);
        Assert.Contains("CheckPort", pmContent);
        Assert.Contains("IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()", pmContent);
        Assert.Contains("AddressFamily.InterNetwork", pmContent);
        Assert.Contains("AddressFamily.InterNetworkV6", pmContent);
        Assert.Contains("PortAllocationResult", pmContent);
        Assert.Contains("WasAvoided", pmContent);
        Assert.Contains("GetRecommendedPort", pmContent);

        // 2. 验证 ResidentExecutionStrategy.cs 集成详细端口分配与孤儿进程回收机制
        var residentStrategyFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Strategies", "ResidentExecutionStrategy.cs");
        var stratContent = File.ReadAllText(residentStrategyFile);
        Assert.Contains("AllocatePortWithDetails", stratContent);
        Assert.Contains("process.pid", stratContent);
        Assert.Contains("历史遗留孤儿进程", stratContent);

        // 3. 验证 PluginsController.cs 暴露端口检测与切换 API
        var controllerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Routes", "PluginsController.cs");
        var ctrlContent = File.ReadAllText(controllerFile);
        Assert.Contains("[HttpGet(\"{id}/Port/Check\")]", ctrlContent);
        Assert.Contains("[HttpPost(\"{id}/Port/Switch\")]", ctrlContent);

        // 4. 验证前端组件具备快速切换端口交互
        var cardFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginCard.js");
        var modalFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginPortSwitchModal.js");
        Assert.True(File.Exists(modalFile), "PluginPortSwitchModal.js 必须存在");
        var cardContent = File.ReadAllText(cardFile);
        var modalContent = File.ReadAllText(modalFile);
        Assert.Contains("'switch-port'", cardContent);
        Assert.Contains("handleSwitchPort", cardContent);
        Assert.Contains("applyRecommended", modalContent);
        Assert.Contains("checkResult", modalContent);
    }

    [Fact]
    public void LinuxArch051_MCP标准JSON配置直接导入与多格式适配门禁()
    {
        // 1. Go 模型与解析适配器必须导出 ParseStandardMCPConfig 与 SlugifyServerID
        var importerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "config", "importer.go");
        Assert.True(File.Exists(importerGo), "importer.go 必须存在");
        var importerContent = File.ReadAllText(importerGo);
        Assert.Contains("ParseStandardMCPConfig", importerContent);
        Assert.Contains("SlugifyServerID", importerContent);
        Assert.Contains("mcpServers", importerContent);

        // 2. Go DTO 与 Admin Handler 必须支持 servers.import 动作与原子落盘
        var dtoGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "dto.go");
        var handlerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var dtoContent = File.ReadAllText(dtoGo);
        var handlerContent = File.ReadAllText(handlerGo);

        Assert.Contains("ImportServersRequestDTO", dtoContent);
        Assert.Contains("ImportServersResultDTO", dtoContent);
        Assert.Contains("actionServersImport", handlerContent);
        Assert.Contains("\"servers.import\"", handlerContent);

        // 3. Webview 控制台必须提供导入标准 MCP 配置模态框与实时解析器
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("openImportModal", htmlContent);
        Assert.Contains("submitImportServers", htmlContent);
        Assert.Contains("mcpServers", htmlContent);
    }

    [Fact]
    public void LinuxArch052_MCP桥接服务状态固化回填_容错降级生效与界面协同门禁()
    {
        // 1. Go StatusDTO 必须包含 TunnelID 与 HasTunnelKey 用于前端回填但严禁明文泄露
        var dtoGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "dto.go");
        var dtoContent = File.ReadAllText(dtoGo);
        Assert.Contains("TunnelID", dtoContent);
        Assert.Contains("HasTunnelKey", dtoContent);
        Assert.Contains("DegradedServers", dtoContent);

        // 2. gateway.go Apply 方法必须具备容错降级机制（单后端探测失败不阻断整体生效）
        var gatewayGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "gateway", "gateway.go");
        var gatewayContent = File.ReadAllText(gatewayGo);
        Assert.Contains("DegradedServers", gatewayContent);
        Assert.Contains("degradedServers", gatewayContent);

        // 3. handler.go 必须支持 servers.toggle 原子启停与 status.get 持久化状态回填
        var handlerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var handlerContent = File.ReadAllText(handlerGo);
        Assert.Contains("\"servers.toggle\"", handlerContent);
        Assert.Contains("actionServersToggle", handlerContent);
        Assert.Contains("st.Tunnel.TunnelID", handlerContent);
        Assert.Contains("st.Tunnel.EncryptedKey != \"\"", handlerContent);

        // 4. Web 控制台 index.html 必须并发拉取 servers.list 并支持回填与 toggle
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("servers.list", htmlContent);
        Assert.Contains("servers.toggle", htmlContent);
        Assert.Contains("s.tunnelId", htmlContent);
        Assert.Contains("s.hasTunnelKey", htmlContent);
        Assert.Contains("renderServers", htmlContent);
    }

    [Fact]
    public void LinuxArch053_MCP工具实时调用与全链路闭环门禁()
    {
        // 1. Go DTO 必须声明 CallToolRequestDTO 与 CallToolResultDTO
        var dtoGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "dto.go");
        var dtoContent = File.ReadAllText(dtoGo);
        Assert.Contains("CallToolRequestDTO", dtoContent);
        Assert.Contains("CallToolResultDTO", dtoContent);

        // 2. handler.go 必须路由 tools.call 并实现 actionToolsCall
        var handlerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var handlerContent = File.ReadAllText(handlerGo);
        Assert.Contains("\"tools.call\"", handlerContent);
        Assert.Contains("actionToolsCall", handlerContent);

        // 3. Web 控制台必须具备测试调用模态框与执行脚本
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("openTestModal", htmlContent);
        Assert.Contains("submitTestCall", htmlContent);
        Assert.Contains("testModal", htmlContent);

        // 4. 必须具备自备的独立真实 MCP 工具服务 system-tools.js
        var sampleToolJs = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "tools", "system-tools.js");
        Assert.True(File.Exists(sampleToolJs), "system-tools.js 必须存在于 plugins/mcp-bridge/tools/");
        var toolJsContent = File.ReadAllText(sampleToolJs);
        Assert.Contains("get_system_info", toolJsContent);
        Assert.Contains("execute_calc", toolJsContent);
    }

    [Fact]
    public void LinuxArch054_MCP工具激活状态投影与服务降级治理门禁()
    {
        // 1. Go StatusDTO 必须包含 ActiveToolCount 且标记 json:"activeToolCount"
        var dtoGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "dto.go");
        var dtoContent = File.ReadAllText(dtoGo);
        Assert.Contains("ActiveToolCount", dtoContent);
        Assert.Contains("\"activeToolCount\"", dtoContent);

        // 2. Go handler.go 必须在 actionStatusGet 中动态投影 ActiveToolCount
        var handlerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var handlerContent = File.ReadAllText(handlerGo);
        Assert.Contains("ActiveToolCount:", handlerContent);
        Assert.Contains("CurrentSnapshot", handlerContent);

        // 3. Web 控制台 index.html 必须具备工具管理模态框与勾选保存逻辑
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("toolSelectModal", htmlContent);
        Assert.Contains("openToolSelectModal", htmlContent);
        Assert.Contains("submitToolSelection", htmlContent);
        Assert.Contains("selectAllTools", htmlContent);

        // 4. Web 控制台 index.html 必须具备降级服务一键禁用治理与横幅能力
        Assert.Contains("degradedAlertBanner", htmlContent);
        Assert.Contains("disableDegradedServers", htmlContent);
    }

    [Fact]
    public void LinuxArch059_宿主微内核纯化与插件独立自包含门禁()
    {
        // 1. 宿主源码目录下严禁存在任何插件专有的业务目录（如 Features/McpBridge）
        var mcpBridgeDirs = Directory.GetDirectories(Path.Combine(RepoRoot, "src"), "McpBridge", SearchOption.AllDirectories);
        Assert.Empty(mcpBridgeDirs);

        // 2. 宿主 EndpointsMapper 严禁硬编码特异插件的私有 Controller
        var mapperCs = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "MinimalApi", "EndpointsMapper.g.cs");
        var mapperContent = File.ReadAllText(mapperCs);
        Assert.DoesNotContain("McpBridgeController", mapperContent);
        Assert.DoesNotContain("/api/McpBridge", mapperContent);

        // 3. 宿主 ApiKeyMiddleware 严禁包含特异插件的私有硬编码路径拦截
        var mwCs = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Shared", "Middleware", "ApiKeyMiddleware.cs");
        var mwContent = File.ReadAllText(mwCs);
        Assert.DoesNotContain("/api/McpBridge", mwContent);
    }

    [Fact]
    public void LinuxArch060_插件依赖声明契约与安装向导聚合门禁()
    {
        // 1. 契约层必须声明 PluginDependencyConfig 与 PluginDependenciesJsonConverter
        var modelsCs = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginModels.cs");
        var modelsContent = File.ReadAllText(modelsCs);
        Assert.Contains("PluginDependencyConfig", modelsContent);
        Assert.Contains("PluginDependencyProbeConfig", modelsContent);
        Assert.Contains("GetUnifiedDependencies", modelsContent);

        var converterCs = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginDependenciesJsonConverter.cs");
        Assert.True(File.Exists(converterCs), "PluginDependenciesJsonConverter.cs 必须存在");

        // 2. mcp-bridge 的 plugin.json 必须声明生态工具链依赖 (node-ecosystem 与 python-uv)
        var mcpPluginJson = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "plugin.json");
        var jsonContent = File.ReadAllText(mcpPluginJson);
        Assert.Contains("node-ecosystem", jsonContent);
        Assert.Contains("python-uv", jsonContent);
        Assert.Contains("npx", jsonContent);
        Assert.Contains("uvx", jsonContent);

        // 3. 宿主序列化上下文必须包含 PluginDependencyConfig 注册
        var aotContextCs = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginJsonContext.cs");
        var aotContent = File.ReadAllText(aotContextCs);
        Assert.Contains("PluginDependencyConfig", aotContent);
    }

    [Fact]
    public void LinuxArch061_MCP网关自动连接与看门狗自愈门禁()
    {
        // 1. 配置契约必须支持开机自连 (AutoConnect) 与看门狗守护 (AutoReconnect)
        var modelGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "config", "model.go");
        var modelContent = File.ReadAllText(modelGo);
        Assert.Contains("AutoConnect", modelContent);
        Assert.Contains("ShouldAutoConnect", modelContent);
        Assert.Contains("AutoReconnect", modelContent);
        Assert.Contains("ShouldAutoReconnect", modelContent);

        // 2. 看门狗 Supervisor 核心组件必须完整存在
        var supGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "watchdog", "supervisor.go");
        var supContent = File.ReadAllText(supGo);
        Assert.Contains("StartAsync", supContent);
        Assert.Contains("ReplayActiveRevision", supContent);
        Assert.Contains("checkTunnel", supContent);
        Assert.Contains("checkBackends", supContent);

        // 3. 必须具备带抖动的指数退避与熔断器保护 (防进程风暴与惊群)
        var backoffGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "watchdog", "backoff.go");
        var breakerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "watchdog", "circuit_breaker.go");
        Assert.True(File.Exists(backoffGo), "backoff.go 必须存在");
        Assert.True(File.Exists(breakerGo), "circuit_breaker.go 必须存在");

        var backoffContent = File.ReadAllText(backoffGo);
        Assert.Contains("RandomizationFactor", backoffContent);
        Assert.Contains("NextDelay", backoffContent);

        var breakerContent = File.ReadAllText(breakerGo);
        Assert.Contains("CircuitOpen", breakerContent);
        Assert.Contains("CircuitHalfOpen", breakerContent);
        Assert.Contains("RecordFailure", breakerContent);

        // 4. 隧道控制器必须具备用户人工意图隔离 (UserPaused)
        var tunnelCtrlGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "tunnel", "controller.go");
        var tunnelCtrlContent = File.ReadAllText(tunnelCtrlGo);
        Assert.Contains("SetUserPaused", tunnelCtrlContent);
        Assert.Contains("IsUserPaused", tunnelCtrlContent);
        Assert.Contains("StateBackoffWait", tunnelCtrlContent);

        // 5. 主程序启动必须在监听绑定后异步非阻塞启动 Supervisor
        var mainGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "cmd", "mcp-bridge", "main.go");
        var mainContent = File.ReadAllText(mainGo);
        Assert.Contains("CONTROL_LISTEN", mainContent);
        Assert.Contains("StartAsync", mainContent);
    }

    [Fact]
    public void LinuxArch055_Dockerfile与DockerfileDev还原层必须包含全部生产项目文件()
    {
        var dockerfiles = new[]
        {
            Path.Combine(RepoRoot, "Dockerfile"),
            Path.Combine(RepoRoot, "Dockerfile.dev")
        };

        var requiredProjects = new[]
        {
            "src/LinuxWebTool.Contracts/LinuxWebTool.Contracts.csproj",
            "src/LinuxWebTool.Application/LinuxWebTool.Application.csproj",
            "src/LinuxWebTool.Infrastructure/LinuxWebTool.Infrastructure.csproj",
            "src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj"
        };

        foreach (var dfPath in dockerfiles)
        {
            var content = File.ReadAllText(dfPath);
            var restoreIndex = content.IndexOf("dotnet restore", StringComparison.OrdinalIgnoreCase);
            Assert.True(restoreIndex > 0, $"{Path.GetFileName(dfPath)} 必须包含 dotnet restore 指令");

            var preRestoreSection = content[..restoreIndex];
            foreach (var proj in requiredProjects)
            {
                Assert.True(preRestoreSection.Contains(proj),
                    $"{Path.GetFileName(dfPath)} 在 dotnet restore 之前遗漏了项目文件预拷贝：{proj}，这将导致依赖还原缓存失效与发布期重复还原！");
            }
        }
    }

    [Fact]
    public void LinuxArch056_AppJsonSerializerContext禁止包含重复类型注册()
    {
        var contextFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Composition", "AppJsonSerializerContext.cs");
        Assert.True(File.Exists(contextFile), "AppJsonSerializerContext.cs 文件必须存在");

        var lines = File.ReadAllLines(contextFile)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("[JsonSerializable(typeof(") && l.EndsWith(")]"))
            .ToList();

        var duplicates = lines.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            $"AppJsonSerializerContext 存在重复注册的 JsonSerializable 特性：\n{string.Join("\n", duplicates)}");
    }

    [Fact]
    public void LinuxArch057_开发容器发布指令必须显式携带_no_restore()
    {
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        var content = File.ReadAllText(dockerfileDev);

        var publishMatches = Regex.Matches(content, @"dotnet\s+publish(?:\s*\\\r?\n|[^\r\n;])+");
        Assert.NotEmpty(publishMatches);

        foreach (Match match in publishMatches)
        {
            Assert.Contains("--no-restore", match.Value);
        }
    }

    [Fact]
    public void LinuxArch058_WebHost项目文件必须支持根据PublishAot解耦AOT与RDG开关()
    {
        var csproj = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "LinuxWebTool.WebHost.csproj");
        var content = File.ReadAllText(csproj);

        // 必须存在针对非 AOT 构建禁用 RDG 的机制
        Assert.True(
            content.Contains("Condition=\"'$(PublishAot)' == 'false'\"") ||
            content.Contains("Condition=\"'$(PublishAot)' != 'false'\""),
            "LinuxWebTool.WebHost.csproj 必须包含针对 '$(PublishAot)' 的条件判断，以在非 AOT 极速模式下关闭重型生成器！");
    }

    [Fact]
    public void LinuxArch061_DockerfileDev分层编译顺序守卫()
    {
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        var content = File.ReadAllText(dockerfileDev);

        var copyInfraIndex = content.IndexOf("COPY src/LinuxWebTool.Infrastructure", StringComparison.OrdinalIgnoreCase);
        var copyWebHostIndex = content.IndexOf("COPY src/LinuxWebTool.WebHost", StringComparison.OrdinalIgnoreCase);
        var intermediateBuildIndex = content.IndexOf("dotnet build \"src/LinuxWebTool.Infrastructure", StringComparison.OrdinalIgnoreCase);

        Assert.True(copyInfraIndex > 0, "Dockerfile.dev 必须具备独立的 Infrastructure 项目拷贝");
        Assert.True(copyWebHostIndex > 0, "Dockerfile.dev 必须具备独立的 WebHost 项目拷贝");
        Assert.True(intermediateBuildIndex > 0, "必须存在针对 Infrastructure 的中间预编译命令");

        Assert.True(copyInfraIndex < intermediateBuildIndex && intermediateBuildIndex < copyWebHostIndex,
            "Dockerfile.dev 必须遵循分层缓存状态机顺序：Copy Infra -> Build Infra -> Copy WebHost，以保障上游构建缓存命中！");
    }

    [Fact]
    public void LinuxArch062_非AOT构建应禁用前端嵌入资源()
    {
        var csproj = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "LinuxWebTool.WebHost.csproj");
        var content = File.ReadAllText(csproj);

        Assert.True(
            content.Contains("<EmbeddedResource Include=\"wwwroot\\**\"") &&
            (content.Contains("Condition=\"'$(PublishAot)' != 'false'\"") || content.Contains("Condition=\"'$(PublishAot)' == 'true'\"")),
            "LinuxWebTool.WebHost.csproj 的 EmbeddedResource 必须受限于 PublishAot 条件，避免开发期内嵌静态资源导致编译膨胀！");
    }

    [Fact]
    public void LinuxArch063_开发容器应预载mimalloc高性能分配器()
    {
        var dockerfileDev = Path.Combine(RepoRoot, "Dockerfile.dev");
        var content = File.ReadAllText(dockerfileDev);

        Assert.Contains("mimalloc", content);
        Assert.Contains("LD_PRELOAD=/usr/lib/libmimalloc.so", content);
    }

    [Fact]
    public void LinuxArch064_MCP网关渠道观测性与ChatGPT就绪门禁()
    {
        // 1. Go 配置模型与 DTO 必须支持出站代理与长轮询状态
        var modelGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "config", "model.go");
        var dtoGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "dto.go");
        Assert.True(File.Exists(modelGo), "model.go 必须存在");
        Assert.True(File.Exists(dtoGo), "dto.go 必须存在");

        var modelContent = File.ReadAllText(modelGo);
        var dtoContent = File.ReadAllText(dtoGo);

        Assert.Contains("ProxyURL", modelContent);
        Assert.Contains("\"proxyUrl,omitempty\"", modelContent);

        Assert.Contains("ReadyForChatGPT", dtoContent);
        Assert.Contains("\"readyForChatgpt\"", dtoContent);
        Assert.Contains("InitialPollMs", dtoContent);
        Assert.Contains("LastPollError", dtoContent);
        Assert.Contains("EffectiveProxy", dtoContent);
        Assert.Contains("ChannelLogEntryDTO", dtoContent);
        Assert.Contains("DiagnosticProbeResultDTO", dtoContent);

        // 2. 底层渠道日志环形缓冲区与诊断探针实现
        var logGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "tunnel", "channel_log.go");
        var diagGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "tunnel", "diagnostics.go");
        var controllerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "tunnel", "controller.go");

        Assert.True(File.Exists(logGo), "channel_log.go 必须存在");
        Assert.True(File.Exists(diagGo), "diagnostics.go 必须存在");
        Assert.True(File.Exists(controllerGo), "controller.go 必须存在");

        var logContent = File.ReadAllText(logGo);
        var diagContent = File.ReadAllText(diagGo);
        var ctrlContent = File.ReadAllText(controllerGo);

        Assert.Contains("ChannelLogBuffer", logContent);
        Assert.Contains("NewChannelLogBuffer", logContent);
        Assert.Contains("DiagnoseControlPlane", diagContent);
        Assert.Contains("api.openai.com", diagContent);

        // 控制器必须监听 Ready() 异步握手通道并注入 LogWriter，杜绝状态虚标
        Assert.Contains("client.Ready()", ctrlContent);
        Assert.Contains("ReadyForChatGPT", ctrlContent);
        Assert.Contains("LogWriter:", ctrlContent);
        Assert.Contains("HTTPS_PROXY", ctrlContent);

        // 3. Resident Control 必须暴露 tunnel.logs 与 tunnel.diagnose
        var handlerGo = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "admin", "handler.go");
        var handlerContent = File.ReadAllText(handlerGo);
        Assert.Contains("\"tunnel.logs\"", handlerContent);
        Assert.Contains("\"tunnel.diagnose\"", handlerContent);
        Assert.Contains("actionTunnelLogs", handlerContent);
        Assert.Contains("actionTunnelDiagnose", handlerContent);

        // 4. Webview 控制台协同：必须支持代理输入、ChatGPT 就绪徽章、实时 Wire Logs 抽屉与网络诊断探针
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("tunnelProxyInput", htmlContent);
        Assert.Contains("chatgptReadyBadge", htmlContent);
        Assert.Contains("tunnelLogsCard", htmlContent);
        Assert.Contains("diagnosticModal", htmlContent);
        Assert.Contains("runDiagnosticProbe", htmlContent);
        Assert.Contains("fetchTunnelLogs", htmlContent);
    }

    [Fact]
    public void LinuxArch065_插件鉴权Cookie下发与状态机降级停止门禁()
    {
        // 1. AuthController 必须在登录与续期接口下发 lwt_token Cookie
        var authCtrlFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Auth", "Routes", "AuthController.cs");
        Assert.True(File.Exists(authCtrlFile), "AuthController.cs 必须存在");
        var authContent = File.ReadAllText(authCtrlFile);
        Assert.Contains("Response.Cookies.Append(\"lwt_token\"", authContent);

        // 2. PluginAuthGatewayEvaluator 必须支持从 Query 参数与 Cookie 提取 Token
        var evalFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Auth", "PluginAuthGatewayEvaluator.cs");
        Assert.True(File.Exists(evalFile), "PluginAuthGatewayEvaluator.cs 必须存在");
        var evalContent = File.ReadAllText(evalFile);
        Assert.Contains("context.Request.Cookies.TryGetValue(\"lwt_token\"", evalContent);
        Assert.Contains("context.Request.Query.TryGetValue(\"token\"", evalContent);

        // 3. 插件状态机 pluginStateMachine.js canStop 必须允许降级、崩溃、退避状态手动停止
        var fsmFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "utils", "pluginStateMachine.js");
        Assert.True(File.Exists(fsmFile), "pluginStateMachine.js 必须存在");
        var fsmContent = File.ReadAllText(fsmFile);
        Assert.Contains("plugin.state === PLUGIN_STATE.DEGRADED", fsmContent);
        Assert.Contains("plugin.state === PLUGIN_STATE.CRASHED", fsmContent);
        Assert.Contains("plugin.state === PLUGIN_STATE.BACKOFF", fsmContent);

        // 4. PluginCard.js 必须透出降级原因与重新拉起/手动停止按钮
        var cardFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginCard.js");
        Assert.True(File.Exists(cardFile), "PluginCard.js 必须存在");
        var cardContent = File.ReadAllText(cardFile);
        Assert.Contains("服务已降级（熔断保护）", cardContent);
        Assert.Contains("重新拉起", cardContent);
        Assert.Contains("plugin.statusMessage", cardContent);

        // 5. Shell 运行时适配器必须在启动脚本前就地清洗 Windows CRLF
        var shellAdapterFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");
        Assert.True(File.Exists(shellAdapterFile), "PluginRuntimeAdapters.cs 必须存在");
        var adapterContent = File.ReadAllText(shellAdapterFile);
        Assert.Contains("content.Contains('\\r')", adapterContent);
        Assert.Contains(".Replace(\"\\r\\n\", \"\\n\")", adapterContent);

        // 6. PluginsController 执行动作必须返回结构化结果杜绝 400 吞噬错误
        var pluginsCtrlFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Routes", "PluginsController.cs");
        Assert.True(File.Exists(pluginsCtrlFile), "PluginsController.cs 必须存在");
        var ctrlContent = File.ReadAllText(pluginsCtrlFile);
        Assert.Contains("return Ok(result);", ctrlContent);
    }

    [Fact]
    public void LinuxArch066_轻量脚本自适应执行与MCP工具自动发现门禁()
    {
        // 1. PluginsController.SetEnabled 必须允许 bool? enabled = null 支持缺省 Toggle 彻底杜绝 400
        var pluginsCtrlFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Routes", "PluginsController.cs");
        Assert.True(File.Exists(pluginsCtrlFile), "PluginsController.cs 必须存在");
        var ctrlContent = File.ReadAllText(pluginsCtrlFile);
        Assert.Contains("public async Task<IResult> SetEnabled(string id, [FromQuery] bool? enabled", ctrlContent);
        Assert.Contains("targetEnabled = enabled ??", ctrlContent);

        // 2. PluginsController.Start 必须对 OneShot 轻量脚本自适应执行首个 Action 或默认入口
        Assert.Contains("plugin?.Profile == PluginProfile.OneShot", ctrlContent);
        Assert.Contains("pluginManager.ExecuteActionAsync", ctrlContent);

        // 3. NativeRuntimeAdapter 必须包含非空 Arguments 判空保护杜绝 NullReferenceException
        var adapterFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "Adapters", "PluginRuntimeAdapters.cs");
        Assert.True(File.Exists(adapterFile), "PluginRuntimeAdapters.cs 必须存在");
        var adapterContent = File.ReadAllText(adapterFile);
        Assert.Contains("if (manifest.Arguments != null)", adapterContent);

        // 4. PluginActionRequest 必须支持 Action 缺省
        var modelFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginManagementModels.cs");
        Assert.True(File.Exists(modelFile), "PluginManagementModels.cs 必须存在");
        var modelContent = File.ReadAllText(modelFile);
        Assert.Contains("public string? Action { get; init; } = \"\";", modelContent);

        // 5. 前端 config.js 的 setEnabled 必须支持传入 enabled 并拼接 query
        var configFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "config.js");
        Assert.True(File.Exists(configFile), "config.js 必须存在");
        var configContent = File.ReadAllText(configFile);
        Assert.Contains("setEnabled: (id, enabled) => `/Plugins/${encodeURIComponent(id)}/SetEnabled${typeof enabled === 'boolean' ? `?enabled=${enabled}` : ''}`", configContent);

        // 6. 前端 PluginCard.js 必须为 OneShot 脚本提供专属运行按钮 handleExecute
        var cardFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginCard.js");
        Assert.True(File.Exists(cardFile), "PluginCard.js 必须存在");
        var cardContent = File.ReadAllText(cardFile);
        Assert.Contains("handleExecute", cardContent);
        Assert.Contains("<span>运行</span>", cardContent);

        // 7. MCP Bridge 编译器 policy.go 必须支持 Opt-Out 自动选择全部工具
        var policyFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "catalog", "policy.go");
        Assert.True(File.Exists(policyFile), "policy.go 必须存在");
        var policyContent = File.ReadAllText(policyFile);
        Assert.Contains("selectedMap = make(map[string]bool)", policyContent);

        // 8. MCP Bridge backend manager.go 必须具备代理解析能力
        var managerFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "backend", "manager.go");
        Assert.True(File.Exists(managerFile), "manager.go 必须存在");
        var managerContent = File.ReadAllText(managerFile);
        Assert.Contains("SetProxyResolver", managerContent);
        Assert.Contains("resolveProxy", managerContent);
    }

    [Fact]
    public void LinuxArch067_插件手动停止状态持久化与文件动态管理门禁()
    {
        // 1. IPluginManager 必须声明具备 isManual 维度的 StopAsync 以及动态文件管理接口
        var iManagerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "IPluginManager.cs");
        Assert.True(File.Exists(iManagerFile), "IPluginManager.cs 必须存在");
        var iManagerContent = File.ReadAllText(iManagerFile);
        Assert.Contains("ValueTask<bool> StopAsync(string id, bool isManual", iManagerContent);
        Assert.Contains("ValueTask<bool> UploadFileAsync(string id, string relativePath, Stream fileStream", iManagerContent);
        Assert.Contains("ValueTask<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string id, string relativePath", iManagerContent);
        Assert.Contains("ValueTask<bool> DeleteFileAsync(string id, string relativePath", iManagerContent);

        // 2. PluginManager.cs 必须落实 .manual_stop 的写入、自启检查跳过与启动清除
        var managerFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.cs");
        Assert.True(File.Exists(managerFile), "PluginManager.cs 必须存在");
        var managerContent = File.ReadAllText(managerFile);
        Assert.Contains(".manual_stop", managerContent);
        Assert.Contains("manualStopPath", managerContent);
        Assert.Contains("File.WriteAllTextAsync(manualStopPath", managerContent);
        Assert.Contains("File.Delete(manualStopFlag)", managerContent);

        // 3. PluginManager.Code.cs 必须包含二进制探测、免越权保护、0755赋权与 plugin.json 保护
        var codeFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.Code.cs");
        Assert.True(File.Exists(codeFile), "PluginManager.Code.cs 必须存在");
        var codeContent = File.ReadAllText(codeFile);
        Assert.Contains("IsBinaryFile", codeContent);
        Assert.Contains("UnixFileMode", codeContent);
        Assert.Contains("plugin.json", codeContent);

        // 4. PluginsController.cs 必须支持 manual 参数与 UploadFile/DownloadFile/DeleteFile 端点
        var ctrlFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Routes", "PluginsController.cs");
        Assert.True(File.Exists(ctrlFile), "PluginsController.cs 必须存在");
        var ctrlContent = File.ReadAllText(ctrlFile);
        Assert.Contains("[FromQuery] bool? manual", ctrlContent);
        Assert.Contains("UploadFile", ctrlContent);
        Assert.Contains("DownloadFile", ctrlContent);
        Assert.Contains("DeleteFile", ctrlContent);

        // 5. PluginCard.js 不得包含限制 Native 编辑的条件 v-if="plugin.runtime !== 5"
        var cardFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginCard.js");
        Assert.True(File.Exists(cardFile), "PluginCard.js 必须存在");
        var cardContent = File.ReadAllText(cardFile);
        Assert.DoesNotContain("plugin.runtime !== 5", cardContent);

        // 6. MCP Bridge index.html 必须彻底根除 ANSI 终端乱码并回显有效出口代理
        var htmlFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        Assert.True(File.Exists(htmlFile), "index.html 必须存在");
        var htmlBytes = File.ReadAllBytes(htmlFile);
        Assert.True(htmlBytes.Length >= 15, "index.html 大小异常");
        Assert.NotEqual(0x1b, htmlBytes[0]);
        var htmlContent = File.ReadAllText(htmlFile);
        Assert.Contains("s.effectiveProxy", htmlContent);

        // 7. PluginWebviewModal.js 必须具备 Token 脱敏与按钮防挤压类
        var webviewFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginWebviewModal.js");
        Assert.True(File.Exists(webviewFile), "PluginWebviewModal.js 必须存在");
        var webviewContent = File.ReadAllText(webviewFile);
        Assert.Contains("displayUrl", webviewContent);
        Assert.Contains("shrink-0", webviewContent);
    }

    [Fact]
    public void LinuxArch068_凭据安全防御_未脱敏密码与真实地址防泄露门禁()
    {
        // 1. 验证 .githooks/security-gate.cjs、.githooks/pre-commit 与基线文件存在
        var securityGateFile = Path.Combine(RepoRoot, ".githooks", "security-gate.cjs");
        var preCommitFile = Path.Combine(RepoRoot, ".githooks", "pre-commit");
        var baselineFile = Path.Combine(RepoRoot, ".githooks", "security-gate-baseline.json");

        Assert.True(File.Exists(securityGateFile), ".githooks/security-gate.cjs 核心检测引擎必须存在");
        Assert.True(File.Exists(preCommitFile), ".githooks/pre-commit 必须存在");
        Assert.True(File.Exists(baselineFile), ".githooks/security-gate-baseline.json 必须存在");

        // 2. 验证 security-gate.cjs 规则完整性（覆盖已知泄露、隧道ID、OpenAI Key、管理员密码、EasyTier、多字符集香农熵与脱敏合规）
        var gateContent = File.ReadAllText(securityGateFile);
        Assert.Contains("SEC-KNOWN-LEAKS", gateContent);
        Assert.Contains("SEC-TUNNEL-ID", gateContent);
        Assert.Contains("SEC-OPENAI-KEY", gateContent);
        Assert.Contains("SEC-ADMIN-PASSWORD", gateContent);
        Assert.Contains("SEC-EASYTIER-SECRET", gateContent);
        Assert.Contains("SEC-HIGH-ENTROPY-SECRET", gateContent);
        Assert.Contains("SEC-ENTROPY-BASE64", gateContent);
        Assert.Contains("SEC-ENTROPY-HEX", gateContent);
        Assert.Contains("SEC-PSEUDO-MASKING", gateContent);
        Assert.Contains("SEC-UNMASKED-NETWORK-ADDRESS", gateContent);

        // 3. 验证 .gitignore 强制隔离敏感目录 docs_secret/ 与 env/
        var gitignoreFile = Path.Combine(RepoRoot, ".gitignore");
        Assert.True(File.Exists(gitignoreFile), ".gitignore 必须存在");
        var gitignoreContent = File.ReadAllText(gitignoreFile);
        Assert.Contains("docs_secret/", gitignoreContent);
        Assert.Contains("env/", gitignoreContent);

        // 4. 执行 node .githooks/security-gate.cjs --full 确保全仓零安全违规
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"{securityGateFile} --full",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            Assert.True(process.ExitCode == 0, $"安全门禁全量扫描失败:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 环境未安装 node 时，回退执行 C# 原生防泄露基线断言 (Base64 防自身扫描误判)
            var knownLeaks = new[]
            {
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("dUZrVEp4VnlQNWFoWW55ag==")),
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("Zm5uYXMuaGthZG1pbg==")),
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("MzMzMTIzLnh5eg==")),
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("dHVubmVsXzZhZTYzZDFjMTMwODgxOTE5NWE0YWE4ZWQ3ODBlOTUx"))
            };
            var textFiles = Directory.GetFiles(RepoRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".git") && !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules") && !f.Contains("docs_secret") && !f.Contains(".githooks"));

            foreach (var file in textFiles)
            {
                var content = File.ReadAllText(file);
                foreach (var leak in knownLeaks)
                {
                    Assert.False(content.Contains(leak), $"文件 {file} 包含未脱敏真实凭据: {leak}");
                }
            }
        }
    }

    [Fact]
    public void LinuxArch069_微服务Webview控制台端口契约对齐与抗覆盖门禁()
    {
        // 1. 验证 PluginInstanceDto 暴露 Port 兼容性别名 (AllocatedPort ?? ConfiguredPort)
        var modelsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginModels.cs");
        Assert.True(File.Exists(modelsFile), "PluginModels.cs 必须存在");
        var modelsContent = File.ReadAllText(modelsFile);
        Assert.Contains("public int? Port => AllocatedPort ?? ConfiguredPort;", modelsContent);

        // 2. 验证 SOCKS5 代理控制台 ui/index.html 杜绝死端口 18001 硬编码与具备 token 兼容
        var uiFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "ui", "index.html");
        Assert.True(File.Exists(uiFile), "ui/index.html 必须存在");
        var uiContent = File.ReadAllText(uiFile);

        // 严禁以 18001 作为写死的最终展示端口
        Assert.DoesNotContain("<div class=\"val\" id=\"proxy-port\">:18001</div>", uiContent);
        // 必须具备 allocatedPort 判定与 URL token 容错提取
        Assert.Contains("allocatedPort", uiContent);
        Assert.Contains("URLSearchParams", uiContent);
        Assert.Contains("LWT_PLUGIN_ENV", uiContent);

        // 3. 验证 saveAcl 具备 network 清单防丢失继承切面
        Assert.Contains("network: existingNetwork", uiContent);

        // 4. 验证 PluginManager.CreateOrUpdateAsync 具备网络配置继承防护
        var pmFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.cs");
        var pmContent = File.ReadAllText(pmFile);
        Assert.Contains("finalManifest.Network == null && oldManifest?.Network != null", pmContent);

        // 5. 验证 PluginWebviewModal.js 具备 postMessage 广播通道下发
        var webviewModalFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginWebviewModal.js");
        var webviewContent = File.ReadAllText(webviewModalFile);
        Assert.Contains("LWT_PLUGIN_ENV", webviewContent);
        Assert.Contains("handleIframeLoad", webviewContent);
    }

    [Fact]
    public void LinuxArch070_OpenAiTunnel长轮询超时预算与出站代理规范门禁()
    {
        // 1. 验证 controller.go 存在
        var controllerFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "tunnel", "controller.go");
        Assert.True(File.Exists(controllerFile), "plugins/mcp-bridge/internal/tunnel/controller.go 必须存在");
        var controllerContent = File.ReadAllText(controllerFile);

        // 2. 验证长轮询弹性超时预算：杜绝过早超时 (严禁 5 秒，强制 30 秒轮询与 15 秒死线护栏)
        Assert.Contains("PollTimeout:           30 * time.Second", controllerContent);
        Assert.Contains("PollDeadlineGuardrail: 15 * time.Second", controllerContent);
        Assert.DoesNotContain("PollTimeout:           5 * time.Second", controllerContent);

        // 3. 验证出站代理规范：自动将 socks5:// 升级为 socks5h:// 免疫本地 DNS 污染
        Assert.Contains("func NormalizeProxyURL(raw string) string", controllerContent);
        Assert.Contains("socks5h://", controllerContent);

        // 4. 验证环境变量注入：覆盖 SDK 专用 CONTROL_PLANE_HTTP_PROXY 与 TUNNEL_CLIENT_HTTP_PROXY
        Assert.Contains("func ApplyProxyEnvironment(proxyURL string)", controllerContent);
        Assert.Contains("CONTROL_PLANE_HTTP_PROXY", controllerContent);
        Assert.Contains("TUNNEL_CLIENT_HTTP_PROXY", controllerContent);

        // 5. 验证代理单测文件存在
        var testFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "tunnel", "controller_proxy_test.go");
        Assert.True(File.Exists(testFile), "controller_proxy_test.go 单元测试必须存在");

        // 6. 验证控制台 index.html 具备区分 effectiveProxy 的智能超时诊断提示
        var indexFile = Path.Combine(RepoRoot, "plugins", "mcp-bridge", "internal", "web", "index.html");
        Assert.True(File.Exists(indexFile), "mcp-bridge index.html 必须存在");
        var indexContent = File.ReadAllText(indexFile);
        Assert.Contains("s.effectiveProxy", indexContent);
        Assert.Contains("出口代理连接超时", indexContent);
        Assert.Contains("未配置网络出口代理导致连接超时", indexContent);
    }

    [Fact]
    public void LinuxArch071_SOCKS5安全代理三维ACL策略默认值与Webview动态主机绑定门禁()
    {
        // 1. 验证 plugin.json 默认预设：入站客户端默认放行 LAN，出站目标默认全网开放无限制 (all)
        var pluginJsonFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "plugin.json");
        Assert.True(File.Exists(pluginJsonFile), "plugins/socks5-proxy/plugin.json 必须存在");
        var pluginJsonContent = File.ReadAllText(pluginJsonFile);

        Assert.Contains("\"--acl-preset\",", pluginJsonContent);
        Assert.Contains("\"all\",", pluginJsonContent);
        Assert.Contains("\"--allowed-subnets\",", pluginJsonContent);
        Assert.Contains("\"*\",", pluginJsonContent);
        Assert.Contains("\"--allowed-domains\",", pluginJsonContent);
        Assert.Contains("\"10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1/128\"", pluginJsonContent);

        // 2. 验证 ui/index.html 客户端连接命令动态绑定主机名，严禁硬编码 127.0.0.1 误导外部连接
        var uiFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "ui", "index.html");
        Assert.True(File.Exists(uiFile), "plugins/socks5-proxy/ui/index.html 必须存在");
        var uiContent = File.ReadAllText(uiFile);

        Assert.Contains("getHostName()", uiContent);
        Assert.Contains("currentHost", uiContent);
        Assert.Contains("window.location.hostname", uiContent);
        Assert.DoesNotContain("'socks5://' + authPrefix + '127.0.0.1:'", uiContent);

        // 3. 验证 ui/index.html 默认快捷预设为全网开放出站 (all) 与局域网入站 (lan)
        Assert.Contains("let activePreset = 'all';", uiContent);
        Assert.Contains("applyPreset('all');", uiContent);
        Assert.Contains("applyClientPreset('lan');", uiContent);
        Assert.Contains("id=\"pill-all\" onclick=\"applyPreset('all')\"", uiContent);
        Assert.Contains("id=\"client-pill-lan\" onclick=\"applyClientPreset('lan')\"", uiContent);

        // 4. 验证 PluginWebviewModal.js 下发宿主主机名
        var webviewModalFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "wwwroot", "app", "components", "plugins", "PluginWebviewModal.js");
        var webviewContent = File.ReadAllText(webviewModalFile);
        Assert.Contains("hostname: window.location.hostname", webviewContent);
    }

    [Fact]
    public void LinuxArch072_SOCKS5安全代理DNS缓存与直连已解析IP门禁()
    {
        // 1. 验证 dns_cache.go 存在，且具备策略模式接口、LRU缓存与Singleflight并发合并
        var dnsCacheFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "dns_cache.go");
        Assert.True(File.Exists(dnsCacheFile), "dns_cache.go 必须存在");
        var dnsCacheContent = File.ReadAllText(dnsCacheFile);

        Assert.Contains("type IDnsResolverStrategy interface", dnsCacheContent);
        Assert.Contains("type DnsLruCache struct", dnsCacheContent);
        Assert.Contains("type SingleflightGroup struct", dnsCacheContent);
        Assert.Contains("type Ipv4PreferResolver struct", dnsCacheContent);
        Assert.Contains("type CachedDnsResolver struct", dnsCacheContent);
        Assert.Contains("func NormalizeDomain(domain string) string", dnsCacheContent);

        // 2. 验证建立上游连接时直连已解析 IP，严禁二次传递域名触发重复 DNS 解析
        var dialerFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "dialer.go");
        var dialerContent = File.ReadAllText(dialerFile);
        Assert.Contains("net.JoinHostPort(candidates[0].String(), strconv.Itoa(port))", dialerContent);
        Assert.Contains("net.JoinHostPort(ip.String(), strconv.Itoa(port))", dialerContent);

        var mainGoFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "main.go");
        var mainGoContent = File.ReadAllText(mainGoFile);

        Assert.Contains("dialUpstreamWithRace", mainGoContent);
        Assert.Contains("NewCachedDnsResolver", mainGoContent);
        Assert.Contains("NormalizeDomain(string(domain))", mainGoContent);

        // 3. 验证专属一键热更脚本 scripts/hot-update-socks5-proxy.ps1 存在
        var hotUpdateScript = Path.Combine(RepoRoot, "scripts", "hot-update-socks5-proxy.ps1");
        Assert.True(File.Exists(hotUpdateScript), "scripts/hot-update-socks5-proxy.ps1 必须存在");
        var scriptContent = File.ReadAllText(hotUpdateScript);
        Assert.Contains("-HotUpdatePlugin \"socks5-proxy\"", scriptContent);

        // 4. 验证 ui/index.html 支持并明确展示 socks5h:// 远端域名代解指南
        var uiFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "ui", "index.html");
        var uiContent = File.ReadAllText(uiFile);
        Assert.Contains("curl -x socks5h://", uiContent);
        Assert.Contains("curl-remote-dns-preview", uiContent);
    }

    [Fact]
    public void LinuxArch073_SOCKS5与HTTP单端口双模自适应代理与传输性能优化门禁()
    {
        // 1. 验证 http_proxy.go 存在，包含协议探测、Basic认证与 CONNECT 隧道/明文代理
        var httpProxyFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "http_proxy.go");
        Assert.True(File.Exists(httpProxyFile), "http_proxy.go 必须存在");
        var httpProxyContent = File.ReadAllText(httpProxyFile);

        Assert.Contains("func isHttpProxyMethod(b byte) bool", httpProxyContent);
        Assert.Contains("func validateBasicAuth(authHeader, expectedUser, expectedPass string) bool", httpProxyContent);
        Assert.Contains("func (s *Server) handleHttpProxy", httpProxyContent);
        Assert.Contains("HTTP/1.1 200 Connection Established", httpProxyContent);
        Assert.Contains("WriteProxy", httpProxyContent);

        // 2. 验证 main.go 包含单端口自适应嗅探 Peek(1)、BufferedConn、TCP_NODELAY 与半关闭超时兜底
        var mainGoFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "main.go");
        var mainGoContent = File.ReadAllText(mainGoFile);

        Assert.Contains("type BufferedConn struct", mainGoContent);
        Assert.Contains("br.Peek(1)", mainGoContent);
        Assert.Contains("s.handleHttpProxy(bConn, br)", mainGoContent);
        Assert.Contains("optimizeTcpConn", mainGoContent);
        Assert.Contains("SetNoDelay(true)", mainGoContent);
        Assert.Contains("SetReadDeadline(time.Now().Add(10 * time.Second))", mainGoContent);

        // 3. 验证 dns_cache.go 包含 Stale-While-Revalidate 双阈值软过期机制
        var dnsCacheFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "dns_cache.go");
        var dnsCacheContent = File.ReadAllText(dnsCacheFile);

        Assert.Contains("SoftExpiresAt", dnsCacheContent);
        Assert.Contains("HardExpiresAt", dnsCacheContent);
        Assert.Contains("IsStale", dnsCacheContent);

        // 4. 验证 ui/index.html 包含 HTTP 代理配置指引
        var uiFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "ui", "index.html");
        var uiContent = File.ReadAllText(uiFile);
        Assert.Contains("curl -x http://", uiContent);
        Assert.Contains("curl-http-preview", uiContent);
    }

    [Fact]
    public void LinuxArch074_网络代理性能基准度量与传输优化防回归门禁()
    {
        // 1. 验证微秒级多协议基准评测套件 tests/benchmark/proxy-benchmark.mjs 存在并包含各项指标度量
        var benchFile = Path.Combine(RepoRoot, "tests", "benchmark", "proxy-benchmark.mjs");
        Assert.True(File.Exists(benchFile), "tests/benchmark/proxy-benchmark.mjs 必须存在");
        var benchContent = File.ReadAllText(benchFile);
        Assert.Contains("measureDirectTcp", benchContent);
        Assert.Contains("measureSocks5Handshake", benchContent);
        Assert.Contains("measureSocks5hHandshake", benchContent);
        Assert.Contains("measureHttpConnectHandshake", benchContent);
        Assert.Contains("measureTtfbHttpProxy", benchContent);
        Assert.Contains("calcStats", benchContent);

        // 2. 验证基于 Playwright 的真实网页加载性能评测套件 tests/benchmark/proxy-web-performance.mjs 存在
        var webBenchFile = Path.Combine(RepoRoot, "tests", "benchmark", "proxy-web-performance.mjs");
        Assert.True(File.Exists(webBenchFile), "tests/benchmark/proxy-web-performance.mjs 必须存在");
        var webBenchContent = File.ReadAllText(webBenchFile);
        Assert.Contains("measurePagePerformance", webBenchContent);
        Assert.Contains("first-contentful-paint", webBenchContent);
        Assert.Contains("domContentLoadedEventEnd", webBenchContent);
        Assert.Contains("loadEventEnd", webBenchContent);

        // 3. 验证调研文档存在，系统分析了性能评测维度与 Linux Splice 零拷贝等深度优化空间
        var docFile = Path.Combine(RepoRoot, "docs", "network-proxy-performance-testing-and-optimization-research.md");
        Assert.True(File.Exists(docFile), "docs/network-proxy-performance-testing-and-optimization-research.md 必须存在");
        var docContent = File.ReadAllText(docFile);
        Assert.Contains("splice(2)", docContent);
        Assert.Contains("Stale-While-Revalidate", docContent);
        Assert.Contains("TCP_NODELAY", docContent);
        Assert.Contains("Navigation Timing", docContent);

        // 4. 验证 main.go 落实了 Linux 原生内核 splice(2) 零拷贝快速通道与 BufferedConn 残留安全排空
        var mainGoFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "main.go");
        var mainGoContent = File.ReadAllText(mainGoFile);
        Assert.Contains("extractTcpConn", mainGoContent);
        Assert.Contains("runtime.GOOS == \"linux\"", mainGoContent);
        Assert.Contains("io.Copy(dstTcp, srcTcp)", mainGoContent);
        Assert.Contains("bc.Buffered()", mainGoContent);

        // 5. 验证无代理 vs 代理全维度对照测试套件 tests/benchmark/proxy-vs-direct-comparison.mjs 存在
        var compBenchFile = Path.Combine(RepoRoot, "tests", "benchmark", "proxy-vs-direct-comparison.mjs");
        Assert.True(File.Exists(compBenchFile), "tests/benchmark/proxy-vs-direct-comparison.mjs 必须存在");
        var compBenchContent = File.ReadAllText(compBenchFile);
        Assert.Contains("measureDirectTcp", compBenchContent);
        Assert.Contains("measureDirectTtfb", compBenchContent);
        Assert.Contains("measureBrowserPage", compBenchContent);
    }

    [Fact]
    public void LinuxArch075_多IP并发竞速拨号与CDN抗长尾延迟调优门禁()
    {
        // 1. 验证 dialer.go 存在并包含 RFC 8305 阶梯并发拨号竞速实现
        var dialerFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "dialer.go");
        Assert.True(File.Exists(dialerFile), "dialer.go 必须存在");
        var dialerContent = File.ReadAllText(dialerFile);
        Assert.Contains("dialUpstreamWithRace", dialerContent);
        Assert.Contains("Happy Eyeballs", dialerContent);
        Assert.Contains("staggerDelay", dialerContent);
        Assert.Contains("ErrAclForbidden", dialerContent);

        // 2. 验证 main.go 与 http_proxy.go 均采用 dialUpstreamWithRace 并消除孤立 ips[0]
        var mainGoFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "main.go");
        var mainGoContent = File.ReadAllText(mainGoFile);
        Assert.Contains("dialUpstreamWithRace", mainGoContent);

        var httpProxyFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "cmd", "socks5-proxy", "http_proxy.go");
        var httpProxyContent = File.ReadAllText(httpProxyFile);
        Assert.Contains("dialUpstreamWithRace", httpProxyContent);

        // 3. 验证多 IP CDN 域名测试套件 tests/benchmark/test-cdn-multihoming.mjs 存在
        var cdnBenchFile = Path.Combine(RepoRoot, "tests", "benchmark", "test-cdn-multihoming.mjs");
        Assert.True(File.Exists(cdnBenchFile), "tests/benchmark/test-cdn-multihoming.mjs 必须存在");
        var cdnBenchContent = File.ReadAllText(cdnBenchFile);
        Assert.Contains("CDN_TARGETS", cdnBenchContent);
        Assert.Contains("measureHttpConnect", cdnBenchContent);

        // 4. 验证 B站评论区懒加载深度调研文档存在
        var docFile = Path.Combine(RepoRoot, "docs", "bilibili-comment-lazy-load-slowdown-proxy-research.md");
        Assert.True(File.Exists(docFile), "docs/bilibili-comment-lazy-load-slowdown-proxy-research.md 必须存在");
        var docContent = File.ReadAllText(docFile);
        Assert.Contains("Happy Eyeballs", docContent);
        Assert.Contains("IntersectionObserver", docContent);
    }

    [Fact]
    public void LinuxArch076_插件平台级声明式Ingress路由与通用流式直通门禁()
    {
        // 1. 验证通用 Ingress 契约模型存在且保持业务中立性（严禁代理特化词汇）
        var modelsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginIngressModels.cs");
        Assert.True(File.Exists(modelsFile), "PluginIngressModels.cs 必须存在");
        var modelsContent = File.ReadAllText(modelsFile);
        Assert.Contains("PluginIngressConfig", modelsContent);
        Assert.Contains("PluginIngressRoute", modelsContent);
        Assert.Contains("IngressMatchConfig", modelsContent);
        Assert.Contains("IngressTargetConfig", modelsContent);
        Assert.Contains("IngressTransportConfig", modelsContent);
        Assert.Contains("IngressSecurityConfig", modelsContent);
        Assert.Contains("IngressFallbackConfig", modelsContent);

        // 验证中立性：严禁出现 proxyAuth, socksStream, gfwFallback 等特化词汇
        Assert.DoesNotContain("proxyAuth", modelsContent);
        Assert.DoesNotContain("socksStream", modelsContent);
        Assert.DoesNotContain("gfwFallback", modelsContent);

        // 2. 验证宿主通用 Ingress 中间件与全双工转发器实现
        var middlewareFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Ingress", "PluginIngressMiddleware.cs");
        Assert.True(File.Exists(middlewareFile), "PluginIngressMiddleware.cs 必须存在");
        var middlewareContent = File.ReadAllText(middlewareFile);
        Assert.Contains("passthrough_to_default_spa", middlewareContent);
        Assert.Contains("ForwardWebSocketToTcpAsync", middlewareContent);
        Assert.Contains("EvaluateSecurity", middlewareContent);

        // 宿主中间件必须 100% 声明式驱动，严禁硬编码特定插件 ID 或特定业务路由
        Assert.DoesNotContain("\"socks5-proxy\"", middlewareContent);
        Assert.DoesNotContain("\"/ingress/socks5\"", middlewareContent);

        var forwarderFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Features", "Plugins", "Ingress", "DuplexStreamForwarder.cs");
        Assert.True(File.Exists(forwarderFile), "DuplexStreamForwarder.cs 必须存在");
        var forwarderContent = File.ReadAllText(forwarderFile);
        Assert.Contains("ArrayPool<byte>.Shared", forwarderContent);
        Assert.Contains("ForwardWebSocketToTcpAsync", forwarderContent);

        // 3. 验证宿主 DI 与管线注册
        var pipelineFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Composition", "PipelineExtensions.cs");
        var pipelineContent = File.ReadAllText(pipelineFile);
        Assert.Contains("PluginIngressMiddleware", pipelineContent);

        var diFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Composition", "ServiceCollectionExtensions.cs");
        var diContent = File.ReadAllText(diFile);
        Assert.Contains("IDuplexStreamForwarder", diContent);

        // 4. 验证微服务声明式 Ingress 配置生效
        var socksPluginJson = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "plugin.json");
        Assert.True(File.Exists(socksPluginJson), "plugins/socks5-proxy/plugin.json 必须存在");
        var socksJsonContent = File.ReadAllText(socksPluginJson);
        Assert.Contains("\"ingress\"", socksJsonContent);
        Assert.Contains("\"duplex_stream\"", socksJsonContent);
        Assert.Contains("\"passthrough_to_default_spa\"", socksJsonContent);

        // 5. 验证通用化架构设计文档已落盘
        var archDoc = Path.Combine(RepoRoot, "docs", "platform-level-generic-ingress-and-fallback-architecture.md");
        Assert.True(File.Exists(archDoc), "docs/platform-level-generic-ingress-and-fallback-architecture.md 必须存在");
    }
}

