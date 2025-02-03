using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Proxy.Sources;
using Namotion.Proxy.OpcUa.Server;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaProxyExtensions
{
    public static IServiceCollection AddOpcUaServerProxy<TProxy>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TProxy : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaServerProxy(
            sourceName,
            sp => sp.GetRequiredService<TProxy>(),
            pathPrefix,
            rootName);
    }

    public static IServiceCollection AddOpcUaServerProxy<TSubject>(
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
                var proxy = subjectSelector(sp);
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, pathPrefix);
                return new OpcUaServerSubjectSource<TSubject>(
                    proxy,
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<OpcUaServerSubjectSource<TSubject>>>(),
                    rootName);
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<OpcUaServerSubjectSource<TSubject>>())
            .AddSingleton<IHostedService>(sp =>
            {
                var proxy = subjectSelector(sp);
                return new SubjectSourceBackgroundService<TSubject>(
                    sp.GetRequiredService<OpcUaServerSubjectSource<TSubject>>(),
                    proxy.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService<TSubject>>>());
            });
    }

    //public static IServiceCollection AddOpcUaClientProxySource<TProxy>(
    //    this IServiceCollection serviceCollection,
    //    string sourceName,
    //    string serverUrl,
    //    string? pathPrefix = null,
    //    string? rootName = null)
    //    where TProxy : IInterceptorCollection
    //{
    //    return serviceCollection.AddOpcUaClientProxySource(
    //        sourceName,
    //        serverUrl,
    //        sp => sp.GetRequiredService<TProxy>(),
    //        pathPrefix,
    //        rootName);
    //}

    //public static IServiceCollection AddOpcUaClientProxySource<TProxy>(
    //    this IServiceCollection serviceCollection,
    //    string sourceName,
    //    string serverUrl,
    //    Func<IServiceProvider, TProxy> resolveProxy,
    //    string? pathPrefix = null,
    //    string? rootName = null)
    //    where TProxy : IInterceptorCollection
    //{
    //    return serviceCollection
    //        .AddSingleton(sp =>
    //        {
    //            var interceptable = resolveProxy(sp);
    //            var context = interceptable.Context ??
    //                throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

    //            var sourcePathProvider = new AttributeBasedSourcePathProvider(
    //                sourceName, context, pathPrefix);

    //            return new OpcUaClientTrackableSource<TProxy>(
    //                interceptable,
    //                serverUrl,
    //                sourcePathProvider,
    //                sp.GetRequiredService<ILogger<OpcUaClientTrackableSource<TProxy>>>(),
    //                rootName);
    //        })
    //        .AddSingleton<IHostedService>(sp => sp.GetRequiredService<OpcUaClientTrackableSource<TProxy>>())
    //        .AddSingleton<IHostedService>(sp =>
    //        {
    //            var interceptable = resolveProxy(sp);
    //            var context = interceptable.Context ??
    //                throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

    //            return new SubjectSourceBackgroundService<TProxy>(
    //                sp.GetRequiredService<OpcUaClientTrackableSource<TProxy>>(),
    //                context,
    //                sp.GetRequiredService<ILogger<SubjectSourceBackgroundService<TProxy>>>());
    //        });
    //}
}
