using System.Net;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class LoginAuditContentionTests
{
    [Fact]
    public async Task Valid_login_returns_token_even_when_audit_database_is_locked()
    {
        var app = await TestServerFixture.GetAppAsync();
        using var client = await TestServerFixture.CreateAnonymousClientAsync();
        var factory = app.Services.GetRequiredService<DbConnectionFactory>();
        using var owner = factory.CreateConnection();
        owner.Open();
        using var transaction = owner.BeginTransaction();
        using var command = owner.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operation_log (Id, Action) VALUES (@id, 'hold-write-lock')";
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.ExecuteNonQuery();

        var request = Task.Run(() => client.PostAsync("/api/Auth/Login",
            new StringContent("{\"userName\":\"admin\",\"password\":\"integration-test-password\"}", Encoding.UTF8, "application/json")));
        try
        {
            using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("token").GetString()));
        }
        finally
        {
            transaction.Rollback();
            await request;
        }
    }
}
