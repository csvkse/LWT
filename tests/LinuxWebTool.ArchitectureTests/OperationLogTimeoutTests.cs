using System.Diagnostics;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class OperationLogTimeoutTests
{
    [Fact]
    public async Task Audit_insert_does_not_wait_for_default_database_timeout_when_locked()
    {
        // Shared in-memory database keeps the test independent of disk performance.
        var factory = new DbConnectionFactory($"Data Source=audit-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30");
        using var owner = factory.CreateConnection();
        owner.Open();
        DbSetup.Initialize(factory);
        using var transaction = owner.BeginTransaction();
        using var command = owner.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operation_log (Id, Action) VALUES ('locked', 'test')";
        command.ExecuteNonQuery();

        var clock = Stopwatch.StartNew();
        var insert = Task.Run(() => new OperationLogStore(factory).InsertAsync(new OperationLog { Action = "登录" }));
        try
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => insert.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 });
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            transaction.Rollback();
            try { await insert; } catch (SqliteException) { }
        }
    }
}
