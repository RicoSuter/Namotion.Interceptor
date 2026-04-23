namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Base metadata for attributes attached to any registered subject member.
/// For properties, declare attributes with <see cref="PropertyAttributeAttribute"/>.
/// This base class exists to allow future member kinds (such as methods) to share
/// the same polymorphic attribute discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class MemberAttributeAttribute : Attribute
{
    public MemberAttributeAttribute(string memberName, string attributeName)
    {
        MemberName = memberName;
        AttributeName = attributeName;
    }

    public string MemberName { get; }

    public string AttributeName { get; }
}
