using System.Collections.Generic;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

public class SubjectUpdateFlowTests
{
    [Fact]
    public void CreateCompleteUpdate_ShouldIncludeStringProperty()
    {
        // Arrange - Create server subject with value
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "TestValue"
        };

        // Act - Create complete update
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);

        // Assert - Update should contain the property in root subject
        Assert.NotEmpty(update.Subjects);
        var rootProps = update.Subjects[update.Root];
        Assert.True(rootProps.ContainsKey("Name"), "Update should contain 'Name' property");
        Assert.Equal(SubjectPropertyUpdateKind.Value, rootProps["Name"].Kind);
        Assert.Equal("TestValue", rootProps["Name"].Value);
    }

    [Fact]
    public void ApplySubjectUpdate_ShouldSetStringProperty()
    {
        // Arrange - Create client subject
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext);
        Assert.Equal("", clientRoot.Name); // Default value

        // Create update manually with flat structure
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects =
            {
                ["1"] = new Dictionary<string, SubjectPropertyUpdate>
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "UpdatedValue" }
                }
            }
        };

        // Act - Apply update
        clientRoot.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("UpdatedValue", clientRoot.Name);
    }

    [Fact]
    public void FullFlow_CreateThenApply_ShouldSyncValue()
    {
        // Arrange - Server
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "ServerValue",
            Number = 123.45m
        };

        // Arrange - Client
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext);

        // Act - Create update from server and apply to client
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
        clientRoot.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("ServerValue", clientRoot.Name);
        Assert.Equal(123.45m, clientRoot.Number);
    }

    [Fact]
    public void WelcomePayload_SerializeAndApply_CollectionWithItems_ShouldSyncCollectionIndexesCorrectly()
    {
        // Arrange - Server with collection items
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "RootWithItems",
            Items =
            [
                new TestItem(serverContext) { Label = "First", Value = 100 },
                new TestItem(serverContext) { Label = "Second", Value = 200 }
            ]
        };

        // Arrange - Client with null Items (to trigger "create new collection" code path)
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext)
        {
            Items = null! // Force null to test create collection path which uses Index
        };

        // Act - Create Welcome, serialize, deserialize, apply (this is the path where Index becomes JsonElement)
        var serializer = new JsonWsSerializer();
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);

        var welcome = new WelcomePayload { Version = 1, Format = WsFormat.Json, State = update };
        var bytes = serializer.SerializeMessage(MessageType.Welcome, null, welcome);
        var (_, _, payloadBytes) = serializer.DeserializeMessageEnvelope(bytes);
        var deserializedWelcome = serializer.Deserialize<WelcomePayload>(payloadBytes.Span);

        // This should not throw an InvalidCastException for JsonElement -> int conversion
        clientRoot.ApplySubjectUpdate(deserializedWelcome.State!, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("RootWithItems", clientRoot.Name);
        Assert.Equal(2, clientRoot.Items.Length);
        Assert.Equal("First", clientRoot.Items[0].Label);
        Assert.Equal(100, clientRoot.Items[0].Value);
        Assert.Equal("Second", clientRoot.Items[1].Label);
        Assert.Equal(200, clientRoot.Items[1].Value);
    }

    [Fact]
    public void WelcomePayload_SerializeAndApply_CollectionGrows_ShouldReuseExistingAndAddNew()
    {
        // Arrange - Server with more items than client
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "ServerWithMoreItems",
            Items =
            [
                new TestItem(serverContext) { Label = "UpdatedFirst", Value = 10 },
                new TestItem(serverContext) { Label = "UpdatedSecond", Value = 20 },
                new TestItem(serverContext) { Label = "NewThird", Value = 30 }
            ]
        };

        // Arrange - Client with existing items
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var existingItem0 = new TestItem(clientContext) { Label = "OldFirst", Value = 1 };
        var existingItem1 = new TestItem(clientContext) { Label = "OldSecond", Value = 2 };
        var clientRoot = new TestRoot(clientContext)
        {
            Items = [existingItem0, existingItem1]
        };

        // Act - Serialize and apply
        var serializer = new JsonWsSerializer();
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);
        var welcome = new WelcomePayload { Version = 1, Format = WsFormat.Json, State = update };
        var bytes = serializer.SerializeMessage(MessageType.Welcome, null, welcome);
        var (_, _, payloadBytes) = serializer.DeserializeMessageEnvelope(bytes);
        var deserializedWelcome = serializer.Deserialize<WelcomePayload>(payloadBytes.Span);

        clientRoot.ApplySubjectUpdate(deserializedWelcome.State!, DefaultSubjectFactory.Instance);

        // Assert - Collection should be replaced with new one containing 3 items
        Assert.Equal("ServerWithMoreItems", clientRoot.Name);
        Assert.Equal(3, clientRoot.Items.Length);

        // Existing items should be reused (same instance) with updated values
        Assert.Same(existingItem0, clientRoot.Items[0]);
        Assert.Equal("UpdatedFirst", clientRoot.Items[0].Label);
        Assert.Equal(10, clientRoot.Items[0].Value);

        Assert.Same(existingItem1, clientRoot.Items[1]);
        Assert.Equal("UpdatedSecond", clientRoot.Items[1].Label);
        Assert.Equal(20, clientRoot.Items[1].Value);

        // New item should be created
        Assert.NotSame(existingItem0, clientRoot.Items[2]);
        Assert.NotSame(existingItem1, clientRoot.Items[2]);
        Assert.Equal("NewThird", clientRoot.Items[2].Label);
        Assert.Equal(30, clientRoot.Items[2].Value);
    }

    [Fact]
    public void WelcomePayload_SerializeAndApply_ShouldSyncValue()
    {
        // Arrange - Server
        var serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var serverRoot = new TestRoot(serverContext)
        {
            Name = "Initial"
        };

        // Arrange - Client
        var clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var clientRoot = new TestRoot(clientContext);

        // Act - Create Welcome, serialize, deserialize, apply
        var serializer = new JsonWsSerializer();
        var update = SubjectUpdate.CreateCompleteUpdate(serverRoot, []);

        // Verify the original update has the property
        var rootProps = update.Subjects[update.Root];
        Assert.True(rootProps.ContainsKey("Name"), "Original update should contain Name");
        Assert.Equal("Initial", rootProps["Name"].Value);

        var welcome = new WelcomePayload { Version = 1, Format = WsFormat.Json, State = update };

        var bytes = serializer.SerializeMessage(MessageType.Welcome, null, welcome);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Initial", json); // Verify JSON contains the value

        var (_, _, payloadBytes) = serializer.DeserializeMessageEnvelope(bytes);
        var deserializedWelcome = serializer.Deserialize<WelcomePayload>(payloadBytes.Span);

        // Verify deserialized update has the property
        Assert.NotNull(deserializedWelcome.State);
        var deserializedRootProps = deserializedWelcome.State.Subjects[deserializedWelcome.State.Root];
        Assert.True(deserializedRootProps.ContainsKey("Name"), "Deserialized update should contain Name");

        var nameUpdate = deserializedRootProps["Name"];
        Assert.Equal(SubjectPropertyUpdateKind.Value, nameUpdate.Kind);
        Assert.NotNull(nameUpdate.Value);

        clientRoot.ApplySubjectUpdate(deserializedWelcome.State!, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("Initial", clientRoot.Name);
    }
}
