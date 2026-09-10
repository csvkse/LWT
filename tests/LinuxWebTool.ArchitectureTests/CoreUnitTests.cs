using LinuxWebTool.Infrastructure.Security;
using LinuxWebTool.Infrastructure.Support;
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
            Assert.Equal("C:/absolute.log", paths.Resolve("C:/absolute.log"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
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
