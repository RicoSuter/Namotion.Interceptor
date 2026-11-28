using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Server;

public class MatterServerControllerEventTests
{
    private const ulong TestNodeId = 0xFFFFFFFE00000001;

    private static JsonObject CreateNode(ulong nodeId, bool available = true) => new()
    {
        ["node_id"] = nodeId,
        ["date_commissioned"] = "2024-01-01T00:00:00.000000",
        ["last_interview"] = "2024-01-01T12:00:00.000000",
        ["interview_version"] = 6,
        ["available"] = available,
        ["is_bridge"] = true,
        ["attributes"] = new JsonObject
        {
            ["0/40/1"] = "Test Vendor",
            ["1/29/0"] = new JsonArray(new JsonObject { ["0"] = 14, ["1"] = 1 }),
            ["2/6/0"] = true
        },
        ["attribute_subscriptions"] = new JsonArray(),
        ["endpoint_parents"] = new JsonObject { ["1"] = 0, ["2"] = 1 }
    };

    private static string CreateLabel(int length, char character)
    {
        // Varying characters catch a buffer that grows without keeping what was already received.
        return string.Create(length, character, static (span, first) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = (char)(first + index % 26);
            }
        });
    }

    private static async Task<FakeMatterServerSession> StartListeningAsync(MatterServerOptions? options = null)
    {
        var session = await FakeMatterServer.StartAndConnectAsync(options);
        try
        {
            session.Server.Handler = request => request["command"]!.GetValue<string>() == "start_listening" ? new JsonArray(CreateNode(TestNodeId)) : null;
            session.Nodes = await session.Controller.StartListeningAsync();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task WhenStartingToListen_ThenNodesAreRead()
    {
        // Arrange & Act
        await using var session = await StartListeningAsync();

        // Assert
        var node = Assert.Single(session.Nodes);
        Assert.Equal(TestNodeId, node.NodeId);
        Assert.True(node.Available);
        Assert.True(node.IsBridge);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), node.DateCommissioned);
        Assert.Equal("Test Vendor", node.Attributes[new AttributePath(0, 40, 1)].GetString());
        foreach (var deviceType in node.Attributes[new AttributePath(1, 29, 0)].EnumerateList())
        {
            Assert.True(deviceType.TryGetField(0, "deviceType", out var id));
            Assert.Equal(14, id.GetInt64());
        }

        Assert.Equal(0, node.EndpointParents[1]);
        Assert.Equal(1, node.EndpointParents[2]);
    }

    [Fact]
    public async Task WhenNodeEventsArrive_ThenTheyAreDeliveredInOrder()
    {
        // Arrange
        await using var session = await StartListeningAsync();

        // Act
        await session.Server.SendEventAsync("node_added", CreateNode(7));
        await session.Server.SendEventAsync("node_updated", CreateNode(7, available: false));
        await session.Server.SendEventAsync("attribute_updated", new JsonArray(7, "2/6/0", false));
        await session.Server.SendEventAsync("endpoint_added", new JsonObject { ["node_id"] = 7, ["endpoint_id"] = 3 });
        await session.Server.SendEventAsync("endpoint_removed", new JsonObject { ["node_id"] = 7, ["endpoint_id"] = 3 });
        await session.Server.SendEventAsync("node_removed", 7);

        // Assert
        Assert.Equal(7UL, Assert.IsType<MatterNodeAddedEvent>(await session.Controller.Events.ReadWithTimeoutAsync()).Node.NodeId);
        Assert.False(Assert.IsType<MatterNodeUpdatedEvent>(await session.Controller.Events.ReadWithTimeoutAsync()).Node.Available);
        var attributeChanged = Assert.IsType<MatterAttributeUpdatedEvent>(await session.Controller.Events.ReadWithTimeoutAsync());
        Assert.Equal(new AttributePath(2, 6, 0), attributeChanged.Path);
        Assert.False(attributeChanged.Value.GetBoolean());
        Assert.Equal(new MatterEndpointAddedEvent(7, 3), await session.Controller.Events.ReadWithTimeoutAsync());
        Assert.Equal(new MatterEndpointRemovedEvent(7, 3), await session.Controller.Events.ReadWithTimeoutAsync());
        Assert.Equal(new MatterNodeRemovedEvent(7), await session.Controller.Events.ReadWithTimeoutAsync());
    }

    [Fact]
    public async Task WhenNodeReportsMatterEvent_ThenEventWithNamedFieldsIsDelivered()
    {
        // Arrange
        await using var session = await StartListeningAsync();

        // Act
        await session.Server.SendEventAsync("node_event", new JsonObject
        {
            ["node_id"] = TestNodeId, ["endpoint_id"] = 1, ["cluster_id"] = 59, ["event_id"] = 1,
            ["event_number"] = 12345, ["priority"] = 1, ["timestamp"] = 1704067200000, ["timestamp_type"] = 1,
            ["data"] = new JsonObject { ["newPosition"] = 1 }
        });

        // Assert
        var received = Assert.IsType<MatterNodeEventReceivedEvent>(await session.Controller.Events.ReadWithTimeoutAsync());
        Assert.Equal(new EventPath(1, 59, 1), received.Path);
        Assert.Equal(12345UL, received.EventNumber);
        Assert.Equal(1704067200000, received.Timestamp);
        Assert.Equal(MatterEventTimestampType.Epoch, received.TimestampType);
        Assert.True(received.Data.TryGetField(0, "newPosition", out var position));
        Assert.Equal(1, position.GetInt64());
    }

    [Fact]
    public async Task WhenServerInfoIsUpdated_ThenServerInfoChanges()
    {
        // Arrange
        await using var session = await StartListeningAsync();
        var info = FakeMatterServer.CreateServerInfo(15, 11);
        info["wifi_credentials_set"] = true;

        // Act
        await session.Server.SendEventAsync("server_info_updated", info);

        // Assert
        var changed = Assert.IsType<MatterServerInfoUpdatedEvent>(await session.Controller.Events.ReadWithTimeoutAsync());
        Assert.True(changed.ServerInfo.WifiCredentialsSet);
        Assert.True(session.Controller.ServerInfo.WifiCredentialsSet);
    }

    [Fact]
    public async Task WhenUnknownOrMalformedEventArrives_ThenLaterEventsAreStillDelivered()
    {
        // Arrange
        await using var session = await StartListeningAsync();

        // Act
        await session.Server.SendEventAsync("future_event", new JsonObject());
        await session.Server.SendEventAsync("attribute_updated", new JsonArray(7, "not-a-path", 1));
        var nodeWithoutId = CreateNode(7);
        nodeWithoutId.Remove("node_id");
        await session.Server.SendEventAsync("node_added", nodeWithoutId);
        await session.Server.SendRawAsync("not json");
        await session.Server.SendEventAsync("node_removed", 8);

        // Assert
        Assert.Equal(new MatterNodeRemovedEvent(8), await session.Controller.Events.ReadWithTimeoutAsync());
    }

    [Fact]
    public async Task WhenServerClosesConnection_ThenEventsCompleteWithErrorAndRequestsFail()
    {
        // Arrange
        await using var session = await StartListeningAsync();

        // Act
        session.Server.CloseClient();

        // Assert
        var closeError = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.Events.WaitForCompletionAsync());
        var first = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.RemoveNodeAsync(1));
        var second = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.RemoveNodeAsync(1));
        Assert.NotSame(first, second);
        Assert.Same(closeError, first.InnerException);
        Assert.Same(closeError, second.InnerException);
    }

    [Theory]
    [InlineData(1024, 0)]
    [InlineData(1024, 1)]
    [InlineData(16 * 1024, 0)] // fills the initial buffer exactly
    [InlineData(16 * 1024, 1)]
    public async Task WhenMessageReachesMaximumSize_ThenOnlyALargerMessageFailsTheConnection(int maxMessageSize, int bytesOverMaximum)
    {
        // Arrange
        await using var session = await StartListeningAsync(new MatterServerOptions { MaxMessageSize = maxMessageSize });
        const string emptyMessage = "{\"event\":\"padding\",\"data\":\"\"}";
        var message = emptyMessage.Insert(emptyMessage.Length - 2, new string('x', maxMessageSize + bytesOverMaximum - emptyMessage.Length));

        // Act
        await session.Server.SendRawAsync(message);

        // Assert
        Assert.Equal(maxMessageSize + bytesOverMaximum, message.Length);
        if (bytesOverMaximum == 0)
        {
            await session.Server.SendEventAsync("node_removed", 8);
            Assert.Equal(new MatterNodeRemovedEvent(8), await session.Controller.Events.ReadWithTimeoutAsync());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => session.Controller.Events.WaitForCompletionAsync());
            var exception = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.RemoveNodeAsync(1));
            Assert.IsType<InvalidDataException>(exception.InnerException);
        }
    }

    [Fact]
    public async Task WhenMessagesLargerThanTheInitialBufferArrive_ThenTheirContentIsIntact()
    {
        // Arrange: 100 KB grows the 16 KiB buffer, 3 MB grows it past the size that is kept, and a small message follows
        await using var session = await StartListeningAsync();
        var labels = new[] { CreateLabel(100 * 1024, 'a'), CreateLabel(3 * 1024 * 1024, 'b'), CreateLabel(10, 'c') };

        foreach (var label in labels)
        {
            // Act
            var node = CreateNode(7);
            node["attributes"]!["1/40/5"] = label;
            await session.Server.SendEventAsync("node_updated", node);

            // Assert
            var updated = Assert.IsType<MatterNodeUpdatedEvent>(await session.Controller.Events.ReadWithTimeoutAsync());
            Assert.Equal(label, updated.Node.Attributes[new AttributePath(1, 40, 5)].GetString());
            Assert.Equal("Test Vendor", updated.Node.Attributes[new AttributePath(0, 40, 1)].GetString());
        }
    }

    [Fact]
    public async Task WhenEndpointParentEntryIsMalformed_ThenOnlyThatEntryIsSkipped()
    {
        // Arrange
        await using var session = await StartListeningAsync();
        var node = CreateNode(7);
        node["endpoint_parents"] = new JsonObject { ["70000"] = 0, ["1"] = 0, ["2"] = "x", ["3"] = 1 };

        // Act
        await session.Server.SendEventAsync("node_added", node);

        // Assert
        var added = Assert.IsType<MatterNodeAddedEvent>(await session.Controller.Events.ReadWithTimeoutAsync());
        Assert.Equal(new Dictionary<ushort, ushort> { [1] = 0, [3] = 1 }, added.Node.EndpointParents);
    }

    [Fact]
    public async Task WhenServerShutsDown_ThenEventsCompleteWithErrorAndRequestsFail()
    {
        // Arrange
        await using var session = await StartListeningAsync();

        // Act
        await session.Server.SendEventAsync("server_shutdown", new JsonObject());

        // Assert
        await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.Events.WaitForCompletionAsync());
        var exception = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.RemoveNodeAsync(1));
        Assert.IsType<WebSocketException>(exception.InnerException);
    }

    [Fact]
    public async Task WhenDisposed_ThenEventsCompleteWithoutErrorAndRequestsThrow()
    {
        // Arrange
        await using var session = await StartListeningAsync();

        // Act
        await session.Controller.DisposeAsync();

        // Assert
        await session.Controller.Events.WaitForCompletionAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Controller.RemoveNodeAsync(1));
    }
}
