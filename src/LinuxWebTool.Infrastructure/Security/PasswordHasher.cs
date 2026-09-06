using System.Security.Cryptography;
using System.Text;

namespace LinuxWebTool.Infrastructure.Security;

/// <summary>管理员口令校验：支持 SHA256(Salt+password) 十六进制哈希，或配置文件中的明文口令直接比对。</summary>
public static class PasswordHasher
{
    private const string Salt = "LinuxWebTool::v1::";

    public static string Hash(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        var candidate = Encoding.UTF8.GetBytes(Hash(password));
        var expected = Encoding.UTF8.GetBytes(stored.Trim().ToLowerInvariant());
        if (expected.Length == 64 && IsHex(expected))
        {
            return CryptographicOperations.FixedTimeEquals(candidate, expected);
        }

        // 允许在 appsettings 里直接放明文口令（个人工具便利性），比对同样走固定时间。
        var plain = Encoding.UTF8.GetBytes(stored);
        var input = Encoding.UTF8.GetBytes(password);
        return input.Length == plain.Length && CryptographicOperations.FixedTimeEquals(input, plain);
    }

    private static bool IsHex(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            var isHex = (b is >= (byte)'0' and <= (byte)'9')
                || (b is >= (byte)'a' and <= (byte)'f')
                || (b is >= (byte)'A' and <= (byte)'F');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }
}
