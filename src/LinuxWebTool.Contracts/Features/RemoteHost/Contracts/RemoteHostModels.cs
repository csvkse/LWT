using System.Text.Json.Serialization;

namespace LinuxWebTool.Contracts.Features.RemoteHost.Contracts;

/// <summary>创建远程主机请求契约</summary>
public sealed record CreateRemoteHostRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port = 22,
    [property: JsonPropertyName("protocol")] string Protocol = "ssh",
    [property: JsonPropertyName("authType")] string AuthType = "Password",
    [property: JsonPropertyName("username")] string Username = "root",
    [property: JsonPropertyName("credential")] string? Credential = null,
    [property: JsonPropertyName("passphrase")] string? Passphrase = null,
    [property: JsonPropertyName("hostKeyFingerprint")] string? HostKeyFingerprint = null,
    [property: JsonPropertyName("tags")] string? Tags = null,
    [property: JsonPropertyName("remark")] string? Remark = null,
    [property: JsonPropertyName("sortOrder")] int SortOrder = 0);

/// <summary>更新远程主机请求契约</summary>
public sealed record UpdateRemoteHostRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port = 22,
    [property: JsonPropertyName("protocol")] string Protocol = "ssh",
    [property: JsonPropertyName("authType")] string AuthType = "Password",
    [property: JsonPropertyName("username")] string Username = "root",
    [property: JsonPropertyName("credential")] string? Credential = null,
    [property: JsonPropertyName("passphrase")] string? Passphrase = null,
    [property: JsonPropertyName("hostKeyFingerprint")] string? HostKeyFingerprint = null,
    [property: JsonPropertyName("tags")] string? Tags = null,
    [property: JsonPropertyName("remark")] string? Remark = null,
    [property: JsonPropertyName("sortOrder")] int SortOrder = 0);

/// <summary>远程主机列表项响应契约（脱敏，凭据不外露）</summary>
public sealed record RemoteHostItemResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("authType")] string AuthType,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("hasCredential")] bool HasCredential,
    [property: JsonPropertyName("hostKeyFingerprint")] string? HostKeyFingerprint,
    [property: JsonPropertyName("tags")] string? Tags,
    [property: JsonPropertyName("remark")] string? Remark,
    [property: JsonPropertyName("sortOrder")] int SortOrder,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("lastConnectedAt")] DateTime? LastConnectedAt,
    [property: JsonPropertyName("isOnline")] bool? IsOnline = null,
    [property: JsonPropertyName("latencyMs")] int? LatencyMs = null);

/// <summary>测试远程连接请求契约</summary>
public sealed record TestRemoteHostRequest(
    [property: JsonPropertyName("host")] string? Host = null,
    [property: JsonPropertyName("port")] int Port = 22,
    [property: JsonPropertyName("protocol")] string Protocol = "ssh",
    [property: JsonPropertyName("authType")] string AuthType = "Password",
    [property: JsonPropertyName("username")] string Username = "root",
    [property: JsonPropertyName("credential")] string? Credential = null,
    [property: JsonPropertyName("passphrase")] string? Passphrase = null,
    [property: JsonPropertyName("id")] string? Id = null);

/// <summary>测试远程连接响应契约</summary>
public sealed record TestRemoteHostResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("latencyMs")] int? LatencyMs = null,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint = null,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null);

/// <summary>探测主机公钥指纹请求契约</summary>
public sealed record ProbeRemoteHostKeyRequest(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port = 22);

/// <summary>探测主机公钥指纹响应契约</summary>
public sealed record ProbeRemoteHostKeyResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint = null,
    [property: JsonPropertyName("keyType")] string? KeyType = null,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null);
