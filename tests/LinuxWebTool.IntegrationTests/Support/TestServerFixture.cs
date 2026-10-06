using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinuxWebTool.WebHost;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace LinuxWebTool.IntegrationTests.Support;

public static class TestServerFixture
{
    private static readonly string _dataDir = Path.Combine(Path.GetTempPath(), "linuxwebtool-tests", Guid.NewGuid().ToString("N"));
    private static readonly Lazy<Task<WebApplication>> _appInstance = new(CreateAppAsync);
    private static string? _adminToken;

    public static string DataDirectory => _dataDir;

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:Directory"] = _dataDir,
            ["Admin:UserName"] = "admin",
            ["Admin:Password"] = "integration-test-password",
            ["Swagger:Enabled"] = "false",
        });

        builder.AddApplicationServices();
        var app = builder.Build();
        app.UseApplicationPipeline();
        await app.StartAsync();
        return app;
    }

    public static async Task<WebApplication> GetAppAsync() => await _appInstance.Value;

    public static async Task<string> GetAdminTokenAsync()
    {
        if (_adminToken != null) return _adminToken;

        var app = await GetAppAsync();
        var client = app.GetTestClient();
        var login = await client.PostAsync("/api/Auth/Login", Json("{\"username\":\"admin\",\"password\":\"integration-test-password\"}"));
        login.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        _adminToken = doc.RootElement.GetProperty("token").GetString()!;
        return _adminToken;
    }

    public static async Task<HttpClient> CreateAdminClientAsync()
    {
        var app = await GetAppAsync();
        var token = await GetAdminTokenAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<HttpClient> CreateAnonymousClientAsync()
    {
        var app = await GetAppAsync();
        return app.GetTestClient();
    }

    public static async Task<HttpClient> CreateApiKeyClientAsync(string apiKey, bool useAuthorizationBearer = false)
    {
        var client = await CreateAnonymousClientAsync();
        if (useAuthorizationBearer)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
        return client;
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
