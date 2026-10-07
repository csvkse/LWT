using System.Net;
using System.Text.Json;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Composition;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class ApiKeyFunctionalTests
{
    [Fact]
    public async Task ApiKey_Admin_Crud_Lifecycle_Works()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建 API Key
        var createPayload = new CreateApiKeyRequest(
            Name: "Integration Test Key",
            AllowApi: true,
            AllowMcp: true,
            AllowTerminal: true,
            AllowSchedules: false,
            AllowFiles: true,
            AllowTranscode: false,
            AllowGateway: true);

        var createJson = JsonSerializer.Serialize(createPayload, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var createResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var createdBody = await createResp.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createdBody);
        var keyId = createDoc.RootElement.GetProperty("id").GetString()!;
        var rawKey = createDoc.RootElement.GetProperty("rawKey").GetString()!;
        var keyPrefix = createDoc.RootElement.GetProperty("keyPrefix").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(keyId));
        Assert.StartsWith("lwt_live_", rawKey);
        Assert.StartsWith("lwt_live_", keyPrefix);

        // 2. 列表查询（返回列表脱敏，仅含 keyPrefix，不含 rawKey）
        var listResp = await adminClient.GetAsync("/api/ApiKeys");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listBody = await listResp.Content.ReadAsStringAsync();
        Assert.Contains(keyPrefix, listBody);
        Assert.DoesNotContain(rawKey, listBody);

        // 3. 更新 API Key（修改名称并禁用）
        var updatePayload = new UpdateApiKeyRequest(
            Name: "Integration Test Key (Disabled)",
            IsEnabled: false,
            AllowApi: true,
            AllowMcp: false,
            AllowTerminal: false,
            AllowSchedules: false,
            AllowFiles: false,
            AllowTranscode: false,
            AllowGateway: false,
            ExpiresAt: null);

        var updateJson = JsonSerializer.Serialize(updatePayload, AppJsonSerializerContext.Default.UpdateApiKeyRequest);
        var updateResp = await adminClient.PutAsync($"/api/ApiKeys/{keyId}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 4. 删除 API Key
        var deleteResp = await adminClient.DeleteAsync($"/api/ApiKeys/{keyId}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // 5. 再次查询确认已删除
        var afterDeleteResp = await adminClient.GetAsync("/api/ApiKeys");
        Assert.DoesNotContain(keyId, await afterDeleteResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiKey_Authentication_Accepts_XApiKey_Bearer_And_Query()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();

        // 创建仅授予 Files 权限的测试 Key
        var createPayload = new CreateApiKeyRequest(
            Name: "Header Auth Test Key",
            AllowApi: true,
            AllowMcp: false,
            AllowTerminal: false,
            AllowSchedules: false,
            AllowFiles: true,
            AllowTranscode: false,
            AllowGateway: false);

        var createJson = JsonSerializer.Serialize(createPayload, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var createResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(createJson));
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var rawKey = createDoc.RootElement.GetProperty("rawKey").GetString()!;

        var tempDir = Path.Combine(Path.GetTempPath(), $"lwt_auth_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var testPath = Uri.EscapeDataString(tempDir);

            // 1. 通过 X-Api-Key 标头访问
            var xKeyClient = await TestServerFixture.CreateApiKeyClientAsync(rawKey, useAuthorizationBearer: false);
            var resp1 = await xKeyClient.GetAsync($"/api/Files?path={testPath}");
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // 2. 通过 Authorization: Bearer lwt_... 标头访问
            var bearerClient = await TestServerFixture.CreateApiKeyClientAsync(rawKey, useAuthorizationBearer: true);
            var resp2 = await bearerClient.GetAsync($"/api/Files?path={testPath}");
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

            // 3. 通过 query 参数 ?apiKey= 访问
            var anonClient = await TestServerFixture.CreateAnonymousClientAsync();
            var resp3 = await anonClient.GetAsync($"/api/Files?path={testPath}&apiKey={rawKey}");
            Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

            // 4. 通过 fallback query 参数 ?key= 访问
            var resp4 = await anonClient.GetAsync($"/api/Files?path={testPath}&key={rawKey}");
            Assert.Equal(HttpStatusCode.OK, resp4.StatusCode);

            // 5. 错误 API Key 必须被拒绝 401
            var badKeyClient = await TestServerFixture.CreateApiKeyClientAsync("lwt_live_nonexistent_key_99999");
            var resp5 = await badKeyClient.GetAsync($"/api/Files?path={testPath}");
            Assert.Equal(HttpStatusCode.Unauthorized, resp5.StatusCode);
            using var errDoc = JsonDocument.Parse(await resp5.Content.ReadAsStringAsync());
            Assert.Contains("无效", errDoc.RootElement.GetProperty("message").GetString()!);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ApiKey_Channel_Control_AllowApi_Enforced()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();

        // 创建 AllowApi = false（仅 MCP 允许）的 Key
        var createPayload = new CreateApiKeyRequest(
            Name: "Mcp-Only Key",
            AllowApi: false,
            AllowMcp: true,
            AllowTerminal: true,
            AllowSchedules: true,
            AllowFiles: true,
            AllowTranscode: true,
            AllowGateway: true);

        var createJson = JsonSerializer.Serialize(createPayload, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var createResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(createJson));
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var rawKey = createDoc.RootElement.GetProperty("rawKey").GetString()!;

        var client = await TestServerFixture.CreateApiKeyClientAsync(rawKey);

        // 尝试访问任意常规 REST API，期望 403 Forbidden 且提示通道未开启
        var resp = await client.GetAsync("/api/Commands");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        using var bodyDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("该 API Key 未被授予访问 REST API 通道权限", bodyDoc.RootElement.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task ApiKey_Module_Level_Permissions_Enforced()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();

        // 场景 A: 仅授权 Files，拒绝 Terminal 与 Gateway
        var fileOnlyReq = new CreateApiKeyRequest(
            Name: "Files Only Key",
            AllowApi: true,
            AllowMcp: false,
            AllowTerminal: false,
            AllowSchedules: false,
            AllowFiles: true,
            AllowTranscode: false,
            AllowGateway: false);

        var fileOnlyJson = JsonSerializer.Serialize(fileOnlyReq, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var fileOnlyResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(fileOnlyJson));
        using var fileDoc = JsonDocument.Parse(await fileOnlyResp.Content.ReadAsStringAsync());
        var fileKey = fileDoc.RootElement.GetProperty("rawKey").GetString()!;

        var fileClient = await TestServerFixture.CreateApiKeyClientAsync(fileKey);

        // Files 允许 (200 OK)
        var tempDir = Path.Combine(Path.GetTempPath(), $"lwt_mod_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var filesResp = await fileClient.GetAsync($"/api/Files?path={Uri.EscapeDataString(tempDir)}");
            Assert.Equal(HttpStatusCode.OK, filesResp.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }

        // Terminal 拒绝 (403 Forbidden)
        var termResp = await fileClient.GetAsync("/api/Terminal/Support");
        Assert.Equal(HttpStatusCode.Forbidden, termResp.StatusCode);
        using var termErrDoc = JsonDocument.Parse(await termResp.Content.ReadAsStringAsync());
        Assert.Contains("无权访问", termErrDoc.RootElement.GetProperty("message").GetString()!);

        // Gateway 拒绝 (403 Forbidden)
        var gwResp = await fileClient.GetAsync("/api/Gateway/Routes");
        Assert.Equal(HttpStatusCode.Forbidden, gwResp.StatusCode);
        using var gwDoc = JsonDocument.Parse(await gwResp.Content.ReadAsStringAsync());
        Assert.Contains("无权访问", gwDoc.RootElement.GetProperty("message").GetString()!);

        // Schedules 拒绝 (403 Forbidden)
        var schResp = await fileClient.GetAsync("/api/Schedules");
        Assert.Equal(HttpStatusCode.Forbidden, schResp.StatusCode);
        using var schDoc = JsonDocument.Parse(await schResp.Content.ReadAsStringAsync());
        Assert.Contains("无权访问", schDoc.RootElement.GetProperty("message").GetString()!);

        // 场景 B: 仅授权 Terminal，拒绝 Files
        var termOnlyReq = new CreateApiKeyRequest(
            Name: "Terminal Only Key",
            AllowApi: true,
            AllowMcp: false,
            AllowTerminal: true,
            AllowSchedules: false,
            AllowFiles: false,
            AllowTranscode: false,
            AllowGateway: false);

        var termOnlyJson = JsonSerializer.Serialize(termOnlyReq, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var termOnlyResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(termOnlyJson));
        using var termDoc = JsonDocument.Parse(await termOnlyResp.Content.ReadAsStringAsync());
        var termKey = termDoc.RootElement.GetProperty("rawKey").GetString()!;

        var termClient = await TestServerFixture.CreateApiKeyClientAsync(termKey);

        // Terminal 允许 (200 OK)
        var termOkResp = await termClient.GetAsync("/api/Terminal/Support");
        Assert.Equal(HttpStatusCode.OK, termOkResp.StatusCode);

        // Files 拒绝 (403 Forbidden)
        var filesForbiddenResp = await termClient.GetAsync("/api/Files?path=%2F");
        Assert.Equal(HttpStatusCode.Forbidden, filesForbiddenResp.StatusCode);
    }

    [Fact]
    public async Task ApiKey_Blocked_From_Administrative_Routes()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();

        // 即使全部权限都开启的 API Key，也严禁访问管理专属接口
        var allReq = new CreateApiKeyRequest(
            Name: "Full Scoped Key",
            AllowApi: true,
            AllowMcp: true,
            AllowTerminal: true,
            AllowSchedules: true,
            AllowFiles: true,
            AllowTranscode: true,
            AllowGateway: true);

        var allJson = JsonSerializer.Serialize(allReq, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var allResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(allJson));
        using var allDoc = JsonDocument.Parse(await allResp.Content.ReadAsStringAsync());
        var fullKey = allDoc.RootElement.GetProperty("rawKey").GetString()!;

        var fullKeyClient = await TestServerFixture.CreateApiKeyClientAsync(fullKey);

        // 1. 管理 API Keys 自身禁止被普通 API Key 访问
        var keysResp = await fullKeyClient.GetAsync("/api/ApiKeys");
        Assert.Equal(HttpStatusCode.Forbidden, keysResp.StatusCode);

        // 2. FRP 穿透管理配置禁止被普通 API Key 访问
        var frpResp = await fullKeyClient.GetAsync("/api/FrpTunnel/Lines");
        Assert.Equal(HttpStatusCode.Forbidden, frpResp.StatusCode);

        // 3. 修改管理员账号凭据禁止被普通 API Key 访问
        var pwdResp = await fullKeyClient.PostAsync("/api/Auth/ChangeCredential", TestServerFixture.Json("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, pwdResp.StatusCode);
    }

    [Fact]
    public async Task ApiKey_Disabled_Immediately_Fails_Validation()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();

        var req = new CreateApiKeyRequest(
            Name: "Key to Disable",
            AllowApi: true,
            AllowMcp: true,
            AllowTerminal: true,
            AllowSchedules: true,
            AllowFiles: true,
            AllowTranscode: true,
            AllowGateway: true);

        var json = JsonSerializer.Serialize(req, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var resp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(json));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var keyId = doc.RootElement.GetProperty("id").GetString()!;
        var rawKey = doc.RootElement.GetProperty("rawKey").GetString()!;

        var keyClient = await TestServerFixture.CreateApiKeyClientAsync(rawKey);

        // 启用状态下正常可用
        var okResp = await keyClient.GetAsync("/api/Terminal/Support");
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);

        // 管理员将其禁用
        var updatePayload = new UpdateApiKeyRequest(
            Name: "Key to Disable",
            IsEnabled: false,
            AllowApi: true,
            AllowMcp: true,
            AllowTerminal: true,
            AllowSchedules: true,
            AllowFiles: true,
            AllowTranscode: true,
            AllowGateway: true,
            ExpiresAt: null);

        var updateJson = JsonSerializer.Serialize(updatePayload, AppJsonSerializerContext.Default.UpdateApiKeyRequest);
        var updateResp = await adminClient.PutAsync($"/api/ApiKeys/{keyId}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 禁用后应立即拒绝访问 401
        var disabledResp = await keyClient.GetAsync("/api/Terminal/Support");
        Assert.Equal(HttpStatusCode.Unauthorized, disabledResp.StatusCode);
    }

    [Fact]
    public async Task ApiKey_Extension_Suffix_On_Protected_Route_Does_Not_Bypass_Auth()
    {
        var anonymousClient = await TestServerFixture.CreateAnonymousClientAsync();

        // 尝试用伪装成静态资源的 URL 访问 MCP 路由，必须被 ApiKeyMiddleware 拦截返回 401
        var mcpResp = await anonymousClient.GetAsync("/mcp/v1/tools.js");
        Assert.Equal(HttpStatusCode.Unauthorized, mcpResp.StatusCode);

        // 使用无效 Key 访问带 .js 后缀的 API 路由，必须执行 Key 校验并返回 401（不能因 .js 后缀跳过校验）
        var invalidKeyClient = await TestServerFixture.CreateApiKeyClientAsync("lwt_live_invalid_key_123");
        var apiResp = await invalidKeyClient.GetAsync("/api/Terminal/Support.js");
        Assert.Equal(HttpStatusCode.Unauthorized, apiResp.StatusCode);
    }
}
