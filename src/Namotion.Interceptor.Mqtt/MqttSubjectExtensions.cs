using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttSubjectExtensions
{
    /// <summary>
    /// Adds an MQTT client source that subscribes to an MQTT broker and synchronizes properties.
    /// </summary>
    public static IServiceCollection AddMqttSubjectClient<TSubject>(
        this IServiceCollection serviceCollection,
        string brokerHost,
        string sourceName,
        int brokerPort = 1883,
        string? topicPrefix = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectClient(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new MqttClientConfiguration
            {
                BrokerHost = brokerHost,
                BrokerPort = brokerPort,
                TopicPrefix = topicPrefix,
                PathProvider = new AttributeBasedSourcePathProvider(sourceName, "/", null)
            });
    }

    /// <summary>
    /// Adds an MQTT client source with custom configuration.
    /// </summary>
    public static IServiceCollection AddMqttSubjectClient(
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
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<MqttClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<MqttSubjectClientSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }

    /// <summary>
    /// Adds an MQTT server that publishes property changes to an MQTT broker.
    /// </summary>
    public static IServiceCollection AddMqttSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string brokerHost,
        string sourceName,
        int brokerPort = 1883,
        string? topicPrefix = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectServer(
            sp => sp.GetRequiredService<TSubject>(),
            sp => new MqttServerConfiguration
            {
                BrokerHost = brokerHost,
                BrokerPort = brokerPort,
                TopicPrefix = topicPrefix,
                PathProvider = new AttributeBasedSourcePathProvider(sourceName, "/", null)
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
                return new MqttSubjectServerBackgroundService(
                    subject,
                    sp.GetRequiredKeyedService<MqttServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<MqttSubjectServerBackgroundService>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttSubjectServerBackgroundService>(key));
    }
}
