namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A registry which tracks subjects and their child subjects, property attributes and additional metadata.
/// The singleton authority for the registry slot on its context: it holds the one projection every
/// consumer navigates, so any implementation reserves the slot and a second registration throws.
/// </summary>
public interface ISubjectRegistry : ISingletonContextService<ISubjectRegistry>
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
}
