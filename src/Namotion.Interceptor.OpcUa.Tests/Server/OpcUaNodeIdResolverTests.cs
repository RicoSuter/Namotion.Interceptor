using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class OpcUaNodeIdResolverTests
{
    private readonly OpcUaNodeIdResolver _resolver;
    private readonly MockSystemContext _context;
    private readonly NodeIdDictionary<NodeState> _predefinedNodes;

    public OpcUaNodeIdResolverTests()
    {
        _resolver = new OpcUaNodeIdResolver(NullLogger.Instance);
        _context = new MockSystemContext();
        _predefinedNodes = new NodeIdDictionary<NodeState>();
    }

    #region Cache Tests

    [Fact]
    public void Resolve_SameInputTwice_ReturnsCachedResult()
    {
        // Arrange
        var identifier = "FolderType";

        // Act
        var result1 = _resolver.Resolve(identifier, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);
        var result2 = _resolver.Resolve(identifier, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result1);
        Assert.Same(result1, result2);
    }

    [Fact]
    public void Resolve_NullResult_IsCached()
    {
        // Arrange - use unknown identifier to get null result
        var identifier = "UnknownType";

        // Act - resolve twice
        var result1 = _resolver.Resolve(identifier, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);
        var result2 = _resolver.Resolve(identifier, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert - both should be null (cached null)
        Assert.Null(result1);
        Assert.Null(result2);
    }

    [Fact]
    public void Resolve_SameIdentifierDifferentNamespaces_CachedSeparately()
    {
        // Arrange - add two nodes with same BrowseName but different namespaces
        var ns1 = "http://namespace1.example/";
        var ns2 = "http://namespace2.example/";
        _context.NamespaceUris.Append(ns1);
        _context.NamespaceUris.Append(ns2);

        var node1 = new BaseObjectState(null)
        {
            NodeId = new NodeId("Node1", 1),
            BrowseName = new QualifiedName("SharedName", (ushort)_context.NamespaceUris.GetIndex(ns1))
        };
        var node2 = new BaseObjectState(null)
        {
            NodeId = new NodeId("Node2", 2),
            BrowseName = new QualifiedName("SharedName", (ushort)_context.NamespaceUris.GetIndex(ns2))
        };
        _predefinedNodes[node1.NodeId] = node1;
        _predefinedNodes[node2.NodeId] = node2;

        // Act
        var result1 = _resolver.Resolve("SharedName", ns1, NodeIdCategory.ObjectType, _context, _predefinedNodes);
        var result2 = _resolver.Resolve("SharedName", ns2, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert - should be different NodeIds (cached separately by namespace)
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotEqual(result1, result2);
        Assert.Equal(node1.NodeId, result1);
        Assert.Equal(node2.NodeId, result2);
    }

    [Fact]
    public void Resolve_NullIdentifier_ReturnsNull()
    {
        // Act
        var result = _resolver.Resolve(null, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_EmptyIdentifier_ReturnsNull()
    {
        // Act
        var result = _resolver.Resolve("", null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Standard Type Lookup Tests

    [Theory]
    [InlineData("FolderType")]
    [InlineData("BaseObjectType")]
    public void Resolve_StandardObjectType_ReturnsCorrectNodeId(string identifier)
    {
        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void Resolve_FolderType_ReturnsCorrectNodeId()
    {
        // Act
        var result = _resolver.Resolve("FolderType", null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ObjectTypeIds.FolderType, result);
    }

    [Theory]
    [InlineData("HasComponent")]
    [InlineData("HasProperty")]
    [InlineData("Organizes")]
    public void Resolve_StandardReferenceType_ReturnsCorrectNodeId(string identifier)
    {
        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.ReferenceType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("Double")]
    [InlineData("String")]
    [InlineData("Int32")]
    [InlineData("Boolean")]
    public void Resolve_StandardDataType_ReturnsCorrectNodeId(string identifier)
    {
        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.DataType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("BaseDataVariableType")]
    [InlineData("PropertyType")]
    public void Resolve_StandardVariableType_ReturnsCorrectNodeId(string identifier)
    {
        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.VariableType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void Resolve_UnknownStandardType_ReturnsNull()
    {
        // Act
        var result = _resolver.Resolve("NonExistentType", null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region NodeId String Parsing Tests

    [Fact]
    public void Resolve_NumericNodeIdString_ParsesCorrectly()
    {
        // Arrange
        var identifier = "ns=2;i=1001";

        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.Node, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.NamespaceIndex);
        Assert.Equal(1001u, result.Identifier);
    }

    [Fact]
    public void Resolve_StringNodeId_ParsesCorrectly()
    {
        // Arrange
        var identifier = "ns=2;s=MyNode";

        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.Node, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.NamespaceIndex);
        Assert.Equal("MyNode", result.Identifier);
    }

    [Fact]
    public void Resolve_ExpandedNodeIdString_ResolvesNamespaceUri()
    {
        // Arrange
        var namespaceUri = "http://example.com/UA/";
        _context.NamespaceUris.Append(namespaceUri);
        var identifier = $"nsu={namespaceUri};i=1001";

        // Act
        var result = _resolver.Resolve(identifier, null, NodeIdCategory.Node, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1001u, result.Identifier);
    }

    #endregion

    #region BrowseName Lookup Tests

    [Fact]
    public void Resolve_BrowseNameWithNamespace_FindsInPredefinedNodes()
    {
        // Arrange
        var namespaceUri = "http://example.com/UA/";
        var namespaceIndex = (ushort)_context.NamespaceUris.GetIndexOrAppend(namespaceUri);
        var expectedNodeId = new NodeId(1001, namespaceIndex);
        var browseName = new QualifiedName("CustomType", namespaceIndex);

        var node = new BaseObjectTypeState();
        node.NodeId = expectedNodeId;
        node.BrowseName = browseName;
        _predefinedNodes.Add(expectedNodeId, node);

        // Act
        var result = _resolver.Resolve("CustomType", namespaceUri, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedNodeId, result);
    }

    [Fact]
    public void Resolve_BrowseNameNotFound_ReturnsNull()
    {
        // Arrange
        var namespaceUri = "http://example.com/UA/";
        _context.NamespaceUris.GetIndexOrAppend(namespaceUri);

        // Act
        var result = _resolver.Resolve("NonExistentType", namespaceUri, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_WithNamespace_NamespaceNotRegistered_ReturnsNull()
    {
        // Arrange - namespace not added to context

        // Act
        var result = _resolver.Resolve("SomeType", "http://unregistered.com/", NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Resolution Order Tests

    [Fact]
    public void Resolve_WithNamespace_PrefersBrowseNameOverStandardType()
    {
        // Arrange - create a custom "FolderType" in a custom namespace
        var namespaceUri = "http://example.com/UA/";
        var namespaceIndex = (ushort)_context.NamespaceUris.GetIndexOrAppend(namespaceUri);
        var customNodeId = new NodeId(9999, namespaceIndex);
        var browseName = new QualifiedName("FolderType", namespaceIndex);

        var node = new BaseObjectTypeState();
        node.NodeId = customNodeId;
        node.BrowseName = browseName;
        _predefinedNodes.Add(customNodeId, node);

        // Act
        var result = _resolver.Resolve("FolderType", namespaceUri, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert - should return custom node, not standard FolderType
        Assert.Equal(customNodeId, result);
    }

    [Fact]
    public void Resolve_WithoutNamespace_TriesNodeIdParseThenStandard()
    {
        // This test verifies the order: NodeId parse first, then standard lookup
        // "ns=0;i=61" is the standard FolderType

        // Act
        var result = _resolver.Resolve("ns=0;i=61", null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ObjectTypeIds.FolderType, result);
    }

    #endregion

    #region Category Tests

    [Fact]
    public void Resolve_NodeCategory_DoesNotLookupStandardTypes()
    {
        // Arrange - "FolderType" would resolve as ObjectType, but Node category should not find it
        // Act
        var result = _resolver.Resolve("FolderType", null, NodeIdCategory.Node, _context, _predefinedNodes);

        // Assert - null because Node category doesn't do standard type lookups
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_DifferentCategories_CachedSeparately()
    {
        // Arrange
        var identifier = "HasComponent";

        // Act - resolve as ReferenceType (should find it)
        var refResult = _resolver.Resolve(identifier, null, NodeIdCategory.ReferenceType, _context, _predefinedNodes);
        // Act - resolve as ObjectType (should not find it)
        var objResult = _resolver.Resolve(identifier, null, NodeIdCategory.ObjectType, _context, _predefinedNodes);

        // Assert
        Assert.NotNull(refResult);
        Assert.Null(objResult);
    }

    #endregion

    /// <summary>
    /// Mock system context for testing. Only NamespaceUris is used by the resolver.
    /// </summary>
    private class MockSystemContext : ISystemContext
    {
        public NamespaceTable NamespaceUris { get; } = new NamespaceTable();
        public StringTable ServerUris { get; } = new StringTable();
        public IEncodeableFactory EncodeableFactory => null!;
        public INodeIdFactory NodeIdFactory => null!;
        public INodeTable NodeTable => null!;
        public ITypeTable TypeTable => null!;
        public NodeStateFactory NodeStateFactory => null!;
        public object? UserIdentity => null;
        public IList<string>? PreferredLocales => null;
        public DiagnosticsMasks DiagnosticsMask => DiagnosticsMasks.None;
        public StringTable? StringTable => null;
        public uint? MaxStringLength => null;
        public uint? MaxArrayLength => null;
        public uint? MaxByteStringLength => null;
        public uint? MaxMessageSize => null;
        public IOperationContext OperationContext => null!;
        public TypeInfo TypeInfo { get; } = new TypeInfo(BuiltInType.Null, -1);
        public object? SystemHandle => null;
        public string? AuditEntryId => null;
        public ITelemetryContext? Telemetry => null;
    }
}
