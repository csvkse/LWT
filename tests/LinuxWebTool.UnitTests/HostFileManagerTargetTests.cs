using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Features.Files.Adapters;
using LinuxWebTool.WebHost.Features.Files.Factories;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class HostFileManagerTargetTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/etc", true)]
    [InlineData("/boot", true)]
    [InlineData("/dev", true)]
    [InlineData("/proc", true)]
    [InlineData("/sys", true)]
    [InlineData("/bin", true)]
    [InlineData("/sbin", true)]
    [InlineData("/lib", true)]
    [InlineData("/lib64", true)]
    [InlineData("/usr", true)]
    [InlineData("/var", true)]
    [InlineData("/root", true)]
    [InlineData("/proc/cpuinfo", true)]
    [InlineData("/sys/class/net", true)]
    [InlineData("/dev/null", true)]
    public void HostCriticalPathGuard_ShouldBlock_DestructiveOperationsOnSystemRoots(string path, bool isDestructive)
    {
        var isProtected = HostCriticalPathGuard.IsProtected(path, isDestructive, out var reason);

        Assert.True(isProtected);
        Assert.NotNull(reason);
        Assert.True(reason.Contains("受") || reason.Contains("严禁"));
    }

    [Theory]
    [InlineData("/tmp/demo.txt")]
    [InlineData("/home/user/app.log")]
    [InlineData("/opt/mytools/config.json")]
    [InlineData("/data/backups/db.tar.gz")]
    public void HostCriticalPathGuard_ShouldAllow_DestructiveOperationsOnUserData(string path)
    {
        var isProtected = HostCriticalPathGuard.IsProtected(path, isDestructiveOperation: true, out var reason);

        Assert.False(isProtected);
        Assert.Null(reason);
    }

    [Fact]
    public void HostCriticalPathGuard_ShouldAllow_NonDestructiveReadOnSystemRoots()
    {
        var isProtected = HostCriticalPathGuard.IsProtected("/etc", isDestructiveOperation: false, out var reason);

        Assert.False(isProtected);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("/", "/proc/1/root")]
    [InlineData("/etc", "/proc/1/root/etc")]
    [InlineData("/var/log/syslog", "/proc/1/root/var/log/syslog")]
    [InlineData("/tmp", "/proc/1/root/tmp")]
    public void HostPathResolver_ShouldMap_VirtualPathToProcRoot(string virtualPath, string expectedPrefix)
    {
        var physical = HostPathResolver.ResolvePhysicalPath(virtualPath);
        Assert.Equal(expectedPrefix, physical);
    }

    [Theory]
    [InlineData("/proc", true)]
    [InlineData("/proc/1/status", true)]
    [InlineData("/sys", true)]
    [InlineData("/sys/kernel", true)]
    [InlineData("/dev", true)]
    [InlineData("/dev/urandom", true)]
    [InlineData("/etc", false)]
    [InlineData("/var", false)]
    [InlineData("/home", false)]
    public void HostPathResolver_ShouldIdentify_PseudoAndSpecialFileSystems(string path, bool expected)
    {
        var result = HostPathResolver.IsPseudoOrSpecialFileSystem(path);
        Assert.Equal(expected, result);
    }
}
