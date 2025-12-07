using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Storage;

/// <summary>
/// Interface for components that persist subject configurations to storage.
/// </summary>
public interface IConfigurationWriter
{
    /// <summary>
    /// Writes the subject's configuration to storage.
    /// </summary>
    /// <param name="subject">The subject to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>true if this writer handled the subject; false otherwise.</returns>
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken ct);
}
