using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A registered property declared as an attribute of another property via
/// <see cref="PropertyAttributeAttribute"/>. Provides strongly-typed access to
/// the attribute's name, the owning property's name, and the owning property
/// itself.
/// </summary>
public sealed class RegisteredSubjectAttribute : RegisteredSubjectProperty
{
    internal RegisteredSubjectAttribute(
        RegisteredSubject parent, string name, Type type,
        IReadOnlyCollection<Attribute> reflectionAttributes,
        PropertyAttributeAttribute attributeMetadata)
        : base(parent, name, type, reflectionAttributes)
    {
        AttributeName = attributeMetadata.AttributeName;
        PropertyName = attributeMetadata.PropertyName;
    }

    /// <summary>
    /// Gets the name of this attribute (the second argument of <see cref="PropertyAttributeAttribute"/>).
    /// </summary>
    public string AttributeName { get; }

    /// <summary>
    /// Gets the name of the property this attribute is attached to.
    /// </summary>
    public string PropertyName { get; }

    /// <inheritdoc />
    public override string BrowseName => AttributeName;

    /// <summary>
    /// Gets the member this attribute is attached to.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the attributed member cannot be resolved on the parent subject.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectMember GetAttributedMember()
    {
        return Parent.TryGetMember(PropertyName)
            ?? throw new InvalidOperationException(
                $"The attributed member '{PropertyName}' could not be found on the parent subject.");
    }
}
