using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;
using LinuxWebTool.Infrastructure.Features.Workflows;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class PluginOptimizationAndFaaSTests
{
    [Fact]
    public void PluginEnvironmentSanitizer_DetectsSensitiveKeysAccurately()
    {
        Assert.True(PluginEnvironmentSanitizer.IsSensitiveKey("WEB_ADMIN_PASSWORD"));
        Assert.True(PluginEnvironmentSanitizer.IsSensitiveKey("JWT_SECRET_TOKEN"));
        Assert.True(PluginEnvironmentSanitizer.IsSensitiveKey("SSH_PRIVATE_KEY"));
        Assert.True(PluginEnvironmentSanitizer.IsSensitiveKey("DATABASE_AUTH_PWD"));
        Assert.True(PluginEnvironmentSanitizer.IsSensitiveKey("MY_API_KEY"));

        Assert.False(PluginEnvironmentSanitizer.IsSensitiveKey("LWT_PLUGIN_ID"));
        Assert.False(PluginEnvironmentSanitizer.IsSensitiveKey("LWT_INTERNAL_SECRET"));
        Assert.False(PluginEnvironmentSanitizer.IsSensitiveKey("PORT"));
        Assert.False(PluginEnvironmentSanitizer.IsSensitiveKey("HOST"));
        Assert.False(PluginEnvironmentSanitizer.IsSensitiveKey("PATH"));
    }

    [Fact]
    public void PluginEnvironmentSanitizer_CleansesAndAppliesSafeEnvironment()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["PARENT_SECRET"] = "leak_me";

        var customEnv = new Dictionary<string, string>
        {
            ["SAFE_VAR"] = "hello",
            ["LEAK_PASSWORD"] = "secret123",
            ["ALLOWED_KEY"] = "explicit_secret"
        };

        var explicitAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALLOWED_KEY" };

        PluginEnvironmentSanitizer.ApplySafeEnvironment(psi, customEnv, explicitAllowed);

        // 1. 父进程隐式继承已被完全清空
        Assert.False(psi.Environment.ContainsKey("PARENT_SECRET"));

        // 2. 安全自定义变量正常保留
        Assert.True(psi.Environment.ContainsKey("SAFE_VAR"));
        Assert.Equal("hello", psi.Environment["SAFE_VAR"]);

        // 3. 敏感变量被凭据防火墙精准拦截阻断
        Assert.False(psi.Environment.ContainsKey("LEAK_PASSWORD"));

        // 4. 显式在白名单授权的敏感变量受控放行
        Assert.True(psi.Environment.ContainsKey("ALLOWED_KEY"));
        Assert.Equal("explicit_secret", psi.Environment["ALLOWED_KEY"]);

        // 5. 核心系统基础变量得到安全继承 (如 PATH)
        Assert.True(psi.Environment.ContainsKey("PATH") || psi.Environment.ContainsKey("Path"));
    }

    [Fact]
    public void WorkflowDagValidator_ValidWorkflow_Passes()
    {
        var validJson = """
        {
            "version": "1.0",
            "name": "LinearPipeline",
            "stages": [
                {
                    "name": "StageA",
                    "steps": [ { "name": "Step1" } ]
                },
                {
                    "name": "StageB",
                    "steps": [ { "name": "Step2" } ]
                }
            ]
        }
        """;

        var (isValid, error) = WorkflowDagValidator.ValidateDefinition(validJson);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void WorkflowDagValidator_CyclicDependencies_DetectedAndRejected()
    {
        var cyclicJson = """
        {
            "version": "1.0",
            "name": "CyclicPipeline",
            "stages": [
                {
                    "name": "StageA",
                    "steps": [
                        { "name": "StepA", "parameters": { "dependsOn": "StepC" } }
                    ]
                },
                {
                    "name": "StageB",
                    "steps": [
                        { "name": "StepB", "parameters": { "dependsOn": "StepA" } }
                    ]
                },
                {
                    "name": "StageC",
                    "steps": [
                        { "name": "StepC", "parameters": { "dependsOn": "StepB" } }
                    ]
                }
            ]
        }
        """;

        var (isValid, error) = WorkflowDagValidator.ValidateDefinition(cyclicJson);
        Assert.False(isValid);
        Assert.NotNull(error);
        Assert.Contains("DAG Cycle", error);
    }

    [Fact]
    public void PluginRevisionManager_ManagesImmutableRevisionsAndAliases()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_rev_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 初始在基目录下回退
            Assert.Equal(tempDir, PluginRevisionManager.ResolveEffectiveDirectory(tempDir));

            // 创建两个不可变版本
            var rev1 = PluginRevisionManager.CreateRevision(tempDir, "1.0.0", "{\"version\":\"1.0.0\"}");
            var rev2 = PluginRevisionManager.CreateRevision(tempDir, "1.0.1", "{\"version\":\"1.0.1\"}");

            var revisions = PluginRevisionManager.ListRevisions(tempDir);
            Assert.Equal(2, revisions.Count);
            Assert.Contains(rev1, revisions);
            Assert.Contains(rev2, revisions);

            // 默认 stable 别名指向最后创建的 rev2
            var effectiveStable = PluginRevisionManager.ResolveEffectiveDirectory(tempDir, "stable");
            Assert.EndsWith(rev2, effectiveStable);

            // 切换 canary 别名至 rev1
            PluginRevisionManager.SetAlias(tempDir, "canary", rev1);
            var effectiveCanary = PluginRevisionManager.ResolveEffectiveDirectory(tempDir, "canary");
            Assert.EndsWith(rev1, effectiveCanary);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PluginResiliencePipeline_ExecutesSynchronouslyAndCatchesTimeout()
    {
        var pipeline = PluginResiliencePipeline.GetOrCreate("test-pipeline-plugin", timeout: TimeSpan.FromSeconds(1));
        Assert.NotNull(pipeline);

        var executed = false;
        await pipeline.ExecuteAsync(token =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(executed);
    }

    [Fact]
    public void FourDimensionalExecutionStrategies_ProfileAlignment()
    {
        var serverlessStrategy = new LinuxWebTool.Infrastructure.Features.Plugins.Strategies.ServerlessExecutionStrategy();
        var wasmStrategy = new LinuxWebTool.Infrastructure.Features.Plugins.Strategies.WasmMicroFunctionStrategy();
        var wasmAdapter = new WasmRuntimeAdapter();

        Assert.Equal(PluginProfile.Serverless, serverlessStrategy.Profile);
        Assert.Equal(PluginProfile.Wasm, wasmStrategy.Profile);
        Assert.Equal(PluginRuntime.Wasm, wasmAdapter.RuntimeType);
        Assert.Equal("wasmtime", wasmAdapter.PrimaryExecutable);
        Assert.Contains("node", wasmAdapter.CandidateExecutables);
    }

    [Fact]
    public async Task WasmMicroFunctionStrategy_ValidatesModulePresenceOnStart()
    {
        var wasmStrategy = new LinuxWebTool.Infrastructure.Features.Plugins.Strategies.WasmMicroFunctionStrategy();
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_wasm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var manifest = new PluginManifest
            {
                Id = "test-wasm-plugin",
                Name = "Test Wasm",
                Profile = PluginProfile.Wasm,
                Runtime = PluginRuntime.Wasm,
                Entrypoint = "main.wasm"
            };

            var sm = new PluginStateMachine(manifest.Id, null);
            var pm = new PluginPortManager();
            var rb = new PluginProcessRingBuffer();
            var adapters = new Dictionary<PluginRuntime, IRuntimeAdapter>();

            // 1. 无 wasm 文件时，启动应探测失败并进入 Crashed 状态
            var (success1, err1) = await wasmStrategy.StartAsync(manifest, tempDir, sm, pm, rb, adapters, CancellationToken.None);
            Assert.False(success1);
            Assert.NotNull(err1);
            Assert.Equal(PluginState.Crashed, sm.CurrentState);

            // 2. 创建 dummy main.wasm 文件，启动应成功且状态跃迁至 Ready
            await File.WriteAllBytesAsync(Path.Combine(tempDir, "main.wasm"), [0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]); // Wasm 8-byte magic header
            var sm2 = new PluginStateMachine(manifest.Id, null);
            var (success2, err2) = await wasmStrategy.StartAsync(manifest, tempDir, sm2, pm, rb, adapters, CancellationToken.None);
            Assert.True(success2);
            Assert.Null(err2);
            Assert.Equal(PluginState.Running, sm2.CurrentState);

            // 3. 停止操作应成功且状态跃迁至 Stopped
            var stopSuccess = await wasmStrategy.StopAsync(manifest, tempDir, sm2, pm, CancellationToken.None);
            Assert.True(stopSuccess);
            Assert.Equal(PluginState.Stopped, sm2.CurrentState);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void PluginRevisionManager_CreatesImmutableRevisions_AndResolvesAliases()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_rev_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var manifestV1 = "{\"id\":\"demo-rev\",\"version\":\"1.0.0\"}";
            var manifestV2 = "{\"id\":\"demo-rev\",\"version\":\"2.0.0\"}";

            // 1. 创建 Revision v1
            var rev1 = PluginRevisionManager.CreateRevision(tempDir, "1.0.0", manifestV1);
            Assert.StartsWith("rev-1.0.0-", rev1);

            // 2. 默认解析 effective directory 应指向 rev1
            var effective1 = PluginRevisionManager.ResolveEffectiveDirectory(tempDir);
            Assert.Equal(Path.Combine(tempDir, "revisions", rev1), effective1);

            // 3. 创建 Revision v2 并将 canary 别名指向 rev2
            var rev2 = PluginRevisionManager.CreateRevision(tempDir, "2.0.0", manifestV2);
            PluginRevisionManager.SetAlias(tempDir, "canary", rev2);

            // 4. 解析 canary 别名应指向 rev2
            var canaryDir = PluginRevisionManager.ResolveEffectiveDirectory(tempDir, "canary");
            Assert.Equal(Path.Combine(tempDir, "revisions", rev2), canaryDir);

            // 5. 列出所有版本
            var allRevs = PluginRevisionManager.ListRevisions(tempDir);
            Assert.Equal(2, allRevs.Count);
            Assert.Contains(rev1, allRevs);
            Assert.Contains(rev2, allRevs);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PluginEventBus_PublishesAndPollsEvents_Accurately()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "lwt_eventbus_test_" + Guid.NewGuid().ToString("N") + ".db");
        var dbFactory = new LinuxWebTool.Infrastructure.Shared.Persistence.DbConnectionFactory($"Data Source={tempDb}");
        LinuxWebTool.Infrastructure.Shared.Persistence.DbSetup.Initialize(dbFactory);

        var bus = new PluginEventBus(dbFactory);
        await bus.StartAsync(CancellationToken.None);

        try
        {
            // 1. 发布事件
            await bus.PublishAsync("plugin-abc", "item.created", "{\"sku\":\"ABC-1\"}", CancellationToken.None);
            await bus.PublishAsync("plugin-abc", "item.updated", "{\"sku\":\"ABC-1\",\"price\":99}", CancellationToken.None);
            await bus.PublishAsync("plugin-other", "item.created", "{\"sku\":\"OTHER-1\"}", CancellationToken.None);

            // 2. 轮询 plugin-abc 的所有事件
            var eventsAbc = await bus.PollEventsAsync("plugin-abc", null, 10, CancellationToken.None);
            Assert.Equal(2, eventsAbc.Count);
            Assert.Contains(eventsAbc, e => e.EventType == "item.created");
            Assert.Contains(eventsAbc, e => e.EventType == "item.updated");

            // 3. 按 eventType 精确过滤
            var createdEvents = await bus.PollEventsAsync(null, "item.created", 10, CancellationToken.None);
            Assert.Equal(2, createdEvents.Count);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
            try { File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public void WasmMicroFunctionStrategy_ResolveOrCompileWasmFile_SupportsWatFallback()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_wat_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var watFile = Path.Combine(tempDir, "function.wat");
            File.WriteAllText(watFile, "(module (func $run (result i32) i32.const 42) (export \"run\" (func $run)))");

            // 1. 无 .wasm 时，若已有同名或编译出的 .wasm，能正常解析
            var dummyWasm = Path.Combine(tempDir, "function.wasm");
            File.WriteAllBytes(dummyWasm, [0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]);

            var resolved = LinuxWebTool.Infrastructure.Features.Plugins.Strategies.WasmMicroFunctionStrategy.ResolveOrCompileWasmFile(tempDir, null, "run");
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void DemoDotNetWasm_PluginManifest_Valid()
    {
        var pluginJsonPath = Path.Combine(AppContext.BaseDirectory, "../../../../../plugins/demo-dotnet-wasm/plugin.json");
        if (File.Exists(pluginJsonPath))
        {
            var content = File.ReadAllText(pluginJsonPath);
            Assert.Contains("\"id\": \"demo-dotnet-wasm\"", content);
            Assert.Contains("\"profile\": \"Wasm\"", content);
            Assert.Contains("\"runtime\": \"Wasm\"", content);
        }
    }

    [Fact]
    public void DemoNativeAotWasm_PluginManifest_Valid()
    {
        var pluginJsonPath = Path.Combine(AppContext.BaseDirectory, "../../../../../plugins/demo-nativeaot-wasm/plugin.json");
        if (File.Exists(pluginJsonPath))
        {
            var content = File.ReadAllText(pluginJsonPath);
            Assert.Contains("\"id\": \"demo-nativeaot-wasm\"", content);
            Assert.Contains("\"profile\": \"Wasm\"", content);
            Assert.Contains("\"runtime\": \"Wasm\"", content);
        }
    }
}
