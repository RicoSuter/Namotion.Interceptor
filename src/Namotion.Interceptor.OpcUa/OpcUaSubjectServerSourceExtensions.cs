using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaSubjectServerSourceExtensions
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
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = subjectSelector(sp);
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix);
                return new OpcUaSubjectClientSource(
                    subject,
                    serverUrl,
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<OpcUaSubjectClientSource>>(),
                    rootName);
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                // TODO: Register only once and inject all sources?
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaSubjectClientSource>(key),
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>());
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
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = subjectSelector(sp);
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix);
                return new OpcUaSubjectServerSource(
                    subject,
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<OpcUaSubjectServerSource>>(),
                    rootName);
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<OpcUaSubjectServerSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<OpcUaSubjectServerSource>(key),
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>());
            });
    }
}
