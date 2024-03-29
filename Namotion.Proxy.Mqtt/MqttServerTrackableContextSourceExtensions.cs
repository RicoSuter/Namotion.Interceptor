using Microsoft.Extensions.Logging;
using Namotion.Trackable.Sources;
using Namotion.Trackable.Mqtt;
using Namotion.Proxy.Sources;
using Namotion.Proxy;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttServerTrackableContextSourceExtensions
{
    public static IServiceCollection AddMqttServerProxySource<TProxy>(
        this IServiceCollection serviceCollection, string sourceName, string? pathPrefix = null)
        where TProxy : IProxy
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var sourcePathProvider = new AttributeBasedSourcePathProvider(
                    sourceName, sp.GetRequiredService<IProxyContext>(), pathPrefix);

                return new MqttServerTrackableSource<TProxy>(
                    sp.GetRequiredService<IProxyContext>(),
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<MqttServerTrackableSource<TProxy>>>());
            })
            .AddHostedService(sp => sp.GetRequiredService<MqttServerTrackableSource<TProxy>>())
            .AddHostedService(sp =>
            {
                return new TrackableContextSourceBackgroundService<TProxy>(
                    sp.GetRequiredService<MqttServerTrackableSource<TProxy>>(),
                    sp.GetRequiredService<IProxyContext>(),
                    sp.GetRequiredService<ILogger<TrackableContextSourceBackgroundService<TProxy>>>());
            });
    }
}
