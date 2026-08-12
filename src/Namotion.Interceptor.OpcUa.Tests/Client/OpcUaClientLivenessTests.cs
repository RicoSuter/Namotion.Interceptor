using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Covers where the OPC UA client drops its liveness latch. The latch is raised and lowered by
/// explicit calls rather than computed on every read, so every path that takes the session away owes
/// a lowering call, and a missing one leaves the client claiming to serve a session it no longer has
/// until some later timer happens to correct it.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaClientLivenessTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaClientLivenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The connect attempt reaches the health check loop, which raises liveness, and only then fails
    /// in the initial state load. The retry loop records that failure but touches no liveness, so the
    /// listen lifetime's own teardown is the only thing that can drop it.
    /// </summary>
    [Fact]
    public async Task WhenTheAttemptFailsAfterTheSessionIsUp_ThenLivenessDropsWithTheAttempt()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<TestRoot>(logger);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        using var loggerFactory = CreateClientLoggerFactory(logger);
        var root = new TestRoot(CreateClientContext());

        // Read by the converter only once the client is running, which cannot happen before the
        // assignment below.
        OpcUaSubjectClientSource? startedSource = null;
        var converter = new PoisonedInitialLoadConverter(() => startedSource?.Diagnostics.IsOperational ?? false);

        var configuration = CreateClientConfiguration(
            port,
            loggerFactory,
            converter,
            healthCheckInterval: TimeSpan.FromSeconds(1),

            // Far longer than the window asserted in, so the next attempt's own lowering call cannot
            // be what clears the latch here.
            retryTime: TimeSpan.FromMinutes(5));

        await using var source = new OpcUaSubjectClientSource(
            root, configuration, loggerFactory.CreateLogger<OpcUaSubjectClientSource>());
        startedSource = source;

        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => converter.SawOperational,
                timeout: TimeSpan.FromSeconds(90),
                message: "The client should report itself operational before the poisoned load runs");

            // Recorded by the retry loop after the listen lifetime has already been torn down, so
            // observing it means the whole teardown this test is about has completed.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.LastError is not null,
                timeout: TimeSpan.FromSeconds(60),
                message: "The poisoned initial state load should fail the connect attempt");

            // Assert
            Assert.False(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The fault-injected kill clears the session without going through either of the paths that
    /// report a lost connection, so it owes the lowering call itself.
    /// </summary>
    [Fact]
    public async Task WhenTheSessionIsKilled_ThenLivenessDropsWithTheKill()
    {
        // Arrange
        var logger = new TestLogger(_output);
        using var port = await OpcUaTestPortPool.AcquireAsync();

        await using var server = new OpcUaTestServer<TestRoot>(logger);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
            },
            baseAddress: port.BaseAddress,
            certificateStoreBasePath: port.CertificateStoreBasePath);

        using var loggerFactory = CreateClientLoggerFactory(logger);
        var root = new TestRoot(CreateClientContext());

        var configuration = CreateClientConfiguration(
            port,
            loggerFactory,
            new OpcUaValueConverter(),

            // Far longer than the window asserted in, so the health check loop cannot be what drops
            // liveness after the kill.
            healthCheckInterval: TimeSpan.FromSeconds(60));

        await using var source = new OpcUaSubjectClientSource(
            root, configuration, loggerFactory.CreateLogger<OpcUaSubjectClientSource>());

        try
        {
            // Waited out rather than killed mid-load, so a load that fails on the missing session
            // cannot be what tears the attempt down and drops liveness for its own reasons.
            await source.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(90),
                message: "Initial sync should complete");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(30),
                message: "The health check loop should report the session as operational");

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert - the kill clears the session before it returns, so liveness has to be gone with
            // it rather than a health check interval later.
            Assert.False(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    private static IInterceptorSubjectContext CreateClientContext() =>
        InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithSourceMonitoring();

    private static ILoggerFactory CreateClientLoggerFactory(TestLogger logger) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(logger, "Client", LogLevel.Information);
        });

    private static OpcUaClientConfiguration CreateClientConfiguration(
        PortLease port,
        ILoggerFactory loggerFactory,
        OpcUaValueConverter valueConverter,
        TimeSpan healthCheckInterval,
        TimeSpan? retryTime = null) => new()
    {
        ServerUrl = port.ServerUrl,
        RootPath = ["Root"],
        TypeResolver = new OpcUaTypeResolver(loggerFactory.CreateLogger<OpcUaTypeResolver>()),
        ValueConverter = valueConverter,
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),

        SubscriptionHealthCheckInterval = healthCheckInterval,
        RetryTime = retryTime ?? TimeSpan.FromSeconds(10),

        ReconnectInterval = TimeSpan.FromSeconds(1),
        ReconnectHandlerTimeout = TimeSpan.FromSeconds(5),
        MaxReconnectDuration = TimeSpan.FromSeconds(20),

        SessionTimeout = TimeSpan.FromSeconds(30),
        KeepAliveInterval = TimeSpan.FromSeconds(5),
        OperationTimeout = TimeSpan.FromSeconds(30),

        BufferTime = TimeSpan.FromMilliseconds(100),
        CertificateStoreBasePath = port.CertificateStoreBasePath
    };

    /// <summary>
    /// Hands one property a value of the wrong CLR type, which makes the initial state load throw
    /// where it writes the read values into the model, after the client is fully set up.
    /// </summary>
    /// <remarks>
    /// Deliberately never throws itself. The subscription callback converts through the same instance
    /// on the SDK's publish thread, and the writes it makes swallow their own failures, so a throwing
    /// converter would fail there instead of failing the connect attempt.
    /// </remarks>
    private sealed class PoisonedInitialLoadConverter : OpcUaValueConverter
    {
        private readonly Func<bool> _isOperational;
        private int _sawOperational; // 0 = false, 1 = true (written from the load and callback threads)

        internal PoisonedInitialLoadConverter(Func<bool> isOperational)
        {
            _isOperational = isOperational;
        }

        /// <summary>
        /// Gets whether the gate below ever saw liveness standing. The poison is only handed out
        /// afterwards, so this is what makes the failure land on a client reporting itself as serving
        /// rather than on one that never got there.
        /// </summary>
        internal bool SawOperational => Volatile.Read(ref _sawOperational) == 1;

        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
        {
            if (property.Name != nameof(TestRoot.Name))
            {
                return base.ConvertToPropertyValue(nodeValue, property);
            }

            // Parks the caller until the health check loop has raised liveness. Bounded, so an
            // assumption that stops holding fails the assertions rather than hanging the run.
            if (SpinWait.SpinUntil(_isOperational, TimeSpan.FromSeconds(15)))
            {
                Interlocked.Exchange(ref _sawOperational, 1);
            }

            // An int for a string property: the generated setter casts, so the write throws.
            return 0;
        }
    }
}
