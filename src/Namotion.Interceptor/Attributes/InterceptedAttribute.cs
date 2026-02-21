namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Marks a property as intercepted by the source generator.
/// Applied automatically to generated partial property implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class InterceptedAttribute : Attribute
{
}
