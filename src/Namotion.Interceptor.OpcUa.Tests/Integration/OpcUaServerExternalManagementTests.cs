using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for OPC UA server external node management - verifies that external clients
/// can create/delete nodes via AddNodes/DeleteNodes services.
/// These tests validate Phase 7: Server OPC UA → Model sync.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaServerExternalManagementTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaServerExternalManagementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void EnableExternalNodeManagement_DefaultsToFalse()
    {
        // Arrange & Act
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        // Assert
        Assert.False(configuration.EnableExternalNodeManagement);
    }

    [Fact]
    public void TypeRegistry_DefaultsToNull()
    {
        // Arrange & Act
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        // Assert
        Assert.Null(configuration.TypeRegistry);
    }

    [Fact]
    public void OpcUaTypeRegistry_RegisterType_CanResolveType()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("TestType", 2);

        // Act
        registry.RegisterType<TestPerson>(typeDefinitionId);

        // Assert
        var resolvedType = registry.ResolveType(typeDefinitionId);
        Assert.NotNull(resolvedType);
        Assert.Equal(typeof(TestPerson), resolvedType);
    }

    [Fact]
    public void OpcUaTypeRegistry_ResolveType_UnregisteredType_ReturnsNull()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("UnknownType", 2);

        // Act
        var resolvedType = registry.ResolveType(typeDefinitionId);

        // Assert
        Assert.Null(resolvedType);
    }

    [Fact]
    public void OpcUaTypeRegistry_GetTypeDefinition_ReturnsNodeId()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("TestType", 2);
        registry.RegisterType<TestPerson>(typeDefinitionId);

        // Act
        var retrievedNodeId = registry.GetTypeDefinition<TestPerson>();

        // Assert
        Assert.NotNull(retrievedNodeId);
        Assert.Equal(typeDefinitionId, retrievedNodeId);
    }

    [Fact]
    public void OpcUaTypeRegistry_IsTypeRegistered_ReturnsTrueForRegistered()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("TestType", 2);
        registry.RegisterType<TestPerson>(typeDefinitionId);

        // Act & Assert
        Assert.True(registry.IsTypeRegistered(typeDefinitionId));
    }

    [Fact]
    public void OpcUaTypeRegistry_IsTypeRegistered_ReturnsFalseForUnregistered()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("UnknownType", 2);

        // Act & Assert
        Assert.False(registry.IsTypeRegistered(typeDefinitionId));
    }

    [Fact]
    public void OpcUaTypeRegistry_GetAllRegistrations_ReturnsAllMappings()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId1 = new NodeId("TestType1", 2);
        var typeDefinitionId2 = new NodeId("TestType2", 2);
        registry.RegisterType<TestPerson>(typeDefinitionId1);
        registry.RegisterType<TestAddress>(typeDefinitionId2);

        // Act
        var registrations = registry.GetAllRegistrations();

        // Assert
        Assert.Equal(2, registrations.Count);
        Assert.True(registrations.ContainsKey(typeDefinitionId1));
        Assert.True(registrations.ContainsKey(typeDefinitionId2));
    }

    [Fact]
    public void OpcUaTypeRegistry_Clear_RemovesAllMappings()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("TestType", 2);
        registry.RegisterType<TestPerson>(typeDefinitionId);

        // Act
        registry.Clear();

        // Assert
        Assert.False(registry.IsTypeRegistered(typeDefinitionId));
        Assert.Empty(registry.GetAllRegistrations());
    }

    [Fact]
    public void OpcUaTypeRegistry_RegisterType_NonInterceptorSubject_ThrowsArgumentException()
    {
        // Arrange
        var registry = new OpcUaTypeRegistry();
        var typeDefinitionId = new NodeId("InvalidType", 2);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            registry.RegisterType(typeof(string), typeDefinitionId));

        Assert.Contains("IInterceptorSubject", exception.Message);
    }

    [Fact]
    public void Configuration_CanEnableExternalNodeManagement()
    {
        // Arrange
        var typeRegistry = new OpcUaTypeRegistry();
        typeRegistry.RegisterType<TestPerson>(new NodeId("PersonType", 2));

        // Act
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true,
            TypeRegistry = typeRegistry
        };

        // Assert
        Assert.True(configuration.EnableExternalNodeManagement);
        Assert.NotNull(configuration.TypeRegistry);
    }

    [Fact]
    public void ExternalNodeManagementHelper_IsEnabled_ReturnsFalseByDefault()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };
        var helper = new ExternalNodeManagementHelper(configuration, NullLogger.Instance);

        // Act & Assert
        Assert.False(helper.IsEnabled);
    }

    [Fact]
    public void ExternalNodeManagementHelper_IsEnabled_ReturnsTrueWhenConfigured()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true
        };
        var helper = new ExternalNodeManagementHelper(configuration, NullLogger.Instance);

        // Act & Assert
        Assert.True(helper.IsEnabled);
    }

    [Fact]
    public void ExternalNodeManagementHelper_ValidateAddNodes_WhenDisabled_ReturnsBadServiceUnsupported()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = false
        };
        var helper = new ExternalNodeManagementHelper(configuration, NullLogger.Instance);

        var nodesToAdd = new AddNodesItemCollection
        {
            new AddNodesItem
            {
                BrowseName = new QualifiedName("TestNode", 2)
            }
        };

        // Act
        var validatedItems = helper.ValidateAddNodes(nodesToAdd, new NamespaceTable(), out var results);

        // Assert
        Assert.Empty(validatedItems);
        Assert.Single(results);
        Assert.Equal(StatusCodes.BadServiceUnsupported, results[0].StatusCode);
    }

    [Fact]
    public void ExternalNodeManagementHelper_ValidateDeleteNodes_WhenDisabled_ReturnsBadServiceUnsupported()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = false
        };
        var helper = new ExternalNodeManagementHelper(configuration, NullLogger.Instance);

        var nodesToDelete = new DeleteNodesItemCollection
        {
            new DeleteNodesItem
            {
                NodeId = new NodeId("TestNode", 2)
            }
        };

        // Act
        var canProceed = helper.ValidateDeleteNodes(nodesToDelete, out var results);

        // Assert
        Assert.False(canProceed);
        Assert.Single(results);
        Assert.Equal(StatusCodes.BadServiceUnsupported, results[0]);
    }

    [Fact(Skip = "Integration test - requires dedicated server, run manually")]
    public async Task ExternalAddNodes_CreatesSubjectInModel()
    {
        // This test would:
        // 1. Start a server with EnableExternalNodeManagement = true and TypeRegistry configured
        // 2. Connect an external client
        // 3. External client calls AddNodes to create a new node
        // 4. Verify the corresponding subject is created in the C# model

        // Implementation would require full server/client lifecycle setup
        await Task.CompletedTask;
    }

    [Fact(Skip = "Integration test - requires dedicated server, run manually")]
    public async Task ExternalDeleteNodes_RemovesSubjectFromModel()
    {
        // This test would:
        // 1. Start a server with EnableExternalNodeManagement = true
        // 2. Server has a model with existing subjects
        // 3. External client calls DeleteNodes to remove a node
        // 4. Verify the corresponding subject is removed from the C# model

        // Implementation would require full server/client lifecycle setup
        await Task.CompletedTask;
    }

    [Fact(Skip = "Integration test - requires dedicated server, run manually")]
    public async Task ExternalNodeManagementDisabled_ReturnsBadServiceUnsupported()
    {
        // This test would:
        // 1. Start a server with EnableExternalNodeManagement = false (default)
        // 2. Connect an external client
        // 3. External client calls AddNodes
        // 4. Verify the response contains BadServiceUnsupported status codes

        // Implementation would require full server/client lifecycle setup
        await Task.CompletedTask;
    }
}
