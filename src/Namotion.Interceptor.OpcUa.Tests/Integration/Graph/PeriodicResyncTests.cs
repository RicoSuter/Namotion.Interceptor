using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for periodic resync mode (polling-based structural change detection).
/// Uses dedicated server/client instances with EnablePeriodicResync=true and EnableModelChangeEvents=false.
/// These tests verify that the client can detect structural changes by periodically re-browsing the server.
/// </summary>
[Trait("Category", "Integration")]
public class PeriodicResyncTests
{
    private readonly ITestOutputHelper _output;
    private readonly TestLogger _logger;

    public PeriodicResyncTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = new TestLogger(output);
    }

    [Fact]
    public async Task AddToCollection_ClientDetectsChange()
    {
        var port = await OpcUaTestPortPool.AcquireAsync();
        try
        {
            var server = new OpcUaTestServer<TestRoot>(_logger);
            var client = new OpcUaTestClient<TestRoot>(_logger, ConfigurePeriodicResyncClient);

            await using (server)
            await using (client)
            {
                await server.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = true,
                        Name = "PeriodicResyncTest",
                        Number = 1m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>()
                    },
                    baseAddress: port.BaseAddress,
                    certificateStoreBasePath: port.CertificateStoreBasePath,
                    configureServer: ConfigureLiveSyncServer);

                await client.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = false,
                        Name = "",
                        Number = 0m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>()
                    },
                    root => root.Connected,
                    serverUrl: port.ServerUrl,
                    certificateStoreBasePath: $"{port.CertificateStoreBasePath}/client");

                _logger.Log("Server and client started");

                // Verify client starts with empty collection
                Assert.Empty(client.Root!.People);

                // Act: Add a person to the server's collection
                var serverPerson = new TestPerson(server.Context!)
                {
                    FirstName = "PeriodicSync",
                    LastName = "Person"
                };
                server.Root!.People = [serverPerson];
                _logger.Log("Added person to server collection");

                // Assert: Client detects the change via periodic resync
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.People.Length == 1,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should detect new person via periodic resync");

                var clientPerson = client.Root!.People[0];
                await AsyncTestHelpers.WaitUntilAsync(
                    () => clientPerson.FirstName == "PeriodicSync",
                    timeout: TimeSpan.FromSeconds(10),
                    message: "Client should sync person properties");

                Assert.Equal("PeriodicSync", clientPerson.FirstName);
                Assert.Equal("Person", clientPerson.LastName);
                _logger.Log($"Client synced person: {clientPerson.FirstName} {clientPerson.LastName}");
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    [Fact]
    public async Task RemoveFromCollection_ClientDetectsChange()
    {
        var port = await OpcUaTestPortPool.AcquireAsync();
        try
        {
            var server = new OpcUaTestServer<TestRoot>(_logger);
            var client = new OpcUaTestClient<TestRoot>(_logger, ConfigurePeriodicResyncClient);

            await using (server)
            await using (client)
            {
                // Start server with initial person
                var person1 = default(TestPerson);
                var person2 = default(TestPerson);

                await server.StartAsync(
                    context =>
                    {
                        var root = new TestRoot(context)
                        {
                            Connected = true,
                            Name = "PeriodicResyncTest",
                            Number = 1m,
                            PeopleByName = new Dictionary<string, TestPerson>()
                        };
                        person1 = new TestPerson(context) { FirstName = "Keep", LastName = "Person" };
                        person2 = new TestPerson(context) { FirstName = "Remove", LastName = "Person" };
                        root.People = [person1, person2];
                        return root;
                    },
                    baseAddress: port.BaseAddress,
                    certificateStoreBasePath: port.CertificateStoreBasePath,
                    configureServer: ConfigureLiveSyncServer);

                await client.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = false,
                        Name = "",
                        Number = 0m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>()
                    },
                    root => root.Connected,
                    serverUrl: port.ServerUrl,
                    certificateStoreBasePath: $"{port.CertificateStoreBasePath}/client");

                _logger.Log("Server and client started");

                // Wait for client to sync initial state
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.People.Length == 2,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should sync initial two people");

                _logger.Log($"Client has {client.Root!.People.Length} people");

                // Act: Remove one person from the server's collection
                server.Root!.People = [person1!];
                _logger.Log("Removed person from server collection");

                // Assert: Client detects the removal via periodic resync
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.People.Length == 1,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should detect removal via periodic resync");

                Assert.Single(client.Root!.People);
                Assert.Equal("Keep", client.Root!.People[0].FirstName);
                _logger.Log("Client detected removal");
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    [Fact]
    public async Task AssignReference_ClientDetectsChange()
    {
        var port = await OpcUaTestPortPool.AcquireAsync();
        try
        {
            var server = new OpcUaTestServer<TestRoot>(_logger);
            var client = new OpcUaTestClient<TestRoot>(_logger, ConfigurePeriodicResyncClient);

            await using (server)
            await using (client)
            {
                await server.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = true,
                        Name = "PeriodicResyncTest",
                        Number = 1m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>(),
                        Person = null!
                    },
                    baseAddress: port.BaseAddress,
                    certificateStoreBasePath: port.CertificateStoreBasePath,
                    configureServer: ConfigureLiveSyncServer);

                await client.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = false,
                        Name = "",
                        Number = 0m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>(),
                        Person = null!
                    },
                    root => root.Connected,
                    serverUrl: port.ServerUrl,
                    certificateStoreBasePath: $"{port.CertificateStoreBasePath}/client");

                _logger.Log("Server and client started");

                // Verify client starts with null reference
                Assert.Null(client.Root!.Person);

                // Act: Assign a person reference on the server
                var serverPerson = new TestPerson(server.Context!)
                {
                    FirstName = "RefTest",
                    LastName = "Person"
                };
                server.Root!.Person = serverPerson;
                _logger.Log("Assigned Person reference on server");

                // Assert: Client detects the change via periodic resync
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.Person != null,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should detect reference assignment via periodic resync");

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.Person?.FirstName == "RefTest",
                    timeout: TimeSpan.FromSeconds(10),
                    message: "Client should sync person properties");

                Assert.NotNull(client.Root!.Person);
                Assert.Equal("RefTest", client.Root!.Person.FirstName);
                Assert.Equal("Person", client.Root!.Person.LastName);
                _logger.Log($"Client synced Person: {client.Root!.Person.FirstName}");
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    [Fact]
    public async Task ClearReference_ClientDetectsChange()
    {
        var port = await OpcUaTestPortPool.AcquireAsync();
        try
        {
            var server = new OpcUaTestServer<TestRoot>(_logger);
            var client = new OpcUaTestClient<TestRoot>(_logger, ConfigurePeriodicResyncClient);

            await using (server)
            await using (client)
            {
                // Start server with initial person reference
                await server.StartAsync(
                    context =>
                    {
                        var root = new TestRoot(context)
                        {
                            Connected = true,
                            Name = "PeriodicResyncTest",
                            Number = 1m,
                            People = [],
                            PeopleByName = new Dictionary<string, TestPerson>()
                        };
                        root.Person = new TestPerson(context)
                        {
                            FirstName = "ToClear",
                            LastName = "Person"
                        };
                        return root;
                    },
                    baseAddress: port.BaseAddress,
                    certificateStoreBasePath: port.CertificateStoreBasePath,
                    configureServer: ConfigureLiveSyncServer);

                await client.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = false,
                        Name = "",
                        Number = 0m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>(),
                        Person = null!
                    },
                    root => root.Connected,
                    serverUrl: port.ServerUrl,
                    certificateStoreBasePath: $"{port.CertificateStoreBasePath}/client");

                _logger.Log("Server and client started");

                // Wait for client to sync initial reference
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.Person?.FirstName == "ToClear",
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should sync initial Person reference");

                _logger.Log($"Client synced initial Person: {client.Root!.Person?.FirstName}");

                // Act: Clear the reference on server
                server.Root!.Person = null!;
                _logger.Log("Cleared Person reference on server");

                // Assert: Client detects the change via periodic resync
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.Person == null,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should detect reference clear via periodic resync");

                Assert.Null(client.Root!.Person);
                _logger.Log("Client detected reference clear");
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    [Fact]
    public async Task AddToDictionary_ClientDetectsChange()
    {
        var port = await OpcUaTestPortPool.AcquireAsync();
        try
        {
            var server = new OpcUaTestServer<TestRoot>(_logger);
            var client = new OpcUaTestClient<TestRoot>(_logger, ConfigurePeriodicResyncClient);

            await using (server)
            await using (client)
            {
                await server.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = true,
                        Name = "PeriodicResyncTest",
                        Number = 1m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>()
                    },
                    baseAddress: port.BaseAddress,
                    certificateStoreBasePath: port.CertificateStoreBasePath,
                    configureServer: ConfigureLiveSyncServer);

                await client.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = false,
                        Name = "",
                        Number = 0m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>()
                    },
                    root => root.Connected,
                    serverUrl: port.ServerUrl,
                    certificateStoreBasePath: $"{port.CertificateStoreBasePath}/client");

                _logger.Log("Server and client started");

                // Verify client starts with empty dictionary
                Assert.Empty(client.Root!.PeopleByName ?? new Dictionary<string, TestPerson>());

                // Act: Add to dictionary on server
                var serverPerson = new TestPerson(server.Context!)
                {
                    FirstName = "DictTest",
                    LastName = "Person"
                };
                server.Root!.PeopleByName = new Dictionary<string, TestPerson>
                {
                    ["testkey"] = serverPerson
                };
                _logger.Log("Added 'testkey' to server dictionary");

                // Assert: Client detects the change via periodic resync
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.PeopleByName?.ContainsKey("testkey") ?? false,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should detect dictionary entry via periodic resync");

                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Root!.PeopleByName?["testkey"].FirstName == "DictTest",
                    timeout: TimeSpan.FromSeconds(10),
                    message: "Client should sync dictionary entry properties");

                Assert.True(client.Root!.PeopleByName?.ContainsKey("testkey"));
                Assert.Equal("DictTest", client.Root!.PeopleByName!["testkey"].FirstName);
                _logger.Log($"Client synced dictionary entry: {client.Root!.PeopleByName["testkey"].FirstName}");
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    [Fact]
    public async Task RemoveFromDictionary_ClientDetectsChange()
    {
        var port = await OpcUaTestPortPool.AcquireAsync();
        try
        {
            var server = new OpcUaTestServer<TestRoot>(_logger);
            var client = new OpcUaTestClient<TestRoot>(_logger, ConfigurePeriodicResyncClient);

            await using (server)
            await using (client)
            {
                // Start server with initial dictionary entries
                var person1 = default(TestPerson);
                var person2 = default(TestPerson);

                await server.StartAsync(
                    context =>
                    {
                        var root = new TestRoot(context)
                        {
                            Connected = true,
                            Name = "PeriodicResyncTest",
                            Number = 1m,
                            People = []
                        };
                        person1 = new TestPerson(context) { FirstName = "Keep", LastName = "Entry" };
                        person2 = new TestPerson(context) { FirstName = "Remove", LastName = "Entry" };
                        root.PeopleByName = new Dictionary<string, TestPerson>
                        {
                            ["keep"] = person1,
                            ["remove"] = person2
                        };
                        return root;
                    },
                    baseAddress: port.BaseAddress,
                    certificateStoreBasePath: port.CertificateStoreBasePath,
                    configureServer: ConfigureLiveSyncServer);

                await client.StartAsync(
                    context => new TestRoot(context)
                    {
                        Connected = false,
                        Name = "",
                        Number = 0m,
                        People = [],
                        PeopleByName = new Dictionary<string, TestPerson>()
                    },
                    root => root.Connected,
                    serverUrl: port.ServerUrl,
                    certificateStoreBasePath: $"{port.CertificateStoreBasePath}/client");

                _logger.Log("Server and client started");

                // Wait for client to sync initial state
                await AsyncTestHelpers.WaitUntilAsync(
                    () => (client.Root!.PeopleByName?.Count ?? 0) == 2,
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should sync initial two dictionary entries");

                _logger.Log($"Client has {client.Root!.PeopleByName?.Count} dictionary entries");

                // Act: Remove one entry from the server's dictionary
                server.Root!.PeopleByName = new Dictionary<string, TestPerson>
                {
                    ["keep"] = person1!
                };
                _logger.Log("Removed 'remove' key from server dictionary");

                // Assert: Client detects the removal via periodic resync
                await AsyncTestHelpers.WaitUntilAsync(
                    () => !(client.Root!.PeopleByName?.ContainsKey("remove") ?? false),
                    timeout: TimeSpan.FromSeconds(15),
                    message: "Client should detect removal via periodic resync");

                Assert.False(client.Root!.PeopleByName?.ContainsKey("remove"));
                Assert.True(client.Root!.PeopleByName?.ContainsKey("keep"));
                _logger.Log("Client detected dictionary entry removal");
            }
        }
        finally
        {
            port.Dispose();
        }
    }

    /// <summary>
    /// Configures the client for periodic resync mode (polling, no model change events).
    /// </summary>
    private static void ConfigurePeriodicResyncClient(OpcUaClientConfiguration config)
    {
        config.EnableLiveSync = true;
        config.EnableModelChangeEvents = false;  // Disable event-based detection
        config.EnablePeriodicResync = true;      // Enable polling mode
        config.PeriodicResyncInterval = TimeSpan.FromSeconds(1);
        config.BufferTime = TimeSpan.FromMilliseconds(50);
    }

    /// <summary>
    /// Configures the server for live sync (structural change publishing).
    /// </summary>
    private static void ConfigureLiveSyncServer(OpcUaServerConfiguration config)
    {
        config.EnableLiveSync = true;
        config.BufferTime = TimeSpan.FromMilliseconds(50);
    }
}
