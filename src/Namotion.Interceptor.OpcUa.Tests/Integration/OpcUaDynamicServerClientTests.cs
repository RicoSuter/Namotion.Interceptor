using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaDynamicServerClientTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<DynamicSubject>? _client;
    private PortLease? _port;

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
            _port = await OpcUaTestPortPool.AcquireAsync();
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
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
            _port?.Dispose();
        }
    }

    private async Task StartServerAsync()
    {
        _logger = new TestLogger(_output);
        _server = new OpcUaTestServer<TestRoot>(_logger);
        await _server.StartAsync(
            context => new TestRoot(context),
            (_, root) =>
            {
                root.Connected = true;
                root.Name = "Foo bar";
                root.ScalarNumbers = [10, 20, 30, 40, 50];
                root.ScalarStrings = ["Server", "Test", "Array"];
                root.Person = new TestPerson
                {
                    FirstName = "John",
                    LastName = "Smith",
                    Scores = [1, 2],
                    Address = new TestAddress { City = "Seattle", ZipCode = "98101" }
                };
                root.People =
                [
                    new TestPerson
                    {
                        FirstName = "John",
                        LastName = "Doe",
                        Scores = [85.5, 92.3],
                        Address = new TestAddress { City = "Portland", ZipCode = "97201" }
                    },
                    new TestPerson
                    {
                        FirstName = "Jane",
                        LastName = "Doe",
                        Scores = [88.1, 95.7],
                        Address = new TestAddress { City = "Vancouver", ZipCode = "98660" }
                    }
                ];
                root.PeopleByName = new Dictionary<string, TestPerson>
                {
                    ["john"] = new TestPerson
                    {
                        FirstName = "John",
                        LastName = "Dict",
                        Address = new TestAddress { City = "Boston", ZipCode = "02101" }
                    }
                };
                root.Sensor = new TestSensor
                {
                    Value = 25.5,
                    Unit = "°C",
                    MinValue = -40.0,
                    MaxValue = 85.0
                };
            },
            baseAddress: _port!.BaseAddress,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<DynamicSubject>(_logger!);
        await _client.StartAsync(
            context => new DynamicSubject(context),
            isConnected: root => root.TryGetRegisteredProperty(nameof(TestRoot.Connected))?.GetValue() is true,
            serverUrl: _port!.ServerUrl,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }
}