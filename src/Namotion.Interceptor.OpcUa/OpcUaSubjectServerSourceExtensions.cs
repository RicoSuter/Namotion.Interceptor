using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Sources;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaSubjectServerSourceExtensions
{
    public static IServiceCollection AddOpcUaSubjectServer<TProxy>(
        this IServiceCollection serviceCollection,
        string sourceName,
        string? pathPrefix = null,
        string? rootName = null)
        where TProxy : IInterceptorSubject
    {
        return serviceCollection.AddOpcUaSubjectServer(
            sourceName,
            sp => sp.GetRequiredService<TProxy>(),
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
                var subject = subjectSelector(sp);
                return new SubjectSourceBackgroundService<TSubject>(
                    subject,
                    sp.GetRequiredService<OpcUaSubjectServerSource<TSubject>>(),
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
