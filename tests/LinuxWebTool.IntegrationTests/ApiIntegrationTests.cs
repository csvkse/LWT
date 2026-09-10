using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinuxWebTool.WebHost.Composition;
using LinuxWebTool.WebHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class ApiIntegrationTests
{
    private static readonly Lazy<Task<HttpClient>> Client = new(CreateClientAsync);

    private static async Task<HttpClient> CreateClientAsync()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "linuxwebtool-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:Directory"] = dataDirectory,
            ["Admin:UserName"] = "admin",
            ["Admin:Password"] = "integration-test-password",
            ["Swagger:Enabled"] = "false",
        });
        builder.AddApplicationServices();
        var app = builder.Build();
        app.UseApplicationPipeline();
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Protected_api_requires_authentication()
    {
        var client = await Client.Value;
        client.DefaultRequestHeaders.Authorization = null;
        var response = await client.GetAsync("/api/Commands");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_credentials_are_rejected()
    {
        var response = await (await Client.Value).PostAsync("/api/Auth/Login", Json("{\"username\":\"admin\",\"password\":\"wrong-password\"}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_returns_json_not_spa_html()
    {
        var client = await Client.Value;
        client.DefaultRequestHeaders.Authorization = null;
        var response = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_then_query_core_aot_sensitive_endpoints()
    {
        var client = await Client.Value;
        await LoginAsync(client);

        foreach (var path in new[] { "/api/Auth/Check", "/api/Commands", "/api/History?page=1&pageSize=20", "/api/Logs/Operations?page=1&pageSize=20", "/api/Schedules" })
        {
            var response = await client.GetAsync(path);
            Assert.True((int)response.StatusCode < 500, $"{path} 返回 {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Command_and_group_crud_persists_through_api()
    {
        var client = await Client.Value;
        await LoginAsync(client);

        var groupResponse = await client.PostAsync("/api/Groups", Json("{\"name\":\"integration-group\",\"bizType\":0,\"sortOrder\":1}"));
        Assert.Equal(HttpStatusCode.OK, groupResponse.StatusCode);
        using var groupDocument = JsonDocument.Parse(await groupResponse.Content.ReadAsStringAsync());
        var groupId = groupDocument.RootElement.GetProperty("id").GetGuid();

        var commandResponse = await client.PostAsync("/api/Commands", Json($"{{\"name\":\"integration-command\",\"commandText\":\"printf integration\",\"scriptType\":0,\"groupId\":\"{groupId}\"}}"));
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        using var commandDocument = JsonDocument.Parse(await commandResponse.Content.ReadAsStringAsync());
        var commandId = commandDocument.RootElement.GetProperty("id").GetGuid();

        var listResponse = await client.GetAsync("/api/Commands?keyword=integration-command");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains("integration-command", await listResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Commands/{commandId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Groups/{groupId}")).StatusCode);
    }

    [Fact]
    public async Task File_write_rejects_missing_parent_without_server_error()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var response = await client.PostAsync("/api/Files/Content", Json("{\"path\":\"/directory-that-does-not-exist/aot-test.txt\",\"content\":\"test\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Schedule_crud_validates_command_reference_and_persists()
    {
        var client = await Client.Value;
        await LoginAsync(client);

        var commandResponse = await client.PostAsync("/api/Commands", Json("{\"name\":\"schedule-command\",\"commandText\":\"printf schedule\",\"scriptType\":0}"));
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        using var commandDocument = JsonDocument.Parse(await commandResponse.Content.ReadAsStringAsync());
        var commandId = commandDocument.RootElement.GetProperty("id").GetGuid();

        var scheduleResponse = await client.PostAsync("/api/Schedules", Json($"{{\"name\":\"integration-schedule\",\"commandId\":\"{commandId}\",\"cronExpression\":\"0 0 0 1 1 ? 2099\",\"enabled\":false}}"));
        Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
        using var scheduleDocument = JsonDocument.Parse(await scheduleResponse.Content.ReadAsStringAsync());
        var scheduleId = scheduleDocument.RootElement.GetProperty("id").GetGuid();

        var records = await client.GetAsync($"/api/Schedules/{scheduleId}/Records?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, records.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Schedules/{scheduleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Commands/{commandId}")).StatusCode);
    }

    [Fact]
    public async Task Transcode_preset_export_import_round_trip_is_available()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var create = await client.PostAsync("/api/Transcode/Presets", Json("{\"name\":\"integration-preset\",\"container\":\"mkv\",\"videoCodec\":\"libx264\",\"videoQuality\":23,\"audioCodec\":\"aac\",\"audioBitrate\":\"128k\"}"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var createdDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var presetId = createdDocument.RootElement.GetProperty("id").GetGuid();

        var export = await client.GetAsync("/api/Transcode/Presets/Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("integration-preset", await export.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/Transcode/Presets/Import", Json("[]"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Transcode/Presets/{presetId}")).StatusCode);
    }

    [Fact]
    public async Task Watch_rule_crud_and_toggle_use_existing_preset()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var preset = await client.PostAsync("/api/Transcode/Presets", Json("{\"name\":\"watch-preset\",\"container\":\"mp4\",\"videoCodec\":\"libx264\",\"audioCodec\":\"aac\"}"));
        Assert.Equal(HttpStatusCode.OK, preset.StatusCode);
        using var presetDocument = JsonDocument.Parse(await preset.Content.ReadAsStringAsync());
        var presetId = presetDocument.RootElement.GetProperty("id").GetGuid();

        var create = await client.PostAsync("/api/Transcode/WatchRules", Json($"{{\"name\":\"integration-watch\",\"watchPath\":\"/\",\"presetId\":\"{presetId}\",\"pollSeconds\":30,\"enabled\":false}}"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var ruleDocument = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var ruleId = ruleDocument.RootElement.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Transcode/WatchRules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/Transcode/WatchRules/{ruleId}/Toggle", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Transcode/WatchRules/{ruleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/Transcode/Presets/{presetId}")).StatusCode);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsync("/api/Auth/Login", Json("{\"username\":\"admin\",\"password\":\"integration-test-password\"}"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
