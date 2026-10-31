using System.Security;
using System.Text.Json;
using LinuxWebTool.Application.Features.Plugins;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class NativeCommandBindingTests
{
    private sealed class MockProgramResolver : INativeProgramResolver
    {
        public string ResolveExecutable(string source, string programRef, string? packageDir)
        {
            return Path.Combine(packageDir ?? "C:\\test", programRef);
        }
    }

    [Fact]
    public async Task ResolveAsync_LiteralAndScalarBindings_ProducesCorrectArgv()
    {
        var resolver = new CommandBindingResolver(new MockProgramResolver());
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-1",
            ProgramRef = "bin/tool",
            ArgumentBindings =
            [
                new CommandArgumentBinding { Type = "literal", Value = "--mode" },
                new CommandArgumentBinding { Type = "literal", Value = "fast" },
                new CommandArgumentBinding { Type = "inputScalar", Value = "count", Prefix = "--count=" }
            ]
        };

        var json = JsonDocument.Parse("{\"count\": 42}").RootElement;
        var invocation = new CommandInvocation
        {
            PluginIdentity = "test-plugin",
            Action = "test-action",
            Input = json
        };

        var spec = await resolver.ResolveAsync(def, invocation, "C:\\mock\\work", "op-1", CancellationToken.None);

        Assert.Equal(3, spec.ArgumentList.Count);
        Assert.Equal("--mode", spec.ArgumentList[0]);
        Assert.Equal("fast", spec.ArgumentList[1]);
        Assert.Equal("--count=42", spec.ArgumentList[2]);
        Assert.Equal("closed", spec.StdinMode);
    }

    [Fact]
    public async Task ResolveAsync_ConditionField_SkipsWhenConditionFalse()
    {
        var resolver = new CommandBindingResolver(new MockProgramResolver());
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-2",
            ProgramRef = "bin/tool",
            ArgumentBindings =
            [
                new CommandArgumentBinding { Type = "literal", Value = "--verbose", ConditionField = "debug" },
                new CommandArgumentBinding { Type = "literal", Value = "--normal" }
            ]
        };

        var json = JsonDocument.Parse("{\"debug\": false}").RootElement;
        var invocation = new CommandInvocation
        {
            PluginIdentity = "test-plugin",
            Action = "test-action",
            Input = json
        };

        var spec = await resolver.ResolveAsync(def, invocation, "C:\\mock\\work", "op-1", CancellationToken.None);

        Assert.Single(spec.ArgumentList);
        Assert.Equal("--normal", spec.ArgumentList[0]);
    }

    [Fact]
    public async Task ResolveAsync_InputScalarWithNewlines_ThrowsSecurityException()
    {
        var resolver = new CommandBindingResolver(new MockProgramResolver());
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-3",
            ProgramRef = "bin/tool",
            ArgumentBindings =
            [
                new CommandArgumentBinding { Type = "inputScalar", Value = "text" }
            ]
        };

        var json = JsonDocument.Parse("{\"text\": \"hello\\nrm -rf /\"}").RootElement;
        var invocation = new CommandInvocation
        {
            PluginIdentity = "test-plugin",
            Action = "test-action",
            Input = json
        };

        await Assert.ThrowsAsync<SecurityException>(() =>
            resolver.ResolveAsync(def, invocation, "C:\\mock\\work", "op-1", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_InputPathWithTraversal_ThrowsSecurityException()
    {
        var resolver = new CommandBindingResolver(new MockProgramResolver());
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-4",
            ProgramRef = "bin/tool",
            ArgumentBindings =
            [
                new CommandArgumentBinding { Type = "inputPath", Value = "file" }
            ]
        };

        var json = JsonDocument.Parse("{\"file\": \"../../windows/win.ini\"}").RootElement;
        var invocation = new CommandInvocation
        {
            PluginIdentity = "test-plugin",
            Action = "test-action",
            Input = json
        };

        await Assert.ThrowsAsync<SecurityException>(() =>
            resolver.ResolveAsync(def, invocation, "C:\\mock\\work", "op-1", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ResolveAsync_JsonStdin_ExtractsPayload()
    {
        var resolver = new CommandBindingResolver(new MockProgramResolver());
        var def = new NativeCommandDefinition
        {
            CommandId = "cmd-5",
            ProgramRef = "bin/tool",
            StdinBinding = new CommandStdinBinding { Mode = "json", SourceField = "body" }
        };

        var json = JsonDocument.Parse("{\"body\": {\"key\": \"value\"}}").RootElement;
        var invocation = new CommandInvocation
        {
            PluginIdentity = "test-plugin",
            Action = "test-action",
            Input = json
        };

        var spec = await resolver.ResolveAsync(def, invocation, "C:\\mock\\work", "op-1", CancellationToken.None);

        Assert.Equal("json", spec.StdinMode);
        Assert.NotNull(spec.StdinPayload);
        Assert.Contains("key", spec.StdinPayload);
    }
}
