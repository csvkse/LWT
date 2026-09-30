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

    [Fact]
    public async Task Pty_engine_starts_and_reads_session()
    {
        var engine = new LinuxWebTool.Infrastructure.Terminal.CrossPlatformPtyEngine();
        var session = await engine.StartSessionAsync(new LinuxWebTool.Contracts.Terminal.PtyStartOptions());
        Assert.NotNull(session);
        Assert.False(session.HasExited);

        var cmdBytes = System.Text.Encoding.UTF8.GetBytes("\r\n");
        await session.StandardInput.WriteAsync(cmdBytes);
        await session.StandardInput.FlushAsync();

        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var initialRead = await session.StandardOutput.ReadAsync(buffer, cts.Token);
        Assert.True(initialRead > 0);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Jwt_validation_roundtrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "linuxwebtool-jwt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = new ConfigurationBuilder().Build();
            var dataPaths = new DataPaths(config, new TestHostEnvironment(tempDir));
            var issuer = new JwtIssuer(config, dataPaths);
            var (token, _) = issuer.Issue("admin");

            var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, issuer.BuildValidationParameters());
            Assert.True(result.IsValid, $"Validation failed: {result.Exception?.Message}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
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
