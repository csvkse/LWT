using System.IO.Compression;
using System.Security;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class PluginCodeAndZipTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PluginManager _manager;

    public PluginCodeAndZipTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_plugin_code_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _manager = new PluginManager(NullLogger<PluginManager>.Instance, _tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Plugin_ImportZip_ValidArchive_ShouldSucceed()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("plugin.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                var manifest = new PluginManifest
                {
                    Id = "zip-imported-plugin",
                    Name = "Zip 导入插件",
                    Profile = PluginProfile.OneShot,
                    Runtime = PluginRuntime.Python,
                    Entrypoint = "main.py"
                };
                writer.Write(JsonSerializer.Serialize(manifest));
            }

            var scriptEntry = archive.CreateEntry("main.py");
            using (var writer = new StreamWriter(scriptEntry.Open()))
            {
                writer.Write("print('Hello from zip imported plugin')");
            }
        }
        memoryStream.Position = 0;

        var imported = await _manager.ImportZipAsync(memoryStream);
        Assert.NotNull(imported);
        Assert.Equal("zip-imported-plugin", imported.Id);
        Assert.Equal("Zip 导入插件", imported.Name);

        var retrieved = await _manager.GetByIdAsync("zip-imported-plugin");
        Assert.NotNull(retrieved);
        Assert.Equal("zip-imported-plugin", retrieved.Id);
    }

    [Fact]
    public async Task Plugin_Code_GetAndSave_CreatesBackupAndPreservesContent()
    {
        await _manager.GetAllAsync(); // seed plugins
        var pluginId = "demo-py-api";

        // 1. 读取初始代码
        var codeDto = await _manager.GetCodeAsync(pluginId);
        Assert.NotNull(codeDto);
        Assert.NotEmpty(codeDto.Files);
        Assert.Equal("main.py", codeDto.ActiveFile);
        Assert.Contains("Python-API", codeDto.Content);

        // 2. 保存新代码
        var newCode = "# Modified test code\nprint('new version')\n";
        var saveResult = await _manager.SaveCodeAsync(pluginId, new PluginCodeSaveRequest
        {
            File = "main.py",
            Content = newCode
        });
        Assert.True(saveResult);

        // 3. 再次读取验证
        var reloaded = await _manager.GetCodeAsync(pluginId, "main.py");
        Assert.Equal(newCode, reloaded.Content);

        // 4. 验证 .bak 快照文件存在
        var pluginDir = Path.Combine(_tempRoot, "data", "plugins", pluginId);
        var bakFiles = Directory.GetFiles(pluginDir, "main.py.*.bak");
        Assert.NotEmpty(bakFiles);
    }

    [Fact]
    public async Task Plugin_Code_PathTraversal_ThrowsSecurityException()
    {
        await _manager.GetAllAsync();
        var pluginId = "demo-py-api";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await _manager.GetCodeAsync(pluginId, "../../../etc/passwd");
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await _manager.SaveCodeAsync(pluginId, new PluginCodeSaveRequest
            {
                File = "../../evil.py",
                Content = "hacked"
            });
        });
    }
}
