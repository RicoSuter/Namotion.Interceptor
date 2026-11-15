using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Loads or creates the application certificate of a client or server from its own store, warns before it expires and,
/// in a directory store, replaces it once it has expired or fails the application URI or host check, when it is
/// self-signed and automatic creation is on.
/// </summary>
internal static class ApplicationCertificateLoader
{
    // How long before expiry a warning is logged, so a new certificate can be provided or trusted in time.
    private static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(30);

    // The SDK deletes a certificate before it creates one, so applications sharing a store, such as a client and
    // a server in one process, must not load or replace concurrently.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // The thumbprint each identifier was configured with before its first load. The SDK pins the thumbprint of the
    // certificate it loads on the identifier, which must not count as a configured pin on the next load.
    private static readonly ConditionalWeakTable<CertificateIdentifier, StrongBox<string?>> ConfiguredThumbprints = new();

    /// <summary>
    /// Ensures the application certificate of <paramref name="application"/> is in its own store with its private key,
    /// creating one in a directory store when none exists and <paramref name="autoCreate"/> is true, and returns it.
    /// Within 30 days of expiry a warning is logged. In a directory store, a self-signed certificate that has expired,
    /// or fails the SDK's application URI check or, for a server, its host check against the base addresses, is deleted
    /// with its private key and replaced when <paramref name="autoCreate"/> is true. A certificate in any other store
    /// type, such as the Windows certificate store, is used as is and never created, deleted or replaced. When the
    /// identifier was configured with a thumbprint, only that certificate is used. A failure to load, create or replace
    /// the certificate propagates.
    /// </summary>
    /// <returns>The loaded certificate with its private key, owned by the caller.</returns>
    public static async Task<Certificate> EnsureLoadedAsync(
        ApplicationInstance application,
        bool autoCreate,
        ushort lifetimeInMonths,
        ITelemetryContext telemetryContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var configuration = GetConfiguration(application);
        var identifier = GetIdentifier(configuration);

        var storeLock = GetStoreLock(identifier);
        await storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuredThumbprint = ConfiguredThumbprints.GetValue(identifier, static id => new StrongBox<string?>(id.Thumbprint)).Value;
            identifier.Thumbprint = configuredThumbprint;

            // Only a directory store is managed: a certificate in any other store type was put there by someone else
            // and may be the only copy of its key.
            var manageStore = autoCreate && IsManagedStore(identifier);
            application.DisableCertificateAutoCreation = !manageStore;
            var hasDeletedMismatched = manageStore &&
                await DeleteMismatchedCertificatesAsync(configuration, identifier, telemetryContext, logger, cancellationToken).ConfigureAwait(false);

            var certificate = await LoadOrCreateAsync(
                application, configuration, identifier, configuredThumbprint, lifetimeInMonths, telemetryContext, cancellationToken).ConfigureAwait(false);
            try
            {
                if (hasDeletedMismatched)
                {
                    logger.LogInformation(
                        "Loaded the OPC UA application certificate {Thumbprint} from the store '{StorePath}' after deleting the " +
                        "certificates that did not match the application.",
                        certificate.Thumbprint, identifier.StorePath);
                }

                // The store can hold several expired certificates for the application, and each reload finds the next one,
                // so this deletes them one by one until a valid one is loaded or created.
                var replacementsLeft = -1;
                while (manageStore && HasExpired(certificate) && X509Utils.IsSelfSigned(certificate))
                {
                    if (replacementsLeft < 0)
                    {
                        replacementsLeft = await CountCertificatesAsync(identifier, telemetryContext, cancellationToken).ConfigureAwait(false);
                    }

                    if (replacementsLeft-- == 0)
                    {
                        throw new InvalidOperationException(
                            $"The expired certificate {certificate.Thumbprint} in the store '{identifier.StorePath}' could not be replaced.");
                    }

                    var replacement = await ReplaceAsync(
                        application, configuration, identifier, configuredThumbprint, certificate, lifetimeInMonths, telemetryContext, cancellationToken).ConfigureAwait(false);
                    logger.LogWarning(
                        "Replaced the expired OPC UA application certificate {Thumbprint} in the store '{StorePath}', which expired {NotAfter:u}, " +
                        "with the certificate {NewThumbprint}. Peers that trusted the old certificate have to trust the replacement.",
                        certificate.Thumbprint, identifier.StorePath, certificate.NotAfter.ToUniversalTime(), replacement.Thumbprint);
                    certificate.Dispose();
                    certificate = replacement;
                }

                LogExpiryWarning(certificate, manageStore, identifier.StorePath ?? string.Empty, logger);
                return certificate;
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Deletes the own store folder of <paramref name="identifier"/> with every certificate and private key in it,
    /// under the same lock as <see cref="EnsureLoadedAsync"/>. An empty store path or a failure to delete is logged
    /// as a warning and otherwise ignored.
    /// </summary>
    public static async Task CleanStoreAsync(CertificateIdentifier identifier, ILogger logger, CancellationToken cancellationToken)
    {
        var storePath = identifier.StorePath;
        if (string.IsNullOrEmpty(storePath))
        {
            logger.LogWarning("Certificate store path is empty, skipping cleanup.");
            return;
        }

        var storeLock = GetStoreLock(identifier);
        await storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(storePath))
            {
                Directory.Delete(storePath, true);
                logger.LogDebug("Cleaned certificate store at {Path}.", storePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean certificate store at {Path}. Continuing with existing certificates.", storePath);
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Gets the lock that serializes access to the store of <paramref name="identifier"/> within the process.
    /// </summary>
    internal static SemaphoreSlim GetStoreLock(CertificateIdentifier identifier) =>
        StoreLocks.GetOrAdd(GetStoreKey(identifier), static _ => new SemaphoreSlim(1, 1));

    private static ApplicationConfiguration GetConfiguration(ApplicationInstance application) =>
        application.ApplicationConfiguration
        ?? throw new InvalidOperationException("The OPC UA application instance has no application configuration.");

    private static CertificateIdentifier GetIdentifier(ApplicationConfiguration configuration) =>
        configuration.SecurityConfiguration.ApplicationCertificate
        ?? throw new InvalidOperationException("The OPC UA application configuration has no application certificate.");

    /// <summary>
    /// Gets whether the loader creates, deletes and replaces certificates in the store of <paramref name="identifier"/>,
    /// which it does only for a directory store.
    /// </summary>
    internal static bool IsManagedStore(CertificateIdentifier identifier) =>
        string.Equals(identifier.StoreType ?? CertificateStoreType.Directory, CertificateStoreType.Directory, StringComparison.OrdinalIgnoreCase);

    private static void LogExpiryWarning(Certificate certificate, bool replacesSelfSigned, string storePath, ILogger logger)
    {
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var remaining = notAfter - DateTime.UtcNow;
        if (remaining >= ExpiryWarningWindow)
        {
            return;
        }

        if (remaining <= TimeSpan.Zero)
        {
            logger.LogWarning(
                "The OPC UA application certificate {Thumbprint} in the store '{StorePath}' expired {NotAfter:u}. " +
                "Provide a new certificate; it is not replaced automatically.",
                certificate.Thumbprint, storePath, notAfter);
        }
        else if (replacesSelfSigned && X509Utils.IsSelfSigned(certificate))
        {
            logger.LogWarning(
                "The OPC UA application certificate {Thumbprint} in the store '{StorePath}' expires {NotAfter:u}. It is replaced " +
                "with a new self-signed certificate once it has expired, and peers that trust it then have to trust the new one.",
                certificate.Thumbprint, storePath, notAfter);
        }
        else
        {
            logger.LogWarning(
                "The OPC UA application certificate {Thumbprint} in the store '{StorePath}' expires {NotAfter:u}. " +
                "Provide a new certificate before then; it is not replaced automatically.",
                certificate.Thumbprint, storePath, notAfter);
        }
    }

    /// <summary>
    /// Deletes, one by one, the self-signed certificate that the SDK would load for the application while it fails the
    /// SDK's application URI or host check, logging each deletion, and returns whether any was deleted.
    /// </summary>
    private static async Task<bool> DeleteMismatchedCertificatesAsync(
        ApplicationConfiguration configuration,
        CertificateIdentifier identifier,
        ITelemetryContext telemetryContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // A pinned thumbprint makes the SDK refuse to create a certificate, so deleting the pinned one would only fail.
        if (!string.IsNullOrEmpty(identifier.Thumbprint))
        {
            return false;
        }

        var deletionsLeft = -1;
        while (true)
        {
            // The SDK selects with this call, which takes the first certificate whose key loads rather than the best
            // one, so it is mirrored exactly.
            using var stored = await CertificateIdentifierResolver.LoadPrivateKeyWithStoreResolverAsync(
                identifier,
                configuration.CertificateManager as ICertificateStoreResolver,
                configuration.SecurityConfiguration.CertificatePasswordProvider,
                configuration.ApplicationUri,
                telemetryContext,
                cancellationToken).ConfigureAwait(false);

            if (stored is null || !X509Utils.IsSelfSigned(stored) ||
                await GetMismatchAsync(configuration, stored, cancellationToken).ConfigureAwait(false) is not { } reason)
            {
                return deletionsLeft >= 0;
            }

            if (deletionsLeft < 0)
            {
                deletionsLeft = await CountCertificatesAsync(identifier, telemetryContext, cancellationToken).ConfigureAwait(false);
            }

            if (deletionsLeft-- == 0)
            {
                throw new InvalidOperationException(
                    $"The certificate {stored.Thumbprint} in the store '{identifier.StorePath}', which does not match the application, " +
                    "could not be replaced.");
            }

            await DeleteAsync(identifier, stored, telemetryContext, cancellationToken).ConfigureAwait(false);
            logger.LogWarning(
                "Deleted the self-signed OPC UA application certificate {Thumbprint} and its private key from the store '{StorePath}' " +
                "because {Reason}, so a new one is created. Peers that trusted it have to trust the new one.",
                stored.Thumbprint, identifier.StorePath, reason);
        }
    }

    /// <summary>
    /// Applies the domain and application URI checks of the SDK's ApplicationInstance, which are private, and returns
    /// why <paramref name="certificate"/> fails them, or null when it passes.
    /// </summary>
    private static async Task<string?> GetMismatchAsync(
        ApplicationConfiguration configuration,
        Certificate certificate,
        CancellationToken cancellationToken)
    {
        if (configuration.ApplicationType != ApplicationType.Client)
        {
            var certificateDomains = X509Utils.GetDomainsFromCertificate(certificate);
            IPAddress[]? addresses = null;
            foreach (var domain in configuration.GetServerDomainNames().ToArray() ?? [])
            {
                if (certificateDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isLocalhost = string.Equals(domain, "localhost", StringComparison.OrdinalIgnoreCase);
                if (isLocalhost)
                {
                    if (certificateDomains.Contains(Utils.GetHostName(), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    addresses ??= await Utils.GetHostAddressesAsync(Utils.GetHostName(), cancellationToken).ConfigureAwait(false);
                    if (addresses.Any(address => certificateDomains.Contains(address.ToString(), StringComparer.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                }

                return isLocalhost
                    ? $"it does not cover the host name '{Utils.GetHostName()}' that localhost in the base addresses stands for"
                    : $"it does not cover the host '{domain}' of the base addresses";
            }
        }

        if (!X509Utils.CompareApplicationUriWithCertificate(certificate, configuration.ApplicationUri ?? string.Empty, out var certificateUris))
        {
            return certificateUris.Count == 0
                ? $"it carries no application URI instead of '{configuration.ApplicationUri}'"
                : $"it carries the application URI {string.Join(", ", certificateUris)} instead of '{configuration.ApplicationUri}'";
        }

        return null;
    }

    private static bool HasExpired(Certificate certificate) =>
        certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow;

    private static async Task<int> CountCertificatesAsync(
        CertificateIdentifier identifier,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        using var store = OpenStore(identifier, telemetryContext);
        using var certificates = await store.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return certificates.Count;
    }

    private static async Task<Certificate> ReplaceAsync(
        ApplicationInstance application,
        ApplicationConfiguration configuration,
        CertificateIdentifier identifier,
        string? configuredThumbprint,
        Certificate expired,
        ushort lifetimeInMonths,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        await DeleteAsync(identifier, expired, telemetryContext, cancellationToken).ConfigureAwait(false);

        // The SDK pinned the deleted certificate when it loaded it; only a configured pin is kept.
        identifier.Thumbprint = configuredThumbprint;
        return await LoadOrCreateAsync(
            application, configuration, identifier, configuredThumbprint, lifetimeInMonths, telemetryContext, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(
        CertificateIdentifier identifier,
        Certificate certificate,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        using var store = OpenStore(identifier, telemetryContext);
        if (!await store.DeleteAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The certificate {certificate.Thumbprint} could not be deleted from the store '{identifier.StorePath}'.");
        }
    }

    private static ICertificateStore OpenStore(CertificateIdentifier identifier, ITelemetryContext telemetryContext) =>
        CertificateIdentifierResolver.OpenStore(identifier, telemetryContext)
        ?? throw new InvalidOperationException("The store path of the application certificate is not set.");

    private static async Task<Certificate> LoadOrCreateAsync(
        ApplicationInstance application,
        ApplicationConfiguration configuration,
        CertificateIdentifier identifier,
        string? configuredThumbprint,
        ushort lifetimeInMonths,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        var isValid = await application.CheckApplicationInstanceCertificatesAsync(
            silent: true,
            lifetimeInMonths,
            cancellationToken).ConfigureAwait(false);

        // The SDK pinned the thumbprint of the certificate it selected on the identifier, so this loads that one.
        var certificate = isValid
            ? await CertificateIdentifierResolver.LoadPrivateKeyWithStoreResolverAsync(
                identifier,
                configuration.CertificateManager as ICertificateStoreResolver,
                configuration.SecurityConfiguration.CertificatePasswordProvider,
                configuration.ApplicationUri,
                telemetryContext,
                cancellationToken).ConfigureAwait(false)
            : null;

        if (certificate is not { HasPrivateKey: true })
        {
            certificate?.Dispose();
            throw new InvalidOperationException("No valid application certificate with a private key was found.");
        }

        // When no certificate carries the configured thumbprint, the SDK falls back to any certificate for the
        // application URI, which would silently replace the pinned one.
        if (!string.IsNullOrEmpty(configuredThumbprint) &&
            !string.Equals(certificate.Thumbprint, configuredThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            var thumbprint = certificate.Thumbprint;
            certificate.Dispose();
            throw new InvalidOperationException(
                $"The application certificate {configuredThumbprint} configured by thumbprint is not in the store '{identifier.StorePath}'; " +
                $"the store offered the certificate {thumbprint} instead, which is not used.");
        }

        return certificate;
    }

    private static string GetStoreKey(CertificateIdentifier identifier)
    {
        var storePath = identifier.StorePath;
        if (string.IsNullOrEmpty(storePath))
        {
            throw new InvalidOperationException("The store path of the application certificate is not set.");
        }

        return identifier.StoreType == CertificateStoreType.Directory
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(storePath))
            : $"{identifier.StoreType}|{storePath}";
    }
}
