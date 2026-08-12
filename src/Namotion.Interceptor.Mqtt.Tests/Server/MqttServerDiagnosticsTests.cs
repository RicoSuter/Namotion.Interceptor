using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mqtt.Tests.Server;

/// <summary>
/// Pins the wiring between the broker and the diagnostics it publishes: that the diagnostics read the
/// metrics the connector itself writes to, and that a broker which has never listened says so. The
/// liveness transitions themselves need a bound port and are covered by
/// <see cref="MqttServerLivenessTests"/>.
/// </summary>
public class MqttServerDiagnosticsTests
{
    [Fact]
    public async Task WhenNeverStarted_ThenTheServerReportsNotOperationalAndNoThroughput()
    {
        // Arrange
        await using var server = CreateServer(new MqttServerConfiguration());

        // Act
        var diagnostics = server.Diagnostics;

        // Assert
        Assert.False(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.StartTime);
        Assert.Null(diagnostics.LastError);
        Assert.Equal(0, diagnostics.ConnectedClientCount);

        // Null rather than 0: the broker measures neither direction.
        Assert.Null(diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(diagnostics.Throughput.OutgoingPerSecond);
    }

    /// <summary>
    /// A diagnostics view built over a second <see cref="Connectors.Diagnostics.ConnectorMetrics"/>
    /// would keep reporting no error at all, which is what this reads back.
    /// </summary>
    [Fact]
    public async Task WhenTheBindAddressCannotBeParsed_ThenTheFailureReachesTheServerDiagnostics()
    {
        // Arrange: the address is parsed inside the pump, before the broker binds anything.
        await using var server = CreateServer(new MqttServerConfiguration { BrokerHost = "not-an-ip-address" });

        // Act
        await Assert.ThrowsAsync<FormatException>(() => server.StartAsync(CancellationToken.None));

        // Assert
        Assert.IsType<FormatException>(server.Diagnostics.LastError);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.False(server.Diagnostics.IsOperational);
    }

    private static MqttSubjectServer CreateServer(MqttServerConfiguration configuration)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new MqttSubjectServer(
            new DeliveryRuleTestRoot(context), configuration, NullLogger<MqttSubjectServer>.Instance);
    }
}
