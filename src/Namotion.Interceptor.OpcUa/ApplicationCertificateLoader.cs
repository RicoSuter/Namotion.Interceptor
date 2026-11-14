using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Loads or creates the application certificate of a client or server, warns before it expires and replaces it
/// once it has expired when it is self-signed and automatic creation is on.
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
    /// is left as is. Within 30 days of expiry a warning is logged. An expired self-signed certificate is deleted from
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
            var certificate = await LoadOrCreateAsync(application, lifetimeInMonths, cancellationToken).ConfigureAwait(false);

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
        using (var store = OpenStore(identifier, telemetryContext))
        {
            if (!await store.DeleteAsync(expired.Thumbprint, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"The expired certificate {expired.Thumbprint} could not be deleted from the store '{identifier.StorePath}'.");
            }
        }

        identifier.Certificate = null;
        return await LoadOrCreateAsync(application, lifetimeInMonths, cancellationToken).ConfigureAwait(false);
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
