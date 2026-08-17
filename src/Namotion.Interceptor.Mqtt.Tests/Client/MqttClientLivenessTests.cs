using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mqtt.Tests.Client;

/// <summary>
/// The client reports liveness from three places the broker drives: the initial connect, the
/// broker's disconnect event, and the connection monitor's own reconnect. None of them is observable
/// without a real broker.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class MqttClientLivenessTests
{
    [InterceptorSubject]
    public partial class LivenessTestRoot
    {
        [Path("mqtt", "Name")]
        public partial string Name { get; set; }

        public LivenessTestRoot()
        {
            Name = string.Empty;
        }
    }

    [Fact]
    public async Task WhenTheClientConnects_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort);

        // Act
        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);

        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational,
            message: "The client should report operational once it has connected to the broker.");

        // Assert
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
        Assert.NotNull(source.Diagnostics.StartTime);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(source.Diagnostics.IsOperational);

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenTheConnectionDrops_ThenLivenessFallsAndRisesAgainOnReconnect()
    {
        // Arrange - a reconnect delay far longer than the poll interval below, so the client cannot
        // be back before the drop has been observed.
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort, reconnectDelay: TimeSpan.FromSeconds(2));

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational once it has connected to the broker.");

            var connectedAt = source.Diagnostics.OperationalChangeTime;

            // Act - Disconnect is the soft fault: it breaks the broker connection without stopping the
            // connector, so the connection monitor reconnects to the still-running broker.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !source.Diagnostics.IsOperational,
                message: "A disconnected client should stop reporting that it is serving.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational again once the monitor has reconnected.");

            // The rise is a second transition rather than the first one never having been dropped.
            Assert.True(source.Diagnostics.OperationalChangeTime > connectedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheClientIsForceKilled_ThenLivenessCyclesAndTheTransportIsReplaced()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort, reconnectDelay: TimeSpan.FromSeconds(1));

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational once it has connected to the broker.");

            var firstClient = GetCurrentClient(source);

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !source.Diagnostics.IsOperational,
                timeout: TimeSpan.FromSeconds(3),
                message: "A force-killed client should report the transport outage.");
            var downAt = source.Diagnostics.OperationalChangeTime;

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "A force-killed client should become operational on a replacement transport.");

            Assert.NotSame(firstClient, GetCurrentClient(source));
            Assert.False(firstClient.IsConnected);
            Assert.True(source.Diagnostics.OperationalChangeTime > downAt);
            Assert.Null(source.Diagnostics.LastError);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenDisconnectCallbackArrivesAfterReconnect_ThenHealthyConnectionRemainsOperational()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort, reconnectDelay: TimeSpan.FromMilliseconds(200));
        using var stateRecorder = SourceStateRecorder.SubscribeTo(source);

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational once it has connected to the broker.");
            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "The initial subscription should complete.",
                SourceState.Synchronized);

            var client = GetCurrentClient(source);
            var monitor = GetConnectionMonitor(source);
            var delayedDisconnectedHandler = GetDisconnectedHandler(source);
            var disconnectedArgs = new TaskCompletionSource<MqttClientDisconnectedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task CaptureDisconnectedArgsAsync(MqttClientDisconnectedEventArgs args)
            {
                disconnectedArgs.TrySetResult(args);
                return Task.CompletedTask;
            }

            // Hold the raw MQTTnet callback while the monitor handles the confirmed transport loss.
            client.DisconnectedAsync -= delayedDisconnectedHandler;
            client.DisconnectedAsync += CaptureDisconnectedArgsAsync;
            try
            {
                await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
                var delayedArgs = await disconnectedArgs.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // The monitor detects the loss independently, buffers, and reconnects before
                // MQTTnet is allowed to deliver its delayed raw callback.
                monitor.SignalReconnectNeeded();

                await AsyncTestHelpers.WaitUntilAsync(
                    () => !source.Diagnostics.IsOperational,
                    message: "The confirmed disconnect should mark the client non-operational.");
                await stateRecorder.WaitForStatesAsync(
                    TimeSpan.FromSeconds(15),
                    "The confirmed disconnect should start buffering.",
                    SourceState.Synchronized,
                    SourceState.Synchronizing);

                await AsyncTestHelpers.WaitUntilAsync(
                    () => source.Diagnostics.IsOperational,
                    message: "The client should report operational again once the monitor has reconnected.");
                await stateRecorder.WaitForStatesAsync(
                    TimeSpan.FromSeconds(30),
                    "The client should finish synchronizing after reconnecting.",
                    SourceState.Synchronized,
                    SourceState.Synchronizing,
                    SourceState.Synchronized);

                // Act
                await delayedDisconnectedHandler(delayedArgs);

                // Assert
                Assert.True(source.Diagnostics.IsOperational);
                Assert.True(client.IsConnected);
            }
            finally
            {
                client.DisconnectedAsync -= CaptureDisconnectedArgsAsync;
                client.DisconnectedAsync += delayedDisconnectedHandler;
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    private static MqttSubjectServer CreateBroker(int brokerPort)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new MqttSubjectServer(
            new LivenessTestRoot(context) { Name = "Initial" },
            new MqttServerConfiguration { BrokerPort = brokerPort, Mapper = CreateMapper() },
            NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttSubjectClientSource CreateClientSource(int brokerPort, TimeSpan? reconnectDelay = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceTransactions()
            .WithSourceMonitoring();

        return new MqttSubjectClientSource(
            new LivenessTestRoot(context),
            new MqttClientConfiguration
            {
                // The broker binds IPv4 only, so dialling it by name would let the client spend its
                // connect timeout on the IPv6 loopback first.
                BrokerHost = "127.0.0.1",
                BrokerPort = brokerPort,
                Mapper = CreateMapper(),
                ReconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1),
                MaximumReconnectDelay = TimeSpan.FromSeconds(4),
                HealthCheckInterval = TimeSpan.FromSeconds(1)
            },
            NullLogger<MqttSubjectClientSource>.Instance);
    }

    private static MqttCompositeMapper CreateMapper() => new(
        new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
        new MqttAttributeMapper("mqtt"));

    private static IMqttClient GetCurrentClient(MqttSubjectClientSource source)
    {
        var client = typeof(MqttSubjectClientSource)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(source);

        return client as IMqttClient ?? throw new InvalidOperationException("The source has no active MQTT client.");
    }

    private static MqttConnectionMonitor GetConnectionMonitor(MqttSubjectClientSource source)
    {
        var monitor = typeof(MqttSubjectClientSource)
            .GetField("_connectionMonitor", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(source);

        return monitor as MqttConnectionMonitor ??
            throw new InvalidOperationException("The source has no active MQTT connection monitor.");
    }

    private static Func<MqttClientDisconnectedEventArgs, Task> GetDisconnectedHandler(
        MqttSubjectClientSource source)
    {
        var method = typeof(MqttSubjectClientSource)
            .GetMethod("OnDisconnectedAsync", BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The source has no disconnected handler.");

        return method.CreateDelegate<Func<MqttClientDisconnectedEventArgs, Task>>(source);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
