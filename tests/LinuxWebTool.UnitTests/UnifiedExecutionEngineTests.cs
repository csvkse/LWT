using System.Text.Json;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Security;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Features.Workflows.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class UnifiedExecutionEngineTests : IDisposable
{
    private sealed class DummyHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LinuxWebTool.UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestShellExecutor : IShellExecutor
    {
        public Func<ShellRequest, ShellResult>? Handler { get; set; }

        public Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
        {
            if (Handler != null)
            {
                return Task.FromResult(Handler(request));
            }

            var now = DateTime.Now;
            return Task.FromResult(new ShellResult
            {
                ExitCode = 0,
                StandardOutput = "OK",
                ErrorOutput = string.Empty,
                DurationMs = 10,
                StartTime = now,
                EndTime = now.AddMilliseconds(10)
            });
        }
    }

    private readonly string _tempDir;
    private readonly DbConnectionFactory _dbFactory;
    private readonly CommandStore _commandStore;
    private readonly ScheduleStore _scheduleStore;
    private readonly ExecutionStore _executionStore;
    private readonly WorkflowStore _workflowStore;
    private readonly RemoteHostStore _remoteHostStore;
    private readonly HostCircuitBreaker _circuitBreaker;
    private readonly TestShellExecutor _shellExecutor;
    private readonly UnifiedExecutionEngine _engine;

    public UnifiedExecutionEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lwt_engine_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.db");
        _dbFactory = DbSetup.CreateFactory($"Data Source={dbPath}");
        DbSetup.Initialize(_dbFactory);

        _commandStore = new CommandStore(_dbFactory);
        _scheduleStore = new ScheduleStore(_dbFactory);
        _executionStore = new ExecutionStore(_dbFactory);
        _workflowStore = new WorkflowStore(_dbFactory);
        _remoteHostStore = new RemoteHostStore(_dbFactory);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:MasterKey"] = Convert.ToBase64String(new byte[32])
        }).Build();

        var env = new DummyHostEnvironment(_tempDir);
        var dataPaths = new DataPaths(config, env);
        var protector = new HostCredentialProtector(config, dataPaths, NullLogger<HostCredentialProtector>.Instance);
        var clientFactory = new RemoteHostClientFactory(protector, NullLoggerFactory.Instance);
        _circuitBreaker = new HostCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));

        _shellExecutor = new TestShellExecutor();
        var localStrategy = new LocalCommandExecutionStrategy(_shellExecutor, NullLogger<LocalCommandExecutionStrategy>.Instance);
        var remoteStrategy = new RemoteSshCommandExecutionStrategy(_remoteHostStore, clientFactory, _circuitBreaker, NullLogger<RemoteSshCommandExecutionStrategy>.Instance);
        var resolver = new ExecutionStrategyResolver(localStrategy, remoteStrategy);

        var shellActivity = new ShellCommandActivity(_commandStore, resolver, NullLogger<ShellCommandActivity>.Instance);
        var httpActivity = new HttpWebhookActivity(NullLogger<HttpWebhookActivity>.Instance);
        var registry = new ActivityRegistry([shellActivity, httpActivity]);

        _engine = new UnifiedExecutionEngine(
            resolver,
            _executionStore,
            _commandStore,
            _remoteHostStore,
            _workflowStore,
            registry,
            NullLogger<UnifiedExecutionEngine>.Instance
        );
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task BatchExecute_WhenTargetHostDoesNotExist_ShouldAutoSkipWithoutThrowing()
    {
        var nonExistentHostId = Guid.NewGuid().ToString();

        var batchReq = new BatchExecutionRequest
        {
            CommandIds = [],
            Commands =
            [
                new BatchExecutionItem(CommandId: null, CommandText: "echo 1", ScriptType: 0, Arguments: null, TimeoutSeconds: 30, TargetHostId: null, Name: "Step 1"),
                new BatchExecutionItem(CommandId: null, CommandText: "echo 2", ScriptType: 0, Arguments: null, TimeoutSeconds: 30, TargetHostId: null, Name: "Step 2")
            ],
            TargetHostId = nonExistentHostId,
            RespectIndividualHost = false,
            ExecutionMode = ExecutionMode.Sequential,
            FailurePolicy = FailurePolicy.FailFast
        };

        var response = await _engine.ExecuteBatchAsync(batchReq, triggerBy: "unit-test");

        Assert.NotNull(response);
        Assert.Equal(ExecutionStatus.Skipped, response.OverallStatus);
        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, r =>
        {
            Assert.Equal(ExecutionStatus.Skipped, r.Status);
            Assert.Contains("[目标缺失]", r.SkipReason);
        });

        // Verify records in DB
        var records = (await _executionStore.GetByBatchIdAsync(response.BatchId)).ToList();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal((int)ExecutionStatus.Skipped, r.Status));
    }

    [Fact]
    public async Task BatchExecute_FailFastPolicy_ShouldSkipRemainingStepsWhenFirstFails()
    {
        _shellExecutor.Handler = req =>
        {
            var now = DateTime.Now;
            if (req.CommandText == "fail_cmd")
            {
                return new ShellResult
                {
                    ExitCode = 1,
                    StandardOutput = string.Empty,
                    ErrorOutput = "command failed",
                    DurationMs = 10,
                    StartTime = now,
                    EndTime = now.AddMilliseconds(10)
                };
            }
            return new ShellResult
            {
                ExitCode = 0,
                StandardOutput = "success",
                ErrorOutput = string.Empty,
                DurationMs = 10,
                StartTime = now,
                EndTime = now.AddMilliseconds(10)
            };
        };

        var batchReq = new BatchExecutionRequest
        {
            CommandIds = [],
            Commands =
            [
                new BatchExecutionItem(CommandId: null, CommandText: "fail_cmd", ScriptType: 0, Arguments: null, TimeoutSeconds: 30, TargetHostId: null, Name: "Failing Step"),
                new BatchExecutionItem(CommandId: null, CommandText: "echo after", ScriptType: 0, Arguments: null, TimeoutSeconds: 30, TargetHostId: null, Name: "Subsequent Step")
            ],
            TargetHostId = null, // local
            RespectIndividualHost = true,
            ExecutionMode = ExecutionMode.Sequential,
            FailurePolicy = FailurePolicy.FailFast
        };

        var response = await _engine.ExecuteBatchAsync(batchReq, triggerBy: "unit-test");

        Assert.Equal(ExecutionStatus.Failure, response.OverallStatus);
        Assert.Equal(2, response.Results.Count);

        // Step 1 was attempted and failed
        Assert.Equal(ExecutionStatus.Failure, response.Results[0].Status);
        Assert.Equal(1, response.Results[0].ExitCode);

        // Step 2 was fast-failed / skipped
        Assert.Equal(ExecutionStatus.Skipped, response.Results[1].Status);
        Assert.Contains("前序命令执行失败", response.Results[1].SkipReason);
    }

    [Fact]
    public async Task BatchExecute_ContinuePolicy_ShouldExecuteRemainingStepsEvenWhenFirstFails()
    {
        _shellExecutor.Handler = req =>
        {
            var now = DateTime.Now;
            if (req.CommandText == "fail_cmd")
            {
                return new ShellResult
                {
                    ExitCode = 1,
                    StandardOutput = string.Empty,
                    ErrorOutput = "command failed",
                    DurationMs = 10,
                    StartTime = now,
                    EndTime = now.AddMilliseconds(10)
                };
            }
            return new ShellResult
            {
                ExitCode = 0,
                StandardOutput = "success",
                ErrorOutput = string.Empty,
                DurationMs = 10,
                StartTime = now,
                EndTime = now.AddMilliseconds(10)
            };
        };

        var batchReq = new BatchExecutionRequest
        {
            CommandIds = [],
            Commands =
            [
                new BatchExecutionItem(CommandId: null, CommandText: "fail_cmd", ScriptType: 0, Arguments: null, TimeoutSeconds: 30, TargetHostId: null, Name: "Failing Step"),
                new BatchExecutionItem(CommandId: null, CommandText: "echo after", ScriptType: 0, Arguments: null, TimeoutSeconds: 30, TargetHostId: null, Name: "Subsequent Step")
            ],
            TargetHostId = null, // local
            RespectIndividualHost = true,
            ExecutionMode = ExecutionMode.Sequential,
            FailurePolicy = FailurePolicy.Continue
        };

        var response = await _engine.ExecuteBatchAsync(batchReq, triggerBy: "unit-test");

        Assert.Equal(ExecutionStatus.Failure, response.OverallStatus);
        Assert.Equal(2, response.Results.Count);

        // Step 1 was attempted and failed
        Assert.Equal(ExecutionStatus.Failure, response.Results[0].Status);

        // Step 2 was executed and succeeded
        Assert.Equal(ExecutionStatus.Success, response.Results[1].Status);
        Assert.Equal("success", response.Results[1].StandardOutput);
    }

    [Fact]
    public async Task WorkflowExecute_MultiStage_WithFailFast_ShouldHaltAndRecordSkipped()
    {
        _shellExecutor.Handler = req =>
        {
            var now = DateTime.Now;
            if (req.CommandText == "stage1_fail")
            {
                return new ShellResult
                {
                    ExitCode = 2,
                    StandardOutput = string.Empty,
                    ErrorOutput = "stage 1 error",
                    DurationMs = 10,
                    StartTime = now,
                    EndTime = now.AddMilliseconds(10)
                };
            }
            return new ShellResult
            {
                ExitCode = 0,
                StandardOutput = "stage 2 ok",
                ErrorOutput = string.Empty,
                DurationMs = 10,
                StartTime = now,
                EndTime = now.AddMilliseconds(10)
            };
        };

        var definition = new WorkflowDefinition(
            Version: "1.0",
            Name: "Test Pipeline",
            DefaultFailurePolicy: "FailFast",
            FailurePolicy: FailurePolicy.FailFast,
            Stages:
            [
                new WorkflowStage(
                    StageOrder: 1,
                    Name: "Stage 1",
                    Mode: "Sequential",
                    ExecutionMode: ExecutionMode.Sequential,
                    MaxConcurrency: 1,
                    FailurePolicy: FailurePolicy.FailFast,
                    Steps:
                    [
                        new WorkflowStep(Name: "Step 1.1", StepType: "InlineScript", CommandText: "stage1_fail")
                    ]
                ),
                new WorkflowStage(
                    StageOrder: 2,
                    Name: "Stage 2",
                    Mode: "Sequential",
                    ExecutionMode: ExecutionMode.Sequential,
                    MaxConcurrency: 1,
                    FailurePolicy: FailurePolicy.FailFast,
                    Steps:
                    [
                        new WorkflowStep(Name: "Step 2.1", StepType: "InlineScript", CommandText: "stage2_ok")
                    ]
                )
            ]
        );

        var workflowId = Guid.NewGuid();
        var entity = new WorkflowEntity
        {
            Id = workflowId,
            Name = "Pipeline 1",
            ExecutionMode = (int)ExecutionMode.Sequential,
            FailurePolicy = (int)FailurePolicy.FailFast,
            DefinitionJson = JsonSerializer.Serialize(definition, WorkflowJsonContext.Default.WorkflowDefinition),
            Enabled = true,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        };
        await _workflowStore.InsertAsync(entity);

        var result = await _engine.ExecuteWorkflowAsync(workflowId, triggerBy: "unit-test");

        Assert.Equal(ExecutionStatus.Failure, result.OverallStatus);
        Assert.Equal(2, result.Results.Count);

        // Step 1 failed
        Assert.Equal(ExecutionStatus.Failure, result.Results[0].Status);

        // Step 2 skipped due to previous stage failure
        Assert.Equal(ExecutionStatus.Skipped, result.Results[1].Status);
        Assert.Contains("熔断跳过", result.Results[1].SkipReason);

        // Database records should match
        var dbRecords = (await _executionStore.GetByBatchIdAsync(result.BatchId)).ToList();
        Assert.Equal(2, dbRecords.Count);
    }
}
