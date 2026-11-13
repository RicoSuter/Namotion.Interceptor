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
    /// How long before expiry a self-signed certificate is renewed, so the server operator can trust the new one in time.
    /// </summary>
    internal static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(30);

    // Loading, creating and renewing read, write and delete the shared store, so without serialization
    // sources sharing a store each create their own certificate.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ILogger _logger;
    private LoadedCertificate? _loaded;

    public ApplicationCertificateLoader(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensures the application certificate of <paramref name="application"/> is loaded with its private key.
    /// A certificate already set with its private key is left as is. Renewing a self-signed certificate deletes
    /// the old certificate and its private key from the own store.
    /// </summary>
    /// <exception cref="ServiceResultException">The SDK found no usable certificate and did not create one.</exception>
    /// <exception cref="InvalidOperationException">The store path is not set or no certificate with a private key was loaded.</exception>
    public async Task EnsureLoadedAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;

        // Loading overwrites the in-memory certificate, even with null, so a provided one must be left alone.
        if (identifier.Certificate is { HasPrivateKey: true } || TryUseLoaded(identifier, configuration))
        {
            return;
        }

        var storeLock = StoreLocks.GetOrAdd(GetStoreKey(identifier), static _ => new SemaphoreSlim(1, 1));
        await storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryUseLoaded(identifier, configuration))
            {
                return;
            }

            // Tells a certificate the check below creates apart from one already in the store. FindAsync keeps what
            // it finds, without the private key, on the identifier, so it is cleared for the check to load the key.
            var existed = await identifier.FindAsync(
                false,
                application.ApplicationConfiguration.ApplicationUri,
                configuration.ResolvedTelemetryContext,
                cancellationToken).ConfigureAwait(false) is not null;
            identifier.Certificate = null;

            var certificate = await LoadOrCreateAsync(application, configuration, cancellationToken).ConfigureAwait(false);
            var isNew = !existed;
            if (!isNew && NeedsRenewal(certificate, configuration))
            {
                (certificate, isNew) = await RenewAsync(application, configuration, certificate, cancellationToken).ConfigureAwait(false);
            }

            // Renewing a certificate created in this call would create a new one on every connect.
            var keepUntilExpiry = isNew && NeedsRenewal(certificate, configuration);
            if (keepUntilExpiry)
            {
                _logger.LogWarning(
                    "The OPC UA client application certificate {Thumbprint} expires {NotAfter:u}, within the renewal window of {RenewalWindow} days, " +
                    "right after its creation. The configured lifetime of {LifetimeInMonths} months is too short, so it is not renewed.",
                    certificate.Thumbprint, certificate.NotAfter.ToUniversalTime(), RenewalWindow.TotalDays,
                    configuration.ApplicationCertificateLifetimeInMonths);
            }

            Volatile.Write(ref _loaded, new LoadedCertificate(certificate, keepUntilExpiry));
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="certificate"/> expires within <see cref="RenewalWindow"/>, is self-signed and
    /// auto-creation is on. A provisioned self-signed certificate is replaced too.
    /// </summary>
    internal static bool NeedsRenewal(X509Certificate2 certificate, OpcUaClientConfiguration configuration) =>
        configuration.AutoCreateApplicationCertificate &&
        certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewalWindow &&
        X509Utils.IsSelfSigned(certificate);

    private bool TryUseLoaded(CertificateIdentifier identifier, OpcUaClientConfiguration configuration)
    {
        var loaded = Volatile.Read(ref _loaded);
        if (loaded is null || (!loaded.KeepUntilExpiry && NeedsRenewal(loaded.Certificate, configuration)))
        {
            return false;
        }

        identifier.Certificate = loaded.Certificate;
        return true;
    }

    private async Task<(X509Certificate2 Certificate, bool IsNew)> RenewAsync(
        ApplicationInstance application,
        OpcUaClientConfiguration configuration,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var identifier = application.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate;
        _logger.LogWarning(
            "Renewing the OPC UA client application certificate {Thumbprint}, which expires {NotAfter:u}. " +
            "Servers that trust the client have to trust the new certificate.",
            certificate.Thumbprint, certificate.NotAfter.ToUniversalTime());

        try
        {
            using (var store = new CertificateStoreIdentifier(identifier.StorePath, identifier.StoreType, false)
                .OpenStore(configuration.ResolvedTelemetryContext))
            {
                if (!await store.DeleteAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"The certificate {certificate.Thumbprint} could not be deleted from the store {identifier.StorePath}.");
                }
            }

            identifier.Certificate = null;
            return (await LoadOrCreateAsync(application, configuration, cancellationToken).ConfigureAwait(false), true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException &&
                                          certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
        {
            // Still valid, so the connection keeps working; the next connect attempt retries the renewal.
            _logger.LogError(exception,
                "Renewing the OPC UA client application certificate {Thumbprint} failed. It is used until it expires {NotAfter:u}.",
                certificate.Thumbprint, certificate.NotAfter.ToUniversalTime());

            identifier.Certificate = certificate;
            return (certificate, false);
        }
    }

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

    private sealed record LoadedCertificate(X509Certificate2 Certificate, bool KeepUntilExpiry);
}
