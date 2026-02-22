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
    /// Registers a subject ID in the reverse index.
    /// </summary>
    /// <param name="subjectId">The subject ID.</param>
    /// <param name="subject">The subject.</param>
    void RegisterSubjectId(string subjectId, IInterceptorSubject subject);

    /// <summary>
    /// Removes a subject ID from the reverse index.
    /// </summary>
    /// <param name="subjectId">The subject ID to remove.</param>
    void UnregisterSubjectId(string subjectId);

    /// <summary>
    /// Tries to get a subject by its ID from the reverse index.
    /// </summary>
    /// <param name="subjectId">The subject ID.</param>
    /// <param name="subject">The subject if found.</param>
    /// <returns>True if a subject with the given ID was found.</returns>
    bool TryGetSubjectById(string subjectId, out IInterceptorSubject subject);
}
