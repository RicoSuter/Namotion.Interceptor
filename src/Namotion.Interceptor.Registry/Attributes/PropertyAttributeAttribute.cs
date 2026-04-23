namespace Namotion.Interceptor.Registry.Attributes;

/// <summary>
/// Attaches a named attribute to a property. Apply to a partial property declaration
/// to express <c>owningProperty@attributeName</c> metadata — the decorated property
/// becomes an attribute of the property identified by the first constructor argument.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PropertyAttributeAttribute : MemberAttributeAttribute
{
    public PropertyAttributeAttribute(string propertyName, string attributeName)
        : base(propertyName, attributeName)
    {
    }

    public string PropertyName => MemberName;
}
