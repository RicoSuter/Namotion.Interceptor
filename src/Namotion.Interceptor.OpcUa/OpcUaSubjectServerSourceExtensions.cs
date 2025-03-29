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
    
    public static IServiceCollection AddOpcUaSubjectClient<TSubject>(
        this IServiceCollection serviceCollection,
        string serverUrl,
        string sourceName,
        Func<IServiceProvider, TSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var subject = subjectSelector(sp);
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix);
                return new OpcUaSubjectClientSource<TSubject>(
                    subject,
                    serverUrl,
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<OpcUaSubjectClientSource<TSubject>>>(),
                    rootName);
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<OpcUaSubjectClientSource<TSubject>>())
            .AddSingleton<IHostedService>(sp =>
            {
                // TODO: Register only once and inject all sources?
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredService<OpcUaSubjectClientSource<TSubject>>(),
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

    public static IServiceCollection AddOpcUaSubjectServer<TSubject>(
        this IServiceCollection serviceCollection,
        string sourceName,
        Func<IServiceProvider, TSubject> subjectSelector,
        string? pathPrefix = null,
        string? rootName = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var subject = subjectSelector(sp);
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, ".", pathPrefix);
                return new OpcUaSubjectServerSource<TSubject>(
                    subject,
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<OpcUaSubjectServerSource<TSubject>>>(),
                    rootName);
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<OpcUaSubjectServerSource<TSubject>>())
            .AddSingleton<IHostedService>(sp =>
            {
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredService<OpcUaSubjectServerSource<TSubject>>(),
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>());
            });
    }
}
