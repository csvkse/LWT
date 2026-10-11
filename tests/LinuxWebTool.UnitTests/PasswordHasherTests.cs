using LinuxWebTool.Infrastructure.Features.Security.Adapters;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_GeneratesConsistent64HexChars()
    {
        var hash1 = PasswordHasher.Hash("secret-password-123");
        var hash2 = PasswordHasher.Hash("secret-password-123");

        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
        Assert.Matches("^[0-9a-f]{64}$", hash1);
    }

    [Fact]
    public void Verify_ValidPassword_ReturnsTrue()
    {
        var password = "super-safe-password";
        var hash = PasswordHasher.Hash(password);

        Assert.True(PasswordHasher.Verify(password, hash));
        Assert.True(PasswordHasher.Verify(password, hash.ToUpperInvariant())); // 大小写容错
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("correct-password");

        Assert.False(PasswordHasher.Verify("wrong-password", hash));
        Assert.False(PasswordHasher.Verify(string.Empty, hash));
        Assert.False(PasswordHasher.Verify("correct-password", string.Empty));
    }

    [Fact]
    public void Verify_SupportsPlainTextPasswords_InConfig()
    {
        const string plainText = "admin123456";

        Assert.True(PasswordHasher.Verify(plainText, plainText));
        Assert.False(PasswordHasher.Verify("wrong", plainText));
    }
}
