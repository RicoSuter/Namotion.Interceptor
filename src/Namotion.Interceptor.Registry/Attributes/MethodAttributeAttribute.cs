namespace Namotion.Interceptor.Registry.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class MethodAttributeAttribute : MemberAttributeAttribute
{
    public MethodAttributeAttribute(string methodName, string attributeName)
        : base(methodName, attributeName)
    {
    }

    public string MethodName => MemberName;
}
