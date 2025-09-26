using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.Registry;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Collection("OPC UA Integration")]
public class OpcUaDynamicServerClientTests
{
    private readonly ITestOutputHelper _output;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<DynamicSubject>? _client;

    public OpcUaDynamicServerClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenConnectingToServer_ThenStaticallyMissingPropertiesAreDynamicallyAdded()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            // Act
            var settings = new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };

            var root = _client!.Root!;
            var jsonObject = root.ToJsonObject(settings);
            var json = jsonObject.ToJsonString(settings);

            // Assert
            var scalarStrings = root.TryGetRegisteredProperty("ScalarStrings")!.GetValue();
            Assert.Equal(typeof(string[]), scalarStrings?.GetType());

            await Verify(json);
        }
        finally
        {
            await (_server?.StopAsync() ?? Task.CompletedTask);
            await (_client?.StopAsync() ?? Task.CompletedTask);
        }
    }

    private async Task StartServerAsync()
    {
        _server = new OpcUaTestServer<TestRoot>(_output);
        await _server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Connected = true;
                root.Name = "Foo bar";
                root.ScalarNumbers = [10, 20, 30, 40, 50];
                root.ScalarStrings = ["Server", "Test", "Array"];
                root.NestedNumbers = [[100, 200], [300, 400]];
                root.Person = new TestPerson { FirstName = "John", LastName = "Smith", Scores = [1, 2] };
                root.People =
                [
                    new TestPerson { FirstName = "John", LastName = "Doe", Scores = [85.5, 92.3] },
                    new TestPerson { FirstName = "Jane", LastName = "Doe", Scores = [88.1, 95.7] }
                ];
            });
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<DynamicSubject>(_output);
        await _client.StartAsync(
            context => new DynamicSubject(context),
            isConnected: root => root.TryGetRegisteredProperty(nameof(TestRoot.Connected))?.GetValue() is true);
    }
}