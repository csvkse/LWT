using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.McpBridge.Contracts;

namespace LinuxWebTool.Infrastructure.Features.McpBridge.Adapters;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SaveTunnelRequest))]
[JsonSerializable(typeof(SaveServerRequest))]
[JsonSerializable(typeof(SaveSelectionRequest))]
[JsonSerializable(typeof(ApplyConfigRequest))]
[JsonSerializable(typeof(McpBridgeStatusDto))]
[JsonSerializable(typeof(McpBridgeMutationResult))]
[JsonSerializable(typeof(McpBridgeOperationResult))]
[JsonSerializable(typeof(McpBridgeServersResult))]
[JsonSerializable(typeof(McpBridgeProbeResult))]
[JsonSerializable(typeof(McpBridgeDiscoveryResult))]
[JsonSerializable(typeof(McpBridgeToolsPage))]
[JsonSerializable(typeof(McpBridgeApplyResult))]
[JsonSerializable(typeof(McpBridgeAuditPage))]
[JsonSerializable(typeof(McpServerDto))]
[JsonSerializable(typeof(McpToolDto))]
[JsonSerializable(typeof(McpBridgeAuditItem))]
public sealed partial class McpBridgeJsonContext : JsonSerializerContext
{
}
