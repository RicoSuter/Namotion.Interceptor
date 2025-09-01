using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

public record RegisteredSubjectAttribute : RegisteredSubjectProperty
{
    private readonly PropertyAttributeAttribute? _attributeMetadata;

    internal RegisteredSubjectAttribute(
        PropertyReference property, Type type, IReadOnlyCollection<Attribute> reflectionAttributes, PropertyAttributeAttribute attributeMetadata) 
        : base(property, type, reflectionAttributes)
    {
        _attributeMetadata = attributeMetadata;
    }
    
    /// <summary>
    /// Gets the attribute with information about this attribute property.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this property is not an attribute.</exception>
    public PropertyAttributeAttribute AttributeMetadata => _attributeMetadata 
        ?? throw new InvalidOperationException("The property is not an attribute.");
    
    public override string BrowseName => AttributeMetadata.AttributeName;
    
    /// <summary>
    /// Gets the attribute property this property is attached to.
    /// </summary>
    /// <returns>The property.</returns>
    /// <exception cref="InvalidOperationException">Thrown when this property is not an attribute.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the property this attribute is attached could not be found.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty GetAttributedProperty()
    {
        return Parent.TryGetProperty(AttributeMetadata.PropertyName) ??
               throw new InvalidOperationException($"The attributed property '{AttributeMetadata.PropertyName}' could not be found on the parent subject.");
    }
}