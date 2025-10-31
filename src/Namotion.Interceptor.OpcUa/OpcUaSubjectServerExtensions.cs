using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.OpcUa;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaSubjectServerExtensions
{
    public static IServiceCollection AddOpcUaSubjectClient<TSubject>(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectClient(
            serverUrl,
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaSubjectClient(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaSubjectClient(subjectSelector, sp => new OpcUaClientConfiguration
        {
            ServerUrl = serverUrl,
            RootName = rootName,
            SourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix),
            TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        });
    }

    public static IServiceCollection AddOpcUaSubjectClient(
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
                return new OpcUaSubjectClientSource(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaClientConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime);
            });
    }

    public static IServiceCollection AddOpcUaSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectServer(
            sourceName,
            sp => sp.GetRequiredService<TSubject>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaSubjectServer(
        this IServiceCollection serviceCollection,
        string sourceName,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
    {
        return serviceCollection.AddOpcUaSubjectServer(subjectSelector, _ => new OpcUaServerConfiguration
        {
            RootName = rootName,
            SourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix),
            ValueConverter = new OpcUaValueConverter()
        });
    }

    public static IServiceCollection AddOpcUaSubjectServer(
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
                return new OpcUaSubjectServerSource(
                    subject,
                    sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key),
                    sp.GetRequiredService<ILogger<OpcUaSubjectServerSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectServerSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var configuration = sp.GetRequiredKeyedService<OpcUaServerConfiguration>(key);
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaSubjectServerSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime);
            });
    }
}
