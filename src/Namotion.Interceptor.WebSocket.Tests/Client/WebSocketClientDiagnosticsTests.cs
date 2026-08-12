using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

/// <summary>
/// Pins the wiring between the client and the diagnostics it publishes: that the claimed-property
/// gauge is pointed at the client's own ownership manager, and that a client which has never
/// connected says so. The liveness transitions themselves need a server and are covered by
/// <see cref="WebSocketClientLivenessTests"/>.
/// </summary>
public class WebSocketClientDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree plus the defaults a fresh <c>SourceMetrics</c> reports,
    /// not behavioural coverage: nothing here can fail while the members exist. Its value is that
    /// the two throughput rates stay <c>null</c> rather than being wired to a counter this connector
    /// does not feed, which would report a misleading zero.
    /// </summary>
    [Fact]
    public async Task WhenNeverConnected_ThenTheSourceReportsNotOperationalAndNoThroughput()
    {
        // Arrange & Act
        await using var source = CreateClientSource();

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
        Assert.Null(source.Diagnostics.OperationalChangeTime);
        Assert.Null(source.Diagnostics.StartTime);
        Assert.Null(source.Diagnostics.LastError);
        Assert.Equal(0, source.Diagnostics.ClaimedPropertyCount);

        // Null rather than 0: the client measures neither direction.
        Assert.Null(source.Diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(source.Diagnostics.Throughput.OutgoingPerSecond);
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

    private static WebSocketSubjectClientSource CreateClientSource()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new WebSocketSubjectClientSource(
            new TestRoot(context),
            // Never dialled: nothing in these tests starts a connect attempt.
            new WebSocketClientConfiguration { ServerUri = new Uri("ws://localhost:59999/ws") },
            NullLogger<WebSocketSubjectClientSource>.Instance);
    }
}
