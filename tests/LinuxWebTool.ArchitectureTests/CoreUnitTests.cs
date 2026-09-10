using LinuxWebTool.Infrastructure.Security;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class CoreUnitTests
{
    [Fact]
    public void Password_hash_round_trip_and_wrong_password_fail()
    {
        var hash = PasswordHasher.Hash("unit-test-password");

        Assert.NotEqual("unit-test-password", hash);
        Assert.True(PasswordHasher.Verify("unit-test-password", hash));
        Assert.False(PasswordHasher.Verify("wrong-password", hash));
        Assert.False(PasswordHasher.Verify("unit-test-password", string.Empty));
    }

    [Fact]
    public void Data_paths_resolve_relative_paths_under_configured_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "linuxwebtool-unit", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Directory"] = root })
                .Build();
            var paths = new DataPaths(configuration, new TestHostEnvironment(root));

            Assert.Equal(Path.GetFullPath(root), paths.Root);
            Assert.Equal(Path.Combine(paths.Root, "linuxweb.db"), paths.PathFor("linuxweb.db"));
            Assert.Equal(Path.Combine(paths.Root, "logs", "app.log"), paths.Resolve("logs/app.log"));
            var absolutePath = Path.Combine(Path.GetTempPath(), "absolute.log");
            Assert.True(Path.IsPathRooted(absolutePath));
            Assert.Equal(absolutePath, paths.Resolve(absolutePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Log_retention_deletes_only_expired_matching_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "linuxwebtool-retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldApp = Path.Combine(root, "app-20200101.txt");
            var recentApp = Path.Combine(root, "app-recent.txt");
            var unrelated = Path.Combine(root, "keep.txt");
            File.WriteAllText(oldApp, "old");
            File.WriteAllText(recentApp, "recent");
            File.WriteAllText(unrelated, "keep");
            File.SetLastWriteTime(oldApp, DateTime.Now.AddDays(-31));

            var deleted = LogRetentionService.DeleteExpiredFiles(root, DateTime.Now.AddDays(-30), "app-", "debug-");

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(oldApp));
            Assert.True(File.Exists(recentApp));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "LinuxWebTool.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
