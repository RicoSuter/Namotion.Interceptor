using System.Buffers;
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
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// How a failed write batch is reported, which is what decides whether the batches behind it in the
/// same flush are attempted at all: an enumerated failure names the changes the source refused and lets
/// the flush continue, an empty one says the call itself never answered and stops the flush.
/// </summary>
/// <remarks>
/// The client source only reaches its message-building step once it holds a connected client, so this
/// runs against the embedded broker the same way <see cref="OutageStateTests"/> does.
/// </remarks>
[Trait("Category", "Integration")]
public partial class OutboundWriteDispositionTests
{
    [InterceptorSubject]
    public partial class TestRoot
    {
        [Path("mqtt", "Name")]
        public partial string Name { get; set; }

        [Path("mqtt", "Description")]
        public partial string Description { get; set; }

        public TestRoot()
        {
            Name = "";
            Description = "";
        }
    }

    /// <summary>Throws from serialization when the value equals a sentinel, standing in for a user
    /// value converter that persistently refuses one value.</summary>
    private sealed class ThrowOnSerializeSentinelConverter(object sentinel) : IMqttValueConverter
    {
        private readonly JsonMqttValueConverter _inner = new();

        public byte[] Serialize(object? value, Type type)
        {
            if (Equals(value, sentinel))
            {
                throw new InvalidOperationException($"Refusing to serialize '{value}'.");
            }

            return _inner.Serialize(value, type);
        }

        public object? Deserialize(ReadOnlySequence<byte> payload, Type type) => _inner.Deserialize(payload, type);
    }

    /// <summary>
    /// Fails the way a user-supplied source-timestamp serializer can, but only once the test arms it, so
    /// an outbound flush the connector runs on its own cannot consume the failure first.
    /// </summary>
    private sealed class ArmableTimestampSerializer
    {
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public byte[] Serialize(DateTimeOffset timestamp)
        {
            return _armed
                ? throw new InvalidOperationException("The source timestamp serializer refused the change.")
                : MqttHelper.DefaultSerializeTimestamp(timestamp);
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

    [Fact]
    public async Task WhenAUserExtensionPointThrowsWhileBuildingMessages_ThenTheBatchesChangesAreEnumerated()
    {
        // Arrange: the source-timestamp serializer runs before anything is published, so its throw says
        // nothing about the broker connection. Reporting it as a failed call would condemn every batch
        // behind this one unattempted, on this flush and on every retry of it.
        var brokerPort = GetFreeTcpPort();
        var mapper = new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt"));
        var timestampSerializer = new ArmableTimestampSerializer();

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
        var serverRoot = new TestRoot(serverContext) { Name = "Initial" };

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
            .WithLifecycle()
            .WithSourceTransactions()
            .WithSourceMonitoring();
        var clientRoot = new TestRoot(clientContext);

        await using var source = new MqttSubjectClientSource(
            clientRoot,
            new MqttClientConfiguration
            {
                BrokerHost = "localhost",
                BrokerPort = brokerPort,
                Mapper = mapper,
                SourceTimestampSerializer = timestampSerializer.Serialize
            },
            NullLogger<MqttSubjectClientSource>.Instance);

        try
        {
            await server.StartAsync(CancellationToken.None);
            await source.StartAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial subscription should complete");

            timestampSerializer.Arm();
            var change = SubjectPropertyChange.Create(
                new PropertyReference(clientRoot, nameof(TestRoot.Name)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "", "written");

            // Act
            var result = await source.WriteChangesAsync(new[] { change }, CancellationToken.None);

            // Assert
            Assert.NotNull(result.Error);
            Assert.Equal(change.Property, Assert.Single(result.FailedChanges).Property);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenSerializationRefusesOneChangeOfTwo_ThenTheOtherIsStillPublishedAndOnlyTheRefusedOneIsEnumerated()
    {
        // Arrange: a per-change serialize throw used to be logged and skipped, leaving the change
        // unlisted in the result. Unlisted counts as written, so the write was lost for good: no
        // retry, no error report, and a transaction treated it as written and skipped its rollback.
        var brokerPort = GetFreeTcpPort();
        var mapper = new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt"));

        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
        var serverRoot = new TestRoot(serverContext) { Name = "Initial", Description = "Initial" };

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
            .WithLifecycle()
            .WithSourceTransactions()
            .WithSourceMonitoring();
        var clientRoot = new TestRoot(clientContext);

        await using var source = new MqttSubjectClientSource(
            clientRoot,
            new MqttClientConfiguration
            {
                BrokerHost = "localhost",
                BrokerPort = brokerPort,
                Mapper = mapper,
                ValueConverter = new ThrowOnSerializeSentinelConverter("poison")
            },
            NullLogger<MqttSubjectClientSource>.Instance);

        try
        {
            await server.StartAsync(CancellationToken.None);
            await source.StartAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial subscription should complete");

            var writableChange = SubjectPropertyChange.Create(
                new PropertyReference(clientRoot, nameof(TestRoot.Name)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "", "written");
            var refusedChange = SubjectPropertyChange.Create(
                new PropertyReference(clientRoot, nameof(TestRoot.Description)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "", "poison");

            // Act
            var result = await source.WriteChangesAsync(new[] { writableChange, refusedChange }, CancellationToken.None);

            // Assert: the serializable change was published and lands on the server, and the refused
            // one is enumerated so it is retried like any other failure instead of vanishing.
            Assert.NotNull(result.Error);
            Assert.Equal(refusedChange.Property, Assert.Single(result.FailedChanges).Property);
            await AsyncTestHelpers.WaitUntilAsync(
                () => serverRoot.Name == "written",
                timeout: TimeSpan.FromSeconds(30),
                message: "The serializable change should still reach the broker");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await server.StopAsync(CancellationToken.None);
        }
    }
}
