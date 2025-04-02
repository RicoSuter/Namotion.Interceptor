namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A registry which tracks subjects and their child subjects, property attributes and additional metadata.
/// </summary>
public interface ISubjectRegistry
{
    /// <summary>
    /// Gets all known registered subjects.
    /// </summary>
    IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects { get; }
    
    /// <summary>
    /// Gets a registered subject by the subject instance.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The registered subject or null if it is not registered with the registry.</returns>
    RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject);
    
    /// <summary>
    /// Executes an update to be applied to the subject in a synchronized way.
    /// </summary>
    /// <param name="update">The update action.</param>
    void ExecuteSubjectUpdate(Action update);
}
