using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests.Client;

[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class MqttClientOfflineWriteTests
{
    [InterceptorSubject]
    public partial class OfflineWriteTestRoot
    {
        [Path("mqtt", "Name")]
        public partial string Name { get; set; }

        public OfflineWriteTestRoot()
        {
            Name = string.Empty;
        }
    }

    [Fact]
    public async Task WhenAPropertyIsWrittenBeforeTheFirstConnectSucceeds_ThenTheWriteIsPublishedOnceTheBrokerIsReachable()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        var mapper = new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/'));

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
        var serverRoot = new OfflineWriteTestRoot(serverContext);

        await using var server = new MqttSubjectServer(
            serverRoot,
            new MqttServerConfiguration
            {
                BrokerPort = brokerPort,
                Mapper = mapper
            },
            NullLogger<MqttSubjectServer>.Instance);

        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
        var clientRoot = new OfflineWriteTestRoot(clientContext);

        await using var source = new MqttSubjectClientSource(
            clientRoot,
            new MqttClientConfiguration
            {
                BrokerHost = "localhost",
                BrokerPort = brokerPort,
                Mapper = mapper,
                ConnectTimeout = TimeSpan.FromSeconds(1),
                RetryTime = TimeSpan.FromMilliseconds(500)
            },
            NullLogger<MqttSubjectClientSource>.Instance);

        try
        {
            await source.StartAsync(CancellationToken.None);

            // A recorded error proves the pump is running and its first connect attempt has failed.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.LastError is not null,
                message: "The first connect attempt should fail while no broker is listening.");

            // Act
            clientRoot.Name = "Written offline";
            await server.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => serverRoot.Name == "Written offline",
                timeout: TimeSpan.FromSeconds(15),
                message: "A write made while the broker was unreachable should be published once the client connects.");
            Assert.Equal("Written offline", clientRoot.Name);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await server.StopAsync(CancellationToken.None);
        }
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
