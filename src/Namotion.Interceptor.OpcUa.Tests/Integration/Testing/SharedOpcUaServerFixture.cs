using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// xUnit fixture that provides a shared OPC UA server for Category A tests.
/// Starts once per test collection, enables unlimited parallel test execution.
/// </summary>
public class SharedOpcUaServerFixture : IAsyncLifetime
{
    private const int Port = 4840;
    private const string CertificateStorePath = "pki-shared";

    private OpcUaTestServer<SharedTestModel>? _server;
    private readonly TestLogger _logger = new(new NullTestOutputHelper());

    /// <summary>
    /// A no-op test output helper for when no output is available (like in fixture initialization).
    /// </summary>
    private sealed class NullTestOutputHelper : Xunit.Abstractions.ITestOutputHelper
    {
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }

    /// <summary>
    /// The server's root model with partitioned test areas.
    /// </summary>
    public SharedTestModel ServerRoot => _server?.Root ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// The server's interceptor subject context for creating new instances.
    /// </summary>
    public IInterceptorSubjectContext ServerContext => _server?.Context ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// The OPC UA server endpoint URL.
    /// </summary>
    public string ServerUrl => $"opc.tcp://localhost:{Port}";

    public async Task InitializeAsync()
    {
        _server = new OpcUaTestServer<SharedTestModel>(_logger);
        await _server.StartAsync(
            createRoot: context => new SharedTestModel(context),
            initializeDefaults: InitializeTestData,
            baseAddress: $"opc.tcp://localhost:{Port}/",
            certificateStoreBasePath: CertificateStorePath,
            configureServer: config =>
            {
                config.EnableGraphChangePublishing = true;
                config.EnableNodeManagement = true;

                // Configure TypeRegistry so the server can create NestedPerson instances
                // when clients send AddNodes requests with BaseObjectType (the default)
                var typeRegistry = new OpcUaTypeRegistry();
                typeRegistry.RegisterType<NestedPerson>(ObjectTypeIds.BaseObjectType);
                config.TypeRegistry = typeRegistry;
            });
    }

    private void InitializeTestData(IInterceptorSubjectContext context, SharedTestModel root)
    {
        // Set Connected flag for client connection detection
        root.Connected = true;

        // Initialize ReadWrite test areas
        root.ReadWrite = new ReadWriteTestArea(context)
        {
            BasicSync = new BasicSyncArea(context)
            {
                Name = "Initial",
                Number = 0m
            },
            ArraySync = new ArraySyncArea(context)
            {
                ScalarNumbers = [10, 20, 30, 40, 50],
                ScalarStrings = ["Server", "Test", "Array"]
            }
        };

        // Initialize Transaction test areas
        root.Transactions = new TransactionTestArea(context)
        {
            SingleProperty = new TransactionSinglePropertyArea(context)
            {
                Name = "Initial Server Value"
            },
            MultiProperty = new TransactionMultiPropertyArea(context)
            {
                Name = "Initial",
                Number = 0m
            }
        };

        // Initialize Nested structure test areas
        root.Nested = new NestedTestArea(context)
        {
            Person = new NestedPerson(context)
            {
                FirstName = "John",
                LastName = "Smith",
                Scores = [1, 2],
                Address = new NestedAddress(context) { City = "Seattle", ZipCode = "98101" }
            },
            People =
            [
                new NestedPerson(context)
                {
                    FirstName = "John",
                    LastName = "Doe",
                    Scores = [85.5, 92.3],
                    Address = new NestedAddress(context) { City = "Portland", ZipCode = "97201" }
                },
                new NestedPerson(context)
                {
                    FirstName = "Jane",
                    LastName = "Doe",
                    Scores = [88.1, 95.7],
                    Address = new NestedAddress(context) { City = "Vancouver", ZipCode = "98660" }
                }
            ],
            PeopleByName = new Dictionary<string, NestedPerson>
            {
                ["john"] = new NestedPerson(context)
                {
                    FirstName = "John",
                    LastName = "Dict",
                    Address = new NestedAddress(context) { City = "Boston", ZipCode = "02101" }
                },
                ["jane"] = new NestedPerson(context)
                {
                    FirstName = "Jane",
                    LastName = "Dict",
                    Address = new NestedAddress(context) { City = "Chicago", ZipCode = "60601" }
                }
            },
            Sensor = new NestedSensor(context)
            {
                Value = 25.5,
                Unit = "°C",
                MinValue = -40.0,
                MaxValue = 85.0
            },
            Number = 42,
            Number_Unit = "count"
        };

        // Initialize DataTypes test area
        root.DataTypes = new DataTypesTestArea(context)
        {
            BoolValue = true,
            IntValue = 42,
            LongValue = 9876543210L,
            DateTimeValue = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            ByteArray = [0x01, 0x02, 0x03]
        };

        // Initialize Collections test area
        root.Collections = new CollectionsTestArea(context)
        {
            IntArray = [1, 2, 3]
        };

        // Initialize MultiClient test area
        root.MultiClient = new MultiClientTestArea(context)
        {
            SharedValue = "initial",
            Counter = 0,
            LastWriter = null
        };

        // Initialize ClientToServerSync test area
        root.ClientToServerSync = new ClientToServerSyncTestArea(context)
        {
            Person = new NestedPerson(context)
            {
                FirstName = "SyncTest",
                LastName = "Person",
                Scores = [90.0, 95.0],
                Address = new NestedAddress(context) { City = "SyncCity", ZipCode = "11111" }
            },
            Sensor = new NestedSensor(context)
            {
                Value = 50.0,
                Unit = "°C",
                MinValue = 0.0,
                MaxValue = 100.0
            }
        };

        root.ServerToClientReference = new ServerToClientReferenceTestArea(context);
        root.ServerToClientCollection = new ServerToClientCollectionTestArea(context);
        root.ServerToClientDictionary = new ServerToClientDictionaryTestArea(context);
        
        root.ClientToServerReference = new ClientToServerReferenceTestArea(context);
        root.ClientToServerCollection = new ClientToServerCollectionTestArea(context);
        root.ClientToServerDictionary = new ClientToServerDictionaryTestArea(context);
        root.ClientToServerNestedProperty = new ClientToServerNestedPropertyTestArea(context);
    }

    /// <summary>
    /// Creates a new client connected to the shared server.
    /// Each test should create its own client for isolation.
    /// </summary>
    public async Task<OpcUaTestClient<SharedTestModel>> CreateClientAsync(
        TestLogger logger,
        Action<OpcUaClientConfiguration>? configureClient = null)
    {
        var client = new OpcUaTestClient<SharedTestModel>(logger, configureClient);
        await client.StartAsync(
            createRoot: context => new SharedTestModel(context),
            isConnected: root => root.Connected,
            serverUrl: ServerUrl,
            certificateStoreBasePath: $"pki-client-{Guid.NewGuid():N}");
        return client;
    }

    /// <summary>
    /// Creates a new dynamic client that discovers properties from the shared server.
    /// </summary>
    public async Task<OpcUaTestClient<Dynamic.DynamicSubject>> CreateDynamicClientAsync(TestLogger logger)
    {
        var client = new OpcUaTestClient<Dynamic.DynamicSubject>(logger);
        await client.StartAsync(
            createRoot: context => new Dynamic.DynamicSubject(context),
            isConnected: root => root.TryGetRegisteredProperty(nameof(SharedTestModel.Connected))?.GetValue() is true,
            serverUrl: ServerUrl,
            certificateStoreBasePath: $"pki-client-{Guid.NewGuid():N}");
        return client;
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
