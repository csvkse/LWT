using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class HostCircuitBreakerTests
{
    [Fact]
    public void InitialState_ShouldAllowAttempt()
    {
        var breaker = new HostCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));
        var hostId = Guid.NewGuid().ToString();

        Assert.True(breaker.AllowExecution(hostId, out var reason));
        Assert.Null(reason);
        Assert.Equal(CircuitState.Closed, breaker.GetState(hostId));
    }

    [Fact]
    public void FailuresUnderThreshold_ShouldRemainClosed()
    {
        var breaker = new HostCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));
        var hostId = Guid.NewGuid().ToString();

        breaker.ReportFailure(hostId, "Timeout 1");
        Assert.True(breaker.AllowExecution(hostId, out _));
        Assert.Equal(CircuitState.Closed, breaker.GetState(hostId));

        breaker.ReportFailure(hostId, "Timeout 2");
        Assert.True(breaker.AllowExecution(hostId, out _));
        Assert.Equal(CircuitState.Closed, breaker.GetState(hostId));
    }

    [Fact]
    public void FailuresMeetingThreshold_ShouldTripToOpen()
    {
        var breaker = new HostCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));
        var hostId = Guid.NewGuid().ToString();

        breaker.ReportFailure(hostId, "Error 1");
        breaker.ReportFailure(hostId, "Error 2");
        breaker.ReportFailure(hostId, "Error 3");

        Assert.False(breaker.AllowExecution(hostId, out var reason));
        Assert.Equal(CircuitState.Open, breaker.GetState(hostId));
        Assert.NotNull(reason);
        Assert.Contains("熔断", reason);
    }

    [Fact]
    public void Success_ShouldResetFailureCountAndClose()
    {
        var breaker = new HostCircuitBreaker(failureThreshold: 3, openDuration: TimeSpan.FromMinutes(1));
        var hostId = Guid.NewGuid().ToString();

        breaker.ReportFailure(hostId, "Error 1");
        breaker.ReportFailure(hostId, "Error 2");
        breaker.ReportSuccess(hostId);

        Assert.True(breaker.AllowExecution(hostId, out _));
        Assert.Equal(CircuitState.Closed, breaker.GetState(hostId));

        // Now failure count is reset, so 1 more failure should not trip
        breaker.ReportFailure(hostId, "Error 3");
        Assert.True(breaker.AllowExecution(hostId, out _));
        Assert.Equal(CircuitState.Closed, breaker.GetState(hostId));
    }

    [Fact]
    public async Task OpenDurationElapsed_ShouldTransitionToHalfOpenAndRecoverOnSuccess()
    {
        var shortDuration = TimeSpan.FromMilliseconds(50);
        var breaker = new HostCircuitBreaker(failureThreshold: 2, openDuration: shortDuration);
        var hostId = Guid.NewGuid().ToString();

        breaker.ReportFailure(hostId, "Fail 1");
        breaker.ReportFailure(hostId, "Fail 2");

        Assert.False(breaker.AllowExecution(hostId, out _));
        Assert.Equal(CircuitState.Open, breaker.GetState(hostId));

        // Wait for cooldown
        await Task.Delay(100);

        // First attempt in half-open state should be allowed
        Assert.True(breaker.AllowExecution(hostId, out _));
        Assert.Equal(CircuitState.HalfOpen, breaker.GetState(hostId));

        // When attempt succeeds, state becomes Closed
        breaker.ReportSuccess(hostId);
        Assert.Equal(CircuitState.Closed, breaker.GetState(hostId));
        Assert.True(breaker.AllowExecution(hostId, out _));
    }
}
