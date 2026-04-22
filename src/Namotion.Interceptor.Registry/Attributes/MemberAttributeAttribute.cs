namespace Namotion.Interceptor.Registry.Attributes;

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
