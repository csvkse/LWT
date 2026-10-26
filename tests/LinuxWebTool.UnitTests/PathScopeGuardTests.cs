using LinuxWebTool.Infrastructure.Shared.Support;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class PathScopeGuardTests
{
    [Fact]
    public void Unrestricted_When_Config_Is_Null_Or_Empty()
    {
        Assert.True(PathScopeGuard.IsAllowed("/etc/passwd", null, out var err1));
        Assert.Null(err1);

        Assert.True(PathScopeGuard.IsAllowed("/var/log", "", out var err2));
        Assert.Null(err2);

        Assert.True(PathScopeGuard.IsAllowed("/anything", "   ", out var err3));
        Assert.Null(err3);
    }

    [Fact]
    public void Scoped_Single_Root_Allows_Self_And_Children()
    {
        var config = OperatingSystem.IsWindows() ? "C:/workspace" : "/workspace";
        var selfPath = OperatingSystem.IsWindows() ? "C:/workspace" : "/workspace";
        var childPath = OperatingSystem.IsWindows() ? "C:/workspace/sub/test.txt" : "/workspace/sub/test.txt";
        var deepPath = OperatingSystem.IsWindows() ? "C:/workspace/a/b/c/d.json" : "/workspace/a/b/c/d.json";

        Assert.True(PathScopeGuard.IsAllowed(selfPath, config, out var err1), err1);
        Assert.True(PathScopeGuard.IsAllowed(childPath, config, out var err2), err2);
        Assert.True(PathScopeGuard.IsAllowed(deepPath, config, out var err3), err3);
    }

    [Fact]
    public void Scoped_Rejects_Traversal_And_External_Paths()
    {
        var config = OperatingSystem.IsWindows() ? "C:/workspace" : "/workspace";
        var traversal = OperatingSystem.IsWindows() ? "C:/workspace/../secret.txt" : "/workspace/../secret.txt";
        var external = OperatingSystem.IsWindows() ? "C:/Windows/System32" : "/etc/shadow";

        Assert.False(PathScopeGuard.IsAllowed(traversal, config, out var err1));
        Assert.NotNull(err1);

        Assert.False(PathScopeGuard.IsAllowed(external, config, out var err2));
        Assert.NotNull(err2);
    }

    [Fact]
    public void Scoped_Rejects_Prefix_Confusion_Boundary()
    {
        // 关键安全项：/workspace2 不能被错误判定为 /workspace 的子目录
        var config = OperatingSystem.IsWindows() ? "C:/workspace" : "/workspace";
        var confused1 = OperatingSystem.IsWindows() ? "C:/workspace2" : "/workspace2";
        var confused2 = OperatingSystem.IsWindows() ? "C:/workspace_other/file" : "/workspace_other/file";

        Assert.False(PathScopeGuard.IsAllowed(confused1, config, out var err1));
        Assert.NotNull(err1);

        Assert.False(PathScopeGuard.IsAllowed(confused2, config, out var err2));
        Assert.NotNull(err2);
    }

    [Fact]
    public void Scoped_Supports_Multiple_Roots()
    {
        var config = OperatingSystem.IsWindows()
            ? "C:/workspace; D:/data/shared\nC:/opt/tools"
            : "/workspace; /data/shared\n/opt/tools";

        var roots = PathScopeGuard.GetAllowedRoots(config);
        Assert.Equal(3, roots.Count);

        var p1 = OperatingSystem.IsWindows() ? "C:/workspace/file1.txt" : "/workspace/file1.txt";
        var p2 = OperatingSystem.IsWindows() ? "D:/data/shared/sub/file2.txt" : "/data/shared/sub/file2.txt";
        var p3 = OperatingSystem.IsWindows() ? "C:/opt/tools/bin" : "/opt/tools/bin";
        var pOutside = OperatingSystem.IsWindows() ? "C:/other/file.txt" : "/other/file.txt";

        Assert.True(PathScopeGuard.IsAllowed(p1, config, out _));
        Assert.True(PathScopeGuard.IsAllowed(p2, config, out _));
        Assert.True(PathScopeGuard.IsAllowed(p3, config, out _));
        Assert.False(PathScopeGuard.IsAllowed(pOutside, config, out var errOut));
        Assert.NotNull(errOut);
    }
}
