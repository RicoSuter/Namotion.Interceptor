using Microsoft.Extensions.Logging;
using Namotion.Trackable.Sources;
using Namotion.Trackable.Mqtt;
using Namotion.Proxy.Sources;
using Namotion.Proxy;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttServerTrackableContextSourceExtensions
{
    public static IServiceCollection AddMqttServerTrackableSource<TTrackable>(
        this IServiceCollection serviceCollection, string sourceName, string? pathPrefix = null)
        where TTrackable : class
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var sourcePathProvider = new AttributeBasedSourcePathProvider(
                    sourceName, sp.GetRequiredService<IProxyContext>(), pathPrefix);

                return new MqttServerTrackableSource<TTrackable>(
                    sp.GetRequiredService<IProxyContext>(),
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<MqttServerTrackableSource<TTrackable>>>());
            })
            .AddHostedService(sp => sp.GetRequiredService<MqttServerTrackableSource<TTrackable>>())
            .AddHostedService(sp =>
            {
                return new TrackableContextSourceBackgroundService<TTrackable>(
                    sp.GetRequiredService<MqttServerTrackableSource<TTrackable>>(),
                    sp.GetRequiredService<IProxyContext>(),
                    sp.GetRequiredService<ILogger<TrackableContextSourceBackgroundService<TTrackable>>>());
            });
    }
}
