namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Specifies that this service runs after all services without this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RunsLastAttribute : Attribute
{
}
