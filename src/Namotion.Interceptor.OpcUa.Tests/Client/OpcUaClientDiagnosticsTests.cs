using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Covers what the OPC UA client reports about itself before and around a session, which is
/// everything the diagnostics surface answers without a server on the other end.
/// </summary>
public class OpcUaClientDiagnosticsTests
{
    [Fact]
    public async Task WhenNeverConnected_ThenEveryGetterAnswersWithoutThrowing()
    {
        // Arrange & Act
        await using var source = CreateClientSource();
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.LastError);
        Assert.Null(diagnostics.StartTime);
        Assert.False(diagnostics.IsReconnecting);
        Assert.Null(diagnostics.SessionId);
        Assert.Equal(0, diagnostics.SubscriptionCount);
        Assert.Equal(0, diagnostics.MonitoredItemCount);
        Assert.Equal(0, diagnostics.ClaimedPropertyCount);
        Assert.Null(diagnostics.Polling);
        Assert.Null(diagnostics.ReadAfterWrite);
        Assert.Equal(0, diagnostics.Reconnects.TotalAttempts);
        Assert.Null(diagnostics.Reconnects.LastConnectionTime);
    }

    [Fact]
    public async Task WhenReadThroughTheSourceBase_ThenItIsTheSameDiagnosticsObject()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        var throughTheBase = ((SubjectSourceBase)source).Diagnostics;

        // Assert - a covariant override rather than a second, unrelated view.
        Assert.Same(source.Diagnostics, throughTheBase);
    }

    [Fact]
    public async Task WhenTheSessionBecomesHealthy_ThenLivenessRisesAndItsTimestampIsStamped()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        source.NotifySessionHealthy();

        // Assert
        Assert.True(source.Diagnostics.IsOperational);
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenLivenessFallsAndItsTimestampMoves()
    {
        // Arrange
        await using var source = CreateClientSource();
        source.NotifySessionHealthy();
        var upAt = source.Diagnostics.OperationalChangeTime;

        // Act
        WaitForClockTick();
        source.NotifyConnectionLost();

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
        Assert.True(source.Diagnostics.OperationalChangeTime > upAt);
    }

    [Fact]
    public async Task WhenAnErrorIsReportedAndTheClientRecovers_ThenLastErrorSurvives()
    {
        // Arrange - through a metrics instance of its own, because the source's own is protected.
        // What is under test is that the client reads the shared sticky error rather than a
        // client-owned field it used to clear on every successful (re)connection.
        await using var source = CreateClientSource();
        var metrics = new SourceMetrics();
        var diagnostics = new OpcUaClientDiagnostics(source, metrics);
        var error = new InvalidOperationException("session failed");

        // Act
        metrics.ReportError(error);
        metrics.MarkOperational();

        // Assert
        Assert.Same(error, diagnostics.LastError);
        Assert.True(diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenPropertiesAreClaimed_ThenClaimedPropertyCountFollowsTheOwnershipManager()
    {
        // Arrange
        await using var source = CreateClientSource();
        var property = ((TestRoot)source.RootSubject).GetPropertyReference(nameof(TestRoot.Name));

        // Act
        Assert.True(source.Ownership.ClaimSource(property));

        // Assert
        Assert.Equal(1, source.Diagnostics.ClaimedPropertyCount);

        // And it falls again, because it is a gauge rather than a counter.
        source.Ownership.ReleaseSource(property);
        Assert.Equal(0, source.Diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public async Task WhenReconnectionsAreRecorded_ThenTheReconnectBlockReportsThem()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act - distinct counts per outcome, so a getter wired to the wrong counter is caught.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            source.ReconnectionMetrics.RecordAttemptStart();
        }

        source.ReconnectionMetrics.RecordSuccess();
        source.ReconnectionMetrics.RecordFailure();
        source.ReconnectionMetrics.RecordFailure();
        source.ReconnectionMetrics.RecordAbandoned();
        source.ReconnectionMetrics.RecordAbandoned();
        source.ReconnectionMetrics.RecordAbandoned();

        // Assert
        var reconnects = source.Diagnostics.Reconnects;
        Assert.Equal(6, reconnects.TotalAttempts);
        Assert.Equal(1, reconnects.TotalSucceeded);
        Assert.Equal(2, reconnects.TotalFailed);
        Assert.Equal(3, reconnects.TotalAbandoned);
        Assert.NotNull(reconnects.LastConnectionTime);
    }

    /// <summary>
    /// Spins until the wall clock reports a new tick, so a timestamp stamped after this call cannot
    /// land on the same value as one stamped before it. A condition rather than a fixed delay,
    /// because the clock's resolution differs per platform and is coarse on Windows.
    /// </summary>
    private static void WaitForClockTick()
    {
        var start = DateTimeOffset.UtcNow.UtcTicks;

        SpinWait spin = default;
        while (DateTimeOffset.UtcNow.UtcTicks == start)
        {
            spin.SpinOnce();
        }
    }
}
