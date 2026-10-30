using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;
using LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class BackupSyncExcludeTests : IDisposable
{
    private readonly string _tempRoot;

    public BackupSyncExcludeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_exclude_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ExcludePatternMatcher_规范化规则()
    {
        var raw = new List<string>
        {
            "  node_modules/  ",
            "/bin/Debug/",
            "*.tmp",
            "",
            "   ",
            "node_modules",
            @"obj\Release\"
        };

        var normalized = ExcludePatternMatcher.NormalizePatterns(raw);

        Assert.Contains("node_modules", normalized);
        Assert.Contains("bin/Debug", normalized);
        Assert.Contains("*.tmp", normalized);
        Assert.Contains("obj/Release", normalized);
        Assert.Equal(4, normalized.Count);
    }

    [Theory]
    [InlineData("node_modules", "node_modules", "node_modules", true)]
    [InlineData("node_modules", "node_modules", "src/client/node_modules", true)]
    [InlineData("bin", "bin", "src/Project/bin", true)]
    [InlineData(".git", ".git", ".git", true)]
    [InlineData("cache*", "cache_v1", "cache_v1", true)]
    [InlineData("*.tmp", "temp.tmp", "temp.tmp", true)]
    [InlineData("bin", "binary_data", "binary_data", false)]
    [InlineData("src/cache", "cache", "other/cache", false)]
    public void ExcludePatternMatcher_目录剪枝判断(string pattern, string dirName, string relativeDir, bool expected)
    {
        var patterns = ExcludePatternMatcher.NormalizePatterns([pattern]);
        var result = ExcludePatternMatcher.IsDirectoryExcluded(dirName, relativeDir, patterns);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("*.log", "app.log", "app.log", true)]
    [InlineData("*.log", "trace.log", "logs/sub/trace.log", true)]
    [InlineData("*.tmp", "temp.tmp", "temp.tmp", true)]
    [InlineData("Thumbs.db", "Thumbs.db", "Thumbs.db", true)]
    [InlineData("Thumbs.db", "Thumbs.db", "images/Thumbs.db", true)]
    [InlineData("logs/*.log", "error.log", "logs/error.log", true)]
    [InlineData("*.log", "app.txt", "app.txt", false)]
    public void ExcludePatternMatcher_文件排除判断(string pattern, string fileName, string relativeFilePath, bool expected)
    {
        var patterns = ExcludePatternMatcher.NormalizePatterns([pattern]);
        var result = ExcludePatternMatcher.IsFileExcluded(fileName, relativeFilePath, patterns);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExcludePatternMatcher_提前剪枝遍历_彻底跳过排除子树()
    {
        // 构造测试目录结构
        var keep1 = Path.Combine(_tempRoot, "main.txt");
        var keep2 = Path.Combine(_tempRoot, "src", "code.cs");
        var excludedNodeModulesFile = Path.Combine(_tempRoot, "node_modules", "lodash", "index.js");
        var excludedGitFile = Path.Combine(_tempRoot, ".git", "objects", "pack", "sample.pack");
        var excludedLogFile = Path.Combine(_tempRoot, "src", "debug.log");

        Directory.CreateDirectory(Path.GetDirectoryName(keep2)!);
        Directory.CreateDirectory(Path.GetDirectoryName(excludedNodeModulesFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(excludedGitFile)!);

        File.WriteAllText(keep1, "keep1");
        File.WriteAllText(keep2, "keep2");
        File.WriteAllText(excludedNodeModulesFile, "huge dependency");
        File.WriteAllText(excludedGitFile, "git object");
        File.WriteAllText(excludedLogFile, "temporary log");

        var patterns = ExcludePatternMatcher.NormalizePatterns(["node_modules", ".git", "*.log"]);

        var files = ExcludePatternMatcher.EnumerateFilesWithPruning(new DirectoryInfo(_tempRoot), _tempRoot, patterns);

        // 仅保留未被排除的文件
        Assert.Equal(2, files.Count);
        var relPaths = files.Select(f => Path.GetRelativePath(_tempRoot, f.FullName).Replace('\\', '/')).ToList();
        Assert.Contains("main.txt", relPaths);
        Assert.Contains("src/code.cs", relPaths);
        Assert.DoesNotContain("node_modules/lodash/index.js", relPaths);
        Assert.DoesNotContain(".git/objects/pack/sample.pack", relPaths);
        Assert.DoesNotContain("src/debug.log", relPaths);
    }

    [Fact]
    public void ExcludeDetectionPipeline_嗅探技术栈与推荐排除项()
    {
        // 模拟复合项目：Node.js + .NET + Python + Git
        var pkgJson = Path.Combine(_tempRoot, "package.json");
        var nodeModulesDir = Path.Combine(_tempRoot, "node_modules", "express");
        var csproj = Path.Combine(_tempRoot, "App.csproj");
        var binDir = Path.Combine(_tempRoot, "bin", "Debug");
        var pyFile = Path.Combine(_tempRoot, "requirements.txt");
        var pycache = Path.Combine(_tempRoot, "__pycache__");
        var gitDir = Path.Combine(_tempRoot, ".git");
        var logFile = Path.Combine(_tempRoot, "run.log");

        Directory.CreateDirectory(nodeModulesDir);
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(pycache);
        Directory.CreateDirectory(gitDir);

        File.WriteAllText(pkgJson, "{}");
        File.WriteAllText(csproj, "<Project />");
        File.WriteAllText(pyFile, "flask");
        File.WriteAllText(logFile, "log trace");

        var strategies = new IExcludeDetectionStrategy[]
        {
            new NodeJsDetectionStrategy(),
            new DotNetDetectionStrategy(),
            new PythonDetectionStrategy(),
            new VcsDetectionStrategy(),
            new CommonTempAndLogsDetectionStrategy()
        };

        var pipeline = new ExcludeDetectionPipeline(strategies, NullLogger<ExcludeDetectionPipeline>.Instance);

        var result = pipeline.ProbePath(_tempRoot);

        Assert.True(result.Success);
        Assert.Contains("Node.js / 前端依赖", result.DetectedTechStacks);
        Assert.Contains(".NET / C# 编译产物", result.DetectedTechStacks);
        Assert.Contains("Python 运行时与依赖", result.DetectedTechStacks);
        Assert.Contains("版本控制系统", result.DetectedTechStacks);
        Assert.Contains("通用临时与缓存", result.DetectedTechStacks);

        var patterns = result.Recommendations.Select(r => r.Pattern).ToList();
        Assert.Contains("node_modules", patterns);
        Assert.Contains("bin", patterns);
        Assert.Contains("obj", patterns);
        Assert.Contains("__pycache__", patterns);
        Assert.Contains(".git", patterns);
        Assert.Contains("*.log", patterns);

        Assert.NotEmpty(result.PresetTemplates);
    }

    [Fact]
    public void ExcludeDetectionPipeline_空路径或不存在路径安全降级()
    {
        var strategies = new IExcludeDetectionStrategy[]
        {
            new NodeJsDetectionStrategy()
        };
        var pipeline = new ExcludeDetectionPipeline(strategies, NullLogger<ExcludeDetectionPipeline>.Instance);

        var emptyRes = pipeline.ProbePath("");
        Assert.False(emptyRes.Success);
        Assert.NotEmpty(emptyRes.PresetTemplates);

        var notFoundRes = pipeline.ProbePath(Path.Combine(_tempRoot, "non_existent_folder_12345"));
        Assert.False(notFoundRes.Success);
        Assert.NotEmpty(notFoundRes.PresetTemplates);
    }
}
