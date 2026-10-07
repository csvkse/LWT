namespace LinuxWebTool.Contracts.Features.Security.Contracts;

/// <summary>创建 API Key 请求契约</summary>
public sealed record CreateApiKeyRequest(
    string Name,
    bool AllowApi = true,
    bool AllowMcp = true,
    bool AllowTerminal = false,
    bool AllowSchedules = false,
    bool AllowFiles = false,
    bool AllowTranscode = false,
    bool AllowGateway = false,
    DateTime? ExpiresAt = null);

/// <summary>更新 API Key 请求契约</summary>
public sealed record UpdateApiKeyRequest(
    string Name,
    bool IsEnabled,
    bool AllowApi,
    bool AllowMcp,
    bool AllowTerminal,
    bool AllowSchedules,
    bool AllowFiles,
    bool AllowTranscode,
    bool AllowGateway,
    DateTime? ExpiresAt = null);

/// <summary>API Key 创建响应契约（包含一次性明文 Key）</summary>
public sealed record ApiKeyCreatedResponse(
    string Id,
    string Name,
    string RawKey,
    string KeyPrefix,
    bool AllowApi,
    bool AllowMcp,
    bool AllowTerminal,
    bool AllowSchedules,
    bool AllowFiles,
    bool AllowTranscode,
    bool AllowGateway,
    DateTime CreatedAt,
    DateTime? ExpiresAt);

/// <summary>API Key 列表项响应契约（脱敏）</summary>
public sealed record ApiKeyItemResponse(
    string Id,
    string Name,
    string KeyPrefix,
    bool IsEnabled,
    bool AllowApi,
    bool AllowMcp,
    bool AllowTerminal,
    bool AllowSchedules,
    bool AllowFiles,
    bool AllowTranscode,
    bool AllowGateway,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt);
