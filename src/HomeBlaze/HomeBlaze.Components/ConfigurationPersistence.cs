using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Components;

/// <summary>
/// Persists the configuration that a subject is part of.
/// </summary>
internal static class ConfigurationPersistence
{
    /// <summary>
    /// Finds the configuration owner of the subject, writes it through its configuration writer, see
    /// <see cref="RootManagerExtensions.GetConfigurationWriter"/>, and then calls
    /// <see cref="IConfigurable.ApplyConfigurationAsync"/> of the owner, also when the writer wrote nothing, but not
    /// when the write detached the owner, such as a writer that replaced it with a new instance.
    /// </summary>
    /// <remarks>
    /// The write is not canceled, so a configuration is never left half written; the token only cancels the apply step.
    /// </remarks>
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
        var wasAttached = owner.TryGetRegisteredSubject() is not null;
        var isWritten = await configurationWriter.WriteConfigurationAsync(owner, CancellationToken.None);

        // A writer that recreated the owner has detached and released it, and applying would restart a released subject.
        if (!wasAttached || owner.TryGetRegisteredSubject() is not null)
        {
            await configurable.ApplyConfigurationAsync(cancellationToken);
        }

        return isWritten ? ConfigurationPersistResult.Written : ConfigurationPersistResult.NotWritten;
    }
}
