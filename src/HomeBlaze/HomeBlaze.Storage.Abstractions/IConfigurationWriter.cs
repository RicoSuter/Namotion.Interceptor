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
    /// <param name="subject">The subject to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true if the configuration was written; false if this writer stores no configuration that contains the subject.</returns>
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken);
}
