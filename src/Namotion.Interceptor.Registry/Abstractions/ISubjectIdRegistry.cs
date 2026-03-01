namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A reverse index that maps string subject IDs to subject instances.
/// Used by connectors (e.g., WebSocket) to look up subjects by their
/// protocol-level identifiers during update application.
/// </summary>
public interface ISubjectIdRegistry
{
    /// <summary>
    /// Tries to get a subject by its ID from the reverse index.
    /// </summary>
    /// <param name="subjectId">The subject ID.</param>
    /// <param name="subject">The subject if found.</param>
    /// <returns>True if a subject with the given ID was found.</returns>
    bool TryGetSubjectById(string subjectId, out IInterceptorSubject subject);
}

/// <summary>
/// Internal writer interface for managing subject IDs and the reverse index atomically.
/// Both the subject's Data store and the reverse index are updated under a single lock.
/// </summary>
internal interface ISubjectIdRegistryWriter
{
    /// <summary>
    /// Gets an existing subject ID or generates a new one, atomically updating
    /// both the subject's Data store and the reverse index.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <returns>The existing or newly generated subject ID.</returns>
    string GetOrAddSubjectId(IInterceptorSubject subject);

    /// <summary>
    /// Assigns a known subject ID, atomically updating both the subject's Data store
    /// and the reverse index (unregistering any previous ID).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="id">The subject ID to assign.</param>
    void SetSubjectId(IInterceptorSubject subject, string id);
}
