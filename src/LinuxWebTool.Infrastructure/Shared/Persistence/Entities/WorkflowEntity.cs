namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>工作流编排定义实体。</summary>
public class WorkflowEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int ExecutionMode { get; set; }

    public int FailurePolicy { get; set; }

    public string? DefaultTargetHostId { get; set; }

    /// <summary>工作流 DSL 完整定义 JSON。</summary>
    public string DefinitionJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    public DateTime? LastRunTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
