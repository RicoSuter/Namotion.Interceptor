using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

/// <summary>
/// Tests for dynamic OPC UA node creation/removal when subjects are attached/detached at runtime.
/// </summary>
[Trait("Category", "Integration")]
public class DynamicNodeCreationTests : IAsyncLifetime
{
    private readonly TestLogger _logger;
    private OpcUaTestServer<TestRoot>? _server;
    private PortLease? _portLease;

    public DynamicNodeCreationTests(ITestOutputHelper output)
    {
        _logger = new TestLogger(output);
    }

    public async Task InitializeAsync()
    {
        _portLease = await OpcUaTestPortPool.AcquireAsync();
        _server = new OpcUaTestServer<TestRoot>(_logger);
        await _server.StartAsync(
            context => new TestRoot(context),
            baseAddress: _portLease.BaseAddress,
            certificateStoreBasePath: _portLease.CertificateStoreBasePath);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
        _portLease?.Dispose();
    }

    [Fact]
    public async Task WhenSubjectAttached_ThenOpcUaNodesCreated()
    {
        // Arrange
        var root = _server!.Root!;
        var server = (OpcUaSubjectServer)_server.Server!;
        var standardServer = (OpcUaStandardServer?)server.CurrentServer;
        var nodeManager = standardServer?.GetNodeManager();
        Assert.NotNull(nodeManager);

        // Act - attach a new person to the People collection
        var person = new TestPerson() { FirstName = "Dynamic", LastName = "Person" };
        root.People = [person];

        // Assert - verify the subject has an OPC UA node
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var registeredSubject = person.TryGetRegisteredSubject();
                return registeredSubject is not null && nodeManager.TryGetNodeId(registeredSubject, out _);
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Dynamically attached subject should have an OPC UA node");
    }

    [Fact]
    public async Task WhenSubjectDetached_ThenOpcUaNodesRemoved()
    {
        // Arrange
        var root = _server!.Root!;
        var server = (OpcUaSubjectServer)_server.Server!;
        var standardServer = (OpcUaStandardServer?)server.CurrentServer;
        var nodeManager = standardServer?.GetNodeManager();
        Assert.NotNull(nodeManager);

        // Add a person first
        var person = new TestPerson() { FirstName = "Temporary", LastName = "Person" };
        root.People = [person];

        // Wait for node to be created
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var registeredSubject = person.TryGetRegisteredSubject();
                return registeredSubject is not null && nodeManager.TryGetNodeId(registeredSubject, out _);
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Subject should have a node before detach");

        var registeredSubjectForAssert = person.TryGetRegisteredSubject()!;

        // Act - remove the person by setting empty array
        root.People = [];

        // Assert - node should be removed
        await AsyncTestHelpers.WaitUntilAsync(
            () => !nodeManager.TryGetNodeId(registeredSubjectForAssert, out _),
            timeout: TimeSpan.FromSeconds(10),
            message: "Subject should not have a node after detach");
    }

    [Fact]
    public async Task WhenDictionaryEntryAttached_ThenOpcUaNodeCreated()
    {
        // Arrange
        var root = _server!.Root!;
        var server = (OpcUaSubjectServer)_server.Server!;
        var standardServer = (OpcUaStandardServer?)server.CurrentServer;
        var nodeManager = standardServer?.GetNodeManager();
        Assert.NotNull(nodeManager);

        // Act - add a person to the dictionary
        var person = new TestPerson() { FirstName = "Dict", LastName = "Entry" };
        root.PeopleByName = new Dictionary<string, TestPerson> { ["dictPerson"] = person };

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var registeredSubject = person.TryGetRegisteredSubject();
                return registeredSubject is not null && nodeManager.TryGetNodeId(registeredSubject, out _);
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Dictionary entry subject should have an OPC UA node");
    }

    [Fact]
    public async Task WhenReferenceSubjectAttached_ThenOpcUaNodeCreated()
    {
        // Arrange
        var root = _server!.Root!;
        var server = (OpcUaSubjectServer)_server.Server!;
        var standardServer = (OpcUaStandardServer?)server.CurrentServer;
        var nodeManager = standardServer?.GetNodeManager();
        Assert.NotNull(nodeManager);

        // Act - set a new person reference
        var person = new TestPerson() { FirstName = "Reference", LastName = "Person" };
        root.Person = person;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var registeredSubject = person.TryGetRegisteredSubject();
                return registeredSubject is not null && nodeManager.TryGetNodeId(registeredSubject, out _);
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Reference subject should have an OPC UA node");
    }
}

/// <summary>
/// Tests for configuration properties related to structural synchronization.
/// </summary>
public class StructureSynchronizationConfigTests
{
    [Fact]
    public void WhenServerConfigCreated_ThenStructureSyncDefaultsAreCorrect()
    {
        // Arrange & Act
        var config = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        // Assert
        Assert.False(config.EnableStructureSynchronization);
        Assert.False(config.EnablePeriodicResynchronization);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PeriodicResynchronizationInterval);
    }

    [Fact]
    public void WhenClientConfigCreated_ThenStructureSyncDefaultsAreCorrect()
    {
        // Arrange & Act
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(Mock.Of<ISubjectFactory>())
        };

        // Assert
        Assert.False(config.EnableStructureSynchronization);
        Assert.False(config.EnablePeriodicResynchronization);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PeriodicResynchronizationInterval);
    }

    [Fact]
    public void WhenStructureSyncEnabled_ThenConfigReflectsNewValues()
    {
        // Arrange
        var config = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        // Act
        config.EnableStructureSynchronization = true;
        config.EnablePeriodicResynchronization = true;
        config.PeriodicResynchronizationInterval = TimeSpan.FromMinutes(1);

        // Assert
        Assert.True(config.EnableStructureSynchronization);
        Assert.True(config.EnablePeriodicResynchronization);
        Assert.Equal(TimeSpan.FromMinutes(1), config.PeriodicResynchronizationInterval);
    }
}
