using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaConnectorExtensions
{
    public static IServiceCollection AddOpcUaClientConnector<TSubject>(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaClientConnector(
            serverUrl,
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaClientConnector(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaClientConnector(subjectSelector, sp => new OpcUaClientConfiguration
        {
            ServerUrl = serverUrl,
            RootName = rootName,
            ConnectorPathProvider = new AttributeBasedConnectorPathProvider(sourceName, ".", pathPrefix),
            TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        });
    }

    public static IServiceCollection AddOpcUaClientConnector(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaClientConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaClientConnector(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaClientConnector>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaClientConnector>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectUpstreamConnectorBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaClientConnector>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectConnectorBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }

    public static IServiceCollection AddOpcUaServerConnector<TSubject>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaServerConnector(
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaServerConnector(
        this IServiceCollection serviceCollection,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaServerConnector(subjectSelector, _ => new OpcUaServerConfiguration
        {
            RootName = rootName,
            ConnectorPathProvider = new AttributeBasedConnectorPathProvider(sourceName, ".", pathPrefix),
            ValueConverter = new OpcUaValueConverter()
        });
    }

    public static IServiceCollection AddOpcUaServerConnector(
        this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, OpcUaServerConfiguration> configurationProvider)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new OpcUaServerConnector(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaServerConnector>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaServerConnector>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectDownstreamConnectorBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaServerConnector>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectConnectorBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime);
            });
    }
}
