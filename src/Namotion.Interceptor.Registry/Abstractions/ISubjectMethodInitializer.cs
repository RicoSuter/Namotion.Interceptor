namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// Runs once per registered method during subject attach. Parallel to
/// <see cref="ISubjectPropertyInitializer"/>. Triggered for method-level
/// attributes that implement this interface, and for context services
/// registered as <see cref="ISubjectMethodInitializer"/>.
/// </summary>
public interface ISubjectMethodInitializer
{
    /// <summary>
    /// Initializes the given method, typically by attaching metadata via
    /// <c>method.AddAttribute</c>.
    /// </summary>
    /// <param name="method">The method to initialize.</param>
    void InitializeMethod(RegisteredSubjectMethod method);
}
