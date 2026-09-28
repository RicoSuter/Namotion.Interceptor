namespace Namotion.Interceptor.Attributes;

/// <summary>
/// Specifies that this service runs BEFORE the specified types.
/// When a referenced type has multiple registered instances, the service runs before every instance.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RunsBeforeAttribute : Attribute
{
    public Type[] Types { get; }

    public RunsBeforeAttribute(params Type[] types) => Types = types;
}
