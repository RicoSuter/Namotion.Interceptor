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
    /// <summary>
    /// How long before expiry a warning is logged, so a new certificate can be provided or trusted in time.
    /// </summary>
    internal static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(30);

    // The SDK deletes a certificate before it creates one, so applications sharing a store, such as a client and
    // a server in one process, must not load or replace concurrently.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Ensures the application certificate of <paramref name="application"/> is loaded with its private key, creating
    /// one when none exists and <paramref name="autoCreate"/> is true. A certificate already set with its private key
    /// is left as is. Within <see cref="ExpiryWarningWindow"/> of expiry a warning is logged. An expired self-signed
    /// certificate is deleted from the own store with its private key and replaced when <paramref name="autoCreate"/>
    /// is true; any other certificate is never replaced.
    /// </summary>
    /// <exception cref="ServiceResultException">The SDK found no usable certificate and did not create one.</exception>
    /// <exception cref="InvalidOperationException">The store path is not set, no certificate with a private key was loaded, or the expired certificate could not be replaced.</exception>
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

        var storeLock = StoreLocks.GetOrAdd(GetStoreKey(identifier), static _ => new SemaphoreSlim(1, 1));
        await storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            application.DisableCertificateAutoCreation = !autoCreate;
            var certificate = await LoadOrCreateAsync(application, lifetimeInMonths, cancellationToken).ConfigureAwait(false);

            var notAfter = certificate.NotAfter.ToUniversalTime();
            var remaining = notAfter - DateTime.UtcNow;
            if (remaining >= ExpiryWarningWindow)
            {
                return;
            }

            var isReplaceable = autoCreate && X509Utils.IsSelfSigned(certificate);
            if (remaining > TimeSpan.Zero && isReplaceable)
            {
                logger.LogWarning(
                    "The OPC UA application certificate {Thumbprint} in the store '{StorePath}' expires {NotAfter:u}. It is replaced " +
                    "with a new self-signed certificate once it has expired, and peers that trust it then have to trust the new one.",
                    certificate.Thumbprint, identifier.StorePath, notAfter);
            }
            else if (remaining > TimeSpan.Zero)
            {
                logger.LogWarning(
                    "The OPC UA application certificate {Thumbprint} in the store '{StorePath}' expires {NotAfter:u}. " +
                    "Provide a new certificate before then; it is not replaced automatically.",
                    certificate.Thumbprint, identifier.StorePath, notAfter);
            }
            else if (isReplaceable)
            {
                var replacement = await ReplaceAsync(application, certificate, lifetimeInMonths, telemetryContext, cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "Replaced the expired OPC UA application certificate {Thumbprint} in the store '{StorePath}', which expired {NotAfter:u}, " +
                    "with the new self-signed certificate {NewThumbprint}. Peers that trusted the old certificate have to trust the new one.",
                    certificate.Thumbprint, identifier.StorePath, notAfter, replacement.Thumbprint);
            }
            else
            {
                logger.LogWarning(
                    "The OPC UA application certificate {Thumbprint} in the store '{StorePath}' expired {NotAfter:u}. " +
                    "Provide a new certificate; it is not replaced automatically.",
                    certificate.Thumbprint, identifier.StorePath, notAfter);
            }
        }
        finally
        {
            storeLock.Release();
        }
    }

    private static async Task<X509Certificate2> ReplaceAsync(
        ApplicationInstance application,
        X509Certificate2 expired,
        ushort lifetimeInMonths,
        ITelemetryContext telemetryContext,
        CancellationToken cancellationToken)
    {
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;
        using (var store = new CertificateStoreIdentifier(identifier.StorePath, identifier.StoreType, false).OpenStore(telemetryContext))
        {
            if (!await store.DeleteAsync(expired.Thumbprint, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"The expired certificate {expired.Thumbprint} could not be deleted from the store '{identifier.StorePath}'.");
            }
        }

        identifier.Certificate = null;
        var replacement = await LoadOrCreateAsync(application, lifetimeInMonths, cancellationToken).ConfigureAwait(false);
        if (replacement.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            throw new InvalidOperationException(
                $"After deleting the expired certificate {expired.Thumbprint}, the store '{identifier.StorePath}' still holds " +
                $"the expired certificate {replacement.Thumbprint}. Delete it so a new one is created.");
        }

        return replacement;
    }

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
