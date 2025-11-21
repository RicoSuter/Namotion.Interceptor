using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttConnectorExtensions
{
    public static IServiceCollection AddMqttServerConnector<TSubject>(
        this IServiceCollection serviceCollection, string connectorName, string? pathPrefix = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttServerConnector(sp => sp.GetRequiredService<TSubject>(), connectorName, pathPrefix);
    }

    public static IServiceCollection AddMqttServerConnector(this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector, string connectorName, string? pathPrefix = null)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                var attributeBasedConnectorPathProvider = new AttributeBasedConnectorPathProvider(connectorName, "/", pathPrefix);
                return new MqttServerConnector(
                    subject, attributeBasedConnectorPathProvider, sp.GetRequiredService<ILogger<MqttServerConnector>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttServerConnector>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectDownstreamConnectorBackgroundService(
                    sp.GetRequiredKeyedService<MqttServerConnector>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectConnectorBackgroundService>>());
            });
    }
}
