namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// A reverse index that maps string subject IDs to subject instances.
/// Used by connectors (e.g., WebSocket) to look up subjects by their
/// protocol-level identifiers during update application.
/// </summary>
public interface ISubjectIdRegistry
{
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
