using Namotion.Interceptor.Connectors.TwinCAT.Client;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class AdsClientDiagnosticsTests
{
    private class TestDiagnosticsSource : IAdsClientDiagnosticsSource
    {
        public AdsState? CurrentState { get; set; }
        public bool IsConnected { get; set; }
        public int NotificationCount { get; set; }
        public int PolledCount { get; set; }
        public long TotalReconnectionAttempts { get; set; }
        public long SuccessfulReconnections { get; set; }
        public long FailedReconnections { get; set; }
        public DateTimeOffset? LastConnectedAt { get; set; }
        public bool IsCircuitBreakerOpen { get; set; }
        public long CircuitBreakerTripCount { get; set; }
    }

    [Fact]
    public void Properties_ShouldDelegateToSource()
    {
        // Arrange
        var lastConnected = new DateTimeOffset(2025, 1, 28, 12, 0, 0, TimeSpan.Zero);
        var source = new TestDiagnosticsSource
        {
            CurrentState = AdsState.Run,
            IsConnected = true,
            NotificationCount = 42,
            PolledCount = 10,
            TotalReconnectionAttempts = 5,
            SuccessfulReconnections = 3,
            FailedReconnections = 2,
            LastConnectedAt = lastConnected,
            IsCircuitBreakerOpen = false,
            CircuitBreakerTripCount = 1
        };

        // Act
        var diagnostics = new AdsClientDiagnostics(source);

        // Assert
        Assert.Equal(AdsState.Run, diagnostics.State);
        Assert.True(diagnostics.IsConnected);
        Assert.Equal(42, diagnostics.NotificationVariableCount);
        Assert.Equal(10, diagnostics.PolledVariableCount);
        Assert.Equal(5, diagnostics.TotalReconnectionAttempts);
        Assert.Equal(3, diagnostics.SuccessfulReconnections);
        Assert.Equal(2, diagnostics.FailedReconnections);
        Assert.Equal(lastConnected, diagnostics.LastConnectedAt);
        Assert.False(diagnostics.IsCircuitBreakerOpen);
        Assert.Equal(1, diagnostics.CircuitBreakerTripCount);
    }

    [Fact]
    public void Properties_WithNullState_ShouldReturnNull()
    {
        // Arrange
        var source = new TestDiagnosticsSource
        {
            CurrentState = null,
            LastConnectedAt = null
        };

        // Act
        var diagnostics = new AdsClientDiagnostics(source);

        // Assert
        Assert.Null(diagnostics.State);
        Assert.Null(diagnostics.LastConnectedAt);
    }
}
