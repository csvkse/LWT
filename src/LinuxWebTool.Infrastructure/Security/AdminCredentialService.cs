using System.Security.Cryptography;
using System.Text.Json;
using LinuxWebTool.Infrastructure.Support;

namespace LinuxWebTool.Infrastructure.Security;

public sealed class AdminAccount
{
    public string UserName { get; set; } = "admin";

    /// <summary>SHA256(Salt+password) 十六进制。</summary>
    public string PasswordHash { get; set; } = string.Empty;
}

/// <summary>
/// 单管理员账户。
/// 凭据优先级：显式密码（appsettings 的 Admin:Password 或环境变量 Admin__Password，每次启动生效并覆盖文件）
/// > data/admin.json（自动生成密码的持久化）> 首次启动随机生成（密码打印到程序日志）。
/// </summary>
public sealed class AdminCredentialService
{
    private readonly string _filePath;
    private readonly ILogger<AdminCredentialService> _logger;

    public AdminCredentialService(IConfiguration configuration, DataPaths dataPaths, ILogger<AdminCredentialService> logger)
    {
        _logger = logger;
        _filePath = dataPaths.PathFor("admin.json");
        Initialize(configuration);
    }

    public AdminAccount Account { get; private set; } = new();

    public bool Validate(string userName, string password)
    {
        return string.Equals(userName, Account.UserName, StringComparison.Ordinal)
            && PasswordHasher.Verify(password, Account.PasswordHash);
    }

    /// <summary>修改管理员凭据（Web 端改密入口）：只传新用户名则仅改名，只传新密码则仅改密，互不干扰。</summary>
    public void UpdateCredential(string? newUserName, string? newPassword)
    {
        if (!string.IsNullOrWhiteSpace(newUserName))
        {
            Account.UserName = newUserName.Trim();
        }
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            Account.PasswordHash = PasswordHasher.Hash(newPassword);
        }
        Persist(generatedPassword: null);
        _logger.LogInformation("管理员凭据已修改：用户名 {UserName}", Account.UserName);
    }

    private void Initialize(IConfiguration configuration)
    {
        // 优先级：显式密码（appsettings 的 Admin:Password 或环境变量 Admin__Password，ASP.NET Core 配置系统自动合并）
        //       > data/admin.json（自动生成密码的持久化）> 首次启动随机生成。
        // 显式配置每次启动都生效：修改环境变量后重启即可换号/换密码（会同步覆盖 admin.json 的 hash）。
        var configuredUserName = configuration["Admin:UserName"];
        var configuredPassword = configuration["Admin:Password"];
        if (!string.IsNullOrEmpty(configuredPassword))
        {
            var userName = string.IsNullOrWhiteSpace(configuredUserName) ? "admin" : configuredUserName.Trim();
            Account = new AdminAccount
            {
                UserName = userName,
                PasswordHash = PasswordHasher.Hash(configuredPassword),
            };
            Persist(generatedPassword: null);
            _logger.LogInformation("管理员账户来自显式配置（appsettings 或环境变量 Admin__UserName / Admin__Password）：用户名 {UserName}", userName);
            return;
        }

        if (TryLoadFromFile())
        {
            return;
        }

        var generatedPassword = GenerateRandomPassword();
        Account = new AdminAccount
        {
            UserName = string.IsNullOrWhiteSpace(configuredUserName) ? "admin" : configuredUserName.Trim(),
            PasswordHash = PasswordHasher.Hash(generatedPassword),
        };
        Persist(generatedPassword);
        _logger.LogWarning("首次启动已生成管理员账户：用户名 {UserName}，密码 {Password}（已保存到 {File}，请尽快登录并修改配置）",
            Account.UserName, generatedPassword, _filePath);
    }

    private void Persist(string? generatedPassword)
    {
        var persisted = new System.Text.Json.Nodes.JsonObject
        {
            ["userName"] = Account.UserName,
            ["passwordHash"] = Account.PasswordHash,
        };
        if (generatedPassword is not null)
        {
            persisted["generatedPassword"] = generatedPassword;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, persisted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool TryLoadFromFile()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return false;
            }

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_filePath));
            if (node is not System.Text.Json.Nodes.JsonObject obj) return false;

            var userName = obj["userName"]?.GetValue<string>();
            var hash = obj["passwordHash"]?.GetValue<string>();

            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(hash))
            {
                return false;
            }

            Account = new AdminAccount { UserName = userName, PasswordHash = hash };

            // 自动生成密码的场景：文件里保留了明文，每次启动都回显，避免用户忘记密码后无处可查。
            var generated = obj["generatedPassword"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(generated))
            {
                _logger.LogWarning("当前管理员凭据（自动生成，文件 {File}）：用户名 {UserName}，密码 {Password}。可通过网页右上角 ⚙ 修改，或用环境变量 Admin__Password 覆盖",
                    _filePath, userName, generated);
            }
            else
            {
                _logger.LogInformation("管理员账户已从 {File} 加载（用户名 {UserName}，密码来自显式配置或网页修改）", _filePath, userName);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取管理员配置文件失败");
            return false;
        }
    }

    private static string GenerateRandomPassword()
    {
        // 去掉易混淆字符的 16 位密码。
        const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}
