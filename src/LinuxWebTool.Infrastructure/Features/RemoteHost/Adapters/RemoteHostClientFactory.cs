using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Security;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>多协议远程主机适配器工厂</summary>
public sealed class RemoteHostClientFactory
{
    private readonly HostCredentialProtector _protector;
    private readonly ILoggerFactory _loggerFactory;

    public RemoteHostClientFactory(HostCredentialProtector protector, ILoggerFactory loggerFactory)
    {
        _protector = protector;
        _loggerFactory = loggerFactory;
    }

    public IRemoteHostAdapter CreateAdapter(RemoteHostEntity entity)
    {
        var rawCredential = _protector.Decrypt(entity.EncryptedCredential, entity.CredentialNonce, entity.CredentialTag);
        string? credential = rawCredential;
        string? passphrase = null;

        // 如果凭据中用约定分隔符带 passphrase (如 json 或自定义分割)，此处支持解析
        if (rawCredential != null && rawCredential.Contains("\n---PASSPHRASE---\n", StringComparison.Ordinal))
        {
            var parts = rawCredential.Split("\n---PASSPHRASE---\n", 2, StringSplitOptions.None);
            credential = parts[0];
            passphrase = parts[1];
        }

        var logger = _loggerFactory.CreateLogger<SshHostAdapter>();

        return entity.Protocol?.ToLowerInvariant() switch
        {
            "ssh" or null or "" => new SshHostAdapter(
                hostId: entity.Id,
                host: entity.Host,
                port: entity.Port,
                username: entity.Username,
                authType: entity.AuthType,
                credential: credential,
                passphrase: passphrase,
                logger: logger
            ),
            _ => throw new NotSupportedException($"当前尚未支持 '{entity.Protocol}' 协议")
        };
    }

    public IRemoteHostAdapter CreateTransientAdapter(
        string host,
        int port,
        string protocol,
        string authType,
        string username,
        string? credential,
        string? passphrase = null)
    {
        var logger = _loggerFactory.CreateLogger<SshHostAdapter>();
        return protocol.ToLowerInvariant() switch
        {
            "ssh" or null or "" => new SshHostAdapter(
                hostId: "transient",
                host: host,
                port: port,
                username: username,
                authType: authType,
                credential: credential,
                passphrase: passphrase,
                logger: logger
            ),
            _ => throw new NotSupportedException($"当前尚未支持 '{protocol}' 协议")
        };
    }
}
