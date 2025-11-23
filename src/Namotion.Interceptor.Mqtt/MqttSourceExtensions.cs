using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Sources.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttSourceExtensions
{
    public static IServiceCollection AddMqttServer<TSubject>(
        this IServiceCollection serviceCollection, string connectorName, string? pathPrefix = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttServer(sp => sp.GetRequiredService<TSubject>(), connectorName, pathPrefix);
    }

    public static IServiceCollection AddMqttServer(this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector, string connectorName, string? pathPrefix = null)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                var attributeBasedConnectorPathProvider = new AttributeBasedSourcePathProvider(connectorName, "/", pathPrefix);
                return new MqttServerBackgroundService(
                    subject, attributeBasedConnectorPathProvider, sp.GetRequiredService<ILogger<MqttServerBackgroundService>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttServerBackgroundService>(key));
    }
}
