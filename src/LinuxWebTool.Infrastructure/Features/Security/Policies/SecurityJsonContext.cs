using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Security.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Security.Policies;

/// <summary>
/// AOT 友好的安全策略 JSON 序列化上下文
/// </summary>
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(SecurityPolicyRule))]
[JsonSerializable(typeof(List<SecurityPolicyRule>))]
[JsonSerializable(typeof(IReadOnlyList<SecurityPolicyRule>))]
[JsonSerializable(typeof(SecurityPolicyTemplateItemResponse))]
[JsonSerializable(typeof(List<SecurityPolicyTemplateItemResponse>))]
[JsonSerializable(typeof(IReadOnlyList<SecurityPolicyTemplateItemResponse>))]
[JsonSerializable(typeof(CreateSecurityPolicyTemplateRequest))]
[JsonSerializable(typeof(UpdateSecurityPolicyTemplateRequest))]
[JsonSerializable(typeof(SimulatePolicyRequest))]
[JsonSerializable(typeof(SimulatePolicyResponse))]
[JsonSerializable(typeof(ModuleCapabilityItem))]
[JsonSerializable(typeof(List<ModuleCapabilityItem>))]
[JsonSerializable(typeof(IReadOnlyList<ModuleCapabilityItem>))]
public partial class SecurityJsonContext : JsonSerializerContext
{
}
