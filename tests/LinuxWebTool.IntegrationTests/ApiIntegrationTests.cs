using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinuxWebTool.WebHost.Composition;
using LinuxWebTool.WebHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class ApiIntegrationTests
{
    private static readonly string DataDirectory = Path.Combine(Path.GetTempPath(), "linuxwebtool-tests", Guid.NewGuid().ToString("N"));
    private static readonly Lazy<Task<HttpClient>> Client = new(CreateClientAsync);

    private static WebApplication? _app;
    private static string? _lastToken;

    private static async Task<HttpClient> CreateClientAsync()
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
            ["Data:Directory"] = DataDirectory,
            ["Admin:UserName"] = "admin",
            ["Admin:Password"] = "integration-test-password",
            ["Swagger:Enabled"] = "false",
        });
        builder.AddApplicationServices();
        var app = builder.Build();
        app.UseApplicationPipeline();
        await app.StartAsync();
        _app = app;
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
    public async Task Legacy_lowercase_smb_mount_id_can_be_looked_up_by_route_guid()
    {
        var client = await Client.Value;
        await LoginAsync(client);

        var create = await client.PostAsync("/api/SmbMounts", Json("{\"name\":\"integration-smb\",\"server\":\"//server/share\",\"localPath\":\"/mnt/integration-smb\",\"autoMount\":false,\"enabled\":true}"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var document = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var mountId = document.RootElement.GetProperty("id").GetGuid();

        await using (var db = new SqliteConnection($"Data Source={Path.Combine(DataDirectory, "linuxweb.db")}"))
        {
            await db.OpenAsync();
            await using var normalize = db.CreateCommand();
            normalize.CommandText = "UPDATE smb_mount SET Id = lower(Id) WHERE Id = $id";
            normalize.Parameters.AddWithValue("$id", mountId.ToString().ToUpperInvariant());
            Assert.Equal(1, await normalize.ExecuteNonQueryAsync());
        }

        var update = await client.PutAsync($"/api/SmbMounts/{mountId}", Json("{\"name\":\"integration-smb-updated\",\"server\":\"//server/share\",\"localPath\":\"/mnt/integration-smb\",\"autoMount\":false,\"enabled\":true}"));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/SmbMounts/{mountId}")).StatusCode);
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
    public async Task File_upload_requires_authentication_and_reaches_upload_logic()
    {
        var client = await Client.Value;
        await LoginAsync(client);

        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("upload-test"));
        content.Add(file, "file", "upload-test.txt");

        var missing = Path.Combine(Path.GetTempPath(), "directory-that-does-not-exist-" + Guid.NewGuid().ToString("N"));
        var response = await client.PostAsync("/api/Files/Upload?path=" + Uri.EscapeDataString(missing), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("目录不存在", await response.Content.ReadAsStringAsync());
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
        _lastToken = token;
    }

    [Fact]
    public async Task Terminal_support_reports_application_capabilities()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var response = await client.GetAsync("/api/Terminal/Support");
        response.EnsureSuccessStatusCode();
        using var support = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Application", support.RootElement.GetProperty("mode").GetString());
        _ = support.RootElement.GetProperty("nativePty").GetBoolean();
        Assert.False(support.RootElement.TryGetProperty("installCommand", out _));
    }

    [Fact]
    public async Task Terminal_rejects_missing_working_directory_without_starting_a_session()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var missing = Path.Combine(Path.GetTempPath(), "terminal-missing-" + Guid.NewGuid().ToString("N"));
        var response = await client.PostAsync("/api/Terminal/Sessions", Json(JsonSerializer.Serialize(new { workingDirectory = missing })));
        if (response.IsSuccessStatusCode)
        {
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await client.DeleteAsync("/api/Terminal/Sessions/" + created.RootElement.GetProperty("sessionId").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Terminal_accepts_an_existing_directory_with_spaces_and_unicode()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var directory = Path.Combine(Path.GetTempPath(), "terminal 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? sessionId = null;
        try
        {
            var response = await client.PostAsync("/api/Terminal/Sessions", Json(JsonSerializer.Serialize(new { workingDirectory = directory })));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            sessionId = created.RootElement.GetProperty("sessionId").GetString();
            Assert.Equal(directory, created.RootElement.GetProperty("workingDirectory").GetString());
        }
        finally
        {
            if (sessionId is not null) await client.DeleteAsync("/api/Terminal/Sessions/" + sessionId);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Terminal_lists_sessions_and_preserves_process_after_disconnect()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var response = await client.PostAsync("/api/Terminal/Sessions", Json("{}"));
        response.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("sessionId").GetString();
        var pid = created.RootElement.GetProperty("processId").GetInt32();
        try
        {
            var list = await client.GetAsync("/api/Terminal/Sessions");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var sessions = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Contains(sessions.RootElement.EnumerateArray(), s => s.GetProperty("sessionId").GetString() == id);
            var server = _app!.GetTestServer();
            var socketClient = server.CreateWebSocketClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var uri = new Uri(server.BaseAddress, $"/api/terminal/ws/{id}?token={_lastToken}");
            using (var socket = await socketClient.ConnectAsync(uri, timeout.Token))
            {
                var command = Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? "Write-Output 'detached-check'\r" : "printf 'detached-check\\n'\r");
                await socket.SendAsync(command, System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
                var output = new StringBuilder();
                var buffer = new byte[8192];
                while (!output.ToString().Contains("detached-check", StringComparison.Ordinal))
                {
                    var read = await socket.ReceiveAsync(buffer, timeout.Token);
                    output.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                }
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "detach", timeout.Token);
            }
            var details = await client.GetAsync("/api/Terminal/Sessions/" + id);
            details.EnsureSuccessStatusCode();
            using var snapshot = JsonDocument.Parse(await details.Content.ReadAsStringAsync());
            Assert.Equal(pid, snapshot.RootElement.GetProperty("processId").GetInt32());
            Assert.True(snapshot.RootElement.GetProperty("hasUserInput").GetBoolean());
            using var reconnected = await socketClient.ConnectAsync(uri, timeout.Token);
            var replayBuffer = new byte[8192];
            var replay = await reconnected.ReceiveAsync(replayBuffer, timeout.Token);
            Assert.True(replay.Count > 0);
            await reconnected.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        }
        finally
        {
            await client.DeleteAsync("/api/Terminal/Sessions/" + id);
        }
    }

    [Fact]
    public async Task Terminal_session_and_websocket_roundtrip()
    {
        var client = await Client.Value;
        await LoginAsync(client);

        var createResponse = await client.PostAsync("/api/Terminal/Sessions", Json("{\"columns\":80,\"rows\":24}"));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        using var doc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var sessionId = (doc.RootElement.TryGetProperty("sessionId", out var s) ? s.GetString() : null)
            ?? doc.RootElement.GetProperty("SessionId").GetString();
        Assert.False(string.IsNullOrEmpty(sessionId));

        var testServer = _app!.GetTestServer();
        var wsClient = testServer.CreateWebSocketClient();
        var wsUri = new Uri(testServer.BaseAddress, $"/api/terminal/ws/{sessionId}?token={_lastToken}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ws = await wsClient.ConnectAsync(wsUri, cts.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, ws.State);

        var buffer = new byte[1024];
        var receiveResult = await ws.ReceiveAsync(buffer, cts.Token);
        Assert.True(receiveResult.Count > 0, "Received bytes from terminal WebSocket");

        if (ws.State == System.Net.WebSockets.WebSocketState.Open)
        {
            await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "test-done", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Files_list_native_directory_and_windows_drive_roots()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var directory = Path.Combine(Path.GetTempPath(), "lwt-files-中文 space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var response = await client.GetAsync("/api/Files?path=" + Uri.EscapeDataString(directory));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(directory.Replace('\\', '/').TrimEnd('/'), listing.RootElement.GetProperty("path").GetString());
            if (OperatingSystem.IsWindows())
            {
                var roots = await client.GetAsync("/api/Files?path=%2F");
                Assert.Equal(HttpStatusCode.OK, roots.StatusCode);
                using var rootList = JsonDocument.Parse(await roots.Content.ReadAsStringAsync());
                Assert.True(rootList.RootElement.GetProperty("isVirtualRoot").GetBoolean());
                Assert.Contains(rootList.RootElement.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("path").GetString() == Path.GetPathRoot(directory)!.Replace('\\', '/'));
            }
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task Files_native_crud_preserves_data_directory_protection()
    {
        var client = await Client.Value;
        await LoginAsync(client);
        var directory = Path.Combine(Path.GetTempPath(), "lwt-file-crud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "中文 space.txt");
        var renamed = Path.Combine(directory, "renamed.txt");
        var protectedFile = Path.Combine(DataDirectory, "guard-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/Files/Content", Json(JsonSerializer.Serialize(new { path = file, content = "中文内容" })))).StatusCode);
            var read = await client.GetAsync("/api/Files/Content?path=" + Uri.EscapeDataString(file));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal("中文内容", body.RootElement.GetProperty("content").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/Files/Rename", Json(JsonSerializer.Serialize(new { from = file, to = renamed })))).StatusCode);
            Assert.True(File.Exists(renamed));
            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/Files?path=" + Uri.EscapeDataString(renamed))).StatusCode);
            Assert.False(File.Exists(renamed));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/Files/Content", Json(JsonSerializer.Serialize(new { path = protectedFile, content = "forbidden" })))).StatusCode);
            Assert.False(File.Exists(protectedFile));
            if (OperatingSystem.IsWindows())
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/Files?path=" + Uri.EscapeDataString("C:relative"))).StatusCode);
        }
        finally
        {
            foreach (var path in new[] { file, renamed, protectedFile }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
