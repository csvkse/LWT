using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class DataPathsSafetyTests
{
    private sealed class DummyHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LinuxWebTool.UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void DataPaths_DefaultToDataFolderUnderContentRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "lwt-unit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().Build();
            var env = new DummyHostEnvironment(tempRoot);
            var paths = new DataPaths(config, env);

            var expectedRoot = Path.GetFullPath(Path.Combine(tempRoot, "data"));
            Assert.Equal(expectedRoot, paths.Root);
            Assert.Equal(Path.Combine(expectedRoot, "linuxweb.db"), paths.PathFor("linuxweb.db"));
            Assert.Equal(Path.Combine(expectedRoot, "sub", "log.txt"), paths.Resolve("sub/log.txt"));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void DataPaths_HonorsExplicitAbsolutePath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "lwt-explicit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Directory"] = tempRoot })
                .Build();
            var env = new DummyHostEnvironment(Path.GetTempPath());
            var paths = new DataPaths(config, env);

            Assert.Equal(Path.GetFullPath(tempRoot), paths.Root);
            Assert.True(Directory.Exists(tempRoot));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
