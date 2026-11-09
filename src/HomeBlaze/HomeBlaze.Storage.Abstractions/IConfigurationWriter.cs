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
    /// <exception cref="ConfigurationWriteRefusedException">
    /// The file of the configuration has issues that writing it would lose; nothing was written.
    /// </exception>
    Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken);

    /// <summary>
    /// Throws the <see cref="ConfigurationWriteRefusedException"/> that <see cref="WriteConfigurationAsync"/> would throw
    /// for the subject now, without writing anything, so that a caller can check before it changes the subject. Can be
    /// called from any thread. A writer that never refuses a write does nothing, which is the default.
    /// </summary>
    /// <remarks>
    /// A file that is read again after the check can still make the write refuse.
    /// </remarks>
    /// <param name="subject">The subject whose containing configuration would be written.</param>
    /// <exception cref="ConfigurationWriteRefusedException">
    /// The file of the configuration has issues that writing it would lose.
    /// </exception>
    void ThrowIfWriteRefused(IInterceptorSubject subject)
    {
    }
}
