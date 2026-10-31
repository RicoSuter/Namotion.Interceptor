using HomeBlaze.Components.Controls;
using HomeBlaze.History.Blazor;
using HomeBlaze.Host.Clients;
using HomeBlaze.Host.Preferences;
using HomeBlaze.Host.Services;
using HomeBlaze.Host.TimeZones;
using HomeBlaze.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using Namotion.Interceptor.Blazor;
using System.Security.Cryptography.X509Certificates;

namespace HomeBlaze.Host;

/// <summary>
/// Extension methods for registering HomeBlaze.Host services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze host UI services to the service collection.
    /// This includes MudBlazor and cascades to all underlying services.
    /// </summary>
    public static IServiceCollection AddHomeBlazeHost(this IServiceCollection services)
    {
        // Include Host.Services (which includes Services)
        services.AddHomeBlazeHostServices();

        // MudBlazor services
        services.AddMudServices();

        // Every component renders again when a property that its render read changes.
        services.AddBlazorSubjectTracking();

        services.AddHttpContextAccessor();
        services.AddScoped<TimeZoneInterop>();
        services.AddScoped<BrowserPreferences>();
        services.AddScoped<CurrentClientConnection>();
        services.AddScoped<PropertyWriter>();

        // Value widgets open the history of their property through it.
        services.AddPropertyHistoryViewer();

        // TryAdd, so that a test host can register its own clock first.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Adds ASP.NET Core Data Protection with its keys in the data directory (see <see cref="HomeBlazePaths.GetDataProtectionKeysPath"/>),
    /// so antiforgery tokens, circuits and sign-ins stay valid across restarts. The keys are encrypted at rest with the certificate
    /// configured in <see cref="HomeBlazePaths.DataProtectionCertificatePathKey"/>, or with DPAPI on Windows when none is configured,
    /// and are stored unencrypted otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured certificate has no private key.</exception>
    public static IServiceCollection AddHomeBlazeDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName("HomeBlaze")
            .PersistKeysToFileSystem(new DirectoryInfo(HomeBlazePaths.GetDataProtectionKeysPath(configuration)));

        var certificatePath = HomeBlazePaths.GetDataProtectionCertificatePath(configuration);
        if (certificatePath is not null)
        {
            // Loaded here, not lazily, so a missing or unreadable certificate stops the start instead of
            // letting new keys be written unencrypted.
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath, configuration[HomeBlazePaths.DataProtectionCertificatePasswordKey]);

            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    $"The Data Protection certificate '{certificatePath}' has no private key, so the keys it encrypts could not be read back.");
            }

            builder.ProtectKeysWithCertificate(certificate);
        }
        else if (OperatingSystem.IsWindows())
        {
            // A custom key folder turns off the default DPAPI encryption at rest, which only Windows offers.
            builder.ProtectKeysWithDpapi();
        }

        return services;
    }
}
