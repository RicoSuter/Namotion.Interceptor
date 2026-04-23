using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Abstractions;

public class RegisteredSubjectAttribute : RegisteredSubjectProperty
{
    private RegisteredSubjectMember? _attributedMemberCache;

    internal RegisteredSubjectAttribute(
        RegisteredSubject parent, string name, Type type,
        IReadOnlyCollection<Attribute> reflectionAttributes,
        MemberAttributeAttribute attributeMetadata)
        : base(parent, name, type, reflectionAttributes)
    {
        AttributeName = attributeMetadata.AttributeName;
        MemberName = attributeMetadata.MemberName;
    }

    /// <summary>
    /// Gets the name of the attribute.
    /// </summary>
    public string AttributeName { get; }

    /// <summary>
    /// Gets the name of the member this attribute is attached to.
    /// </summary>
    public string MemberName { get; }

    /// <summary>
    /// Gets the browse name of the attribute.
    /// </summary>
    public override string BrowseName => AttributeName;

    /// <summary>
    /// Gets the member this attribute is attached to.
    /// </summary>
    /// <returns>The parent member.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the member could not be found.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectMember GetAttributedMember()
    {
        return _attributedMemberCache ??= Parent.TryGetMember(MemberName) ??
            throw new InvalidOperationException(
                $"The attributed member '{MemberName}' could not be found on the parent subject.");
    }
}
