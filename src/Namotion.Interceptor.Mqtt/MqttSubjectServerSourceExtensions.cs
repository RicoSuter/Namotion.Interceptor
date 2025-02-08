using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Sources;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttSubjectServerSourceExtensions
{
    public static IServiceCollection AddMqttSubjectServer<TSubject>(
        this IServiceCollection serviceCollection, string sourceName, string? pathPrefix = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection.AddMqttSubjectServer(sp => sp.GetRequiredService<TSubject>(), sourceName, pathPrefix);
    }
    
    public static IServiceCollection AddMqttSubjectServer<TSubject>(this IServiceCollection serviceCollection,
        Func<IServiceProvider, TSubject> subjectSelector, string sourceName, string? pathPrefix = null)
        where TSubject : IInterceptorSubject
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var subject = subjectSelector(sp);
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, "/", pathPrefix);
                return new MqttSubjectServerSource<TSubject>(
                    subject, sourcePathProvider,
                    sp.GetRequiredService<ILogger<MqttSubjectServerSource<TSubject>>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<MqttSubjectServerSource<TSubject>>())
            .AddSingleton<IHostedService>(sp =>
            {
                return new SubjectSourceBackgroundService<TSubject>(
                    sp.GetRequiredService<MqttSubjectServerSource<TSubject>>(),
                    sp.GetRequiredService<IInterceptorSubjectContext>(),
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService<TSubject>>>());
            });
    }
}
