using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Loads or creates the application certificate of a client or server, warns before it expires and replaces it
/// once it has expired, or no longer matches the application, when it is self-signed and automatic creation is on.
/// </summary>
internal static class ApplicationCertificateLoader
{
    // How long before expiry a warning is logged, so a new certificate can be provided or trusted in time.
    private static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(30);

    // The SDK deletes a certificate before it creates one, so applications sharing a store, such as a client and
    // a server in one process, must not load or replace concurrently.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Ensures the application certificate of <paramref name="application"/> is loaded with its private key, creating
    /// one when none exists and <paramref name="autoCreate"/> is true. A certificate already set with its private key
    /// is left as is. Within 30 days of expiry a warning is logged. A self-signed certificate that has expired, does not
    /// carry the application URI or, for a server, does not cover the host names of its base addresses is deleted from
    /// the own store with its private key and replaced when <paramref name="autoCreate"/> is true; any other
    /// certificate is never replaced. A failure to load, create or replace the certificate propagates.
    /// </summary>
    public static async Task EnsureLoadedAsync(
        ApplicationInstance application,
        bool autoCreate,
        ushort lifetimeInMonths,
        ITelemetryContext telemetryContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;

        // Loading overwrites the in-memory certificate, even with null, so a provided one must be left alone.
        if (identifier.Certificate is { HasPrivateKey: true })
        {
            return;
        }

        var storeLock = GetStoreLock(identifier);
        await storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            application.DisableCertificateAutoCreation = !autoCreate;
            var mismatched = autoCreate
                ? await DeleteMismatchedCertificatesAsync(application, telemetryContext, cancellationToken).ConfigureAwait(false)
                : null;

            var certificate = await LoadOrCreateAsync(application, lifetimeInMonths, cancellationToken).ConfigureAwait(false);
            foreach (var (thumbprint, reason) in mismatched ?? [])
            {
                logger.LogWarning(
                    "Replaced the OPC UA application certificate {Thumbprint} in the store '{StorePath}' with the certificate " +
                    "{NewThumbprint} because {Reason}. Peers that trusted the old certificate have to trust the replacement.",
                    thumbprint, identifier.StorePath, certificate.Thumbprint, reason);
            }

            // The store can hold several expired certificates for the application, and each reload finds the next one,
            // so this deletes them one by one until a valid one is loaded or created.
            var replacementsLeft = -1;
            while (autoCreate && HasExpired(certificate) && X509Utils.IsSelfSigned(certificate))
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

                var replacement = await ReplaceAsync(application, certificate, lifetimeInMonths, telemetryContext, cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "Replaced the expired OPC UA application certificate {Thumbprint} in the store '{StorePath}', which expired {NotAfter:u}, " +
                    "with the certificate {NewThumbprint}. Peers that trusted the old certificate have to trust the replacement.",
                    certificate.Thumbprint, identifier.StorePath, certificate.NotAfter.ToUniversalTime(), replacement.Thumbprint);
                certificate = replacement;
            }

            LogExpiryWarning(certificate, autoCreate, identifier.StorePath, logger);
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

    private static void LogExpiryWarning(X509Certificate2 certificate, bool autoCreate, string storePath, ILogger logger)
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
        else if (autoCreate && X509Utils.IsSelfSigned(certificate))
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
    /// Deletes the self-signed certificates that the SDK would load for the application but then reject because they do
    /// not match it, and returns their thumbprints with the reason, or null when none was deleted.
    /// </summary>
    private static async Task<List<(string Thumbprint, string Reason)>?> DeleteMismatchedCertificatesAsync(
        ApplicationInstance application,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        var configuration = application.ApplicationConfiguration;
        var identifier = configuration.SecurityConfiguration.ApplicationCertificate;

        // A pinned thumbprint makes the SDK refuse to create a certificate, so deleting the pinned one would only fail.
        if (!string.IsNullOrEmpty(identifier.Thumbprint))
        {
            return null;
        }

        List<(string Thumbprint, string Reason)>? deleted = null;
        while (true)
        {
            // A copy, because finding sets the certificate of the identifier, which the SDK must load with its private key.
            var lookup = new CertificateIdentifier
            {
                StoreType = identifier.StoreType,
                StorePath = identifier.StorePath,
                SubjectName = identifier.SubjectName,
                CertificateType = identifier.CertificateType
            };

            var stored = await lookup.FindAsync(false, configuration.ApplicationUri, telemetryContext, cancellationToken).ConfigureAwait(false);
            if (stored is null || !X509Utils.IsSelfSigned(stored) ||
                await GetMismatchAsync(configuration, stored, cancellationToken).ConfigureAwait(false) is not { } reason)
            {
                return deleted;
            }

            await DeleteAsync(identifier, stored, telemetryContext, cancellationToken).ConfigureAwait(false);
            (deleted ??= []).Add((stored.Thumbprint, reason));
        }
    }

