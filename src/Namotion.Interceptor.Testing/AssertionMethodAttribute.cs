namespace Namotion.Interceptor.Testing;

/// <summary>
/// Identifies a method that verifies a test condition and fails when it is not met.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AssertionMethodAttribute : Attribute
{
}
