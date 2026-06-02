using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors.TwinCAT;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Mapping;
using TwinCAT.Ads;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering TwinCAT ADS client sources in the dependency injection container.
/// </summary>
public static class TwinCatSubjectExtensions
{
    /// <summary>
    /// Registers a TwinCAT ADS client source for the specified subject type.
    /// The subject is resolved from the service provider using <see cref="ServiceProviderServiceExtensions.GetRequiredService{T}"/>.
    /// </summary>
    /// <typeparam name="TSubject">The subject type to synchronize with the PLC.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="host">The PLC host IP or hostname.</param>
    /// <param name="amsPort">The AMS port (default: 851 for TwinCAT3 PLC runtime).</param>
    /// <param name="amsNetId">The AMS Net ID. If null, defaults to <paramref name="host"/> + ".1.1".</param>
    /// <param name="connectorName">The connector name used for attribute-based symbol mapping (default: "ads").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTwinCatSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string host,
        int amsPort = 851,
        string? amsNetId = null,
        string connectorName = AdsConstants.DefaultConnectorName)
        where TSubject : IInterceptorSubject
    {
        return services.AddTwinCatSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            _ => new AdsClientConfiguration
            {
                Host = host,
                AmsNetId = AmsNetId.Parse(amsNetId ?? $"{host}.1.1"),
                AmsPort = amsPort,
                Mapper = AdsCompositeMapper.CreateDefault(connectorName)
            });
    }

    /// <summary>
    /// Registers a TwinCAT ADS client source with full configuration control.
    /// Uses the keyed-services pattern to support multiple registrations in the same container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="subjectSelector">A factory that resolves the root subject from the service provider.</param>
    /// <param name="configurationProvider">A factory that creates the ADS client configuration from the service provider.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTwinCatSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, AdsClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return services
            .AddKeyedSingleton(key, (serviceProvider, _) => configurationProvider(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new TwinCatSubjectClientSource(
                    subject,
                    serviceProvider.GetRequiredKeyedService<AdsClientConfiguration>(key),
                    serviceProvider.GetRequiredService<ILogger<TwinCatSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<TwinCatSubjectClientSource>(key));
    }
}
