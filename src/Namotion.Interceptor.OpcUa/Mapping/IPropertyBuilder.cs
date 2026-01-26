using System.Linq.Expressions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Builder interface for fluent property configuration.
/// </summary>
/// <typeparam name="T">The type being configured.</typeparam>
public interface IPropertyBuilder<T>
{
    // Shared - naming/identification
    IPropertyBuilder<T> BrowseName(string value);
    IPropertyBuilder<T> BrowseNamespaceUri(string value);
    IPropertyBuilder<T> NodeIdentifier(string value);
    IPropertyBuilder<T> NodeNamespaceUri(string value);
    IPropertyBuilder<T> DisplayName(string value);
    IPropertyBuilder<T> Description(string value);

    // Type definition
    /// <summary>
    /// Sets the type definition for the node.
    /// </summary>
    /// <param name="identifier">Type definition identifier (standard type name, NodeId string, or BrowseName).</param>
    /// <param name="namespaceUri">Optional namespace URI for custom types from imported nodesets.</param>
    IPropertyBuilder<T> TypeDefinition(string identifier, string? namespaceUri = null);

    IPropertyBuilder<T> NodeClass(OpcUaNodeClass value);

    /// <summary>
    /// Sets the data type override.
    /// </summary>
    /// <param name="identifier">Data type identifier (standard type name, NodeId string, or BrowseName).</param>
    /// <param name="namespaceUri">Optional namespace URI for custom data types.</param>
    IPropertyBuilder<T> DataType(string identifier, string? namespaceUri = null);

    /// <summary>
    /// Marks this property as the primary value for a VariableNode class (equivalent to [OpcUaValue] attribute).
    /// Use this when mapping a class to a VariableType where the class has a primary value property.
    /// </summary>
    IPropertyBuilder<T> IsValue(bool value = true);

    // Reference configuration
    /// <summary>
    /// Sets the reference type from parent node.
    /// </summary>
    /// <param name="identifier">Reference type identifier (standard type name, NodeId string, or BrowseName).</param>
    /// <param name="namespaceUri">Optional namespace URI for custom reference types.</param>
    IPropertyBuilder<T> ReferenceType(string identifier, string? namespaceUri = null);

    /// <summary>
    /// Sets the reference type for collection/dictionary items.
    /// </summary>
    /// <param name="identifier">Reference type identifier for items.</param>
    /// <param name="namespaceUri">Optional namespace URI for custom reference types.</param>
    IPropertyBuilder<T> ItemReferenceType(string identifier, string? namespaceUri = null);

    // Client-only - monitoring
    IPropertyBuilder<T> SamplingInterval(int value);
    IPropertyBuilder<T> QueueSize(uint value);
    IPropertyBuilder<T> DiscardOldest(bool value);
    IPropertyBuilder<T> DataChangeTrigger(DataChangeTrigger value);
    IPropertyBuilder<T> DeadbandType(DeadbandType value);
    IPropertyBuilder<T> DeadbandValue(double value);

    // Server-only
    IPropertyBuilder<T> ModellingRule(ModellingRule value);
    IPropertyBuilder<T> EventNotifier(byte value);

    /// <summary>
    /// Adds a non-hierarchical reference to the node.
    /// </summary>
    /// <param name="referenceType">Reference type identifier (standard type name, NodeId string, or BrowseName).</param>
    /// <param name="referenceTypeNamespace">Namespace URI for custom reference types. Pass null for standard types.</param>
    /// <param name="targetNodeId">Target node identifier.</param>
    /// <param name="targetNamespaceUri">Target namespace URI. If null, uses the default namespace.</param>
    /// <param name="isForward">Whether this is a forward reference. Default is true.</param>
    IPropertyBuilder<T> AdditionalReference(
        string referenceType,
        string? referenceTypeNamespace,
        string targetNodeId,
        string? targetNamespaceUri = null,
        bool isForward = true);

    // Nested property mapping
    IPropertyBuilder<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure);
}
