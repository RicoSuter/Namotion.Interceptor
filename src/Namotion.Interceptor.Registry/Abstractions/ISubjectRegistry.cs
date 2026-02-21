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
    /// Registers a stable ID for a subject in the reverse index.
    /// </summary>
    /// <param name="stableId">The stable ID.</param>
    /// <param name="subject">The subject.</param>
    void RegisterStableId(string stableId, IInterceptorSubject subject);

    /// <summary>
    /// Removes a stable ID from the reverse index.
    /// </summary>
    /// <param name="stableId">The stable ID to remove.</param>
    void UnregisterStableId(string stableId);

    /// <summary>
    /// Tries to get a subject by its stable ID from the reverse index.
    /// </summary>
    /// <param name="stableId">The stable ID.</param>
    /// <param name="subject">The subject if found.</param>
    /// <returns>True if a subject with the given stable ID was found.</returns>
    bool TryGetSubjectByStableId(string stableId, out IInterceptorSubject subject);
}
