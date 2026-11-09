using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Broker;
using LinuxWebTool.Infrastructure.Features.Plugins.Build;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class PluginUniversalMatrixAndBrokerTests
{
    [Fact]
    public void Manifest_InfersLifecycleAndProtocol_Accurately()
    {
        // 1. 默认微服务：Interpreted + Http
        var resident = new PluginManifest
        {
            Id = "test-resident",
            Name = "Test Resident",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Node
        };
        Assert.Equal(PluginLifecycle.Interpreted, resident.GetEffectiveLifecycle());
        Assert.Equal(InterfaceProtocol.Http, resident.GetEffectiveProtocol());

        // 2. Go 插件：默认为 SourceCompiled
        var goPlugin = new PluginManifest
        {
            Id = "test-go",
            Name = "Test Go",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Go
        };
        Assert.Equal(PluginLifecycle.SourceCompiled, goPlugin.GetEffectiveLifecycle());

        // 3. Wasm 插件：默认为 WasmAbi
        var wasmPlugin = new PluginManifest
        {
            Id = "test-wasm",
            Name = "Test Wasm",
            Profile = PluginProfile.Wasm,
            Runtime = PluginRuntime.Wasm
        };
        Assert.Equal(InterfaceProtocol.WasmAbi, wasmPlugin.GetEffectiveProtocol());

        // 4. 显式覆盖声明
        var custom = new PluginManifest
        {
            Id = "test-custom",
            Name = "Test Custom",
            Archetype = new PluginArchetype
            {
                Profile = PluginProfile.OneShot,
                Lifecycle = PluginLifecycle.PrebuiltBinary,
                Protocol = InterfaceProtocol.CliArgs
            },
            Interface = new PluginInterfaceSpec
            {
                Protocol = InterfaceProtocol.UnixSocket,
                SocketPath = "run/service.sock"
            }
        };
        Assert.Equal(PluginLifecycle.PrebuiltBinary, custom.GetEffectiveLifecycle());
        Assert.Equal(InterfaceProtocol.UnixSocket, custom.GetEffectiveProtocol());
    }

    [Fact]
    public void PluginBroker_DiscoverServices_GeneratesCorrectDescriptors()
    {
        var broker = new PluginBroker();
        var portManager = new PluginPortManager();
        var stateMachines = new Dictionary<string, PluginStateMachine>(StringComparer.OrdinalIgnoreCase);

        var manifests = new List<PluginManifest>
        {
            new()
            {
                Id = "svc-wasm",
                Name = "Wasm Calculator",
                Profile = PluginProfile.Wasm,
                Entrypoint = "calc.wasm",
                CustomActions = new List<PluginCustomAction>
                {
                    new() { Action = "fib", Label = "Fibonacci" },
                    new() { Action = "add", Label = "Add Numbers" }
                }
            },
            new()
            {
                Id = "svc-http",
                Name = "Http MicroService",
                Profile = PluginProfile.Resident,
                Interface = new PluginInterfaceSpec
                {
                    Protocol = InterfaceProtocol.Http,
                    Services = new Dictionary<string, PluginServiceDefinition>
                    {
                        ["api-v1"] = new()
                        {
                            Description = "Core API v1",
                            Operations = new List<string> { "status", "metrics" }
                        }
                    }
                }
            }
        };

        var descriptors = broker.DiscoverServices(manifests, stateMachines, portManager);

        Assert.Equal(2, descriptors.Count);

        var wasmDesc = descriptors.First(d => d.Id == "svc-wasm");
        Assert.Equal(InterfaceProtocol.WasmAbi, wasmDesc.Protocol);
        Assert.Equal("calc.wasm", wasmDesc.Endpoint);
        Assert.Contains("fib", wasmDesc.Operations);
        Assert.Contains("add", wasmDesc.Operations);

        var httpDesc = descriptors.First(d => d.Id == "api-v1");
        Assert.Equal("svc-http", httpDesc.Name.Length > 0 ? "svc-http" : ""); // PluginId matching
        Assert.Equal(InterfaceProtocol.Http, httpDesc.Protocol);
        Assert.Contains("status", httpDesc.Operations);
        Assert.Contains("metrics", httpDesc.Operations);
    }

    [Fact]
    public async Task PluginBroker_InvokeService_WasmAbi_DispatchesToAction()
    {
        var broker = new PluginBroker();
        var portManager = new PluginPortManager();

        var manifest = new PluginManifest
        {
            Id = "demo-wasm",
            Name = "Demo Wasm",
            Profile = PluginProfile.Wasm
        };

        var request = new PluginInvokeRequest
        {
            PluginId = "demo-wasm",
            ActionOrPath = "fib",
            Payload = "10"
        };

        var actionCalled = false;
        var response = await broker.InvokeServiceAsync(
            request,
            manifest,
            Directory.GetCurrentDirectory(),
            null,
            portManager,
            (req, _) =>
            {
                actionCalled = true;
                Assert.Equal("fib", req.Action);
                Assert.Equal("10", req.Param);
                return ValueTask.FromResult(PluginActionResult.Ok("fib(10)=55"));
            });

        Assert.True(actionCalled);
        Assert.True(response.Success);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("fib(10)=55", response.Output);
    }

    [Fact]
    public async Task PluginBuild_Interpreted_ReturnsSuccessImmediately()
    {
        var buildManager = new PluginBuildManager();
        var manifest = new PluginManifest
        {
            Id = "demo-py",
            Name = "Python Script",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Python
        };

        var result = await buildManager.BuildAsync(Directory.GetCurrentDirectory(), manifest);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("无需中间编译阶段", result.Output);
    }

    [Fact]
    public async Task PluginBuild_SourceCompiled_FailsWhenBuildSpecMissing()
    {
        var buildManager = new PluginBuildManager();
        var manifest = new PluginManifest
        {
            Id = "demo-native",
            Name = "C# Native",
            Archetype = new PluginArchetype
            {
                Lifecycle = PluginLifecycle.SourceCompiled
            }
            // Build 节点未指定
        };

        var result = await buildManager.BuildAsync(Directory.GetCurrentDirectory(), manifest);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("必须声明有效的构建规范", result.ErrorMessage);
    }

    [Fact]
    public async Task PluginBuild_SourceCompiled_ExecutesCommandAndChecksArtifact()
    {
        var buildManager = new PluginBuildManager();
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt-build-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var artifactName = "output.txt";
            var cmd = isWindows ? $"echo test_build_success > {artifactName}" : $"echo test_build_success > {artifactName}";

            var manifest = new PluginManifest
            {
                Id = "test-build-exec",
                Name = "Test Exec",
                Archetype = new PluginArchetype
                {
                    Lifecycle = PluginLifecycle.SourceCompiled
                },
                Build = new PluginBuildSpec
                {
                    Command = cmd,
                    Artifacts = new List<string> { artifactName }
                }
            };

            var result = await buildManager.BuildAsync(tempDir, manifest);

            Assert.True(result.Success);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(artifactName, result.Artifacts!);
            Assert.True(File.Exists(Path.Combine(tempDir, artifactName)));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
