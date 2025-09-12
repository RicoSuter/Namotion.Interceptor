using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// An attribute provides additional information about a property of a
/// registered subject but is technical also a property on the subject.
/// </summary>
public record RegisteredSubjectAttribute : RegisteredSubjectProperty
{
    private RegisteredSubjectProperty? _parentPropertyCache;
    
    internal RegisteredSubjectAttribute(
        RegisteredSubject parent, string name, Type type, 
        IReadOnlyCollection<Attribute> reflectionAttributes, 
        PropertyAttributeAttribute attributeMetadata) 
        : base(parent, name, type, reflectionAttributes)
    {
        AttributeMetadata = attributeMetadata;
    }
    
    /// <summary>
    /// Gets the attribute with information about this attribute property.
    /// </summary>
    public PropertyAttributeAttribute AttributeMetadata { get; }

    /// <inheritdoc />
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
        return _parentPropertyCache ??= Parent.TryGetProperty(AttributeMetadata.PropertyName) ??
            throw new InvalidOperationException($"The attributed property '{AttributeMetadata.PropertyName}' could not be found on the parent subject.");
    }
}