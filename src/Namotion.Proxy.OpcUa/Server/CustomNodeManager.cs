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

        LoadNodeSetFromEmbeddedResource<OpcUaBrowseNameAttribute>("NodeSets.Opc.Ua.NodeSet2.xml").Import(context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaBrowseNameAttribute>("NodeSets.Opc.Ua.Di.NodeSet2.xml").Import(context, collection);
        LoadNodeSetFromEmbeddedResource<OpcUaBrowseNameAttribute>("NodeSets.Opc.Ua.Machinery.NodeSet2.xml").Import(context, collection);

        return collection;
    }

    public static UANodeSet LoadNodeSetFromEmbeddedResource<TAssemblyType>(string name)
    {
        var assembly = typeof(TAssemblyType).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.FullName!.Split(',')[0]}.{name}");
        return UANodeSet.Read(stream ?? throw new ArgumentException("Embedded resource could not be found.", nameof(name)));
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        var metadata = _registry.KnownProxies[_proxy];
        if (metadata is not null)
        {
            if (_rootName is not null)
            {
                var node = CreateFolder(ObjectIds.ObjectsFolder, _rootName, _rootName, NamespaceIndex);
                CreateObjectNode(node.NodeId, metadata, _rootName + PathDelimiter);
            }
            else
            {
                CreateObjectNode(ObjectIds.ObjectsFolder, metadata, string.Empty);
            }
        }
    }

    private void CreateObjectNode(NodeId parentNode, RegisteredProxy proxy, string prefix)
    {
        foreach (var property in proxy.Properties)
        {
            var propertyName = _source.SourcePathProvider.TryGetSourcePropertyName(property.Value.Property)!;

            var children = property.Value.Children;
            if (children.Count >= 1)
            {
                if (propertyName is not null)
                {
                    if (children.Count > 1)
                    {
                        var innerPrefix = prefix + propertyName + PathDelimiter;
                        var propertyNode = CreateFolder(parentNode, prefix + propertyName, propertyName, NamespaceIndex);

                        foreach (var child in property.Value.Children)
                        {
                            CreateChildObject(propertyNode.NodeId, property.Value, propertyName, child, innerPrefix);
                        }
                    }
                    else if (children.Count == 1)
                    {
                        var child = children.Single();
                        CreateChildObject(parentNode, property.Value, propertyName, child, prefix);
                    }
                }
            }
            else
            {
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

                    var path = prefix + propertyName;
                    var variable = CreateVariable(parentNode, path, propertyName, NamespaceIndex,
                        Opc.Ua.TypeInfo.Construct(type), 1, referenceType);

                    variable.Value = value;
                    property.Value.Property.SetPropertyData(OpcUaServerTrackableSource<TProxy>.OpcVariableKey, variable);
                }
            }
        }
    }

    private void CreateChildObject(NodeId parentNodeId, RegisteredProxyProperty property, string propertyName, ProxyPropertyChild child, string prefix)
    {
        var index = child.Index is not null ? $"[{child.Index}]" : string.Empty;
        var path = prefix + propertyName + index;

        var referenceTypeAttribute = property.Attributes
            .OfType<OpcUaReferenceTypeAttribute>()
            .FirstOrDefault();

        var referenceType =
            referenceTypeAttribute is not null ?
            typeof(ReferenceTypeIds).GetField(referenceTypeAttribute.Type)?.GetValue(null) as NodeId : null;

        var proxy = _registry.KnownProxies[child.Proxy];
        if (_proxies.TryGetValue(proxy, out var objectNode))
        {
            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceType ?? ReferenceTypeIds.HasComponent, false, objectNode.NodeId);
        }
        else
        {
            var typeDefinition = GetTypeDefinition(child);

            var node = CreateFolder(parentNodeId, path, propertyName + index, NamespaceIndex, typeDefinition, referenceType);
            CreateObjectNode(node.NodeId, proxy, path + PathDelimiter);
            _proxies[proxy] = node;
        }
    }

    private NodeId? GetTypeDefinition(ProxyPropertyChild child)
    {
        var typeDefinitionAttribute = child.Proxy.GetType().GetCustomAttribute<OpcUaTypeDefinitionAttribute>();
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

            return PredefinedNodes.Values.FirstOrDefault(n => 
                n.BrowseName.Name == typeDefinition.Identifier.ToString() && 
                n.BrowseName.NamespaceIndex == typeDefinition.NamespaceIndex)?
                .NodeId;

            //var x = FindNodeInAddressSpace(typeDefinition);
            //var y = FindNodeInAddressSpace(new NodeId(1012,
            //    (ushort)SystemContext.NamespaceUris.GetIndex(typeDefinitionAttribute.Namespace)));

            //return typeDefinition;
        }

        return typeof(ObjectTypeIds).GetField(typeDefinitionAttribute.Type)?.GetValue(null) as NodeId;
    }

    private FolderState CreateFolder(NodeId parentNodeId,
        string path, string name, ushort namespaceIndex,
        NodeId? typeDefinition = null, NodeId? referenceType = null)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var folder = new FolderState(parentNode)
        {
            NodeId = new NodeId(path, namespaceIndex),
            BrowseName = new QualifiedName(name, namespaceIndex),
            DisplayName = new Opc.Ua.LocalizedText(name),
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

    private BaseDataVariableState CreateVariable(NodeId parentNodeId, string path, string name, ushort namespaceIndex,
        Opc.Ua.TypeInfo dataType, int valueRank, NodeId? referenceType)
    {
        var parentNode = FindNodeInAddressSpace(parentNodeId);

        var variable = new BaseDataVariableState(parentNode)
        {
            NodeId = new NodeId(path, namespaceIndex),

            SymbolicName = name,
            BrowseName = new QualifiedName(name, namespaceIndex),
            DisplayName = new Opc.Ua.LocalizedText(name),

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