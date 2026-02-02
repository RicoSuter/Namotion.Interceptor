using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Configuration and helper tests for OPC UA server external node management.
/// Integration tests are covered by OpcUaBidirectionalGraphTests (Client→Server sync).
/// </summary>
[Trait("Category", "Integration")]
public class ServerExternalManagementTests
{
    private readonly ITestOutputHelper _output;

    public ServerExternalManagementTests(ITestOutputHelper output)
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
    public void OpcUaServerExternalNodeValidator_IsEnabled_ReturnsFalseByDefault()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

        // Act & Assert
        Assert.False(helper.IsEnabled);
    }

    [Fact]
    public void OpcUaServerExternalNodeValidator_IsEnabled_ReturnsTrueWhenConfigured()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

        // Act & Assert
        Assert.True(helper.IsEnabled);
    }

    [Fact]
    public void OpcUaServerExternalNodeValidator_ValidateAddNodes_WhenDisabled_ReturnsBadServiceUnsupported()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = false
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

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
    public void OpcUaServerExternalNodeValidator_ValidateDeleteNodes_WhenDisabled_ReturnsBadServiceUnsupported()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = false
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

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

    [Fact]
    public void OpcUaServerExternalNodeValidator_ValidateAddNodes_WhenEnabled_WithValidType_ReturnsGood()
    {
        // Arrange
        var typeRegistry = new OpcUaTypeRegistry();
        var personTypeId = new NodeId("PersonType", 2);
        typeRegistry.RegisterType<TestPerson>(personTypeId);

        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true,
            TypeRegistry = typeRegistry
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

        var namespaceTable = new NamespaceTable();
        namespaceTable.Append("http://test/");

        var nodesToAdd = new AddNodesItemCollection
        {
            new AddNodesItem
            {
                BrowseName = new QualifiedName("NewPerson", 2),
                TypeDefinition = personTypeId
            }
        };

        // Act
        var validatedItems = helper.ValidateAddNodes(nodesToAdd, namespaceTable, out var results);

        // Assert
        Assert.Single(validatedItems);
        Assert.Single(results);
        Assert.Equal(StatusCodes.Good, results[0].StatusCode);
        Assert.Equal(typeof(TestPerson), validatedItems[0].CSharpType);
    }

    [Fact]
    public void OpcUaServerExternalNodeValidator_ValidateAddNodes_WhenEnabled_WithUnknownType_ReturnsBadTypeDefinitionInvalid()
    {
        // Arrange
        var typeRegistry = new OpcUaTypeRegistry();
        // Don't register any types

        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true,
            TypeRegistry = typeRegistry
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

        var nodesToAdd = new AddNodesItemCollection
        {
            new AddNodesItem
            {
                BrowseName = new QualifiedName("NewPerson", 2),
                TypeDefinition = new NodeId("UnknownType", 2)
            }
        };

        // Act
        var validatedItems = helper.ValidateAddNodes(nodesToAdd, new NamespaceTable(), out var results);

        // Assert
        Assert.Empty(validatedItems);
        Assert.Single(results);
        Assert.Equal(StatusCodes.BadTypeDefinitionInvalid, results[0].StatusCode);
    }

    [Fact]
    public void OpcUaServerExternalNodeValidator_ValidateAddNodes_WhenEnabled_WithNoTypeRegistry_ReturnsBadNotSupported()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true,
            TypeRegistry = null // No registry
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

        var nodesToAdd = new AddNodesItemCollection
        {
            new AddNodesItem
            {
                BrowseName = new QualifiedName("NewPerson", 2),
                TypeDefinition = new NodeId("SomeType", 2)
            }
        };

        // Act
        var validatedItems = helper.ValidateAddNodes(nodesToAdd, new NamespaceTable(), out var results);

        // Assert
        Assert.Empty(validatedItems);
        Assert.Single(results);
        Assert.Equal(StatusCodes.BadNotSupported, results[0].StatusCode);
    }

    [Fact]
    public void OpcUaServerExternalNodeValidator_ValidateDeleteNodes_WhenEnabled_ReturnsGood()
    {
        // Arrange
        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            EnableExternalNodeManagement = true
        };
        var helper = new OpcUaServerExternalNodeValidator(configuration, NullLogger.Instance);

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
        Assert.True(canProceed);
        Assert.Single(results);
        Assert.Equal(StatusCodes.Good, results[0]);
    }

    // Note: Integration tests for external AddNodes/DeleteNodes are covered by
    // OpcUaBidirectionalGraphTests (Client→Server tests with EnableRemoteNodeManagement).
}
