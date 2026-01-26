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
    IPropertyBuilder<T> TypeDefinition(string value);
    IPropertyBuilder<T> TypeDefinitionNamespace(string value);
    IPropertyBuilder<T> NodeClass(OpcUaNodeClass value);
    IPropertyBuilder<T> DataType(string value);

    /// <summary>
    /// Marks this property as the primary value for a VariableNode class (equivalent to [OpcUaValue] attribute).
    /// Use this when mapping a class to a VariableType where the class has a primary value property.
    /// </summary>
    IPropertyBuilder<T> IsValue(bool value = true);

    // Reference configuration
    IPropertyBuilder<T> ReferenceType(string value);
    IPropertyBuilder<T> ItemReferenceType(string value);

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
    /// <param name="referenceType">Reference type name (e.g., "HasInterface", "GeneratesEvent").</param>
    /// <param name="targetNodeId">Target node identifier.</param>
    /// <param name="targetNamespaceUri">Target namespace URI. If null, uses the default namespace.</param>
    /// <param name="isForward">Whether this is a forward reference. Default is true.</param>
    IPropertyBuilder<T> AdditionalReference(
        string referenceType,
        string targetNodeId,
        string? targetNamespaceUri = null,
        bool isForward = true);

    // Nested property mapping
    IPropertyBuilder<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure);
}
