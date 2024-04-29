using System.Reactive.Linq;
using System.Reflection;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.OpcUa.Annotations;

using Opc.Ua.Server;
using Opc.Ua;
using Opc.Ua.Export;

namespace Namotion.Proxy.OpcUa.Server;

internal class CustomNodeManager<TProxy> : CustomNodeManager2
    where TProxy : IProxy
{
    private const string PathDelimiter = ".";

    private readonly TProxy _proxy;
    private readonly IProxyRegistry _registry;
    private readonly OpcUaServerTrackableSource<TProxy> _source;
    private readonly string? _rootName;

    private Dictionary<RegisteredProxy, FolderState> _proxies = new();

    public CustomNodeManager(
        TProxy proxy,
        OpcUaServerTrackableSource<TProxy> source,
        IServerInternal server,
        ApplicationConfiguration configuration,
        string? rootName) :
        base(server, configuration, new string[] 
        {
            "https://foobar/",
            "http://opcfoundation.org/UA/",
            "http://opcfoundation.org/UA/DI/",
            "http://opcfoundation.org/UA/Machinery/" 
        })
    {
        _proxy = proxy;
        _registry = proxy.Context?.GetHandler<IProxyRegistry>() ?? throw new ArgumentException($"Registry could not be found.");
        _source = source;
        _rootName = rootName;
    }

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);

        LoadNodeSetFromEmbeddedResource<OpcUaBrowseNameAttribute>("NodeSets.Opc.Ua.NodeSet2.xml", context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaBrowseNameAttribute>("NodeSets.Opc.Ua.Di.NodeSet2.xml", context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaBrowseNameAttribute>("NodeSets.Opc.Ua.Machinery.NodeSet2.xml", context, collection);

        return collection;
    }

    public static void LoadNodeSetFromEmbeddedResource<TAssemblyType>(string name, ISystemContext context, NodeStateCollection nodes)
    {
        var assembly = typeof(TAssemblyType).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.FullName!.Split(',')[0]}.{name}");
     
        var nodeSet = UANodeSet.Read(stream ?? throw new ArgumentException("Embedded resource could not be found.", nameof(name)));
        nodeSet.Import(context, nodes);
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        var metadata = _registry.KnownProxies[_proxy];
        if (metadata is not null)
        {
            if (_rootName is not null)
            {
                var node = CreateFolder(ObjectIds.ObjectsFolder, new NodeId(_rootName, NamespaceIndex), _rootName);
                CreateObjectNode(node.NodeId, metadata, _rootName + PathDelimiter);
            }
            else
            {
                CreateObjectNode(ObjectIds.ObjectsFolder, metadata, string.Empty);
            }
        }
    }

    private void CreateObjectNode(NodeId parentNodeId, RegisteredProxy proxy, string prefix)
    {
        foreach (var property in proxy.Properties)
        {
            var propertyName = _source.SourcePathProvider.TryGetSourcePropertyName(property.Value.Property)!;

            var children = property.Value.Children;
            if (children.Count >= 1)
            {
                if (propertyName is not null)
                {
                    if (children.Count == 1 && children.All(c => c.Index == null))
                    {
                        // reference property
                        var child = children.Single();
                        var childPath = prefix + propertyName;
                        CreateChildObject(parentNodeId, property.Value, child, childPath);
                    }
                    else
                    {
                        // dictionary property
                        var nodeId = new NodeId(prefix + propertyName, NamespaceIndex);
                        var browseName = GetBrowseName(property.Value, NamespaceIndex, null);
                     
                        var propertyNode = CreateFolder(parentNodeId, nodeId, browseName);
                        var innerPrefix = prefix + propertyName + PathDelimiter;

                        foreach (var child in property.Value.Children)
                        {
                            var index = child.Index is not null ? $"[{child.Index}]" : string.Empty;
                            var path = innerPrefix + propertyName + index;

                            CreateChildObject(propertyNode.NodeId, property.Value, child, path);
                        }
                    }
                }
            }
            else
            {
                // primitive variable property
                var sourcePath = _source.TryGetSourcePath(property.Value.Property);
                if (sourcePath is not null)
                {
                    var value = property.Value.GetValue();
                    var type = property.Value.Type;

                    if (type == typeof(decimal))
                    {
                        type = typeof(double);
                        value = Convert.ToDouble(value);
                    }

                    var referenceTypeAttribute = property.Value.Attributes
                        .OfType<OpcUaReferenceTypeAttribute>()
                        .FirstOrDefault();

                    var referenceType =
                        referenceTypeAttribute is not null ?
                        typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;

                    var nodeId = new NodeId(prefix + propertyName, NamespaceIndex);
                    var browseName = GetBrowseName(property.Value, NamespaceIndex, null);
                    var dataType = Opc.Ua.TypeInfo.Construct(type);

                    var variable = CreateVariable(parentNodeId, nodeId, browseName, dataType, 1, referenceType);

                    variable.Value = value;
                    property.Value.Property.SetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey, variable);
                }
            }
        }
    }

    private void CreateChildObject(NodeId parentNodeId, RegisteredProxyProperty property, ProxyPropertyChild child, string path)
    {
        var referenceTypeAttribute = property.Attributes
            .OfType<OpcUaReferenceTypeAttribute>()
            .FirstOrDefault();

        var referenceType =
            referenceTypeAttribute is not null ?
            typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;

        var registeredProxy = _registry.KnownProxies[child.Proxy];
        if (_proxies.TryGetValue(registeredProxy, out var objectNode))
        {
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceType ?? ReferenceTypeIds.HasComponent, false, objectNode.NodeId);
        }
        else
        {
            var typeDefinition = GetTypeDefinition(child.Proxy);

            var nodeId = new NodeId(path, NamespaceIndex);
            var browseName = GetBrowseName(property, NamespaceIndex, child.Index);

            var node = CreateFolder(parentNodeId, nodeId, browseName, typeDefinition, referenceType);
            CreateObjectNode(node.NodeId, registeredProxy, path + PathDelimiter);
           
            _proxies[registeredProxy] = node;
        }
    }

    private NodeId? GetTypeDefinition(IProxy proxy)
    {
        var typeDefinitionAttribute = proxy.GetType().GetCustomAttribute<OpcUaTypeDefinitionAttribute>();
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

    private QualifiedName GetBrowseName(RegisteredProxyProperty property, ushort namespaceIndex, object? index)
    {
        var typeDefinitionAttribute = property.Attributes.OfType<OpcUaBrowseNameAttribute>().SingleOrDefault();
        if (typeDefinitionAttribute is null)
        {
            return new QualifiedName(property.Property.Name + (index is not null ? $"[{index}]" : string.Empty), namespaceIndex);
        }

        if (typeDefinitionAttribute.Namespace is not null)
        {
            return new QualifiedName(typeDefinitionAttribute.Name, (ushort)SystemContext.NamespaceUris.GetIndex(typeDefinitionAttribute.Namespace));
        }

        return new QualifiedName(typeDefinitionAttribute.Name, namespaceIndex);
    }

    private FolderState CreateFolder(
        NodeId parentNodeId, NodeId nodeId, QualifiedName browseName, 
        NodeId? typeDefinition = null, NodeId? referenceType = null)
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

        if (parentNode != null)
        {
            parentNode.AddChild(folder);
        }

        AddPredefinedNode(SystemContext, folder);
        return folder;
    }

    private BaseDataVariableState CreateVariable(
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

        if (parentNode != null)
        {
            parentNode.AddChild(variable);
        }

        AddPredefinedNode(SystemContext, variable);
        return variable;
    }
}
