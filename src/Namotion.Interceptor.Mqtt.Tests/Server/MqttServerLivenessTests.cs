using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Server;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Tests.Server;

/// <summary>
/// The broker owns its own restart loop, so nothing outside it can tell whether it is listening. These
/// pin the transitions the loop is responsible for, and that a restart can register its own outbound
/// change queue: the metrics permit one live registration at a time.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class MqttServerLivenessTests
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
    public async Task WhenTheBrokerIsListening_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        await using var server = CreateServer();

        // Act
        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Diagnostics.IsOperational,
            message: "The broker should report operational once it is listening.");

        // Assert
        Assert.NotNull(server.Diagnostics.OperationalChangeTime);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.Null(server.Diagnostics.LastError);
        Assert.Equal(0, server.Diagnostics.ConnectedClientCount);

        // Act
        await server.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheBrokerIsListening_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange: a buffer time that outlasts the test, so a captured change stays in the processor's
        // queue instead of being flushed away before the depth can be read.
        await using var server = CreateServer(bufferTime: TimeSpan.FromMinutes(5));
        var root = (LivenessTestRoot)server.RootSubject;

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational,
                message: "The broker should report operational once it is listening.");

            // Act
            // Re-written on each poll because the processor only captures changes once it is running.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    root.Name = "v" + probeValue++;
                    return server.Diagnostics.OutboundChanges.Depth > 0;
                },
                message: "The outbound change queue never reported a depth, so it was never registered.");

            // Assert
            Assert.Null(server.Diagnostics.OutboundChanges.Capacity);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheBrokerIsForceKilled_ThenItBecomesOperationalAgain()
    {
        // Arrange
        await using var server = CreateServer();

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational,
                message: "The broker should report operational once it is listening.");

            var firstOperationalTime = server.Diagnostics.OperationalChangeTime;

            // Act
            await ((IFaultInjectable)server).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational &&
                      server.Diagnostics.OperationalChangeTime != firstOperationalTime,
                message: "The broker should report operational again after restarting.");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheBrokerCannotBind_ThenTheFailureIsReported()
    {
        // Arrange: the port is already taken, so the broker fails inside the loop, which swallows the
        // exception rather than letting the base class see it.
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await using var server = CreateServer(port: occupiedPort);

        await server.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.LastError is not null,
                message: "A broker that cannot bind should report the failure.");

            // Assert
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenStoppedAndStartedAgain_ThenEachRunReleasesItsBrokerLifecycleAndInitialStateWork()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var server = CreateServer(
            port: brokerPort,
            initialStateDelay: TimeSpan.FromMinutes(5),
            withLifecycle: true);

        var lifecycleInterceptor = server.RootSubject.Context.TryGetLifecycleInterceptor()!;
        var baselineHandlerCount = GetSubjectDetachingHandlerCount(lifecycleInterceptor);

        using var client = new MqttClientFactory().CreateMqttClient();
        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational,
                message: "The first broker run should become operational.");

            var firstBroker = GetCurrentBroker(server);
            Assert.Equal(baselineHandlerCount + 1, GetSubjectDetachingHandlerCount(lifecycleInterceptor));

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort)
                .Build();
            await client.ConnectAsync(options, CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => GetInitialStateTasks(server).Length == 1,
                message: "The first run should track its delayed initial-state publication.");
            var firstRunInitialStateTask = GetInitialStateTasks(server).Single();

            // Act
            await server.StopAsync(CancellationToken.None);

            // Assert
            Assert.True(IsDisposed(firstBroker));
            Assert.True(firstRunInitialStateTask.IsCompleted);
            Assert.Equal(baselineHandlerCount, GetSubjectDetachingHandlerCount(lifecycleInterceptor));

            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational,
                message: "The second broker run should become operational.");

            // Assert
            Assert.NotSame(firstBroker, GetCurrentBroker(server));
            Assert.Equal(baselineHandlerCount + 1, GetSubjectDetachingHandlerCount(lifecycleInterceptor));
            Assert.True(firstRunInitialStateTask.IsCompleted);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static MqttSubjectServer CreateServer(
        TimeSpan? bufferTime = null,
        int? port = null,
        TimeSpan? initialStateDelay = null,
        bool withLifecycle = false)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        if (withLifecycle)
        {
            context.WithLifecycle();
        }

        var configuration = new MqttServerConfiguration
        {
            BrokerHost = "127.0.0.1",
            BrokerPort = port ?? GetFreeTcpPort(),
            Mapper = new MqttCompositeMapper(
                new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
                new MqttAttributeMapper("mqtt")),
            BufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8),
            InitialStateDelay = initialStateDelay ?? TimeSpan.FromMilliseconds(500)
        };

        return new MqttSubjectServer(
            new LivenessTestRoot(context), configuration, NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttServer GetCurrentBroker(MqttSubjectServer server) =>
        (MqttServer)(typeof(MqttSubjectServer)
            .GetField("_mqttServer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)
            ?? throw new InvalidOperationException("The server has no active MQTT broker."));

    private static Task[] GetInitialStateTasks(MqttSubjectServer server) =>
        ((List<Task>)typeof(MqttSubjectServer)
            .GetField("_runningInitialStateTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!)
        .ToArray();

    private static int GetSubjectDetachingHandlerCount(LifecycleInterceptor lifecycleInterceptor) =>
        ((Delegate?)typeof(LifecycleInterceptor)
            .GetField("SubjectDetaching", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lifecycleInterceptor))?
        .GetInvocationList()
        .Length ?? 0;

    private static bool IsDisposed(MqttServer server)
    {
        for (var type = server.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(
                "IsDisposed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return (bool)property.GetValue(server)!;
            }
        }

        throw new InvalidOperationException("The MQTT broker does not expose its disposal state.");
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
