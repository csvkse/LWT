using System.IO.Compression;
using System.Security;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;
using LinuxWebTool.Infrastructure.Features.Workers;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Features.Mcp.Engine;
using LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class ExtensionsAndWorkersCodeAndZipTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _extDir;
    private readonly string _workersDir;

    public ExtensionsAndWorkersCodeAndZipTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_code_zip_test_" + Guid.NewGuid().ToString("N"));
        _extDir = Path.Combine(_tempRoot, "extensions");
        _workersDir = Path.Combine(_tempRoot, "workers");
        Directory.CreateDirectory(_extDir);
        Directory.CreateDirectory(_workersDir);
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

    private sealed class MockShellExecutor : IShellExecutor
    {
        public Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ShellResult
            {
                ExitCode = 0,
                StandardOutput = "{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":1234}",
                ErrorOutput = string.Empty,
                DurationMs = 15,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow
            });
        }
    }

    private sealed class DummyOperationLogger : IOperationLogger
    {
        public Task LogAsync(string action, string targetType, string targetName, string? detail = null, bool success = true, string? clientIp = null)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class DummySystemdAdapter : ISystemdServiceAdapter
    {
        public Task<IReadOnlyList<SystemdUnitItem>> ListUnitsAsync(SystemdQueryCriteria criteria, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SystemdUnitItem>>([]);
        public Task<ServiceActionResult> ExecuteActionAsync(string unitName, ServiceActionType action, CancellationToken cancellationToken = default) => Task.FromResult(new ServiceActionResult { Success = true });
        public Task<SystemdJournalResult> GetJournalAsync(SystemdJournalRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new SystemdJournalResult { UnitName = request.UnitName });
        public Task<string> GetUnitFileContentAsync(string unitName, CancellationToken cancellationToken = default) => Task.FromResult("");
    }

    private sealed class DummyDockerAdapter : IDockerEngineAdapter
    {
        public Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(bool all = true, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DockerContainerSummary>>([]);
        public Task<ServiceActionResult> ExecuteContainerActionAsync(DockerContainerActionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ServiceActionResult { Success = true });
        public Task<string> GetContainerLogsAsync(DockerLogsRequest request, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<IReadOnlyList<DockerImageSummary>> ListImagesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DockerImageSummary>>([]);
    }

    [Fact]
    public async Task Extension_ImportZip_ValidArchive_ShouldSucceed()
    {
        var executor = new MockShellExecutor();
        var manager = new ScriptExtensionManager(executor, NullLogger<ScriptExtensionManager>.Instance, _extDir);

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.Write("""
                {
                    "id": "zip-imported-service",
                    "name": "Zip 导入服务",
                    "version": "1.0.2",
                    "dependencies": { "runners": ["sh"] }
                }
                """);
            }

            var hookEntry = archive.CreateEntry("hooks/status.sh");
            using (var writer = new StreamWriter(hookEntry.Open()))
            {
                writer.Write("echo '{\"status\":\"Running\"}'");
            }
        }

        memoryStream.Position = 0;
        var result = await manager.ImportZipAsync(memoryStream);

        Assert.True(result.Success);
        Assert.Contains("zip-imported-service", result.ImportedExtensionIds);

        var targetManifest = Path.Combine(_extDir, "zip-imported-service", "manifest.json");
        Assert.True(File.Exists(targetManifest));

        var sourceJson = Path.Combine(_extDir, "zip-imported-service", "source.json");
        Assert.True(File.Exists(sourceJson));
        var sourceText = File.ReadAllText(sourceJson);
        Assert.Contains("\"zip\"", sourceText);
    }

    [Fact]
    public async Task Extension_ImportZip_ZipSlipAttack_ShouldBeBlocked()
    {
        var executor = new MockShellExecutor();
        var manager = new ScriptExtensionManager(executor, NullLogger<ScriptExtensionManager>.Instance, _extDir);

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("../../evil-payload.sh");
        }

        memoryStream.Position = 0;
        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await manager.ImportZipAsync(memoryStream);
        });
    }

    [Fact]
    public async Task Extension_CodeEditing_And_MementoBackup_ShouldWork()
    {
        var executor = new MockShellExecutor();
        var manager = new ScriptExtensionManager(executor, NullLogger<ScriptExtensionManager>.Instance, _extDir);

        // 创建扩展
        var createRes = await manager.CreateExtensionAsync(new ExtensionCreateRequest
        {
            Id = "test-edit-ext",
            Name = "测试编辑扩展",
            Runner = "bash"
        });
        Assert.True(createRes.Success);

        // 获取代码
        var codeDto = await manager.GetCodeAsync("test-edit-ext");
        Assert.Equal("test-edit-ext", codeDto.ExtensionId);
        Assert.NotEmpty(codeDto.AvailableFiles);

        // 保存新代码（触发备忘录快照）
        var saveRes = await manager.SaveCodeAsync("test-edit-ext", new ExtensionCodeSaveRequest
        {
            FilePath = "hooks/start.sh",
            Content = "#!/bin/bash\necho 'Modified content version 2'"
        });
        Assert.True(saveRes);

        // 再次获取应有备份文件
        var updatedDto = await manager.GetCodeAsync("test-edit-ext", "hooks/start.sh");
        Assert.Contains("Modified content version 2", updatedDto.Content);
        Assert.NotEmpty(updatedDto.Backups);

        // 验证越权路径拦截
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await manager.GetCodeAsync("test-edit-ext", "../../../appsettings.json");
        });
    }

    [Fact]
    public async Task Worker_CodeEditing_And_MementoBackup_ShouldWork()
    {
        var manager = new WorkersManager(NullLogger<WorkersManager>.Instance, _tempRoot);

        var worker = await manager.CreateOrUpdateAsync(new WorkerCreateOrUpdateRequest
        {
            Id = "test-worker-edit",
            Name = "测试编辑Worker",
            Runtime = WorkerRuntimeType.Node,
            Entrypoint = "worker.js",
            ScriptContent = "console.log('original v1');"
        });
        Assert.NotNull(worker);

        // 获取代码
        var codeDto = await manager.GetCodeAsync("test-worker-edit");
        Assert.Equal("test-worker-edit", codeDto.WorkerId);
        Assert.Contains("original v1", codeDto.Content);

        // 保存新代码（带自动备忘录快照）
        var saveRes = await manager.SaveCodeAsync("test-worker-edit", new WorkerCodeSaveRequest
        {
            FilePath = "worker.js",
            Content = "console.log('updated v2');",
            RestartAfterSave = false
        });
        Assert.True(saveRes);

        // 再次获取
        var updatedDto = await manager.GetCodeAsync("test-worker-edit", "worker.js");
        Assert.Contains("updated v2", updatedDto.Content);
        Assert.NotEmpty(updatedDto.Backups);

        // 越权路径拦截
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await manager.GetCodeAsync("test-worker-edit", "../secret.env");
        });
    }

    [Fact]
    public async Task WorkersMcpToolProvider_ShouldExecute_CodeAndDebugTools()
    {
        var manager = new WorkersManager(NullLogger<WorkersManager>.Instance, _tempRoot);
        var provider = new WorkersMcpToolProvider(manager, new DummyHttpClientFactory(), new DummyOperationLogger());
        var context = new McpRequestContext(null, new DefaultHttpContext(), "127.0.0.1", CancellationToken.None);

        // 1. worker_create
        var createArgs = JsonDocument.Parse("""
        {
            "worker_id": "mcp-test-worker",
            "name": "MCP 创建的 Worker",
            "runtime": "node",
            "entrypoint": "worker.js",
            "script_content": "console.log('Hello from MCP test');"
        }
        """).RootElement;
        var createRes = await provider.TryExecuteAsync("worker_create", createArgs, context);
        Assert.NotNull(createRes);
        Assert.False(createRes.IsError);

        // 2. worker_get_code
        var getCodeArgs = JsonDocument.Parse("""
        {
            "worker_id": "mcp-test-worker",
            "file": "worker.js"
        }
        """).RootElement;
        var getCodeRes = await provider.TryExecuteAsync("worker_get_code", getCodeArgs, context);
        Assert.NotNull(getCodeRes);
        Assert.False(getCodeRes.IsError);
        Assert.Contains("Hello from MCP test", getCodeRes.Content);

        // 3. worker_edit_code
        var editCodeArgs = JsonDocument.Parse("""
        {
            "worker_id": "mcp-test-worker",
            "file": "worker.js",
            "content": "console.log('Edited via MCP tool');",
            "restart": false
        }
        """).RootElement;
        var editRes = await provider.TryExecuteAsync("worker_edit_code", editCodeArgs, context);
        Assert.NotNull(editRes);
        Assert.False(editRes.IsError);

        // 4. 验证再次获取已被编辑
        var verifyRes = await provider.TryExecuteAsync("worker_get_code", getCodeArgs, context);
        Assert.Contains("Edited via MCP tool", verifyRes!.Content);
    }

    [Fact]
    public async Task ServicesMcpToolProvider_ShouldExecute_CodeAndLogTools()
    {
        var executor = new MockShellExecutor();
        var manager = new ScriptExtensionManager(executor, NullLogger<ScriptExtensionManager>.Instance, _extDir);

        var systemd = new DummySystemdAdapter();
        var docker = new DummyDockerAdapter();
        var provider = new ServicesMcpToolProvider(systemd, docker, manager, new DummyOperationLogger());
        var context = new McpRequestContext(null, new DefaultHttpContext(), "127.0.0.1", CancellationToken.None);

        // 1. extension_create
        var createArgs = JsonDocument.Parse("""
        {
            "extension_id": "mcp-created-ext",
            "name": "MCP 服务扩展",
            "runner": "bash"
        }
        """).RootElement;
        var createRes = await provider.TryExecuteAsync("extension_create", createArgs, context);
        Assert.NotNull(createRes);
        Assert.False(createRes.IsError);

        // 2. extension_get_code
        var getCodeArgs = JsonDocument.Parse("""
        {
            "extension_id": "mcp-created-ext"
        }
        """).RootElement;
        var getCodeRes = await provider.TryExecuteAsync("extension_get_code", getCodeArgs, context);
        Assert.NotNull(getCodeRes);
        Assert.False(getCodeRes.IsError);

        // 3. extension_get_logs
        var getLogsArgs = JsonDocument.Parse("""
        {
            "extension_id": "mcp-created-ext",
            "lines": 50
        }
        """).RootElement;
        var getLogsRes = await provider.TryExecuteAsync("extension_get_logs", getLogsArgs, context);
        Assert.NotNull(getLogsRes);
        Assert.False(getLogsRes.IsError);

        // 4. extension_debug_run
        var debugArgs = JsonDocument.Parse("""
        {
            "extension_id": "mcp-created-ext",
            "hook_name": "status"
        }
        """).RootElement;
        var debugRes = await provider.TryExecuteAsync("extension_debug_run", debugArgs, context);
        Assert.NotNull(debugRes);
        Assert.False(debugRes.IsError);
    }
}
