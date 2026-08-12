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
/// Covers where the OPC UA client raises and drops its liveness latch. The latch moves only when
/// something calls for it, so every path that takes the session away owes a lowering call, and a
/// missing one leaves the client claiming to serve a session it no longer has until some later timer
/// happens to correct it.
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

        // Parks the load in the converter until the arming below, so the failure it goes on to cause
        // lands on a client that is already reporting itself as serving.
        using var converter = new PoisonedLoadConverter(waitForArming: TimeSpan.FromSeconds(15));

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

        try
        {
            // Act
            await source.StartAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(90),
                message: "The client should report itself operational before the poisoned load runs");

            converter.Arm();

            // Recorded by the retry loop after the listen lifetime has already been torn down, so
            // observing it means the whole teardown this test is about has completed.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.LastError is not null,
                timeout: TimeSpan.FromSeconds(60),
                message: "The poisoned initial state load should fail the connect attempt");

            // Assert
            Assert.True(converter.PoisonWasHandedOut);
            Assert.False(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The SDK's own reconnect raises liveness the moment the subscription transfer completes, and
    /// the full state sync that transfer schedules runs on the next health check tick. A sync that
    /// fails clears the session, so the tick running it owes the drop: nothing looks at liveness
    /// again until the tick after it, a whole health check interval later.
    /// </summary>
    [Fact]
    public async Task WhenTheStateSyncAfterAnSdkReconnectFails_ThenLivenessDropsWithTheClearedSession()
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

        // Poisons whatever load runs after the arming below, which is the full state sync, and
        // converts normally until then so the initial load succeeds.
        using var converter = new PoisonedLoadConverter(waitForArming: TimeSpan.Zero);

        var configuration = CreateClientConfiguration(
            port,
            loggerFactory,
            converter,

            // Wide enough that the rise asserted below cannot be a health check tick's, that the tick
            // running the failed sync is the one the drop is asserted against, and that reconnection
            // stall detection (two ticks) cannot take the recovery away from the SDK.
            healthCheckInterval: TimeSpan.FromSeconds(60),

            // The outage has to be detected while the server is still down, so the SDK's first
            // reconnect attempt fails and every attempt after it recreates the session instead of
            // reactivating the old one. Recreating is what carries the subscriptions across.
            keepAliveInterval: TimeSpan.FromSeconds(1));

        await using var source = new OpcUaSubjectClientSource(
            root, configuration, loggerFactory.CreateLogger<OpcUaSubjectClientSource>());

        try
        {
            await source.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(90),
                message: "Initial sync should complete");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(60),
                message: "The first health check tick should report the session as operational");

            // Act - a restart rather than a transport disconnect. A disconnect leaves the session
            // valid on the server, so the SDK reactivates that same session and the client abandons
            // the preserved one instead of transferring it. A restart invalidates it, which makes the
            // SDK replace the session and carry the subscriptions across to the new one.
            converter.Arm();
            await server.RestartAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => !source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(30),
                message: "The failing keep-alive should drop liveness");

            // Assert - raised again by the completed transfer on the reconnect callback's own thread,
            // far inside the health check interval, so no tick can be what raised it.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(30),
                message: "The completed subscription transfer should raise liveness again");

            // And taken back by the tick that runs the failed full state sync, which clears the
            // session the rise was standing on.
            await AsyncTestHelpers.WaitUntilAsync(
                () => !source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(75),
                message: "The failed full state sync should drop liveness with the session it cleared");

            Assert.True(converter.PoisonWasHandedOut);
            Assert.Null(source.Diagnostics.SessionId);
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
        TimeSpan? retryTime = null,
        TimeSpan? keepAliveInterval = null) => new()
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
        KeepAliveInterval = keepAliveInterval ?? TimeSpan.FromSeconds(5),
        OperationTimeout = TimeSpan.FromSeconds(30),

        BufferTime = TimeSpan.FromMilliseconds(100),
        CertificateStoreBasePath = port.CertificateStoreBasePath
    };

    /// <summary>
    /// Hands one property a value of the wrong CLR type once armed, which makes the state load that
    /// converts it throw where it writes the read values into the model.
    /// </summary>
    /// <remarks>
    /// Deliberately never throws itself. The subscription callback converts through the same instance
    /// on the SDK's publish thread, and the writes it makes swallow their own failures, so a throwing
    /// converter would fail there instead of failing the load.
    /// </remarks>
    private sealed class PoisonedLoadConverter : OpcUaValueConverter, IDisposable
    {
        private readonly ManualResetEventSlim _armed = new(false);
        private readonly TimeSpan _waitForArming;
        private int _poisonWasHandedOut; // 0 = false, 1 = true (written from the load and callback threads)

        /// <param name="waitForArming">
        /// How long a conversion of the poisoned property parks waiting to be armed. Zero converts
        /// normally until <see cref="Arm"/> lands; a positive value holds the load there, which is
        /// what makes it fail on a client that already reports itself as serving. Bounded either way,
        /// so an assumption that stops holding fails the assertions rather than hanging the run.
        /// </param>
        internal PoisonedLoadConverter(TimeSpan waitForArming)
        {
            _waitForArming = waitForArming;
        }

        /// <summary>
        /// Gets whether the poison was handed out at least once, which is what tells a failed load
        /// caused by this converter from one caused by something else.
        /// </summary>
        internal bool PoisonWasHandedOut => Volatile.Read(ref _poisonWasHandedOut) == 1;

        internal void Arm() => _armed.Set();

        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
        {
            if (property.Name != nameof(TestRoot.Name) || !_armed.Wait(_waitForArming))
            {
                return base.ConvertToPropertyValue(nodeValue, property);
            }

            Volatile.Write(ref _poisonWasHandedOut, 1);

            // An int for a string property: the generated setter casts, so the write throws.
            return 0;
        }

        public void Dispose() => _armed.Dispose();
    }
}
