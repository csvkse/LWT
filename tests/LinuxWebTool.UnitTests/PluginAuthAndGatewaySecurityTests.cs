using LinuxWebTool.Application.Features.Plugins.Auth;
using LinuxWebTool.Application.Features.Security;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using Xunit;

namespace LinuxWebTool.UnitTests;

/// <summary>
/// 扩展中心长时 HTTP 微服务鉴权与网关安全策略单元测试
/// </summary>
public class PluginAuthAndGatewaySecurityTests
{
    [Fact]
    public void Test_IsPathPermitted_WildcardAndPrefix()
    {
        // 1. null 或 "*" 允许所有
        Assert.True(ApiKeyPermissionMatrix.IsPathPermitted(null, "/plugins/demo/test"));
        Assert.True(ApiKeyPermissionMatrix.IsPathPermitted("*", "/plugins/demo/test"));

        // 2. 通配符前缀匹配
        var allowed = "/plugins/demo-py-api/*, /plugins/mcp-bridge";
        Assert.True(ApiKeyPermissionMatrix.IsPathPermitted(allowed, "/plugins/demo-py-api/status"));
        Assert.True(ApiKeyPermissionMatrix.IsPathPermitted(allowed, "/plugins/demo-py-api"));
        Assert.True(ApiKeyPermissionMatrix.IsPathPermitted(allowed, "/plugins/mcp-bridge/"));
        Assert.True(ApiKeyPermissionMatrix.IsPathPermitted(allowed, "/plugins/mcp-bridge/api"));
        Assert.False(ApiKeyPermissionMatrix.IsPathPermitted(allowed, "/plugins/demo-js-webhook/event"));
        Assert.False(ApiKeyPermissionMatrix.IsPathPermitted(allowed, "/plugins/demo-py-api-attacker"));
    }

    [Fact]
    public void Test_PluginAuthRuleEvaluator_AnonymousMode()
    {
        var manifest = new PluginManifest
        {
            Id = "public-scraper",
            Name = "公开数据抓取",
            Network = new PluginNetworkConfig { AuthMode = "anonymous" }
        };

        var decision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "metrics",
            fullRequestPath: "/plugins/public-scraper/metrics",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: false,
            apiKeyName: null,
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal("Anonymous", decision.AuthType);
        Assert.Equal("anonymous", decision.IdentityName);
    }

    [Fact]
    public void Test_PluginAuthRuleEvaluator_InheritMode_Unauthenticated_Rejected()
    {
        var manifest = new PluginManifest
        {
            Id = "demo-py-api",
            Name = "Python API",
            Network = new PluginNetworkConfig { AuthMode = "inherit" }
        };

        var decision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "status",
            fullRequestPath: "/plugins/demo-py-api/status",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: false,
            apiKeyName: null,
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(401, decision.StatusCode);
        Assert.NotNull(decision.ChallengeHeader);
        Assert.Contains("Bearer realm", decision.ChallengeHeader);
    }

    [Fact]
    public void Test_PluginAuthRuleEvaluator_InheritMode_AdminUser_Allowed()
    {
        var manifest = new PluginManifest
        {
            Id = "demo-py-api",
            Name = "Python API",
            Network = new PluginNetworkConfig { AuthMode = "inherit" }
        };

        var decision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "status",
            fullRequestPath: "/plugins/demo-py-api/status",
            isAdminAuthenticated: true,
            adminUserName: "admin",
            hasValidApiKey: false,
            apiKeyName: null,
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.True(decision.IsAllowed);
        Assert.Equal("AdminSession", decision.AuthType);
        Assert.Equal("admin", decision.IdentityName);
    }

