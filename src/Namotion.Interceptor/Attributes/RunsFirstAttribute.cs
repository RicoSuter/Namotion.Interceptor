namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Specifies that this service runs before all services without this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RunsFirstAttribute : Attribute
{
}
