using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Commands.Contracts;

namespace LinuxWebTool.Contracts.Features.Workflows.Contracts;

/// <summary>工作流单个步骤定义。</summary>
public sealed record WorkflowStep(
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("stepType")] string StepType = "CommandRef", // CommandRef | InlineScript | Http | Notification
    [property: JsonPropertyName("activityType")] string? ActivityType = null,
    [property: JsonPropertyName("commandId")] Guid? CommandId = null,
    [property: JsonPropertyName("commandText")] string? CommandText = null,
    [property: JsonPropertyName("inlineScript")] string? InlineScript = null,
    [property: JsonPropertyName("scriptType")] int ScriptType = 0,
    [property: JsonPropertyName("arguments")] string? Arguments = null,
    [property: JsonPropertyName("target")] ExecutionTarget? Target = null,
    [property: JsonPropertyName("targetHostId")] string? TargetHostId = null,
    [property: JsonPropertyName("timeoutSeconds")] int? TimeoutSeconds = 30,
    [property: JsonPropertyName("failurePolicy")] string? FailurePolicy = null,
    [property: JsonPropertyName("parameters")] Dictionary<string, string>? Parameters = null);

/// <summary>工作流阶段定义。</summary>
public sealed record WorkflowStage(
    [property: JsonPropertyName("stageOrder")] int StageOrder = 1,
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("mode")] string Mode = "Sequential",
    [property: JsonPropertyName("executionMode")] ExecutionMode ExecutionMode = ExecutionMode.Sequential,
    [property: JsonPropertyName("maxConcurrency")] int MaxConcurrency = 5,
    [property: JsonPropertyName("failurePolicy")] FailurePolicy FailurePolicy = FailurePolicy.FailFast,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorkflowStep>? Steps = null);

/// <summary>工作流定义模型。</summary>
public sealed record WorkflowDefinition(
    [property: JsonPropertyName("version")] string Version = "1.0",
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("defaultFailurePolicy")] string DefaultFailurePolicy = "FailFast",
    [property: JsonPropertyName("failurePolicy")] FailurePolicy FailurePolicy = FailurePolicy.FailFast,
    [property: JsonPropertyName("stages")] IReadOnlyList<WorkflowStage>? Stages = null);

/// <summary>保存工作流请求契约。</summary>
public sealed record SaveWorkflowRequest(
    string Name,
    string? Description,
    ExecutionMode ExecutionMode,
    FailurePolicy FailurePolicy,
    string? DefaultTargetHostId,
    string DefinitionJson,
    bool Enabled = true,
    Guid? GroupId = null,
    bool IsPinned = false,
    int SortOrder = 0);

/// <summary>工作流列表项响应契约。</summary>
public sealed record WorkflowItemResponse(
    Guid Id,
    string Name,
    string? Description,
    int ExecutionMode,
    int FailurePolicy,
    string? DefaultTargetHostId,
    string DefinitionJson,
    bool Enabled,
    Guid? GroupId,
    string? GroupName,
    bool IsPinned,
    int SortOrder,
    DateTime? LastRunTime,
    DateTime CreateTime,
    DateTime UpdateTime);

/// <summary>立即运行工作流请求契约。</summary>
public sealed record ToggleWorkflowResponse(bool Enabled);

public sealed record RunWorkflowRequest(
    string? TargetHostId = null,
    string? Arguments = null);
