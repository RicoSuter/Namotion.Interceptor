using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttSubjectExtensions
{
    /// <summary>
    /// Adds an MQTT client source that subscribes to an MQTT broker and synchronizes properties.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="brokerHost">The MQTT broker hostname.</param>
    /// <param name="connectorName">The name to filter [Path] attributes by (e.g., "mqtt").</param>
    /// <param name="brokerPort">The MQTT broker port. Default is 1883.</param>
    /// <param name="topicPrefix">Optional topic prefix.</param>
    /// <param name="configureFluent">Optional fluent mapping configuration layered after attribute mappers.</param>
    public static IServiceCollection AddMqttSubjectClientSource<TSubject>(
        this IServiceCollection serviceCollection,
        string brokerHost,
        string connectorName,
        int brokerPort = 1883,
        string? topicPrefix = null,
        Action<MqttFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectClientSource(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new MqttClientConfiguration
            {
                BrokerHost = brokerHost,
                BrokerPort = brokerPort,
                TopicPrefix = topicPrefix,
                Mapper = BuildMqttMapper(connectorName, configureFluent)
            });
    }

    /// <summary>
    /// Adds an MQTT client source with custom configuration.
    /// </summary>
    public static IServiceCollection AddMqttSubjectClientSource(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, MqttClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new MqttSubjectClientSource(
                    subject,
                    sp.GetRequiredKeyedService<MqttClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<MqttSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttSubjectClientSource>(key));
    }

    /// <summary>
    /// Adds an MQTT server that publishes property changes to connected MQTT clients.
    /// </summary>
    /// <param name="serviceCollection">The service collection.</param>
    /// <param name="connectorName">The name to filter [Path] attributes by (e.g., "mqtt").</param>
    /// <param name="brokerPort">The port to listen on. Default is 1883.</param>
    /// <param name="brokerHost">Optional IP address to bind to. Default binds to all interfaces.</param>
    /// <param name="topicPrefix">Optional topic prefix.</param>
    /// <param name="configureFluent">Optional fluent mapping configuration layered after attribute mappers.</param>
    public static IServiceCollection AddMqttSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string connectorName,
        int brokerPort = 1883,
        string? brokerHost = null,
        string? topicPrefix = null,
        Action<MqttFluentMapping<TSubject>>? configureFluent = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectServer(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new MqttServerConfiguration
            {
                BrokerHost = brokerHost,
                BrokerPort = brokerPort,
                TopicPrefix = topicPrefix,
                Mapper = BuildMqttMapper(connectorName, configureFluent)
            });
    }

    /// <summary>
    /// Adds an MQTT server with custom configuration.
    /// </summary>
    public static IServiceCollection AddMqttSubjectServer(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, MqttServerConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new MqttSubjectServer(
                    subject,
                    sp.GetRequiredKeyedService<MqttServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<MqttSubjectServer>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttSubjectServer>(key));
    }

    private static MqttCompositeMapper BuildMqttMapper<TSubject>(
        string connectorName,
        Action<MqttFluentMapping<TSubject>>? configureFluent)
        where TSubject : IInterceptorSubject
    {
        var mappers = new List<IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>>
        {
            new MqttPathProviderMapper(new AttributeBasedPathProvider(connectorName, '/')),
            new MqttAttributeMapper(connectorName)
        };

        if (configureFluent is not null)
        {
            var fluent = new MqttFluentMapping<TSubject>();
            configureFluent(fluent);
            // Fluent is layered after attributes so it wins on conflicts; omit configureFluent for attribute-only.
            mappers.AddRange(fluent.CreateMappers('/'));
        }

        return new MqttCompositeMapper(mappers.ToArray());
    }
}
