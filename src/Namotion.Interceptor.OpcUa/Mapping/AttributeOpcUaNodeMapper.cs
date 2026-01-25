using System.Reflection;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using OpcUaNode, OpcUaReference, and OpcUaValue attributes.
/// </summary>
public class AttributeOpcUaNodeMapper : IOpcUaNodeMapper
{
    private readonly string? _defaultNamespaceUri;

    /// <summary>
    /// Creates a new attribute-based node mapper.
    /// </summary>
    /// <param name="defaultNamespaceUri">Default namespace URI for nodes without explicit namespace.</param>
    public AttributeOpcUaNodeMapper(string? defaultNamespaceUri = null)
    {
        _defaultNamespaceUri = defaultNamespaceUri;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        // Get class-level OpcUaNode from the property's type (for object references)
        var classAttribute = GetClassLevelOpcUaNodeAttribute(property);

        // Get property-level attributes
        var propertyAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeAttribute>()
            .FirstOrDefault();
        var referenceAttribute = property.ReflectionAttributes
            .OfType<OpcUaReferenceAttribute>()
            .FirstOrDefault();
        var valueAttribute = property.ReflectionAttributes
            .OfType<OpcUaValueAttribute>()
            .FirstOrDefault();

        // No OPC UA configuration at all
        if (classAttribute is null && propertyAttribute is null && referenceAttribute is null && valueAttribute is null)
        {
            return null;
        }

        // Build configuration from class-level first
        var classConfig = classAttribute is not null ? BuildConfigFromNodeAttribute(classAttribute) : null;

        // Then property-level
        var propertyConfig = propertyAttribute is not null ? BuildConfigFromNodeAttribute(propertyAttribute) : null;

        // Start with property config, merge class config as fallback
        var config = propertyConfig?.MergeWith(classConfig) ?? classConfig ?? new OpcUaNodeConfiguration();

        // Apply reference attribute (new style)
        if (referenceAttribute is not null)
        {
            config = config with
            {
                ReferenceType = config.ReferenceType ?? referenceAttribute.ReferenceType,
                ItemReferenceType = config.ItemReferenceType ?? referenceAttribute.ItemReferenceType
            };
        }

        // Apply value attribute
        if (valueAttribute is not null)
        {
            config = config with { IsValue = true };
        }

        return config;
    }

    /// <inheritdoc />
    public Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var nodeIdString = nodeReference.NodeId.Identifier.ToString();
        var nodeNamespaceUri = nodeReference.NodeId.NamespaceUri
            ?? session.NamespaceUris.GetString(nodeReference.NodeId.NamespaceIndex);

        // Priority 1: Explicit NodeIdentifier match
        foreach (var property in subject.Properties)
        {
            var attribute = property.ReflectionAttributes
                .OfType<OpcUaNodeAttribute>()
                .FirstOrDefault();

            if (attribute is not null && attribute.NodeIdentifier == nodeIdString)
            {
                var propertyNamespaceUri = attribute.NodeNamespaceUri ?? _defaultNamespaceUri;
                if (propertyNamespaceUri is null || propertyNamespaceUri == nodeNamespaceUri)
                {
                    return Task.FromResult<RegisteredSubjectProperty?>(property);
                }
            }
        }

        // Priority 2: BrowseName match via attribute
        var browseName = nodeReference.BrowseName.Name;
        foreach (var property in subject.Properties)
        {
            var attribute = property.ReflectionAttributes
                .OfType<OpcUaNodeAttribute>()
                .FirstOrDefault();

            if (attribute?.BrowseName == browseName)
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }

    private static OpcUaNodeAttribute? GetClassLevelOpcUaNodeAttribute(RegisteredSubjectProperty property)
    {
        // For object references, get the OpcUaNode attribute from the referenced type
        if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            var elementType = GetElementType(property.Type);
            return elementType?.GetCustomAttribute<OpcUaNodeAttribute>();
        }
        return null;
    }

    private static Type? GetElementType(Type type)
    {
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            // For Dictionary<K,V>, return V
            if (args.Length == 2) return args[1];
            // For IEnumerable<T>, return T
            if (args.Length == 1) return args[0];
        }
        return type;
    }

    private static OpcUaNodeConfiguration BuildConfigFromNodeAttribute(OpcUaNodeAttribute attribute)
    {
        return new OpcUaNodeConfiguration
        {
            BrowseName = attribute.BrowseName,
            BrowseNamespaceUri = attribute.BrowseNamespaceUri,
            NodeIdentifier = attribute.NodeIdentifier,
            NodeNamespaceUri = attribute.NodeNamespaceUri,
            DisplayName = attribute.DisplayName,
            Description = attribute.Description,
            TypeDefinition = attribute.TypeDefinition,
            TypeDefinitionNamespace = attribute.TypeDefinitionNamespace,
            NodeClass = attribute.NodeClass != OpcUaNodeClass.Auto ? attribute.NodeClass : null,
            DataType = attribute.DataType,
            SamplingInterval = attribute.SamplingInterval != int.MinValue ? attribute.SamplingInterval : null,
            QueueSize = attribute.QueueSize != uint.MaxValue ? attribute.QueueSize : null,
            DiscardOldest = attribute.DiscardOldest switch
            {
                DiscardOldestMode.True => true,
                DiscardOldestMode.False => false,
                _ => null
            },
            DataChangeTrigger = (int)attribute.DataChangeTrigger != -1 ? attribute.DataChangeTrigger : null,
            DeadbandType = (int)attribute.DeadbandType != -1 ? attribute.DeadbandType : null,
            DeadbandValue = !double.IsNaN(attribute.DeadbandValue) ? attribute.DeadbandValue : null,
            ModellingRule = attribute.ModellingRule != Mapping.ModellingRule.Unset ? attribute.ModellingRule : null,
            EventNotifier = attribute.EventNotifier != 0 ? attribute.EventNotifier : null,
        };
    }
}
