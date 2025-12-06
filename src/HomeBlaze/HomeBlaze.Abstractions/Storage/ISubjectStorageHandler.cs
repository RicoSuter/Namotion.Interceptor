using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Storage;

/// <summary>
/// Interface for components that can persist subject configurations to storage.
/// Implementations handle specific storage locations (e.g., root.json, Storage instances).
/// </summary>
public interface ISubjectStorageHandler
{
    /// <summary>
    /// Attempts to write the subject's configuration to storage.
    /// </summary>
    /// <param name="subject">The subject to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>true if this handler owns the subject and saved it; false if not this handler's subject.</returns>
    /// <exception cref="IOException">On transient I/O errors (retry later).</exception>
    Task<bool> WriteAsync(IInterceptorSubject subject, CancellationToken ct);
}
