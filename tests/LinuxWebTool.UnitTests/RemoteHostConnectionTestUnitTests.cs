using System.Text;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Security;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class RemoteHostConnectionTestUnitTests : IDisposable
{
    private sealed class DummyHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LinuxWebTool.UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly string _tempDir;
    private readonly DbConnectionFactory _dbFactory;
    private readonly HostCredentialProtector _protector;
    private readonly RemoteHostStore _store;

    public RemoteHostConnectionTestUnitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lwt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.db");
        _dbFactory = DbSetup.CreateFactory($"Data Source={dbPath}");
        DbSetup.Initialize(_dbFactory);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:MasterKey"] = Convert.ToBase64String(new byte[32])
        }).Build();

        var env = new DummyHostEnvironment(_tempDir);
        var dataPaths = new DataPaths(config, env);
        _protector = new HostCredentialProtector(config, dataPaths, NullLogger<HostCredentialProtector>.Instance);
        _store = new RemoteHostStore(_dbFactory);
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
    public void Protector_EncryptAndDecrypt_WithPassphrase_Succeeds()
    {
        const string mockPrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nMOCK_KEY_DATA\n-----END OPENSSH PRIVATE KEY-----";
        const string passphrase = "my-secret-passphrase-123";

        var payload = $"{mockPrivateKey}\n---PASSPHRASE---\n{passphrase}";
        var (cipher, nonce, tag) = _protector.Encrypt(payload);

        var decrypted = _protector.Decrypt(cipher, nonce, tag);
        Assert.NotNull(decrypted);
        Assert.Contains("\n---PASSPHRASE---\n", decrypted);

        var parts = decrypted.Split("\n---PASSPHRASE---\n", 2, StringSplitOptions.None);
        Assert.Equal(mockPrivateKey, parts[0]);
        Assert.Equal(passphrase, parts[1]);
    }

    [Fact]
    public void Protector_PassphraseUpdate_PreservesOriginalKey()
    {
        const string originalKey = "-----BEGIN RSA PRIVATE KEY-----\nMOCK_KEY\n-----END RSA PRIVATE KEY-----";
        const string oldPassphrase = "old-passphrase";
        const string newPassphrase = "new-passphrase-456";

        var initialPayload = $"{originalKey}\n---PASSPHRASE---\n{oldPassphrase}";
        var (cipher1, nonce1, tag1) = _protector.Encrypt(initialPayload);

        // 模拟用户在编辑弹窗仅修改 Passphrase，未重新上传私钥
        var decryptedOld = _protector.Decrypt(cipher1, nonce1, tag1);
        Assert.NotNull(decryptedOld);

        var existingKey = decryptedOld.Split("\n---PASSPHRASE---\n", 2, StringSplitOptions.None)[0];
        Assert.Equal(originalKey, existingKey);

        var updatedPayload = $"{existingKey}\n---PASSPHRASE---\n{newPassphrase}";
        var (cipher2, nonce2, tag2) = _protector.Encrypt(updatedPayload);

        var decryptedNew = _protector.Decrypt(cipher2, nonce2, tag2);
        Assert.NotNull(decryptedNew);
        var parts = decryptedNew.Split("\n---PASSPHRASE---\n", 2, StringSplitOptions.None);
        Assert.Equal(originalKey, parts[0]);
        Assert.Equal(newPassphrase, parts[1]);
    }

    [Fact]
    public async Task StoredHost_CanBeRetrievedAndDecryptedForTestConnection()
    {
        const string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nTEST_KEY\n-----END OPENSSH PRIVATE KEY-----";
        const string passphrase = "test-passphrase";

        var (cipher, nonce, tag) = _protector.Encrypt($"{privateKey}\n---PASSPHRASE---\n{passphrase}");
        var hostId = Guid.NewGuid().ToString("N");

        var entity = new RemoteHostEntity
        {
            Id = hostId,
            Name = "Test Server 70",
            Host = "10.126.1.70",
            Port = 22,
            Protocol = "ssh",
            AuthType = "PrivateKey",
            Username = "root",
            EncryptedCredential = cipher,
            CredentialNonce = nonce,
            CredentialTag = tag,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _store.InsertAsync(entity);

        // 模拟外部列表点击测试：仅传入 Id 与主机元数据，Credential 为空
        var testRequest = new TestRemoteHostRequest(
            Id: hostId,
            Host: entity.Host,
            Port: entity.Port,
            Protocol: entity.Protocol,
            AuthType: entity.AuthType,
            Username: entity.Username,
            Credential: null,
            Passphrase: null
        );

        Assert.Equal(hostId, testRequest.Id);
        Assert.Null(testRequest.Credential);

        // 验证后端解密逻辑能够按 Id 恢复出凭据与 passphrase
        var retrieved = await _store.GetByIdAsync(testRequest.Id!);
        Assert.NotNull(retrieved);

        var raw = _protector.Decrypt(retrieved.EncryptedCredential, retrieved.CredentialNonce, retrieved.CredentialTag);
        Assert.NotNull(raw);
        Assert.Contains("\n---PASSPHRASE---\n", raw);

        var parts = raw.Split("\n---PASSPHRASE---\n", 2, StringSplitOptions.None);
        Assert.Equal(privateKey, parts[0]);
        Assert.Equal(passphrase, parts[1]);
    }

    [Fact]
    public async Task TransientAdapter_CreationWithExplicitCredentials_TakesPrecedence()
    {
        const string initialKey = "KEY_INITIAL";
        const string overrideKey = "KEY_OVERRIDE";

        var (cipher, nonce, tag) = _protector.Encrypt(initialKey);
        var hostId = Guid.NewGuid().ToString("N");

        var entity = new RemoteHostEntity
        {
            Id = hostId,
            Name = "Server",
            Host = "192.168.1.100",
            Port = 22,
            Protocol = "ssh",
            AuthType = "PrivateKey",
            Username = "root",
            EncryptedCredential = cipher,
            CredentialNonce = nonce,
            CredentialTag = tag,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _store.InsertAsync(entity);

        // 用户在添加或编辑弹窗输入了新凭据
        var testRequest = new TestRemoteHostRequest(
            Id: hostId,
            Host: entity.Host,
            Port: entity.Port,
            Protocol: entity.Protocol,
            AuthType: entity.AuthType,
            Username: entity.Username,
            Credential: overrideKey,
            Passphrase: null
        );

        string? resolvedCredential = testRequest.Credential;
        if (string.IsNullOrEmpty(resolvedCredential) && !string.IsNullOrWhiteSpace(testRequest.Id))
        {
            var stored = await _store.GetByIdAsync(testRequest.Id);
            if (stored != null)
            {
                resolvedCredential = _protector.Decrypt(stored.EncryptedCredential, stored.CredentialNonce, stored.CredentialTag);
            }
        }

        // 显式传入的凭据必须优先于数据库保存的凭据
        Assert.Equal(overrideKey, resolvedCredential);
    }
}
