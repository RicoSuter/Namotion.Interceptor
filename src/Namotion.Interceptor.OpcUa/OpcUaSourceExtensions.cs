using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaSourceExtensions
{
    public static IServiceCollection AddOpcUaClientSource<TSubject>(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaClientSource(
            serverUrl,
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaClientSource(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaClientSource(subjectSelector, sp => new OpcUaClientConfiguration
        {
            ServerUrl = serverUrl,
            RootName = rootName,
            PathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix),
            TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        });
    }

    public static IServiceCollection AddOpcUaClientSource(
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
                return new OpcUaClientSource(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaClientSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaClientSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaClientSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }

    public static IServiceCollection AddOpcUaServer<TSubject>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaServer(
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaServer(
        this IServiceCollection serviceCollection,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaServer(subjectSelector, _ => new OpcUaServerConfiguration
        {
            RootName = rootName,
            PathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix),
            ValueConverter = new OpcUaValueConverter()
        });
    }

    public static IServiceCollection AddOpcUaServer(
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
                return new OpcUaServerBackgroundService(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaServerBackgroundService>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaServerBackgroundService>(key));
    }
}
