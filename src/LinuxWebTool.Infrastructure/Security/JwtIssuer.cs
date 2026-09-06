using System.Text;
using LinuxWebTool.Infrastructure.Support;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LinuxWebTool.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "LinuxWebTool";
    public string Audience { get; set; } = "LinuxWebTool";
    public int ExpireHours { get; set; } = 12;
}

/// <summary>
/// HS256 JWT 签发器。密钥未配置时自动生成 64 字节随机密钥并持久化到 data/jwt-secret.key，
/// 保证重启后已签发 Token 仍然有效。
/// </summary>
public sealed class JwtIssuer
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtIssuer(IConfiguration configuration, DataPaths dataPaths)
    {
        _options = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(_options.SecretKey) || _options.SecretKey.Length < 32)
        {
            _options.SecretKey = LoadOrCreateSecret(dataPaths.PathFor("jwt-secret.key"));
        }
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
    }

    public SymmetricSecurityKey SigningKey => _signingKey;

    public (string Token, DateTime ExpiresAt) Issue(string userName)
    {
        var expiresAt = DateTime.Now.AddHours(_options.ExpireHours);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["sub"] = userName },
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return (token, expiresAt);
    }

    public TokenValidationParameters BuildValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _signingKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "sub",
    };

    private static string LoadOrCreateSecret(string secretFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(secretFile)!);

        if (File.Exists(secretFile))
        {
            var existing = File.ReadAllText(secretFile).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        File.WriteAllText(secretFile, secret);
        return secret;
    }
}
