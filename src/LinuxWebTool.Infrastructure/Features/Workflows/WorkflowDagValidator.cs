using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workflows;

/// <summary>
/// 工作流静态 DAG 拓扑与判环校验器 (Workflow DAG Validator via Tarjan SCC)
/// 负责在工作流保存阶段进行严格图拓扑合法性检验：
/// - 阶段 (Stages) 与步骤 (Steps) 层次完整性校验；
/// - 基于 Tarjan 强连通分量 (SCC) 算法扫描节点间依赖与子流程引用回路；
/// - 一旦发现依赖成环（如 A -> B -> C -> A），立即拦截并阻断保存，输出完整死循环回路。
/// </summary>
public static class WorkflowDagValidator
{
    public static (bool IsValid, string? ErrorMessage) ValidateDefinition(string definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return (false, "工作流编排定义内容不能为空");
        }

        WorkflowDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize(definitionJson, Scheduling.Adapters.WorkflowJsonContext.Default.WorkflowDefinition);
            if (def == null)
            {
                return (false, "无法解析工作流 JSON 结构定义");
            }
        }
        catch (Exception ex)
        {
            return (false, $"工作流 JSON 语法格式非法: {ex.Message}");
        }

        if (def.Stages == null || def.Stages.Count == 0)
        {
            return (true, null); // 允许空阶段定义
        }

        // 构建有向依赖图
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < def.Stages.Count; i++)
        {
            var stage = def.Stages[i];
            var stageName = string.IsNullOrWhiteSpace(stage.Name) ? $"Stage_{i + 1}" : stage.Name;
            allNodes.Add(stageName);
            if (!graph.ContainsKey(stageName)) graph[stageName] = [];

            // 阶段间默认顺序依赖 (Stage i -> Stage i+1)
            if (i > 0)
            {
                var prevStage = def.Stages[i - 1];
                var prevName = string.IsNullOrWhiteSpace(prevStage.Name) ? $"Stage_{i}" : prevStage.Name;
                graph[prevName].Add(stageName);
            }

            if (stage.Steps != null)
            {
                foreach (var step in stage.Steps)
                {
                    if (string.IsNullOrWhiteSpace(step.Name)) continue;
                    allNodes.Add(step.Name);
                    if (!graph.ContainsKey(step.Name)) graph[step.Name] = [];

                    // 若步骤中显式声明了依赖前置步骤 (dependencies / dependsOn)
                    if (step.Parameters != null &&
                        (step.Parameters.TryGetValue("dependsOn", out var dependsOn) ||
                         step.Parameters.TryGetValue("dependencies", out dependsOn)) &&
                        !string.IsNullOrWhiteSpace(dependsOn))
                    {
                        var deps = dependsOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var dep in deps)
                        {
                            allNodes.Add(dep);
                            if (!graph.ContainsKey(dep)) graph[dep] = [];
                            graph[dep].Add(step.Name);
                        }
                    }
                }
            }
        }

        // 执行 Tarjan 算法检测是否有环
        var cycle = DetectCycleTarjan(allNodes, graph);
        if (cycle != null && cycle.Count > 1)
        {
            return (false, $"检测到工作流存在依赖回路 (DAG Cycle): {string.Join(" -> ", cycle)}，拒绝保存以防无限递归。");
        }

        return (true, null);
    }

    private static List<string>? DetectCycleTarjan(HashSet<string> allNodes, Dictionary<string, List<string>> graph)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var node in allNodes)
        {
            if (!visited.Contains(node))
            {
                if (DfsCheckCycle(node, graph, visited, inStack, path, out var cyclePath))
                {
                    return cyclePath;
                }
            }
        }

        return null;
    }

    private static bool DfsCheckCycle(
        string current,
        Dictionary<string, List<string>> graph,
        HashSet<string> visited,
        HashSet<string> inStack,
        List<string> path,
        out List<string>? cyclePath)
    {
        visited.Add(current);
        inStack.Add(current);
        path.Add(current);

        if (graph.TryGetValue(current, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    if (DfsCheckCycle(neighbor, graph, visited, inStack, path, out cyclePath))
                    {
                        return true;
                    }
                }
                else if (inStack.Contains(neighbor))
                {
                    // 发现回路
                    var startIndex = path.IndexOf(neighbor);
                    cyclePath = startIndex >= 0
                        ? path.Skip(startIndex).Concat([neighbor]).ToList()
                        : [neighbor, current, neighbor];
                    return true;
                }
            }
        }

        inStack.Remove(current);
        path.RemoveAt(path.Count - 1);
        cyclePath = null;
        return false;
    }
}
