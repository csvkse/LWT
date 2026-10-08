using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
namespace LinuxWebTool.Infrastructure.Features.Security.Adapters;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "LinuxWebTool";
    public string Audience { get; set; } = "LinuxWebTool";

    /// <summary>Token 默认有效时长（小时）。默认 168 小时（7 天）。</summary>
    public int ExpireHours { get; set; } = 168;

    /// <summary>触发自动续期的剩余时间阈值（小时）。当剩余有效期小于此值且用户活跃时自动续签。默认 72 小时（3 天）。</summary>
    public int RefreshThresholdHours { get; set; } = 72;

    /// <summary>是否开启滑动窗口自动续期。默认 true。</summary>
    public bool EnableAutoRenewal { get; set; } = true;
}

/// <summary>
/// HS256 JWT 签发器。密钥未配置时自动生成 64 字节随机密钥并持久化到 data/jwt-secret.key，
/// 保证重启后已签发 Token 仍然有效。支持滑动会话生命周期感知与无感续约。
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

    public JwtOptions Options => _options;

    public SymmetricSecurityKey SigningKey => _signingKey;

    public static readonly JsonWebTokenHandler TokenHandler = new();

    public (string Token, DateTime ExpiresAt) Issue(string userName, string? securityStamp = null, TimeSpan? lifetime = null)
    {
        var expiresAt = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(_options.ExpireHours));
        var claims = new Dictionary<string, object> { ["sub"] = userName };
        if (!string.IsNullOrEmpty(securityStamp))
        {
            claims["stamp"] = securityStamp;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        };
        var token = TokenHandler.CreateToken(descriptor);
        return (token, expiresAt);
    }

    /// <summary>
    /// 依据原始 Token 校验是否满足滑动窗口续约条件（剩余寿命小于阈值且未过期），并提取用户主体与安全戳记。
    /// </summary>
    public bool ShouldRenew(string rawToken, out string userName, out string securityStamp)
    {
        userName = string.Empty;
        securityStamp = string.Empty;
        if (!_options.EnableAutoRenewal || string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        try
        {
            if (!TokenHandler.CanReadToken(rawToken)) return false;

            var jwt = TokenHandler.ReadJsonWebToken(rawToken);
            userName = jwt.Subject ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userName)) return false;

            if (jwt.TryGetClaim("stamp", out var stampClaim) && stampClaim is not null)
            {
                securityStamp = stampClaim.Value;
            }

            var validToUtc = jwt.ValidTo;
            var remaining = validToUtc - DateTime.UtcNow;
            var threshold = TimeSpan.FromHours(Math.Max(1, _options.RefreshThresholdHours));

            return remaining > TimeSpan.Zero && remaining <= threshold;
        }
        catch
        {
            return false;
        }
    }

    public bool ShouldRenew(string rawToken, out string userName) => ShouldRenew(rawToken, out userName, out _);

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
