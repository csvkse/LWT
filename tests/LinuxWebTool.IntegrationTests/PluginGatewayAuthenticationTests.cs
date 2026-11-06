using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Features.Plugins.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public class PluginGatewayAuthenticationTests
{
    [Fact]
    public async Task Plugin_cookie_and_query_use_real_jwt_validation()
    {
        var token = await TestServerFixture.GetAdminTokenAsync();
        var client = await TestServerFixture.CreateAnonymousClientAsync();
        foreach (var input in new[] { "invalid", token })
        {
            foreach (var cookie in new[] { false, true })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/plugins/auth-test/identity" + (cookie ? "" : "?token=" + Uri.EscapeDataString(input)));
                if (cookie) request.Headers.Add("Cookie", "lwt_token=" + input);
                using var response = await client.SendAsync(request);
                using var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
                Assert.Equal(input == token, json!.RootElement.GetProperty("authenticated").GetBoolean());
            }
        }
    }
    [Theory]
    [InlineData("?token=invalid", "")]
    [InlineData("", "lwt_token=invalid")]
    public async Task Unvalidated_token_or_cookie_never_grants_admin(string query, string cookie)
    {
        var app = await TestServerFixture.GetAppAsync();
        var evaluator = app.Services.GetRequiredService<PluginAuthGatewayEvaluator>();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Path = "/plugins/sample/status";
        context.Request.QueryString = new QueryString(query);
        if (cookie.Length > 0) context.Request.Headers.Cookie = cookie;
        var decision = await evaluator.EvaluateAsync(context, new PluginManifest { Id = "sample", Name = "Sample", Network = new PluginNetworkConfig { AuthMode = "inherit" } });
        Assert.False(decision.IsAllowed);
        Assert.Equal(401, decision.StatusCode);
    }

    [Fact]
    public async Task Validated_admin_identity_is_accepted()
    {
        var app = await TestServerFixture.GetAppAsync();
        var evaluator = app.Services.GetRequiredService<PluginAuthGatewayEvaluator>();
        var context = new DefaultHttpContext { RequestServices = app.Services, User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test")) };
        context.Request.Path = "/plugins/sample/status";
        var decision = await evaluator.EvaluateAsync(context, new PluginManifest { Id = "sample", Name = "Sample", Network = new PluginNetworkConfig { AuthMode = "inherit" } });
        Assert.True(decision.IsAllowed);
    }
}