    /// <summary>
    /// Applies the domain and application URI checks of the SDK's ApplicationInstance, which are private, and returns
    /// why <paramref name="certificate"/> fails them, or null when it passes.
    /// </summary>
    private static async Task<string?> GetMismatchAsync(
        ApplicationConfiguration configuration,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        if (configuration.ApplicationType != ApplicationType.Client)
        {
            var certificateDomains = X509Utils.GetDomainsFromCertificate(certificate);
            IPAddress[]? addresses = null;
            foreach (var domain in configuration.GetServerDomainNames())
            {
                if (Utils.FindStringIgnoreCase(certificateDomains, domain))
                {
                    continue;
                }

                var isLocalhost = string.Equals(domain, "localhost", StringComparison.OrdinalIgnoreCase);
                if (isLocalhost)
                {
                    if (Utils.FindStringIgnoreCase(certificateDomains, Utils.GetHostName()))
                    {
                        continue;
                    }

                    addresses ??= await Utils.GetHostAddressesAsync(Utils.GetHostName(), cancellationToken).ConfigureAwait(false);
                    if (addresses.Any(address => Utils.FindStringIgnoreCase(certificateDomains, address.ToString())))
                    {
                        continue;
                    }
                }

                return isLocalhost
                    ? $"it does not cover the host name '{Utils.GetHostName()}' that localhost in the base addresses stands for"
                    : $"it does not cover the host '{domain}' of the base addresses";
            }
        }

        if (!X509Utils.CompareApplicationUriWithCertificate(certificate, configuration.ApplicationUri, out var certificateUris))
        {
            return certificateUris.Count == 0
                ? $"it carries no application URI instead of '{configuration.ApplicationUri}'"
                : $"it carries the application URI {string.Join(", ", certificateUris)} instead of '{configuration.ApplicationUri}'";
        }

        return null;
    }

    private static bool HasExpired(X509Certificate2 certificate) =>
        certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow;

    private static async Task<int> CountCertificatesAsync(
        CertificateIdentifier identifier,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        using var store = OpenStore(identifier, telemetryContext);
        return (await store.EnumerateAsync(cancellationToken).ConfigureAwait(false)).Count;
    }

    private static async Task<X509Certificate2> ReplaceAsync(
        ApplicationInstance application,
        X509Certificate2 expired,
        ushort lifetimeInMonths,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;
        await DeleteAsync(identifier, expired, telemetryContext, cancellationToken).ConfigureAwait(false);
        identifier.Certificate = null;
        return await LoadOrCreateAsync(application, lifetimeInMonths, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(
        CertificateIdentifier identifier,
        X509Certificate2 certificate,
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
        new CertificateStoreIdentifier(identifier.StorePath, identifier.StoreType, false).OpenStore(telemetryContext);

    private static async Task<X509Certificate2> LoadOrCreateAsync(
        ApplicationInstance application,
        ushort lifetimeInMonths,
        CancellationToken cancellationToken)
    {
        var isValid = await application.CheckApplicationInstanceCertificatesAsync(
            silent: true,
            lifetimeInMonths,
            cancellationToken).ConfigureAwait(false);

        var certificate = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate;
        if (!isValid || certificate is not { HasPrivateKey: true })
        {
            throw new InvalidOperationException("No valid application certificate with a private key was found.");
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
