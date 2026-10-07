using System.Net;
using System.Text.Json;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Composition;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class McpFunctionalTests
{
    [Fact]
    public async Task Mcp_Requires_Authentication_And_AllowMcp_Flag()
    {
        // 1. 未授权匿名请求必须返回 401
        var anonClient = await TestServerFixture.CreateAnonymousClientAsync();
        var initPayload = new
        {
            jsonrpc = "2.0",
            id = "req-anon",
            method = "initialize",
            @params = new { }
        };
        var anonResp = await anonClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(initPayload)));
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. 具有 Api 权限但 AllowMcp = false 的 Key 必须返回 403 Forbidden
        var adminClient = await TestServerFixture.CreateAdminClientAsync();
        var noMcpReq = new CreateApiKeyRequest(
            Name: "No MCP Key",
            AllowApi: true,
            AllowMcp: false,
            AllowTerminal: true,
            AllowSchedules: true,
            AllowFiles: true,
            AllowTranscode: true,
            AllowGateway: true);

        var noMcpJson = JsonSerializer.Serialize(noMcpReq, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var noMcpResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(noMcpJson));
        using var noMcpDoc = JsonDocument.Parse(await noMcpResp.Content.ReadAsStringAsync());
        var rawKey = noMcpDoc.RootElement.GetProperty("rawKey").GetString()!;

        var noMcpClient = await TestServerFixture.CreateApiKeyClientAsync(rawKey);
        var forbiddenResp = await noMcpClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(initPayload)));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
        using var noMcpErrDoc = JsonDocument.Parse(await forbiddenResp.Content.ReadAsStringAsync());
        Assert.Contains("MCP 协议通道权限", noMcpErrDoc.RootElement.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Mcp_Protocol_Initialize_And_Ping()
    {
        var mcpClient = await CreateMcpApiKeyClientAsync(allowTerminal: true, allowFiles: true, allowGateway: true);

        // 1. initialize
        var initPayload = new
        {
            jsonrpc = "2.0",
            id = "init-1",
            method = "initialize",
            @params = new { }
        };
        var initResp = await mcpClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(initPayload)));
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);

        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var result = initDoc.RootElement.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("LinuxWebTool-MCP-Server", result.GetProperty("serverInfo").GetProperty("name").GetString());

        // 2. ping
        var pingPayload = new
        {
            jsonrpc = "2.0",
            id = "ping-1",
            method = "ping",
            @params = new { }
        };
        var pingResp = await mcpClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(pingPayload)));
        Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode);
        using var pingDoc = JsonDocument.Parse(await pingResp.Content.ReadAsStringAsync());
        Assert.True(pingDoc.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Mcp_ToolsList_Performs_Dynamic_Permission_Projection()
    {
        // Key A: 允许 Terminal 与 Files，禁止 Gateway 与 Schedules
        var clientA = await CreateMcpApiKeyClientAsync(
            allowTerminal: true,
            allowFiles: true,
            allowGateway: false,
            allowSchedules: false,
            allowTranscode: false);

        var listReq = new
        {
            jsonrpc = "2.0",
            id = "tools-list-a",
            method = "tools/list",
            @params = new { }
        };
        var respA = await clientA.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(listReq)));
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);

        var bodyA = await respA.Content.ReadAsStringAsync();
        using var docA = JsonDocument.Parse(bodyA);
        var toolsA = docA.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();

        Assert.Contains("terminal_execute_command", toolsA);
        Assert.Contains("file_read_text", toolsA);
        Assert.Contains("file_write_text", toolsA);
        Assert.DoesNotContain("gateway_list_routes", toolsA);
        Assert.DoesNotContain("schedule_create", toolsA);
        Assert.DoesNotContain("transcode_submit_job", toolsA);

        // Key B: 允许 Gateway，禁止 Terminal 与 Files
        var clientB = await CreateMcpApiKeyClientAsync(
            allowTerminal: false,
            allowFiles: false,
            allowGateway: true,
            allowSchedules: false,
            allowTranscode: false);

        var respB = await clientB.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(listReq)));
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);

        using var docB = JsonDocument.Parse(await respB.Content.ReadAsStringAsync());
        var toolsB = docB.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();

        Assert.Contains("gateway_list_routes", toolsB);
        Assert.DoesNotContain("terminal_execute_command", toolsB);
        Assert.DoesNotContain("file_read_text", toolsB);
    }

    [Fact]
    public async Task Mcp_ToolCall_FileReadWrite_RoundTrip()
    {
        var mcpClient = await CreateMcpApiKeyClientAsync(allowTerminal: false, allowFiles: true, allowGateway: false);

        var tempDir = Path.Combine(Path.GetTempPath(), $"lwt_mcp_files_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var targetFile = Path.Combine(tempDir, "mcp_roundtrip.txt").Replace('\\', '/');

        try
        {
            // 1. 调用 file_write_text
            var writeReq = new
            {
                jsonrpc = "2.0",
                id = "write-1",
                method = "tools/call",
                @params = new
                {
                    name = "file_write_text",
                    arguments = new
                    {
                        path = targetFile,
                        content = "Hello from MCP Functional Test!",
                        overwrite = true
                    }
                }
            };

            var writeResp = await mcpClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(writeReq)));
            Assert.Equal(HttpStatusCode.OK, writeResp.StatusCode);
            var writeBody = await writeResp.Content.ReadAsStringAsync();
            using var writeDoc = JsonDocument.Parse(writeBody);
            var writeResult = writeDoc.RootElement.GetProperty("result");
            Assert.False(writeResult.GetProperty("isError").GetBoolean());

            // 物理磁盘验证
            Assert.True(File.Exists(targetFile));
            Assert.Equal("Hello from MCP Functional Test!", await File.ReadAllTextAsync(targetFile));

            // 2. 调用 file_read_text
            var readReq = new
            {
                jsonrpc = "2.0",
                id = "read-1",
                method = "tools/call",
                @params = new
                {
                    name = "file_read_text",
                    arguments = new
                    {
                        path = targetFile
                    }
                }
            };

            var readResp = await mcpClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(readReq)));
            Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);
            using var readDoc = JsonDocument.Parse(await readResp.Content.ReadAsStringAsync());
            var readResult = readDoc.RootElement.GetProperty("result");
            Assert.False(readResult.GetProperty("isError").GetBoolean());
            var contentList = readResult.GetProperty("content").EnumerateArray().ToList();
            Assert.Single(contentList);
            Assert.Contains("Hello from MCP Functional Test!", contentList[0].GetProperty("text").GetString());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Mcp_ToolCall_Terminal_Execution_And_Permission_Denied()
    {
        // 1. 允许 Terminal 的 Key 可以成功执行命令
        var okClient = await CreateMcpApiKeyClientAsync(allowTerminal: true, allowFiles: false, allowGateway: false);
        var testCmd = OperatingSystem.IsWindows() ? "cmd /c echo mcp_term_test_passed" : "echo mcp_term_test_passed";

        var execReq = new
        {
            jsonrpc = "2.0",
            id = "exec-1",
            method = "tools/call",
            @params = new
            {
                name = "terminal_execute_command",
                arguments = new
                {
                    command = testCmd
                }
            }
        };

        var okResp = await okClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(execReq)));
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        using var okDoc = JsonDocument.Parse(await okResp.Content.ReadAsStringAsync());
        var okResult = okDoc.RootElement.GetProperty("result");
        Assert.False(okResult.GetProperty("isError").GetBoolean());
        var text = okResult.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        Assert.Contains("mcp_term_test_passed", text);

        // 2. 未授权 Terminal 的 Key 试图调用 terminal 工具必须被拦截返回错误
        var deniedClient = await CreateMcpApiKeyClientAsync(allowTerminal: false, allowFiles: true, allowGateway: false);
        var deniedResp = await deniedClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(execReq)));
        Assert.Equal(HttpStatusCode.OK, deniedResp.StatusCode);
        using var deniedDoc = JsonDocument.Parse(await deniedResp.Content.ReadAsStringAsync());
        var deniedResult = deniedDoc.RootElement.GetProperty("result");
        Assert.True(deniedResult.GetProperty("isError").GetBoolean());
        var errText = deniedResult.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        Assert.Contains("Permission Denied", errText);
    }

    [Fact]
    public async Task Mcp_Unknown_Method_Returns_Standard_JsonRpc_Error()
    {
        var mcpClient = await CreateMcpApiKeyClientAsync(allowTerminal: true, allowFiles: true, allowGateway: true);
        var badMethodReq = new
        {
            jsonrpc = "2.0",
            id = "bad-1",
            method = "unsupported_method_xyz",
            @params = new { }
        };

        var resp = await mcpClient.PostAsync("/mcp", TestServerFixture.Json(JsonSerializer.Serialize(badMethodReq)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Mcp_Sse_Session_And_Message_Workflow()
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();
        var keyReq = new CreateApiKeyRequest(
            Name: "SSE MCP Test Key",
            AllowApi: true,
            AllowMcp: true,
            AllowTerminal: true,
            AllowSchedules: true,
            AllowFiles: true,
            AllowTranscode: true,
            AllowGateway: true);

        var keyJson = JsonSerializer.Serialize(keyReq, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var keyResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(keyJson));
        using var keyDoc = JsonDocument.Parse(await keyResp.Content.ReadAsStringAsync());
        var rawKey = keyDoc.RootElement.GetProperty("rawKey").GetString()!;

        // 1. 建立 SSE 长连接 GET /mcp/sse
        var sseClient = await TestServerFixture.CreateApiKeyClientAsync(rawKey);
        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/mcp/sse");
        var sseResponse = await sseClient.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);

        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        Assert.Equal("text/event-stream", sseResponse.Content.Headers.ContentType?.MediaType);

        await using var sseStream = await sseResponse.Content.ReadAsStreamAsync(sseCts.Token);
        using var reader = new StreamReader(sseStream);

        // 2. 读取握手 endpoint 事件: "event: endpoint\r\ndata: /mcp/message?sessionId={sessionId}\r\n\r\n"
        var line1 = await reader.ReadLineAsync(sseCts.Token);
        Assert.Equal("event: endpoint", line1);

        var line2 = await reader.ReadLineAsync(sseCts.Token);
        Assert.NotNull(line2);
        Assert.StartsWith("data: /mcp/message?sessionId=", line2);

        var endpointUrl = line2["data: ".Length..].Trim();
        await reader.ReadLineAsync(sseCts.Token); // 消耗空行

        // 3. 通过提取到的 endpoint POST 发送 tools/list
        var postClient = await TestServerFixture.CreateApiKeyClientAsync(rawKey);
        var rpcReq = new
        {
            jsonrpc = "2.0",
            id = "sse-tools-1",
            method = "tools/list",
            @params = new { }
        };

        var postResp = await postClient.PostAsync(endpointUrl, TestServerFixture.Json(JsonSerializer.Serialize(rpcReq)));
        Assert.Equal(HttpStatusCode.Accepted, postResp.StatusCode);

        // 4. 从 SSE 下行管道读取推送的 message 事件
        var evtLine = await reader.ReadLineAsync(sseCts.Token);
        Assert.Equal("event: message", evtLine);

        var dataLine = await reader.ReadLineAsync(sseCts.Token);
        Assert.NotNull(dataLine);
        Assert.StartsWith("data: ", dataLine);

        var respJson = dataLine["data: ".Length..];
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;
        Assert.Equal("sse-tools-1", root.GetProperty("id").GetString());
        var tools = root.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.NotEmpty(tools);
    }

    private static async Task<HttpClient> CreateMcpApiKeyClientAsync(
        bool allowTerminal,
        bool allowFiles,
        bool allowGateway,
        bool allowSchedules = false,
        bool allowTranscode = false)
    {
        var adminClient = await TestServerFixture.CreateAdminClientAsync();
        var keyReq = new CreateApiKeyRequest(
            Name: $"Mcp-Scoped-{Guid.NewGuid():N}",
            AllowApi: true,
            AllowMcp: true,
            AllowTerminal: allowTerminal,
            AllowSchedules: allowSchedules,
            AllowFiles: allowFiles,
            AllowTranscode: allowTranscode,
            AllowGateway: allowGateway);

        var keyJson = JsonSerializer.Serialize(keyReq, AppJsonSerializerContext.Default.CreateApiKeyRequest);
        var keyResp = await adminClient.PostAsync("/api/ApiKeys", TestServerFixture.Json(keyJson));
        using var keyDoc = JsonDocument.Parse(await keyResp.Content.ReadAsStringAsync());
        var rawKey = keyDoc.RootElement.GetProperty("rawKey").GetString()!;

        return await TestServerFixture.CreateApiKeyClientAsync(rawKey);
    }
}
