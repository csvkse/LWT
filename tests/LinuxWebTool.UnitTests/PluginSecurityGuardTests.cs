using System.IO.Compression;
using System.Security;
using LinuxWebTool.Contracts.Shared.Support;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class PluginSecurityGuardTests
{
    [Theory]
    [InlineData("valid-plugin-1")]
    [InlineData("my-tool")]
    [InlineData("caddy")]
    [InlineData("ssh_guard_2")]
    public void IsValidPluginId_ValidNames_ReturnsTrue(string id)
    {
        Assert.True(PluginSecurityGuard.IsValidPluginId(id));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo:bar")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("prn")]
    [InlineData("com1")]
    [InlineData("lpt1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidPluginId_InvalidNames_ReturnsFalse(string? id)
    {
        Assert.False(PluginSecurityGuard.IsValidPluginId(id!));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("run-task")]
    [InlineData("metric_1")]
    public void IsValidActionName_ValidNames_ReturnsTrue(string name)
    {
        Assert.True(PluginSecurityGuard.IsValidActionName(name));
    }

    [Theory]
    [InlineData("../status")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("CON")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidActionName_InvalidNames_ReturnsFalse(string? name)
    {
        Assert.False(PluginSecurityGuard.IsValidActionName(name!));
    }

    [Fact]
    public void EnsureSafePluginDirectory_WithinBase_ReturnsCanonicalPath()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "lwt_sec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBase);
        try
        {
            var path = PluginSecurityGuard.EnsureSafePluginDirectory(tempBase, "safe-plugin");
            Assert.StartsWith(Path.GetFullPath(tempBase), path);
            Assert.EndsWith("safe-plugin", path);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Theory]
    [InlineData("../traversal")]
    [InlineData("sub/folder")]
    [InlineData("CON")]
    [InlineData("bad:stream")]
    public void EnsureSafePluginDirectory_MaliciousSlug_ThrowsSecurityException(string badSlug)
    {
        var tempBase = Path.GetTempPath();
        Assert.Throws<SecurityException>(() =>
            PluginSecurityGuard.EnsureSafePluginDirectory(tempBase, badSlug));
    }

    [Fact]
    public void EnsureSafeArtifactPath_WithinBase_ReturnsSafePath()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "lwt_art_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBase);
        try
        {
            var path = PluginSecurityGuard.EnsureSafeArtifactPath(tempBase, "output.txt");
            Assert.StartsWith(Path.GetFullPath(tempBase), path);
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/absolute/path.txt")]
    [InlineData("CON.txt")]
    [InlineData("bad:stream.txt")]
    public void EnsureSafeArtifactPath_MaliciousRelPath_ThrowsSecurityException(string malicious)
    {
        var tempBase = Path.GetTempPath();
        Assert.Throws<SecurityException>(() =>
            PluginSecurityGuard.EnsureSafeArtifactPath(tempBase, malicious));
    }

    [Fact]
    public void ValidateZipArchive_ZipSlipPath_ThrowsSecurityException()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("../../evil.txt");
        }
        ms.Position = 0;
        using var readArchive = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Throws<SecurityException>(() => PluginSecurityGuard.ValidateZipArchive(readArchive));
    }

    [Fact]
    public void ValidateZipArchive_ValidEntries_Passes()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = archive.CreateEntry("manifest.json");
            using var sw = new StreamWriter(e.Open());
            sw.Write("{}");
        }
        ms.Position = 0;
        using var readArchive = new ZipArchive(ms, ZipArchiveMode.Read);
        PluginSecurityGuard.ValidateZipArchive(readArchive); // should not throw
    }
}
