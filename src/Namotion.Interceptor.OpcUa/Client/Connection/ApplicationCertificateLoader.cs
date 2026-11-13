using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Namotion.Interceptor.OpcUa.Client.Connection;

/// <summary>
/// Loads, creates and renews the client's application certificate for secured connections. One instance per
/// client source keeps the loaded certificate, so later connect attempts do not read the store again.
/// </summary>
internal sealed class ApplicationCertificateLoader
{
    /// <summary>
    /// How long before expiry a self-created certificate is renewed, so the server operator can trust the new one in time.
    /// </summary>
    internal static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(30);

    // Creating a certificate deletes others with the same subject from the store, so sources sharing a store
    // must not load, create or renew at the same time.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new(StringComparer.Ordinal);

    private readonly ILogger _logger;
    private X509Certificate2? _certificate;

    public ApplicationCertificateLoader(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensures the application certificate of <paramref name="application"/> is loaded with its private key.
    /// A certificate already set with its private key is left as is.
    /// </summary>
    public async Task EnsureLoadedAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;

        // Loading overwrites the in-memory certificate, even with null, so a provided one must be left alone.
        if (identifier.Certificate is { HasPrivateKey: true })
        {
            return;
        }

        var cached = Volatile.Read(ref _certificate);
        if (cached is not null && !NeedsRenewal(cached, configuration))
        {
            identifier.Certificate = cached;
            return;
        }

        var storeLock = StoreLocks.GetOrAdd(Path.GetFullPath(identifier.StorePath), static _ => new SemaphoreSlim(1, 1));
        await storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var certificate = await LoadOrCreateAsync(application, configuration, cancellationToken).ConfigureAwait(false);
            if (NeedsRenewal(certificate, configuration))
            {
                _logger.LogWarning(
                    "Renewing the OPC UA client application certificate {Thumbprint}, which expires {NotAfter:u}. " +
                    "Servers that trust the client have to trust the new certificate.",
                    certificate.Thumbprint, certificate.NotAfter.ToUniversalTime());

                using (var store = new CertificateStoreIdentifier(identifier.StorePath, identifier.StoreType, false)
                    .OpenStore(configuration.ResolvedTelemetryContext))
                {
                    await store.DeleteAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false);
                }

                identifier.Certificate = null;
                certificate = await LoadOrCreateAsync(application, configuration, cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _certificate, certificate);
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="certificate"/> is self-signed, created by this client, and expires within <see cref="RenewalWindow"/>.
    /// </summary>
    internal static bool NeedsRenewal(X509Certificate2 certificate, OpcUaClientConfiguration configuration) =>
        configuration.AutoCreateApplicationCertificate &&
        X509Utils.IsSelfSigned(certificate) &&
        certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewalWindow;

    private static async Task<X509Certificate2> LoadOrCreateAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        application.DisableCertificateAutoCreation = !configuration.AutoCreateApplicationCertificate;
        var isValid = await application.CheckApplicationInstanceCertificatesAsync(
            silent: true,
            configuration.ApplicationCertificateLifetimeInMonths,
            cancellationToken).ConfigureAwait(false);

        var certificate = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate;
        if (!isValid || certificate is not { HasPrivateKey: true })
        {
            throw new InvalidOperationException("No valid application certificate with a private key was found.");
        }

        return certificate;
    }
}
