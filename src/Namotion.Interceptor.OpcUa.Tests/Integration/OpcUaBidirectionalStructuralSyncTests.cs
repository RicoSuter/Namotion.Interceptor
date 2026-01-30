using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using System.Threading;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for bidirectional structural synchronization between server and client.
/// Both server and client have C# models that sync structural changes (add/remove subjects).
/// Uses dedicated server/client instances with EnableLiveSync enabled on both sides.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaBidirectionalStructuralSyncTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly TestLogger _logger;
    private IHost? _serverHost;
    private IHost? _clientHost;
    private PortLease? _port;
    private TestRoot? _serverRoot;
    private TestRoot? _clientRoot;
    private IInterceptorSubjectContext? _serverContext;
    private IInterceptorSubjectContext? _clientContext;

    public OpcUaBidirectionalStructuralSyncTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = new TestLogger(output);
    }

    public async Task InitializeAsync()
    {
        _port = await OpcUaTestPortPool.AcquireAsync();

        // Start server with EnableLiveSync + EnableExternalNodeManagement
        var serverBuilder = Host.CreateApplicationBuilder();

        serverBuilder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        serverBuilder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, "Server", LogLevel.Information);
        });

        _serverContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(serverBuilder.Services);

        _serverRoot = new TestRoot(_serverContext)
        {
            Connected = true,
            Name = "BidirectionalSyncTest",
            Number = 1m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>()
        };

        serverBuilder.Services.AddSingleton(_serverRoot);
        serverBuilder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                // Configure type registry for external AddNodes support
                var typeRegistry = new OpcUaTypeRegistry();
                typeRegistry.RegisterType<TestPerson>(ObjectTypeIds.BaseObjectType);

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = _port.BaseAddress,
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    CleanCertificateStore = true,
                    AutoAcceptUntrustedCertificates = true,
                    CertificateStoreBasePath = _port.CertificateStoreBasePath,
                    EnableLiveSync = true,
                    EnableExternalNodeManagement = true,
                    TypeRegistry = typeRegistry,
                    BufferTime = TimeSpan.FromMilliseconds(50)
                };
            });

        _serverHost = serverBuilder.Build();
        await _serverHost.StartAsync();
        _logger.Log("Server started with EnableLiveSync=true, EnableExternalNodeManagement=true");

        // Wait for server to be ready
        await Task.Delay(500);

        // Start client with EnableLiveSync + EnableRemoteNodeManagement
        var clientBuilder = Host.CreateApplicationBuilder();

        clientBuilder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        clientBuilder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, "Client", LogLevel.Information);
        });

        _clientContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(clientBuilder.Services);

        _clientRoot = new TestRoot(_clientContext)
        {
            Connected = false,
            Name = "",
            Number = 0m,
            People = [],
            PeopleByName = new Dictionary<string, TestPerson>()
        };

        clientBuilder.Services.AddSingleton(_clientRoot);
        clientBuilder.Services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TestRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaClientConfiguration
                {
                    ServerUrl = _port.ServerUrl,
                    RootName = "Root",
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,
                    CertificateStoreBasePath = $"{_port.CertificateStoreBasePath}/client",
                    EnableLiveSync = true,
                    EnableRemoteNodeManagement = true,
                    EnableModelChangeEvents = true,
                    EnablePeriodicResync = false,
                    BufferTime = TimeSpan.FromMilliseconds(50)
                };
            });

        _clientHost = clientBuilder.Build();
        await _clientHost.StartAsync();
        _logger.Log("Client started with EnableLiveSync=true, EnableRemoteNodeManagement=true, EnableModelChangeEvents=true");

        // Wait for client to connect and sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => _clientRoot.Connected,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should connect and sync Connected property");

        _logger.Log("Client connected and synced");
    }

    public async Task DisposeAsync()
    {
        if (_clientHost != null)
        {
            await _clientHost.StopAsync();
            _clientHost.Dispose();
        }
        if (_serverHost != null)
        {
            await _serverHost.StopAsync();
            _serverHost.Dispose();
        }
        _port?.Dispose();
    }

    #region Server → Client Structural Sync Tests

    [Fact]
    public async Task ServerAddsToCollection_ClientModelUpdated()
    {
        // Arrange
        var initialClientCount = _clientRoot!.People.Length;
        _logger.Log($"Initial state: server.People.Length={_serverRoot!.People.Length}, client.People.Length={initialClientCount}");

        // Debug: Subscribe to property changes on server to verify tracking
        var changeCount = 0;
        using var changeSubscription = _serverContext!.CreatePropertyChangeQueueSubscription();

        // Start a background task to poll for changes
        var cts = new CancellationTokenSource();
        var pollTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (changeSubscription.TryDequeue(out var change, cts.Token))
                {
                    Interlocked.Increment(ref changeCount);
                    _logger.Log($"[ChangeTracking] Property changed: {change.Property.Name}");
                }
                await Task.Delay(10, cts.Token);
            }
        }, cts.Token);

        // Act - server adds a person to collection
        var newPerson = new TestPerson(_serverContext!)
        {
            FirstName = "ServerAdded",
            LastName = "Person"
        };
        _serverRoot.People = [.._serverRoot.People, newPerson];
        _logger.Log($"Server added person: {newPerson.FirstName} {newPerson.LastName}");

        // Give time for change tracking to fire
        await Task.Delay(500);
        _logger.Log($"Change tracking observed {changeCount} changes");

        // Cancel the poll task
        await cts.CancelAsync();
        try { await pollTask; } catch (OperationCanceledException) { }

        // Assert - client model should update (wait for both length AND property values)
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientCount = _clientRoot.People.Length;
                if (clientCount != initialClientCount + 1)
                {
                    _logger.Log($"Waiting: client.People.Length={clientCount} (expecting {initialClientCount + 1})");
                    return false;
                }

                // Also check that property values have been read
                var lastPerson = _clientRoot.People.LastOrDefault();
                if (lastPerson?.FirstName != "ServerAdded")
                {
                    _logger.Log($"Waiting: collection synced but FirstName='{lastPerson?.FirstName}' (expecting 'ServerAdded')");
                    return false;
                }
                return true;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Client should receive server's collection add with property values. Current: {_clientRoot.People.Length}");

        _logger.Log($"After sync: client.People.Length={_clientRoot.People.Length}");
        Assert.Equal(initialClientCount + 1, _clientRoot.People.Length);

        // Verify the added person's properties synced
        var clientPerson = _clientRoot.People.LastOrDefault();
        Assert.NotNull(clientPerson);
        Assert.Equal("ServerAdded", clientPerson.FirstName);
        Assert.Equal("Person", clientPerson.LastName);
        _logger.Log("Server→Client collection add verified");
    }

    [Fact]
    public async Task ServerRemovesFromCollection_ClientModelUpdated()
    {
        // Arrange - add persons first
        var person1 = new TestPerson(_serverContext!) { FirstName = "Keep", LastName = "This" };
        var person2 = new TestPerson(_serverContext!) { FirstName = "Remove", LastName = "This" };
        _serverRoot!.People = [person1, person2];
        _logger.Log("Server added two persons");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => _clientRoot!.People.Length == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial two persons");

        _logger.Log($"Client synced initial state: {_clientRoot!.People.Length} persons");

        // Act - server removes one person
        _serverRoot.People = [person1];
        _logger.Log("Server removed second person");

        // Assert - client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientCount = _clientRoot.People.Length;
                if (clientCount != 1)
                    _logger.Log($"Waiting: client.People.Length={clientCount} (expecting 1)");
                return clientCount == 1;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Client should receive server's collection remove. Current: {_clientRoot.People.Length}");

        _logger.Log($"After sync: client.People.Length={_clientRoot.People.Length}");
        Assert.Single(_clientRoot.People);
        Assert.Equal("Keep", _clientRoot.People[0].FirstName);
        _logger.Log("Server→Client collection remove verified");
    }

    [Fact]
    public async Task ServerAddsToDictionary_ClientModelUpdated()
    {
        // Arrange
        _logger.Log($"Initial state: server.PeopleByName.Count={_serverRoot!.PeopleByName?.Count ?? 0}");

        // Act - server adds to dictionary
        var newPerson = new TestPerson(_serverContext!)
        {
            FirstName = "DictServer",
            LastName = "Added"
        };
        _serverRoot.PeopleByName = new Dictionary<string, TestPerson>(_serverRoot.PeopleByName ?? new())
        {
            ["serverkey"] = newPerson
        };
        _logger.Log("Server added 'serverkey' to dictionary");

        // Assert - client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasKey = _clientRoot!.PeopleByName?.ContainsKey("serverkey") ?? false;
                if (!hasKey)
                    _logger.Log("Waiting: client.PeopleByName does not contain 'serverkey'");
                return hasKey;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's dictionary add");

        _logger.Log($"After sync: client.PeopleByName contains 'serverkey'");
        Assert.True(_clientRoot!.PeopleByName?.ContainsKey("serverkey"));
        Assert.Equal("DictServer", _clientRoot.PeopleByName!["serverkey"].FirstName);
        _logger.Log("Server→Client dictionary add verified");
    }

    [Fact]
    public async Task ServerRemovesFromDictionary_ClientModelUpdated()
    {
        // Arrange - add entries first
        var person1 = new TestPerson(_serverContext!) { FirstName = "KeepDict", LastName = "Entry" };
        var person2 = new TestPerson(_serverContext!) { FirstName = "RemoveDict", LastName = "Entry" };
        _serverRoot!.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1,
            ["remove"] = person2
        };
        _logger.Log("Server added two dictionary entries");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => (_clientRoot!.PeopleByName?.Count ?? 0) == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial dictionary entries");

        _logger.Log($"Client synced initial state: {_clientRoot!.PeopleByName?.Count} entries");

        // Act - server removes one entry
        _serverRoot.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1
        };
        _logger.Log("Server removed 'remove' key");

        // Assert - client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasKey = _clientRoot.PeopleByName?.ContainsKey("remove") ?? false;
                if (hasKey)
                    _logger.Log("Waiting: client.PeopleByName still contains 'remove'");
                return !hasKey;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's dictionary remove");

        _logger.Log($"After sync: client.PeopleByName does not contain 'remove'");
        Assert.False(_clientRoot.PeopleByName?.ContainsKey("remove"));
        Assert.True(_clientRoot.PeopleByName?.ContainsKey("keep"));
        _logger.Log("Server→Client dictionary remove verified");
    }

    [Fact]
    public async Task ServerAssignsReference_ClientModelUpdated()
    {
        // Arrange
        _logger.Log($"Initial state: server.Person={_serverRoot!.Person?.FirstName ?? "null"}");

        // Act - server assigns a Person reference
        var newPerson = new TestPerson(_serverContext!)
        {
            FirstName = "RefServer",
            LastName = "Assigned"
        };
        _serverRoot.Person = newPerson;
        _logger.Log($"Server assigned Person: {newPerson.FirstName}");

        // Assert - client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientFirstName = _clientRoot!.Person?.FirstName;
                if (clientFirstName != "RefServer")
                    _logger.Log($"Waiting: client.Person.FirstName={clientFirstName ?? "null"} (expecting 'RefServer')");
                return clientFirstName == "RefServer";
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Client should receive server's reference assignment. Current: {_clientRoot!.Person?.FirstName ?? "null"}");

        _logger.Log($"After sync: client.Person.FirstName={_clientRoot.Person?.FirstName}");
        Assert.Equal("RefServer", _clientRoot.Person?.FirstName);
        Assert.Equal("Assigned", _clientRoot.Person?.LastName);
        _logger.Log("Server→Client reference assignment verified");
    }

    [Fact]
    public async Task ServerClearsReference_ClientModelUpdated()
    {
        // Arrange - assign a Person first
        var person = new TestPerson(_serverContext!) { FirstName = "ToClear", LastName = "Ref" };
        _serverRoot!.Person = person;
        _logger.Log("Server assigned initial Person");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => _clientRoot!.Person?.FirstName == "ToClear",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial Person");

        _logger.Log($"Client synced initial Person: {_clientRoot!.Person?.FirstName}");

        // Act - server clears the reference
        _serverRoot.Person = null!;
        _logger.Log("Server cleared Person reference");

        // Assert - client model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var clientPerson = _clientRoot.Person;
                if (clientPerson != null)
                    _logger.Log($"Waiting: client.Person is not null (FirstName={clientPerson.FirstName})");
                return clientPerson == null;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Client should receive server's reference clear");

        _logger.Log("After sync: client.Person is null");
        Assert.Null(_clientRoot.Person);
        _logger.Log("Server→Client reference clear verified");
    }

    #endregion

    #region Client → Server Structural Sync Tests

    [Fact]
    public async Task ClientAddsToCollection_ServerModelUpdated()
    {
        // Arrange
        var initialServerCount = _serverRoot!.People.Length;
        _logger.Log($"Initial state: server.People.Length={initialServerCount}, client.People.Length={_clientRoot!.People.Length}");

        // Act - client adds a person to collection
        var newPerson = new TestPerson(_clientContext!)
        {
            FirstName = "ClientAdded",
            LastName = "Person"
        };
        _clientRoot.People = [.._clientRoot.People, newPerson];
        _logger.Log($"Client added person: {newPerson.FirstName} {newPerson.LastName}");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverCount = _serverRoot.People.Length;
                if (serverCount != initialServerCount + 1)
                    _logger.Log($"Waiting: server.People.Length={serverCount} (expecting {initialServerCount + 1})");
                return serverCount == initialServerCount + 1;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Server should receive client's collection add. Current: {_serverRoot.People.Length}");

        _logger.Log($"After sync: server.People.Length={_serverRoot.People.Length}");
        Assert.Equal(initialServerCount + 1, _serverRoot.People.Length);

        // Verify the added person's properties synced
        var serverPerson = _serverRoot.People.LastOrDefault();
        Assert.NotNull(serverPerson);
        Assert.Equal("ClientAdded", serverPerson.FirstName);
        _logger.Log("Client→Server collection add verified");
    }

    [Fact]
    public async Task ClientRemovesFromCollection_ServerModelUpdated()
    {
        // Arrange - server starts with persons, client syncs them
        var person1 = new TestPerson(_serverContext!) { FirstName = "KeepClient", LastName = "Test" };
        var person2 = new TestPerson(_serverContext!) { FirstName = "RemoveClient", LastName = "Test" };
        _serverRoot!.People = [person1, person2];
        _logger.Log("Server initialized with two persons");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => _clientRoot!.People.Length == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial two persons");

        _logger.Log($"Client synced: {_clientRoot!.People.Length} persons");

        // Act - client removes one person (keep only first)
        _clientRoot.People = [_clientRoot.People[0]];
        _logger.Log("Client removed second person");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverCount = _serverRoot.People.Length;
                if (serverCount != 1)
                    _logger.Log($"Waiting: server.People.Length={serverCount} (expecting 1)");
                return serverCount == 1;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Server should receive client's collection remove. Current: {_serverRoot.People.Length}");

        _logger.Log($"After sync: server.People.Length={_serverRoot.People.Length}");
        Assert.Single(_serverRoot.People);
        _logger.Log("Client→Server collection remove verified");
    }

    [Fact]
    public async Task ClientAddsToDictionary_ServerModelUpdated()
    {
        // Arrange
        _logger.Log($"Initial state: server.PeopleByName.Count={_serverRoot!.PeopleByName?.Count ?? 0}");

        // Act - client adds to dictionary
        var newPerson = new TestPerson(_clientContext!)
        {
            FirstName = "DictClient",
            LastName = "Added"
        };
        _clientRoot!.PeopleByName = new Dictionary<string, TestPerson>(_clientRoot.PeopleByName ?? new())
        {
            ["clientkey"] = newPerson
        };
        _logger.Log("Client added 'clientkey' to dictionary");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasKey = _serverRoot.PeopleByName?.ContainsKey("clientkey") ?? false;
                if (!hasKey)
                    _logger.Log("Waiting: server.PeopleByName does not contain 'clientkey'");
                return hasKey;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's dictionary add");

        _logger.Log($"After sync: server.PeopleByName contains 'clientkey'");
        Assert.True(_serverRoot.PeopleByName?.ContainsKey("clientkey"));
        Assert.Equal("DictClient", _serverRoot.PeopleByName!["clientkey"].FirstName);
        _logger.Log("Client→Server dictionary add verified");
    }

    [Fact]
    public async Task ClientRemovesFromDictionary_ServerModelUpdated()
    {
        // Subscribe to lifecycle events for debugging
        var clientLifecycle = _clientContext!.TryGetLifecycleInterceptor();
        if (clientLifecycle is not null)
        {
            clientLifecycle.SubjectAttached += change =>
                _logger.Log($"[Lifecycle] SubjectAttached: {change.Subject.GetType().Name}, RefCount={change.ReferenceCount}, IsContextAttach={change.IsContextAttach}");
            clientLifecycle.SubjectDetaching += change =>
                _logger.Log($"[Lifecycle] SubjectDetaching: {change.Subject.GetType().Name}, RefCount={change.ReferenceCount}, IsContextDetach={change.IsContextDetach}");
        }
        else
        {
            _logger.Log("[Lifecycle] WARNING: No lifecycle interceptor found on client context!");
        }

        // Arrange - server starts with dictionary entries
        var person1 = new TestPerson(_serverContext!) { FirstName = "KeepDictC", LastName = "Entry" };
        var person2 = new TestPerson(_serverContext!) { FirstName = "RemoveDictC", LastName = "Entry" };
        _serverRoot!.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keepc"] = person1,
            ["removec"] = person2
        };
        _logger.Log("Server initialized with two dictionary entries");

        // Wait for client to sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => (_clientRoot!.PeopleByName?.Count ?? 0) == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should sync initial dictionary entries");

        _logger.Log($"Client synced: {_clientRoot!.PeopleByName?.Count} entries");

        // Log the client's dictionary entries
        if (_clientRoot.PeopleByName is not null)
        {
            foreach (var kvp in _clientRoot.PeopleByName)
            {
                var person = kvp.Value;
                var registered = person.TryGetRegisteredSubject();
                var refCount = person.GetReferenceCount();
                _logger.Log($"  Client dict['{kvp.Key}']: {person.FirstName} {person.LastName}, RefCount={refCount}, IsRegistered={registered is not null}");
            }
        }

        // Act - client removes one entry (create new dict without 'removec')
        var keepPerson = _clientRoot.PeopleByName!["keepc"];
        _logger.Log($"About to set new dictionary. Keeping person: {keepPerson.FirstName}, RefCount={keepPerson.GetReferenceCount()}");

        _clientRoot.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keepc"] = keepPerson
        };
        _logger.Log("Client set new dictionary without 'removec' key");

        // Give lifecycle interceptor time to process
        await Task.Delay(100);
        _logger.Log("After delay, waiting for server update...");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var hasKey = _serverRoot.PeopleByName?.ContainsKey("removec") ?? false;
                if (hasKey)
                    _logger.Log("Waiting: server.PeopleByName still contains 'removec'");
                return !hasKey;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: "Server should receive client's dictionary remove");

        _logger.Log($"After sync: server.PeopleByName does not contain 'removec'");
        Assert.False(_serverRoot.PeopleByName?.ContainsKey("removec"));
        Assert.True(_serverRoot.PeopleByName?.ContainsKey("keepc"));
        _logger.Log("Client→Server dictionary remove verified");
    }

    [Fact]
    public async Task ClientAssignsReference_ServerModelUpdated()
    {
        // Arrange
        _logger.Log($"Initial state: server.Person={_serverRoot!.Person?.FirstName ?? "null"}");

        // Act - client assigns a Person reference
        var newPerson = new TestPerson(_clientContext!)
        {
            FirstName = "RefClient",
            LastName = "Assigned"
        };
        _clientRoot!.Person = newPerson;
        _logger.Log($"Client assigned Person: {newPerson.FirstName}");

        // Assert - server model should update
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var serverFirstName = _serverRoot.Person?.FirstName;
                if (serverFirstName != "RefClient")
                    _logger.Log($"Waiting: server.Person.FirstName={serverFirstName ?? "null"} (expecting 'RefClient')");
                return serverFirstName == "RefClient";
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Server should receive client's reference assignment. Current: {_serverRoot.Person?.FirstName ?? "null"}");

        _logger.Log($"After sync: server.Person.FirstName={_serverRoot.Person?.FirstName}");
        Assert.Equal("RefClient", _serverRoot.Person?.FirstName);
        _logger.Log("Client→Server reference assignment verified");
    }

    #endregion
}
