using LinuxWebTool.Infrastructure.Features.SystemInfo.Platform;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class AppMetricsServiceTests
{
    [Fact]
    public void RecordRequest_CategorizesStatusCodesAccurately()
    {
        var service = new AppMetricsService();

        service.RecordRequest("GET", 200, 10.5);
        service.RecordRequest("POST", 201, 15.0);
        service.RecordRequest("GET", 302, 5.0);
        service.RecordRequest("GET", 401, 8.0);
        service.RecordRequest("GET", 404, 12.0);
        service.RecordRequest("POST", 500, 30.0);
        service.RecordRequest("POST", 503, 25.0);

        var snapshot = service.GetSnapshot();

        Assert.Equal(7, snapshot.TotalRequests);
        Assert.Equal(2, snapshot.Status2xx);
        Assert.Equal(1, snapshot.Status3xx);
        Assert.Equal(2, snapshot.Status4xx);
        Assert.Equal(2, snapshot.Status5xx);
        Assert.Equal(30.0, snapshot.MaxDurationMs);
        Assert.True(snapshot.AverageDurationMs > 0);
    }

    [Fact]
    public void ActiveRequests_And_Sessions_TrackProperly()
    {
        var service = new AppMetricsService();

        service.IncrementActiveRequests();
        service.IncrementActiveRequests();
        Assert.Equal(2, service.GetSnapshot().ActiveRequests);

        service.DecrementActiveRequests();
        Assert.Equal(1, service.GetSnapshot().ActiveRequests);

        service.SetActiveTerminalSessions(5);
        Assert.Equal(5, service.GetSnapshot().ActiveTerminalSessions);
    }

    [Fact]
    public void PercentilesAndAverages_CalculatedAccurately()
    {
        var service = new AppMetricsService();

        for (var i = 1; i <= 100; i++)
        {
            service.RecordRequest("GET", 200, i * 1.0);
        }

        var snapshot = service.GetSnapshot();

        Assert.Equal(100, snapshot.TotalRequests);
        Assert.Equal(50.5, snapshot.AverageDurationMs);
        Assert.Equal(100.0, snapshot.MaxDurationMs);
        Assert.Equal(50.0, snapshot.P50DurationMs);
        Assert.Equal(95.0, snapshot.P95DurationMs);
    }

    [Fact]
    public void HighConcurrency_RecordRequests_ThreadSafe()
    {
        var service = new AppMetricsService();
        const int iterations = 10_000;

        Parallel.For(0, iterations, i =>
        {
            service.IncrementActiveRequests();
            service.RecordRequest("GET", i % 2 == 0 ? 200 : 500, (i % 50) + 1.0);
            service.DecrementActiveRequests();
        });

        var snapshot = service.GetSnapshot();

        Assert.Equal(iterations, snapshot.TotalRequests);
        Assert.Equal(iterations / 2, snapshot.Status2xx);
        Assert.Equal(iterations / 2, snapshot.Status5xx);
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.True(snapshot.AverageDurationMs > 0);
    }
}
