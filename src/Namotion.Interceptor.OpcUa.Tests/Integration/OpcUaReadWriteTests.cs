using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaReadWriteTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;

    private OpcUaTestServer<TestRoot>? _server;
    private OpcUaTestClient<TestRoot>? _client;
    private PortLease? _port;

    public OpcUaReadWriteTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WriteAndReadPrimitives_ShouldUpdateClient()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert
            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Test string property on server
            _server.Root.Name = "Updated Server Name";
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Name == "Updated Server Name",
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should receive server's name update");

            // Test string property on client
            _client.Root.Name = "Updated Client Name";
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Name == "Updated Client Name",
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive client's name update");

            // Test numeric property on server
            _server.Root.Number = 123.45m;
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Number == 123.45m,
                timeout: TimeSpan.FromSeconds(30),
                message: "Client should receive server's number update");

            // Test numeric property on client
            _client.Root.Number = 54.321m;
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Number == 54.321m,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive client's number update");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
            _port?.Dispose();
            _port = null;
        }
    }

    [Fact]
    public async Task WriteAndReadArraysOnServer_ShouldUpdateClient()
    {
        try
        {
            // Arrange - Start Server and Client
            await StartServerAsync();
            await StartClientAsync();

            // Act & Assert - Test basic array synchronization to validate valueRank
            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Test just one simple integer array
            _logger!.Log($"Server initial ScalarNumbers: [{string.Join(", ", _server.Root.ScalarNumbers)}]");
            _logger.Log($"Client initial ScalarNumbers: [{string.Join(", ", _client.Root.ScalarNumbers)}]");

            var newNumbers = new[] { 100, 200, 300 };
            _server.Root.ScalarNumbers = newNumbers;
            _logger.Log($"Server updated ScalarNumbers: [{string.Join(", ", _server.Root.ScalarNumbers)}]");

            // Wait for array synchronization (longer timeout for CI environments)
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.ScalarNumbers.SequenceEqual(newNumbers),
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should receive server's array update");

            _logger.Log($"Client ScalarNumbers after update: [{string.Join(", ", _client.Root.ScalarNumbers)}]");

            // If this fails, it indicates that either:
            // 1. Server-client sync is not working, OR
            // 2. The valueRank is incorrect and arrays can't sync properly
            Assert.Equal(newNumbers, _client.Root.ScalarNumbers);
            _logger.Log($"Basic array sync: [{string.Join(", ", _client.Root.ScalarNumbers)}]");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
            _port?.Dispose();
            _port = null;
        }
    }

    [Fact]
    public async Task WriteAndReadNestedStructures_ShouldUpdateClient()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Act: Write all properties at once (no waiting between writes)
            _server.Root.Person.FirstName = "UpdatedFirst";           // Test 1: Variable on object reference
            _server.Root.People[0].LastName = "UpdatedLast";          // Test 2: Variable on collection item
            _server.Root.PeopleByName!["john"].FirstName = "Johnny";  // Test 3: Variable on dictionary item
            _server.Root.Person.Address!.City = "New York";           // Test 4: Deep nesting
            _server.Root.People[0].Address!.ZipCode = "12345";        // Test 5: Collection + nesting
            _server.Root.Sensor!.Value = 42.5;                        // Test 6: OpcUaValue pattern
            _server.Root.Sensor.Unit = "°F";                          // Test 7: OpcUaValue child property
            _server.Root.Sensor.MinValue = -50.0;                     // Test 8: OpcUaValue child property
            _server.Root.Number_Unit = "items";                       // Test 9: PropertyAttribute subnode

            // Assert: Wait for all properties to sync in single check
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Person.FirstName == "UpdatedFirst" &&
                      _client.Root.People[0].LastName == "UpdatedLast" &&
                      _client.Root.PeopleByName!["john"].FirstName == "Johnny" &&
                      _client.Root.Person.Address!.City == "New York" &&
                      _client.Root.People[0].Address!.ZipCode == "12345" &&
                      Math.Abs(_client.Root.Sensor!.Value - 42.5) < 0.01 &&
                      _client.Root.Sensor!.Unit == "°F" &&
                      _client.Root.Sensor?.MinValue == -50.0 &&
                      _client.Root.Number_Unit == "items",
                timeout: TimeSpan.FromSeconds(60),
                message: "Client should receive all nested structure updates");

            _logger!.Log("All nested structure tests passed!");
        }
        finally
        {
            await (_client?.StopAsync() ?? Task.CompletedTask);
            await (_server?.StopAsync() ?? Task.CompletedTask);
            _port?.Dispose();
            _port = null;
        }
    }

    private async Task StartServerAsync()
    {
        _logger = new TestLogger(_output);
        _port = await OpcUaTestPortPool.AcquireAsync();

        _server = new OpcUaTestServer<TestRoot>(_logger);
        await _server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
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
                    },
                    ["jane"] = new TestPerson
                    {
                        FirstName = "Jane",
                        LastName = "Dict",
                        Address = new TestAddress { City = "Chicago", ZipCode = "60601" }
                    }
                };
                root.Sensor = new TestSensor
                {
                    Value = 25.5,
                    Unit = "°C",
                    MinValue = -40.0,
                    MaxValue = 85.0
                };
                root.Number = 42;
                root.Number_Unit = "count";  // PropertyAttribute subnode test
            },
            baseAddress: _port.BaseAddress,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<TestRoot>(_logger!);
        await _client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            serverUrl: _port!.ServerUrl,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }
}