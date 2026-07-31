namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Specifies that this service runs AFTER the specified types.
/// When a referenced type has multiple registered instances, the service runs after every instance.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RunsAfterAttribute : Attribute
{
    public Type[] Types { get; }

    public RunsAfterAttribute(params Type[] types) => Types = types;
}
