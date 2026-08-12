using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// Pins the wiring between the server and the diagnostics it publishes: that the diagnostics read the
/// metrics the connector itself writes to, and that a server which has never served says so. The
/// liveness transitions themselves need a running transport and are covered by
/// <see cref="Integration.OpcUaServerLivenessTests"/>.
/// </summary>
public class OpcUaServerDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree rather than behavioural coverage: every value asserted
    /// here is what a fresh <c>ConnectorMetrics</c> reports, so this fails only if a member moves or
    /// changes type. The transitions are covered by <see cref="Integration.OpcUaServerLivenessTests"/>.
    /// </summary>
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsNotOperational()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
        Assert.Null(server.Diagnostics.OperationalChangeTime);
        Assert.Null(server.Diagnostics.StartTime);
        Assert.Null(server.Diagnostics.LastError);
        Assert.Equal(0, server.Diagnostics.ConsecutiveFailures);
        Assert.Equal(0, server.Diagnostics.ActiveSessionCount);
    }

    [Fact]
    public void WhenThroughputIsInstrumented_ThenBothDirectionsReportARate()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        // A null rate means "this connector does not measure the direction", so it would mean the two
        // counters the read and write paths feed never reached the metrics the diagnostics read.
        Assert.NotNull(server.Diagnostics.Throughput.IncomingPerSecond);
        Assert.NotNull(server.Diagnostics.Throughput.OutgoingPerSecond);
    }

    /// <summary>
    /// The application instance is built outside the restart loop's own try, so a failure there leaves
    /// the pump rather than being retried. It is the cheapest reachable failure and pins that the
    /// diagnostics read the connector's own metrics: a diagnostics view built over a second
    /// <c>ConnectorMetrics</c> would keep reporting no error at all.
    /// </summary>
    [Fact]
    public async Task WhenTheServerCannotBuildItsApplication_ThenTheFailureReachesItsOwnDiagnostics()
    {
        // Arrange
        using var server = CreateServer(new FailingOpcUaServerConfiguration());

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(server.Diagnostics.LastError);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.False(server.Diagnostics.IsOperational);
    }

    /// <summary>
    /// The restart loop builds a new change queue processor per attempt and registers it against a
    /// QueueMetrics that permits one live registration at a time, so a missing release would make the
    /// second attempt fail on the registration rather than on the transport. Read back through
    /// <c>LastError</c>, which is the only place that distinguishes the two.
    /// <para>
    /// Tagged as an integration test because it drives the server's real exponential backoff and its
    /// certificate store path, so it costs seconds of wall clock and its duration depends on the
    /// backoff jitter.
    /// </para>
    /// </summary>
    [Trait("Category", "Integration")]
    [Fact]
    public async Task WhenAStartAttemptFails_ThenTheNextAttemptCanRegisterItsOwnChangeQueue()
    {
        // Arrange: the certificate check is the first failure point inside the loop's own try, so the
        // attempt gets far enough to have registered its processor before it fails.
        using var server = CreateServer(
            new UncheckableCertificateOpcUaServerConfiguration { CleanCertificateStore = false });

        try
        {
            await server.StartAsync(CancellationToken.None);

            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.ConsecutiveFailures >= 2,
                message: "The server should have retried its failing start at least once.");

            // Assert
            Assert.NotNull(server.Diagnostics.LastError);
            Assert.DoesNotContain(
                "registration is already live",
                server.Diagnostics.LastError!.Message,
                StringComparison.Ordinal);
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static OpcUaSubjectServer CreateServer(OpcUaServerConfiguration? configuration = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new OpcUaSubjectServer(
            new DeliveryRuleTestRoot(context),
            configuration ?? new OpcUaServerConfiguration(),
            NullLogger.Instance);
    }

    private sealed class FailingOpcUaServerConfiguration : OpcUaServerConfiguration
    {
        public override Task<ApplicationInstance> CreateApplicationInstanceAsync() =>
            throw new InvalidOperationException("The OPC UA application instance could not be created.");
    }

    private sealed class UncheckableCertificateOpcUaServerConfiguration : OpcUaServerConfiguration
    {
        public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
        {
            var application = await base.CreateApplicationInstanceAsync().ConfigureAwait(false);

            // Without a configuration the certificate check fails immediately, without touching the
            // certificate stores or a port.
            application.ApplicationConfiguration = null!;
            return application;
        }
    }
}
