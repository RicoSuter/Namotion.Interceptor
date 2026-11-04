using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The client sends its subscriptions in batches rather than as one packet, so a graph with more
/// topics than a batch has to be subscribed completely across several packets.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class MqttClientSubscriptionBatchTests
{
    // More than one batch of 1000 topics, so the last item's topic is subscribed by the second packet.
    private const int ItemCount = 1500;

    [InterceptorSubject]
    public partial class BatchTestItem
    {
        [Path("mqtt", "Value")]
        public partial int Value { get; set; }
    }

    [InterceptorSubject]
    public partial class BatchTestRoot
    {
        [Path("mqtt", "Items")]
        public partial BatchTestItem[] Items { get; set; }

        public BatchTestRoot()
        {
            Items = [];
        }
    }

    [Fact]
    public async Task WhenTopicsExceedOneSubscriptionBatch_ThenTheClientReceivesValuesOfTheLastBatch()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        var mapper = new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt"));

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
        var serverRoot = new BatchTestRoot(serverContext)
        {
            Items = CreateItems(serverContext)
        };

        await using var server = new MqttSubjectServer(
            serverRoot,
            new MqttServerConfiguration { BrokerPort = brokerPort, Mapper = mapper },
            NullLogger<MqttSubjectServer>.Instance);

        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceTransactions()
            .WithSourceMonitoring();
        var clientRoot = new BatchTestRoot(clientContext)
        {
            Items = CreateItems(clientContext)
        };

        await using var source = new MqttSubjectClientSource(
            clientRoot,
            new MqttClientConfiguration
            {
                // The broker binds IPv4 only, so dialling it by name would let the client spend its
                // connect timeout on the IPv6 loopback first.
                BrokerHost = "127.0.0.1",
                BrokerPort = brokerPort,
                Mapper = mapper
            },
            NullLogger<MqttSubjectClientSource>.Instance);

        var stateRecorder = SourceStateRecorder.SubscribeTo(source);
        try
        {
            await server.StartAsync(CancellationToken.None);
            await source.StartAsync(CancellationToken.None);

            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "The subscription of every batch should complete.",
                SourceState.Synchronized);

            // Act
            serverRoot.Items[^1].Value = 42;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.Items[^1].Value == 42,
                timeout: TimeSpan.FromSeconds(30),
                message: "A change of the last item, subscribed by the last batch, should reach the client.");
            Assert.Equal(ItemCount, source.Ownership.Count);
        }
        finally
        {
            stateRecorder.Dispose();
            await source.StopAsync(CancellationToken.None);
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static BatchTestItem[] CreateItems(IInterceptorSubjectContext context)
    {
        var items = new BatchTestItem[ItemCount];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = new BatchTestItem(context);
        }

        return items;
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