    [Fact]
    public void Test_PluginAuthRuleEvaluator_ApiKey_PermissionsAndScope()
    {
        var manifest = new PluginManifest
        {
            Id = "demo-py-api",
            Name = "Python API",
            Network = new PluginNetworkConfig { AuthMode = "inherit" }
        };

        // 1. 未开启 AllowPlugins -> 403 Forbidden
        var noPluginKeyDecision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "status",
            fullRequestPath: "/plugins/demo-py-api/status",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: true,
            apiKeyName: "ReadOnlyKey",
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.False(noPluginKeyDecision.IsAllowed);
        Assert.Equal(403, noPluginKeyDecision.StatusCode);

        // 2. 具备 AllowPlugins 且路径匹配 -> 允许访问
        var validKeyDecision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "status",
            fullRequestPath: "/plugins/demo-py-api/status",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: true,
            apiKeyName: "AutomationKey",
            apiKeyAllowPlugins: true,
            apiKeyAllowedPaths: "/plugins/demo-py-api/*");

        Assert.True(validKeyDecision.IsAllowed);
        Assert.Equal("ApiKey", validKeyDecision.AuthType);
        Assert.Equal("AutomationKey", validKeyDecision.IdentityName);

        // 3. 具备 AllowPlugins 但路径越界 -> 403 Forbidden
        var outOfScopeDecision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "status",
            fullRequestPath: "/plugins/demo-py-api/status",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: true,
            apiKeyName: "RestrictedKey",
            apiKeyAllowPlugins: true,
            apiKeyAllowedPaths: "/plugins/other-plugin/*");

        Assert.False(outOfScopeDecision.IsAllowed);
        Assert.Equal(403, outOfScopeDecision.StatusCode);
    }

    [Fact]
    public void Test_PluginAuthRuleEvaluator_HybridMode_PublicPaths()
    {
        var manifest = new PluginManifest
        {
            Id = "demo-webhook-svc",
            Name = "Webhook Service",
            Network = new PluginNetworkConfig
            {
                AuthMode = "hybrid",
                PublicPaths = ["/webhook", "/health/*"]
            }
        };

        // 1. 公开 Webhook 端点，匿名直接放行
        var webhookDecision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "webhook",
            fullRequestPath: "/plugins/demo-webhook-svc/webhook",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: false,
            apiKeyName: null,
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.True(webhookDecision.IsAllowed);
        Assert.Equal("PublicPath", webhookDecision.AuthType);

        // 2. 公开健康通配端点，匿名放行
        var healthDecision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "health/detailed",
            fullRequestPath: "/plugins/demo-webhook-svc/health/detailed",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: false,
            apiKeyName: null,
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.True(healthDecision.IsAllowed);
        Assert.Equal("PublicPath", healthDecision.AuthType);

        // 3. 私密管理端点，匿名调用被拦截 401
        var adminEndpointDecision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            requestSubPath: "admin/keys",
            fullRequestPath: "/plugins/demo-webhook-svc/admin/keys",
            isAdminAuthenticated: false,
            adminUserName: null,
            hasValidApiKey: false,
            apiKeyName: null,
            apiKeyAllowPlugins: false,
            apiKeyAllowedPaths: null);

        Assert.False(adminEndpointDecision.IsAllowed);
        Assert.Equal(401, adminEndpointDecision.StatusCode);
    }

    [Fact]
    public void Test_PluginRuntimeAdapters_EnforcesLoopbackEnv()
    {
        var manifest = new PluginManifest
        {
            Id = "test-plugin",
            Name = "Test Plugin",
            Network = new PluginNetworkConfig { Mode = "port", Port = 18005 }
        };

        var env = NodeRuntimeAdapter.BuildStandardEnv(manifest, "C:\\test", 18005, null);

        Assert.Equal("127.0.0.1", env["HOST"]);
        Assert.Equal("127.0.0.1", env["BIND_IP"]);
        Assert.Equal("127.0.0.1", env["BIND_ADDRESS"]);
        Assert.Equal("http://127.0.0.1:18005", env["ASPNETCORE_URLS"]);
        Assert.Equal("18005", env["PORT"]);
    }

    [Fact]
    public void Test_NativeRuntimeAdapter_SanitizesZeroAddress()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_test_native_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var binaryName = OperatingSystem.IsWindows() ? "socks5-proxy.exe" : "socks5-proxy";
            File.WriteAllText(Path.Combine(tempDir, binaryName), "#!/bin/sh\nexit 0\n");

            var manifest = new PluginManifest
            {
                Id = "socks5-proxy",
                Name = "Socks5 Proxy",
                Runtime = PluginRuntime.Native,
                Entrypoint = binaryName,
                Arguments = ["--listen", "0.0.0.0:{port}", "--mode", "fast"],
                Network = new PluginNetworkConfig { Mode = "port", Port = 18001 }
            };

            var adapter = new NativeRuntimeAdapter();
            var spec = adapter.BuildStartSpec(manifest, tempDir, 18001);

            Assert.NotNull(spec.ArgumentList);
            Assert.Equal("127.0.0.1:18001", spec.ArgumentList[1]);
            Assert.DoesNotContain("0.0.0.0", spec.Arguments);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_NativeBinaryValidator_HeaderDetection()
    {
        // 1. Shebang 脚本在所有平台均被视为有效
        var shebangHeader = System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\n");
        var (isShebangValid, shebangReason) = NativeBinaryValidator.ValidateHeader(shebangHeader);
        Assert.True(isShebangValid);
        Assert.Null(shebangReason);

        // 2. Windows PE (MZ) 标头
        byte[] peHeader = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00];
        var (isPeValid, peReason) = NativeBinaryValidator.ValidateHeader(peHeader);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(isPeValid);
            Assert.Null(peReason);
        }
        else
        {
            Assert.False(isPeValid);
            Assert.Contains("Windows PE", peReason);
            Assert.Contains("Linux ELF", peReason);
        }

        // 3. Linux ELF 标头 (AMD64 e_machine = 0x003E at offset 18-19)
        byte[] elfHeader = new byte[20];
        elfHeader[0] = 0x7F; elfHeader[1] = 0x45; elfHeader[2] = 0x4C; elfHeader[3] = 0x46; // \x7fELF
        elfHeader[4] = 0x02; // 64-bit
        elfHeader[5] = 0x01; // little endian
        elfHeader[18] = 0x3E; elfHeader[19] = 0x00; // AMD64
        var (isElfValid, elfReason) = NativeBinaryValidator.ValidateHeader(elfHeader);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(isElfValid);
            Assert.Contains("Linux ELF", elfReason);
        }
        else
        {
            if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            {
                Assert.True(isElfValid);
                Assert.Null(elfReason);
            }
        }

        // 4. 头部过短或非法魔数
        byte[] tooShort = [0x7F, 0x45];
        var (isShortValid, _) = NativeBinaryValidator.ValidateHeader(tooShort);
        Assert.False(isShortValid);

        byte[] invalidMagic = [0x00, 0x11, 0x22, 0x33];
        var (isInvalidMagic, invalidReason) = NativeBinaryValidator.ValidateHeader(invalidMagic);
        Assert.False(isInvalidMagic);
        Assert.NotNull(invalidReason);
    }

    [Fact]
    public void Test_NativeBinaryValidator_McpBridgeIsLinuxElf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LinuxWebTool.slnx")))
        {
            dir = dir.Parent!;
        }
        Assert.NotNull(dir);
        var repoRoot = dir.FullName;

        var mcpBridgeBin = Path.Combine(repoRoot, "plugins", "mcp-bridge", "mcp-bridge");
        if (File.Exists(mcpBridgeBin))
        {
            Assert.True(NativeBinaryValidator.IsLinuxElf(mcpBridgeBin), "plugins/mcp-bridge/mcp-bridge 必须是合法的 Linux ELF 二进制文件");
            Assert.False(NativeBinaryValidator.IsWindowsPe(mcpBridgeBin), "plugins/mcp-bridge/mcp-bridge 绝不能是 Windows PE (MZ) 文件");
        }
    }
}
