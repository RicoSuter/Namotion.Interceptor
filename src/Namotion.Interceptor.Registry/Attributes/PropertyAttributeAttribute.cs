namespace Namotion.Interceptor.Registry.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PropertyAttributeAttribute : MemberAttributeAttribute
{
    public PropertyAttributeAttribute(string propertyName, string attributeName)
        : base(propertyName, attributeName)
    {
    }

    public string PropertyName => MemberName;
}
