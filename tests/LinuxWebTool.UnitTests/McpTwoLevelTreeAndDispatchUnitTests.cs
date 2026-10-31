using System.Text.Json;
using LinuxWebTool.Application.Features.Security;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Features.Mcp.Engine;
using LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class McpTwoLevelTreeAndDispatchUnitTests : IDisposable
{
    private readonly DbConnectionFactory _factory;
    private readonly string _dbPath;

    public McpTwoLevelTreeAndDispatchUnitTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mcp_test_{Guid.NewGuid():N}.db");
        _factory = new DbConnectionFactory($"Data Source={_dbPath}");
        DbSetup.Initialize(_factory);
    }

    private OperationLogger CreateOpLogger() => new(new OperationLogStore(_factory), NullLogger<OperationLogger>.Instance);

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public void McpCatalogRegistry_ContainsAllTenCategoriesWithValidRiskLevels()
    {
        var catalog = McpCatalogRegistry.GetCatalog();
        Assert.Equal(10, catalog.Count);

        var expectedCategories = new[] { "commands", "workflows", "schedules", "terminal", "remote_hosts", "files", "transcode", "gateway", "services", "workers" };
        var actualKeys = catalog.Select(c => c.Key).ToList();
        foreach (var exp in expectedCategories)
        {
            Assert.Contains(exp, actualKeys);
        }

        var allTools = catalog.SelectMany(c => c.Tools).ToList();
        Assert.True(allTools.Count >= 25);

        // 工具名唯一性断言
        var uniqueNames = allTools.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(allTools.Count, uniqueNames.Count);

        // 风险等级有效性断言
        var validRiskLevels = new HashSet<string> { "Read", "Execute", "Dangerous" };
        foreach (var t in allTools)
        {
            Assert.Contains(t.RiskLevel, validRiskLevels);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.False(string.IsNullOrWhiteSpace(t.ModuleName));
        }
    }

    [Fact]
    public void McpCatalogRegistry_NativeAot_SerializesWithoutInternalCompilerTypeFailure()
    {
        var catalog = McpCatalogRegistry.GetCatalog();
        Assert.IsType<List<McpCategoryCatalog>>(catalog);

        // 验证使用 AppJsonSerializerContext 序列化成功（零 <>z__ReadOnlyArray 异常）
        var json = JsonSerializer.Serialize(catalog, LinuxWebTool.WebHost.Composition.AppJsonSerializerContext.Default.ListMcpCategoryCatalog);
        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains("\"commands\"", json);
        Assert.Contains("\"command_list\"", json);
        Assert.Contains("moduleCode", json, StringComparison.OrdinalIgnoreCase);

        // 验证反序列化恢复
        var restored = JsonSerializer.Deserialize(json, LinuxWebTool.WebHost.Composition.AppJsonSerializerContext.Default.ListMcpCategoryCatalog);
        Assert.NotNull(restored);
        Assert.Equal(10, restored.Count);
        Assert.All(restored, c => Assert.IsType<List<McpToolCatalogItem>>(c.Tools));
    }

    [Fact]
    public async Task McpServerEngine_ToolsList_FiltersByLevel1AndLevel2()
    {
        var cmdStore = new CommandStore(_factory);
        var execStore = new ExecutionStore(_factory);
        var opLogger = CreateOpLogger();

        var cmdProvider = new CommandMcpToolProvider(cmdStore, new DummyExecutionEngine(), opLogger);
        var providers = new List<IMcpToolProvider> { cmdProvider };
        var engine = new McpServerEngine(providers, NullLogger<McpServerEngine>.Instance);

        var httpContext = new DefaultHttpContext();

        // 1. 无 API Key（系统内省）: 返回全部工具
        var listReq = new McpRpcRequest("2.0", "1", "tools/list", null);
        var respAll = await engine.HandleRequestAsync(listReq, callerKey: null, httpContext);
        Assert.Null(respAll.Error);
        Assert.NotNull(respAll.Result);
        var allToolsJson = respAll.Result.Value.GetProperty("tools");
        Assert.Equal(6, allToolsJson.GetArrayLength());

        // 2. 细粒度勾选 API Key: 仅返回 command_run
        var granularKey = new ApiKeyEntity
        {
            Id = "key-1",
            Name = "Granular Key",
            AllowMcp = true,
            AllowTerminal = true,
            AllowedTools = "[\"command_run\"]"
        };
        var respGranular = await engine.HandleRequestAsync(listReq, callerKey: granularKey, httpContext);
        Assert.Null(respGranular.Error);
        var filteredTools = respGranular.Result!.Value.GetProperty("tools");
        Assert.Equal(1, filteredTools.GetArrayLength());
        Assert.Equal("command_run", filteredTools[0].GetProperty("name").GetString());

        // 3. 模块关闭 API Key: 返回 0 个工具
        var disabledKey = new ApiKeyEntity
        {
            Id = "key-2",
            Name = "Disabled Key",
            AllowMcp = true,
            AllowTerminal = false,
            AllowedTools = "[\"command_run\"]"
        };
        var respDisabled = await engine.HandleRequestAsync(listReq, callerKey: disabledKey, httpContext);
        Assert.Null(respDisabled.Error);
        var emptyTools = respDisabled.Result!.Value.GetProperty("tools");
        Assert.Equal(0, emptyTools.GetArrayLength());
    }

    [Fact]
    public async Task McpServerEngine_ToolCall_BlocksUnpermittedToolsAtLevel2()
    {
        var cmdStore = new CommandStore(_factory);
        var opLogger = CreateOpLogger();
        var cmdProvider = new CommandMcpToolProvider(cmdStore, new DummyExecutionEngine(), opLogger);
        var engine = new McpServerEngine([cmdProvider], NullLogger<McpServerEngine>.Instance);
        var httpContext = new DefaultHttpContext();

        // 创建一个仅被授权 command_run 的 Key
        var key = new ApiKeyEntity
        {
            Id = "key-1",
            Name = "Runner Only",
            AllowMcp = true,
            AllowTerminal = true,
            AllowedTools = "[\"command_run\"]"
        };

        // 尝试调用未勾选的 command_delete: 必须被服务端安全拦截
        var callDelete = new McpRpcRequest(
            "2.0",
            "req-1",
            "tools/call",
            JsonDocument.Parse("""{"name":"command_delete","arguments":{"command_id":"00000000-0000-0000-0000-000000000001"}}""").RootElement);

        var resp = await engine.HandleRequestAsync(callDelete, callerKey: key, httpContext);
        Assert.Null(resp.Error);
        Assert.NotNull(resp.Result);
        var isError = resp.Result.Value.GetProperty("isError").GetBoolean();
        Assert.True(isError);
        var content = resp.Result.Value.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("Permission Denied: API Key is not authorized to execute tool 'command_delete'", content);
    }

    [Fact]
    public async Task CommandMcpToolProvider_CRUD_ExecutesSuccessfully()
    {
        var cmdStore = new CommandStore(_factory);
        var opLogger = CreateOpLogger();
        var provider = new CommandMcpToolProvider(cmdStore, new DummyExecutionEngine(), opLogger);
        var context = new McpRequestContext(null, new DefaultHttpContext(), "127.0.0.1", CancellationToken.None);

        // 1. 创建指令
        var createArgs = JsonDocument.Parse("""
        {
            "name": "MCP测试指令",
            "command_text": "echo 'Hello MCP'",
            "description": "MCP单元测试用例",
            "timeout_seconds": 30
        }
        """).RootElement;
        var createResult = await provider.TryExecuteAsync("command_create", createArgs, context);
        Assert.NotNull(createResult);
        Assert.False(createResult.IsError);
        var createdJson = JsonDocument.Parse(createResult.Content).RootElement;
        Assert.True(createdJson.GetProperty("success").GetBoolean());
        var newId = Guid.Parse(createdJson.GetProperty("id").GetString()!);

        // 2. 查询单条指令详情
        var getArgs = JsonDocument.Parse($"{{\"command_id\":\"{newId}\"}}").RootElement;
        var getResult = await provider.TryExecuteAsync("command_get", getArgs, context);
        Assert.NotNull(getResult);
        Assert.False(getResult.IsError);
        var getJson = JsonDocument.Parse(getResult.Content).RootElement;
        Assert.Equal("MCP测试指令", getJson.GetProperty("name").GetString());
        Assert.Equal("echo 'Hello MCP'", getJson.GetProperty("command_text").GetString());

        // 3. 运行指令
        var runArgs = JsonDocument.Parse($"{{\"command_id\":\"{newId}\"}}").RootElement;
        var runResult = await provider.TryExecuteAsync("command_run", runArgs, context);
        Assert.NotNull(runResult);
        Assert.False(runResult.IsError);
        var runJson = JsonDocument.Parse(runResult.Content).RootElement;
        Assert.Equal("Success", runJson.GetProperty("status").GetString());

        // 4. 删除指令
        var delResult = await provider.TryExecuteAsync("command_delete", getArgs, context);
        Assert.NotNull(delResult);
        Assert.False(delResult.IsError);
        var deletedCmd = await cmdStore.GetByIdAsync(newId);
        Assert.Null(deletedCmd);
    }

    [Fact]
    public async Task ScheduleMcpToolProvider_GetAndCreate_ExecutesSuccessfully()
    {
        var schedStore = new ScheduleStore(_factory);
        var schedMgr = new DummyScheduleManager();
        var opLogger = CreateOpLogger();
        var provider = new ScheduleMcpToolProvider(schedStore, schedMgr, opLogger);
        var context = new McpRequestContext(null, new DefaultHttpContext(), "127.0.0.1", CancellationToken.None);

        // 1. 创建定时任务
        var createArgs = JsonDocument.Parse("""
        {
            "name": "MCP定时清理任务",
            "cron": "0 0 * * *",
            "target_biz_type": 0,
            "command_id": "00000000-0000-0000-0000-000000000002"
        }
        """).RootElement;
        var createResult = await provider.TryExecuteAsync("schedule_create", createArgs, context);
        Assert.NotNull(createResult);
        Assert.False(createResult.IsError);
        var createdJson = JsonDocument.Parse(createResult.Content).RootElement;
        Assert.True(createdJson.GetProperty("success").GetBoolean());
        var schedId = Guid.Parse(createdJson.GetProperty("id").GetString()!);

        // 2. 查询定时任务明细
        var getArgs = JsonDocument.Parse($"{{\"schedule_id\":\"{schedId}\"}}").RootElement;
        var getResult = await provider.TryExecuteAsync("schedule_get", getArgs, context);
        Assert.NotNull(getResult);
        Assert.False(getResult.IsError);
        var getJson = JsonDocument.Parse(getResult.Content).RootElement;
        Assert.Equal("MCP定时清理任务", getJson.GetProperty("name").GetString());
        Assert.Equal("0 0 * * *", getJson.GetProperty("cron").GetString());
        Assert.True(getJson.GetProperty("enabled").GetBoolean());
    }

    private sealed class DummyScheduleManager : IScheduleManager
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime?> SyncAsync(ScheduleTask task) => Task.FromResult<DateTime?>(DateTime.UtcNow.AddHours(1));
        public Task SyncAllAsync(IReadOnlyList<ScheduleTask> tasks) => Task.CompletedTask;
        public Task RemoveAsync(Guid taskId) => Task.CompletedTask;
        public Task TriggerNowAsync(ScheduleTask task) => Task.CompletedTask;
    }

    private sealed class DummyExecutionEngine : IUnifiedExecutionEngine
    {
        public Task<StandardExecutionResult> ExecuteCommandAsync(CommandExecutionContext context, string? commandName = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new StandardExecutionResult(
                ExecutionStatus.Success,
                0,
                "dummy output",
                string.Empty,
                15,
                false));
        }

        public Task<BatchExecutionResponse> ExecuteBatchAsync(BatchExecutionRequest request, string triggerBy, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BatchExecutionResponse(
                "batch-1",
                new List<ExecuteResult>(),
                ExecutionStatus.Success,
                20));
        }

        public Task<BatchExecutionResponse> ExecuteWorkflowAsync(Guid workflowId, string triggerBy, ExecutionTarget? targetOverride = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BatchExecutionResponse(
                "batch-wf-1",
                new List<ExecuteResult>(),
                ExecutionStatus.Success,
                35));
        }
    }
}