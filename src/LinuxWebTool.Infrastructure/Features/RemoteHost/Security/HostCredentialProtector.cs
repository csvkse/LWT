using System.Security.Cryptography;
using System.Text;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Security;

/// <summary>
/// 远程主机凭据 AES-256-GCM 安全加解密器
/// 支持单卷 data/master.key 持久化平移与环境变量 Security:MasterKey 显式覆盖（跨机/容器漂移无损恢复）
/// </summary>
public sealed class HostCredentialProtector
{
    private readonly byte[] _masterKey;
    private readonly ILogger<HostCredentialProtector> _logger;

    public HostCredentialProtector(
        IConfiguration configuration,
        DataPaths dataPaths,
        ILogger<HostCredentialProtector> logger)
    {
        _logger = logger;
        _masterKey = ResolveMasterKey(configuration, dataPaths);
    }

    public (string CipherText, string Nonce, string Tag) Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(_masterKey, 16);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        return (
            Convert.ToBase64String(cipherBytes),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag)
        );
    }

    public string? Decrypt(string? cipherText, string? nonce, string? tag)
    {
        if (string.IsNullOrEmpty(cipherText) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(tag))
        {
            return null;
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(cipherText);
            var nonceBytes = Convert.FromBase64String(nonce);
            var tagBytes = Convert.FromBase64String(tag);
            var plainBytes = new byte[cipherBytes.Length];

            using var aesGcm = new AesGcm(_masterKey, 16);
            aesGcm.Decrypt(nonceBytes, cipherBytes, tagBytes, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "凭据解密失败（主密钥不匹配或数据被篡改，可引导重新录入凭据）");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解密远程凭据时发生意外错误");
            return null;
        }
    }

    private static byte[] ResolveMasterKey(IConfiguration configuration, DataPaths dataPaths)
    {
        // 1. 优先读取环境变量或显式配置
        var configKey = configuration["Security:MasterKey"] ?? Environment.GetEnvironmentVariable("LWT_MASTER_KEY");
        if (!string.IsNullOrWhiteSpace(configKey))
        {
            try
            {
                var trimmed = configKey.Trim();
                if (trimmed.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed["base64:".Length..];
                }
                var keyBytes = Convert.FromBase64String(trimmed);
                if (keyBytes.Length == 32) return keyBytes;
            }
            catch
            {
                // 若 Base64 解析失败，回退 SHA256 派生
                return SHA256.HashData(Encoding.UTF8.GetBytes(configKey.Trim()));
            }
        }

        // 2. 其次读取或初始化 data/master.key 文件（随 data/ 单卷平移）
        var keyFilePath = dataPaths.PathFor("master.key");
        if (File.Exists(keyFilePath))
        {
            try
            {
                var content = File.ReadAllText(keyFilePath).Trim();
                var keyBytes = Convert.FromBase64String(content);
                if (keyBytes.Length == 32) return keyBytes;
            }
            catch
            {
                // 文件损坏时重新生成
            }
        }

        // 3. 首次启动生成全新 256 位安全密钥并持久化
        var newKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
            File.WriteAllText(keyFilePath, Convert.ToBase64String(newKey));
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch { }
            }
        }
        catch
        {
            // 写入失败降级使用内存密钥
        }

        return newKey;
    }
}
