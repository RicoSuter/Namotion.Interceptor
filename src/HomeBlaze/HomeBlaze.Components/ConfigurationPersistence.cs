using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Persists the configuration that a subject is part of.
/// </summary>
internal static class ConfigurationPersistence
{
    /// <summary>
    /// Finds the configuration owner of the subject, writes it through its configuration writer, see
    /// <see cref="RootManagerExtensions.GetConfigurationWriter"/>, and then calls
    /// <see cref="IConfigurable.ApplyConfigurationAsync"/> of the owner, also when the writer wrote nothing.
    /// </summary>
    /// <remarks>
    /// The write is not canceled, so a configuration is never left half written; the token only cancels the apply step.
    /// A caller checks with <see cref="ThrowIfOwnerWriteRefused"/> before it changes the subject. A write that is refused
    /// all the same, because the file was read again in between, still applies the owner, so that it acts on what was
    /// changed, and then throws the refusal: the change is applied but not saved.
    /// </remarks>
    /// <exception cref="ConfigurationWriteRefusedException">The writer refused to write the configuration of the owner.</exception>
    public static async Task<ConfigurationPersistResult> PersistOwnerAsync(
        RootManager rootManager,
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        // The first configurable parent would be wrong here: for a file that is its storage, whose
        // configuration does not contain the file.
        var owner = subject.TryGetConfigurationOwner();
        if (owner is not IConfigurable configurable)
        {
            return ConfigurationPersistResult.NoOwner;
        }

        var configurationWriter = rootManager.GetConfigurationWriter(owner);
        bool isWritten;
        try
        {
            isWritten = await configurationWriter.WriteConfigurationAsync(owner, CancellationToken.None);
        }
        catch (ConfigurationWriteRefusedException)
        {
            await configurable.ApplyConfigurationAsync(cancellationToken);
            throw;
        }

        await configurable.ApplyConfigurationAsync(cancellationToken);
        return isWritten ? ConfigurationPersistResult.Written : ConfigurationPersistResult.NotWritten;
    }

    /// <summary>
    /// Throws the <see cref="ConfigurationWriteRefusedException"/> that <see cref="PersistOwnerAsync"/> would throw now
    /// for the configuration owner of the subject, see <see cref="IConfigurationWriter.ThrowIfWriteRefused"/>, so that a
    /// caller can check before it changes the subject. Does nothing for a subject that is part of no configuration.
    /// </summary>
    /// <exception cref="ConfigurationWriteRefusedException">The writer would refuse to write the configuration of the owner.</exception>
    public static void ThrowIfOwnerWriteRefused(RootManager rootManager, IInterceptorSubject subject)
    {
        if (subject.TryGetConfigurationOwner() is { } owner)
        {
            rootManager.GetConfigurationWriter(owner).ThrowIfWriteRefused(owner);
        }
    }
}
