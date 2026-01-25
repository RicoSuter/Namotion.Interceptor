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

    // Reference configuration
    IPropertyBuilder<T> ReferenceType(string value);
    IPropertyBuilder<T> ItemReferenceType(string value);

    // Client only - monitoring
    IPropertyBuilder<T> SamplingInterval(int value);
    IPropertyBuilder<T> QueueSize(uint value);
    IPropertyBuilder<T> DiscardOldest(bool value);
    IPropertyBuilder<T> DataChangeTrigger(DataChangeTrigger value);
    IPropertyBuilder<T> DeadbandType(DeadbandType value);
    IPropertyBuilder<T> DeadbandValue(double value);

    // Server only
    IPropertyBuilder<T> ModellingRule(ModellingRule value);
    IPropertyBuilder<T> EventNotifier(byte value);

    // Nested property mapping
    IPropertyBuilder<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure);
}
