using System.Security;
using System.Text.Json;
using LinuxWebTool.Application.Features.Plugins;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Storage;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class PluginOperationCoordinatorTests
{
    private sealed class StubArtifactStore : IPluginArtifactStore
    {
        public ValueTask<string> AllocateArtifactDirectoryAsync(string operationId, CancellationToken ct) =>
            ValueTask.FromResult(Path.Combine(Path.GetTempPath(), "art_" + operationId));

        public ValueTask<PluginArtifactRef?> RegisterArtifactAsync(string operationId, string artifactId, string filePath, string? contentType, CancellationToken ct) =>
            ValueTask.FromResult<PluginArtifactRef?>(null);

        public ValueTask<Stream?> OpenArtifactReadStreamAsync(string operationId, string artifactId, string caller, CancellationToken ct) =>
            ValueTask.FromResult<Stream?>(null);

        public ValueTask<PluginArtifactRef?> GetArtifactMetaAsync(string operationId, string artifactId, string caller, CancellationToken ct) =>
            ValueTask.FromResult<PluginArtifactRef?>(null);

        public ValueTask CleanExpiredArtifactsAsync(TimeSpan maxAge, long maxTotalBytes, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }

    private sealed class StubCommandExecutor : INativeCommandExecutor
    {
        public Func<CancellationToken, Task<PluginActionResult>> ExecFunc { get; set; } =
            _ => Task.FromResult(new PluginActionResult { Status = "succeeded", Message = "ok" });

        public Task<PluginActionResult> ExecuteAsync(
            NativeCommandDefinition definition,
            CommandInvocation invocation,
            string workingDir,
            string operationId,
            ICommandProgressObserver? observer,
            CancellationToken ct)
        {
            return ExecFunc(ct);
        }
    }

    [Fact]
    public async Task SubmitAsync_ValidInvocation_ReturnsAcceptedWithOperationId()
    {
        var store = new PluginOperationStore();
        var artifactStore = new StubArtifactStore();
        var executor = new StubCommandExecutor();

        var coordinator = new PluginOperationCoordinator(
            store,
            artifactStore,
            executor,
            _ => Path.GetTempPath(),
            (_, _) => new PluginCustomAction
            {
                Action = "test-act",
                Label = "Test Act",
                Executor = "command",
                Command = new NativeCommandDefinition { CommandId = "c1", ProgramRef = "bin/tool" }
            });

        var invocation = new CommandInvocation
        {
            PluginIdentity = "valid-plugin",
            Action = "test-act",
            Input = JsonDocument.Parse("{}").RootElement
        };

        var accepted = await coordinator.SubmitAsync(invocation, CancellationToken.None);

        Assert.NotNull(accepted.OperationId);
        Assert.StartsWith("/api/PluginOperations/", accepted.StatusUrl);

        var op = await coordinator.GetAsync(accepted.OperationId, "anonymous", CancellationToken.None);
        Assert.NotNull(op);
    }

    [Fact]
    public async Task SubmitAsync_IdempotencyKey_SameInput_ReturnsSameOperation()
    {
        var store = new PluginOperationStore();
        var artifactStore = new StubArtifactStore();
        var executor = new StubCommandExecutor();

        var coordinator = new PluginOperationCoordinator(
            store,
            artifactStore,
            executor,
            _ => Path.GetTempPath(),
            (_, _) => new PluginCustomAction
            {
                Action = "test-act",
                Label = "Test Act",
                Executor = "command",
                Command = new NativeCommandDefinition { CommandId = "c1", ProgramRef = "bin/tool" }
            });

        var invocation1 = new CommandInvocation
        {
            PluginIdentity = "valid-plugin",
            Action = "test-act",
            IdempotencyKey = "key-12345",
            Input = JsonDocument.Parse("{\"param\": 1}").RootElement
        };

        var accepted1 = await coordinator.SubmitAsync(invocation1, CancellationToken.None);

        var invocation2 = new CommandInvocation
        {
            PluginIdentity = "valid-plugin",
            Action = "test-act",
            IdempotencyKey = "key-12345",
            Input = JsonDocument.Parse("{\"param\": 1}").RootElement
        };

        var accepted2 = await coordinator.SubmitAsync(invocation2, CancellationToken.None);

        Assert.Equal(accepted1.OperationId, accepted2.OperationId);
    }

    [Fact]
    public async Task SubmitAsync_IdempotencyKey_DifferentInput_ThrowsConflict()
    {
        var store = new PluginOperationStore();
        var artifactStore = new StubArtifactStore();
        var executor = new StubCommandExecutor();

        var coordinator = new PluginOperationCoordinator(
            store,
            artifactStore,
            executor,
            _ => Path.GetTempPath(),
            (_, _) => new PluginCustomAction
            {
                Action = "test-act",
                Label = "Test Act",
                Executor = "command",
                Command = new NativeCommandDefinition { CommandId = "c1", ProgramRef = "bin/tool" }
            });

        var invocation1 = new CommandInvocation
        {
            PluginIdentity = "valid-plugin",
            Action = "test-act",
            IdempotencyKey = "key-12345",
            Input = JsonDocument.Parse("{\"param\": 1}").RootElement
        };

        await coordinator.SubmitAsync(invocation1, CancellationToken.None);

        var invocation2 = new CommandInvocation
        {
            PluginIdentity = "valid-plugin",
            Action = "test-act",
            IdempotencyKey = "key-12345",
            Input = JsonDocument.Parse("{\"param\": 2}").RootElement // Different input!
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.SubmitAsync(invocation2, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SubmitAsync_InvalidPluginId_ThrowsSecurityException()
    {
        var store = new PluginOperationStore();
        var coordinator = new PluginOperationCoordinator(
            store,
            new StubArtifactStore(),
            new StubCommandExecutor(),
            _ => Path.GetTempPath());

        var invocation = new CommandInvocation
        {
            PluginIdentity = "../escape",
            Action = "test-act",
            Input = JsonDocument.Parse("{}").RootElement
        };

        await Assert.ThrowsAsync<SecurityException>(() =>
            coordinator.SubmitAsync(invocation, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CancelAsync_ActiveOperation_CancelsExecution()
    {
        var store = new PluginOperationStore();
        var artifactStore = new StubArtifactStore();

        var tcs = new TaskCompletionSource<bool>();
        var executor = new StubCommandExecutor
        {
            ExecFunc = async ct =>
            {
                tcs.SetResult(true);
                await Task.Delay(5000, ct); // simulate long task
                return new PluginActionResult { Status = "succeeded" };
            }
        };

        var coordinator = new PluginOperationCoordinator(
            store,
            artifactStore,
            executor,
            _ => Path.GetTempPath(),
            (_, _) => new PluginCustomAction
            {
                Action = "test-act",
                Label = "Test Act",
                Executor = "command",
                Command = new NativeCommandDefinition { CommandId = "c1", ProgramRef = "bin/tool" }
            });

        var invocation = new CommandInvocation
        {
            PluginIdentity = "valid-plugin",
            Action = "test-act",
            Input = JsonDocument.Parse("{}").RootElement
        };

        var accepted = await coordinator.SubmitAsync(invocation, CancellationToken.None);

        // wait until executor actually started
        await tcs.Task;

        var cancelled = await coordinator.CancelAsync(accepted.OperationId, "anonymous", CancellationToken.None);
        Assert.NotNull(cancelled);

        // Wait a short moment for cancellation status update
        await Task.Delay(100);
        var op = await coordinator.GetAsync(accepted.OperationId, "anonymous", CancellationToken.None);
        Assert.NotNull(op);
        Assert.Equal("cancelled", op.Status);
    }
}
