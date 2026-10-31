using System.Text.Json;
using LinuxWebTool.Application.Features.Plugins;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class NativeCommandExecutorTests
{
    private sealed class StubBindingResolver : ICommandBindingResolver
    {
        public ValueTask<NativeProgramSpec> ResolveAsync(
            NativeCommandDefinition definition,
            CommandInvocation invocation,
            string workingDir,
            string operationId,
            CancellationToken ct)
        {
            return ValueTask.FromResult(new NativeProgramSpec
            {
                Source = "package",
                ProgramRef = definition.ProgramRef,
                ExecutablePath = Path.Combine(workingDir, definition.ProgramRef),
                ArgumentList = ["--test"],
                WorkingDirectory = workingDir
            });
        }
    }

    private sealed class StubProcessRunner : INativeProcessRunner
    {
        public Func<NativeProgramSpec, CancellationToken, Task<NativeProcessResult>> RunFunc { get; set; } =
            (_, _) => Task.FromResult(new NativeProcessResult { ExitCode = 0, StandardOutput = "ok" });

        public Task<NativeProcessResult> RunAsync(
            NativeProgramSpec spec,
            ICommandProgressObserver? progressObserver,
            CancellationToken ct)
        {
            return RunFunc(spec, ct);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ZeroExitCode_BoundedText_ReturnsSuccess()
    {
        var runner = new StubProcessRunner
        {
            RunFunc = (_, _) => Task.FromResult(new NativeProcessResult
            {
                ExitCode = 0,
                StandardOutput = "hello bounded text"
            })
        };

        var executor = new NativeCommandExecutor(new StubBindingResolver(), runner);
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-bt",
            ProgramRef = "bin/tool",
            ResultParser = "bounded-text"
        };
        var invocation = new CommandInvocation { PluginIdentity = "p1", Action = "a1" };

        var result = await executor.ExecuteAsync(def, invocation, Path.GetTempPath(), "op-1", null, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("hello bounded text", result.Message);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ReturnsNonZeroExitCodeFailure()
    {
        var runner = new StubProcessRunner
        {
            RunFunc = (_, _) => Task.FromResult(new NativeProcessResult
            {
                ExitCode = 2,
                StandardError = "disk full"
            })
        };

        var executor = new NativeCommandExecutor(new StubBindingResolver(), runner);
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-fail",
            ProgramRef = "bin/tool",
            AcceptedExitCodes = [0]
        };
        var invocation = new CommandInvocation { PluginIdentity = "p1", Action = "a1" };

        var result = await executor.ExecuteAsync(def, invocation, Path.GetTempPath(), "op-1", null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("NonZeroExitCode", result.ErrorCode);
        Assert.Equal("disk full", result.Error);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptedExitCode_ReturnsSuccess()
    {
        var runner = new StubProcessRunner
        {
            RunFunc = (_, _) => Task.FromResult(new NativeProcessResult
            {
                ExitCode = 1, // accepted
                StandardOutput = "diff found"
            })
        };

        var executor = new NativeCommandExecutor(new StubBindingResolver(), runner);
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-diff",
            ProgramRef = "bin/diff-tool",
            AcceptedExitCodes = [0, 1],
            ResultParser = "bounded-text"
        };
        var invocation = new CommandInvocation { PluginIdentity = "p1", Action = "a1" };

        var result = await executor.ExecuteAsync(def, invocation, Path.GetTempPath(), "op-1", null, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal("diff found", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_JsonParser_ValidJson_ReturnsData()
    {
        var runner = new StubProcessRunner
        {
            RunFunc = (_, _) => Task.FromResult(new NativeProcessResult
            {
                ExitCode = 0,
                StandardOutput = "{\"cpu\": 20, \"status\": \"ok\"}"
            })
        };

        var executor = new NativeCommandExecutor(new StubBindingResolver(), runner);
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-json",
            ProgramRef = "bin/tool",
            ResultParser = "json"
        };
        var invocation = new CommandInvocation { PluginIdentity = "p1", Action = "a1" };

        var result = await executor.ExecuteAsync(def, invocation, Path.GetTempPath(), "op-1", null, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal(20, result.Data.Value.GetProperty("cpu").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_JsonParser_InvalidJson_ReturnsProtocolError()
    {
        var runner = new StubProcessRunner
        {
            RunFunc = (_, _) => Task.FromResult(new NativeProcessResult
            {
                ExitCode = 0,
                StandardOutput = "not a valid json"
            })
        };

        var executor = new NativeCommandExecutor(new StubBindingResolver(), runner);
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-json-inv",
            ProgramRef = "bin/tool",
            ResultParser = "json"
        };
        var invocation = new CommandInvocation { PluginIdentity = "p1", Action = "a1" };

        var result = await executor.ExecuteAsync(def, invocation, Path.GetTempPath(), "op-1", null, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("ProtocolError", result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ArtifactsParser_MissingRequiredArtifact_ReturnsArtifactMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_art_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var runner = new StubProcessRunner
            {
                RunFunc = (_, _) => Task.FromResult(new NativeProcessResult { ExitCode = 0 })
            };

            var executor = new NativeCommandExecutor(new StubBindingResolver(), runner);
            var def = new NativeCommandDefinition
            {
                CommandId = "cmd-art",
                ProgramRef = "bin/tool",
                ResultParser = "artifacts",
                ArtifactRules =
                [
                    new CommandArtifactRule { ArtifactId = "report", RelativePath = "report.pdf", Optional = false }
                ]
            };
            var invocation = new CommandInvocation { PluginIdentity = "p1", Action = "a1" };

            var result = await executor.ExecuteAsync(def, invocation, tempDir, "op-1", null, CancellationToken.None);

            Assert.Equal("failed", result.Status);
            Assert.Equal("ArtifactMissing", result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
