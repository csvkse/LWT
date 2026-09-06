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
    private static readonly Assembly ContractsAssembly = typeof(LinuxWebTool.Contracts.Models.SaveCommandRequest).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(LinuxWebTool.Infrastructure.Persistence.DbSetup).Assembly;
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
        Assert.DoesNotContain(references, name => name.Contains("WebHost"));
    }

    [Fact]
    public void LinuxArch004_下层禁止反向引用上层()
    {
        var infraRefs = InfrastructureAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(infraRefs, name => name.Contains("WebHost"));
        var contractsRefs = ContractsAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(contractsRefs, name => name.Contains("Infrastructure") || name.Contains("WebHost"));
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
        var routesDir = Path.Combine(RepoRoot, "src", "LinuxWebTool.WebHost", "Routes");
        Assert.True(Directory.Exists(routesDir), "Routes 目录不存在");
        var violations = Directory.EnumerateFiles(routesDir, "*.cs", SearchOption.AllDirectories)
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
}
