using System.Collections;
using System.Reflection;
using Namotion.Interceptor.OpcUa.Annotations;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Export;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManager : CustomNodeManager2
{
    private const string PathDelimiter = ".";

    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServerSource _source;
    private readonly OpcUaServerConfiguration _configuration;

    private readonly Dictionary<RegisteredSubject, FolderState> _subjects = new();

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServerSource source,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration) :
        base(server, applicationConfiguration, [
            "https://foobar/",
            "http://opcfoundation.org/UA/",
            "http://opcfoundation.org/UA/DI/",
            "http://opcfoundation.org/UA/PADIM",
            "http://opcfoundation.org/UA/Machinery/",
            "http://opcfoundation.org/UA/Machinery/ProcessValues"
        ])
    {
        _subject = subject;
        _source = source;
        _configuration = configuration;
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);

        LoadNodeSetFromEmbeddedResource<OpcUaNodeAttribute>("NodeSets.Opc.Ua.NodeSet2.xml", context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaNodeAttribute>("NodeSets.Opc.Ua.Di.NodeSet2.xml", context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaNodeAttribute>("NodeSets.Opc.Ua.PADIM.NodeSet2.xml", context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaNodeAttribute>("NodeSets.Opc.Ua.Machinery.NodeSet2.xml", context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaNodeAttribute>("NodeSets.Opc.Ua.Machinery.ProcessValues.NodeSet2.xml", context, collection);

        return collection;
    }

    private static void LoadNodeSetFromEmbeddedResource<TAssemblyType>(string name, ISystemContext context, NodeStateCollection nodes)
    {
        var assembly = typeof(TAssemblyType).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.FullName!.Split(',')[0]}.{name}");

        var nodeSet = UANodeSet.Read(stream ?? throw new ArgumentException("Embedded resource could not be found.", nameof(name)));
        nodeSet.Import(context, nodes);
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        var registeredSubject = _subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            if (_configuration.RootName is not null)
            {
                var node = CreateFolder(ObjectIds.ObjectsFolder, 
                    new NodeId(_configuration.RootName, NamespaceIndex), _configuration.RootName, null, null);

                CreateObjectNode(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
            }
            else
            {
                CreateObjectNode(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
            }
        }
    }

    private void CreateObjectNode(NodeId parentNodeId, RegisteredSubject subject, string prefix)
    {
        foreach (var property in subject.Properties)
        {
            var propertyName = GetPropertyName(property);
            if (propertyName is not null)
            {
                if (property.IsSubjectReference)
                {
                    var referencedSubject = property.Children.SingleOrDefault();
                    if (referencedSubject.Subject is not null)
                    {
                        CreateReferenceObjectNode(propertyName, property, referencedSubject, parentNodeId, prefix);
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    CreateArrayObjectNode(propertyName, property, property.Children, parentNodeId, prefix);
                }
                else if (property.IsSubjectDictionary)
                {
                    CreateDictionaryObjectNode(propertyName, property, property.Children, parentNodeId, prefix);
                }
                else
                {
                    CreateVariableNode(propertyName, property, parentNodeId, prefix);
                }
            }
        }
    }

    private string? GetPropertyName(RegisteredSubjectProperty property)
    {
        if (property.IsAttribute)
        {
            var attributedProperty = property.GetAttributedProperty();
            var propertyName = _configuration.SourcePathProvider.TryGetPropertySegment(property);
            if (propertyName is null)
                return null;
            
            return GetPropertyName(attributedProperty) + "__" + propertyName;
        }
        
        return _configuration.SourcePathProvider.TryGetPropertySegment(property);
    }

    private void CreateReferenceObjectNode(string propertyName, RegisteredSubjectProperty property, SubjectPropertyChild child, NodeId parentNodeId, string parentPath)
    {
        var path = parentPath + propertyName;
        var browseName = GetBrowseName(propertyName, property, child.Index);
        var referenceTypeId = GetReferenceTypeId(property);

        CreateChildObject(browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = new NodeId(parentPath + propertyName, NamespaceIndex);
        var browseName = GetBrowseName(propertyName, property, null);

        var typeDefinitionId = GetTypeDefinitionId(property);
        var referenceTypeId = GetReferenceTypeId(property);

        var propertyNode = CreateFolder(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);

        // Child objects below the array folder use path: parentPath + propertyName + "[index]"
        var childReferenceTypeId = GetChildReferenceTypeId(property);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var childPath = $"{parentPath}{propertyName}[{child.Index}]";

            CreateChildObject(childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = new NodeId(parentPath + propertyName, NamespaceIndex);
        var browseName = GetBrowseName(propertyName, property, null);

        var typeDefinitionId = GetTypeDefinitionId(property);
        var referenceTypeId = GetReferenceTypeId(property);

        var propertyNode = CreateFolder(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
        var childReferenceTypeId = GetChildReferenceTypeId(property);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName(child.Index?.ToString(), NamespaceIndex);
            var childPath = parentPath + propertyName + PathDelimiter + child.Index;

            CreateChildObject(childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateVariableNode(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
    {
        var (value, type) = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property.Type);

        var nodeId = new NodeId(parentPath + propertyName, NamespaceIndex);
        var browseName = GetBrowseName(propertyName, property, null);

        var dataTypeInfo = Opc.Ua.TypeInfo.Construct(type);
        var referenceTypeId = GetReferenceTypeId(property);

        var valueRank = GetValueRank(type);
        var variable = CreateVariableNode(parentNodeId, nodeId, browseName, dataTypeInfo, valueRank, referenceTypeId);

        // Adjust access according to property setter
        if (!property.HasSetter)
        {
            variable.AccessLevel = AccessLevels.CurrentRead;
            variable.UserAccessLevel = AccessLevels.CurrentRead;
        }
        else
        {
            variable.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
        }

        // Set array dimensions
        if (type.IsArray && value is Array array)
        {
            variable.ArrayDimensions = new ReadOnlyList<uint>(
                Enumerable.Range(0, array.Rank)
                    .Select(i => (uint)array.GetLength(i))
                    .ToArray()
            );
        }

        variable.Value = value;
        variable.StateChanged += (_, _, changes) =>
        {
            if (changes.HasFlag(NodeStateChangeMasks.Value))
            {
                _source.UpdateProperty(property.Reference, variable.Timestamp, variable.Value);
            }
        };

        property.Reference.SetPropertyData(OpcUaSubjectServerSource.OpcVariableKey, variable);
    }
    
    private void CreateChildObject(
        QualifiedName browseName,
        IInterceptorSubject subject,
        string path,
        NodeId parentNodeId,
        NodeId? referenceTypeId)
    {
        var registeredSubject = subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException("Registered subject not found.");
        if (_subjects.TryGetValue(registeredSubject, out var objectNode))
        {
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, objectNode.NodeId);
        }
        else
        {
            var nodeId = new NodeId(path, NamespaceIndex);
            var typeDefinitionId = GetTypeDefinitionId(subject);

            var node = CreateFolder(parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId);
            CreateObjectNode(node.NodeId, registeredSubject, path + PathDelimiter);

            _subjects[registeredSubject] = node;
        }
    }

    private static int GetValueRank(Type type)
    {
        // Check for string first - it implements IEnumerable<char> but should be treated as scalar
        if (type == typeof(string))
        {
            return -1; // Scalar value
        }

        if (type.IsArray)
        {
            // Return the number of dimensions for multi-dimensional arrays
            return type.GetArrayRank();
        }

        // Check if it's a generic IEnumerable<T> (like List<T>, IList<T>, etc.)
        if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            return 1; // One-dimensional array equivalent
        }

        // Check if it implements IEnumerable (non-generic collections)
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return 1; // One-dimensional array equivalent
        }

        return -1; // Scalar value
    }

    private static NodeId? GetReferenceTypeId(RegisteredSubjectProperty property)
    {
        var referenceTypeAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeReferenceTypeAttribute>()
            .FirstOrDefault();

        return referenceTypeAttribute is not null ? typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;
    }

    private static NodeId? GetChildReferenceTypeId(RegisteredSubjectProperty property)
    {
        var referenceTypeAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeItemReferenceTypeAttribute>()
            .FirstOrDefault();

        return referenceTypeAttribute is not null ? typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;
    }

    private NodeId? GetTypeDefinitionId(RegisteredSubjectProperty property)
    {
        var typeDefinitionAttribute = property.ReflectionAttributes
            .OfType<OpcUaTypeDefinitionAttribute>()
            .FirstOrDefault();

        return GetTypeDefinitionId(typeDefinitionAttribute);
    }

    private NodeId? GetTypeDefinitionId(IInterceptorSubject subject)
    {
        var typeDefinitionAttribute = subject.GetType().GetCustomAttribute<OpcUaTypeDefinitionAttribute>();
        return GetTypeDefinitionId(typeDefinitionAttribute);
    }

    private NodeId? GetTypeDefinitionId(OpcUaTypeDefinitionAttribute? typeDefinitionAttribute)
    {
        if (typeDefinitionAttribute is null)
        {
            return null;
        }

        if (typeDefinitionAttribute.Namespace is not null)
        {
            var typeDefinition = NodeId.Create(
                typeDefinitionAttribute.Type,
                typeDefinitionAttribute.Namespace,
                SystemContext.NamespaceUris);

            return PredefinedNodes.Values.SingleOrDefault(n =>
                    n.BrowseName.Name == typeDefinition.Identifier.ToString() &&
                    n.BrowseName.NamespaceIndex == typeDefinition.NamespaceIndex)?
                .NodeId;
        }

        return typeof(ObjectTypeIds).GetField(typeDefinitionAttribute.Type)?.GetValue(null) as NodeId;
    }

    private QualifiedName GetBrowseName(string propertyName, RegisteredSubjectProperty property, object? index)
    {
        var browseNameProvider = property.ReflectionAttributes.OfType<IOpcUaBrowseNameProvider>().SingleOrDefault();
        if (browseNameProvider is null)
        {
            return new QualifiedName(propertyName + (index is not null ? $"[{index}]" : string.Empty), NamespaceIndex);
        }

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (browseNameProvider.BrowseNamespace is not null)
        {
            return new QualifiedName(browseNameProvider.BrowseName, (ushort)SystemContext.NamespaceUris.GetIndex(browseNameProvider.BrowseNamespace));
        }

        return new QualifiedName(browseNameProvider.BrowseName, NamespaceIndex);
    }

    private FolderState CreateFolder(
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? typeDefinition,
        NodeId? referenceType)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var folder = new FolderState(parentNode)
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = new Opc.Ua.LocalizedText(browseName.Name),
            TypeDefinitionId = typeDefinition ?? ObjectTypeIds.FolderType,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasComponent
        };

        parentNode?.AddChild(folder);

        AddPredefinedNode(SystemContext, folder);
        return folder;
    }

    private BaseDataVariableState CreateVariableNode(
        NodeId parentNodeId, NodeId nodeId, QualifiedName browseName,
        Opc.Ua.TypeInfo dataType, int valueRank, NodeId? referenceType)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var variable = new BaseDataVariableState(parentNode)
        {
            NodeId = nodeId,

            SymbolicName = browseName.Name,
            BrowseName = browseName,
            DisplayName = new Opc.Ua.LocalizedText(browseName.Name),

            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            DataType = Opc.Ua.TypeInfo.GetDataTypeId(dataType),
            ValueRank = valueRank,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,

            ReferenceTypeId = referenceType ?? ReferenceTypeIds.HasProperty
        };

        parentNode?.AddChild(variable);

        AddPredefinedNode(SystemContext, variable);
        return variable;
    }
}