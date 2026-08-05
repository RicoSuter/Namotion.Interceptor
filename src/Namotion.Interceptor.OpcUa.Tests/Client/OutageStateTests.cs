using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Verifies that an outage during the SDK's own auto-reconnect window (triggered from
/// SessionManager.OnKeepAlive, before any manual reconnection takes over) is visible on
/// <see cref="ISubjectSource.State"/> instead of leaving the source reporting Synchronized
/// throughout the whole outage.
/// </summary>
[Trait("Category", "Integration")]
public class OutageStateTests
{
    private readonly ITestOutputHelper _output;

    public OutageStateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenTheSourceReportsConnectingUntilItRecovers()
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

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(logger, "Client", LogLevel.Information);
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithSourceMonitoring();

        var root = new TestRoot(context);

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = port.ServerUrl,
            RootPath = ["Root"],
            TypeResolver = new OpcUaTypeResolver(loggerFactory.CreateLogger<OpcUaTypeResolver>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),

            // Fast detection/recovery so the test observes the outage window without waiting minutes.
            // SubscriptionHealthCheckInterval is set well above KeepAliveInterval on purpose: the health
            // check loop has its own independent dead-session detection (HandleDeadSessionAsync), which
            // sets IsReconnecting via SetReconnecting(true) BEFORE StartBuffering() in ReconnectSessionAsync
            // - the opposite order from OnKeepAlive's ReportConnectionLost-before-IsReconnecting. A wide
            // margin makes it overwhelmingly unlikely for that independent path to race ahead of OnKeepAlive
            // and win, which would make the IsReconnecting-timing assertion below flake for reasons unrelated
            // to the fix under test.
            ReconnectInterval = TimeSpan.FromSeconds(1),
            ReconnectHandlerTimeout = TimeSpan.FromSeconds(5),
            MaxReconnectDuration = TimeSpan.FromSeconds(20),
            SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(10),

            SessionTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(1),
            OperationTimeout = TimeSpan.FromSeconds(30),

            BufferTime = TimeSpan.FromMilliseconds(100),

            CertificateStoreBasePath = port.CertificateStoreBasePath
        };

        await using var source = new OpcUaSubjectClientSource(root, configuration, loggerFactory.CreateLogger<OpcUaSubjectClientSource>());

        try
        {
            await source.StartAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(60),
                message: "Initial sync should complete");
            var firstSynchronizedAt = source.LastSynchronizedAt;

            // Act - Disconnect is the soft fault: it breaks the transport without stopping the
            // connector, which is exactly what a real network blip does. It kills the transport
            // channel while the session stays assigned, so OnKeepAlive sees the bad status and
            // hands off to the SDK's SessionReconnectHandler.BeginReconnect - the code path this
            // test exists to cover.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert - Diagnostics.IsReconnecting flips true inside OnKeepAlive's lock, right after
            // ReportConnectionLost() (see SessionManager.OnKeepAlive). Asserting State==Connecting the
            // moment IsReconnecting is observed true catches the state at the start of the SDK's own
            // reconnect window. A weaker "wait for Connecting, then wait for Synchronized" pair would
            // pass vacuously even without the fix: PerformFullStateSyncIfNeededAsync still buffers
            // briefly once the SDK finishes reconnecting on its own, so Connecting would still flash by
            // right before Synchronized regardless of whether OnKeepAlive reports the loss up front.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(30),
                message: "SDK should begin auto-reconnecting after the transport is disconnected");
            Assert.Equal(SourceState.Connecting, source.State);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(60),
                message: "Source should recover to Synchronized after the SDK reconnects");
            Assert.NotNull(firstSynchronizedAt);
            Assert.True(source.LastSynchronizedAt > firstSynchronizedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }
}
