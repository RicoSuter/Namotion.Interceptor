using Namotion.Interceptor;

namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// Interface for components that persist subject configurations to storage.
/// </summary>
public interface IConfigurationWriter
{
    /// <summary>
    /// Writes the configuration that contains the subject to storage: that of the subject itself when the writer
    /// stores it, else that of the stored subject it is nested in through [Configuration] properties.
    /// </summary>
    /// <param name="subject">The subject whose containing configuration is written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true if the configuration was written; false if nothing was written, such as when this writer stores no configuration that contains the subject.</returns>
    /// <remarks>
    /// A writer can first apply an edit of the stored configuration that it has not loaded yet, so that the edit is
    /// written back instead of being overwritten. Configuration of the subjects that was not saved yet is then
    /// replaced by the edit, and a subject can be replaced by a new one.
    /// </remarks>
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken);
}
