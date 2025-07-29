using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

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

    public static IServiceCollection AddMqttSubjectServer(this IServiceCollection serviceCollection,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector, string sourceName, string? pathPrefix = null)
    {
        var key = Guid.NewGuid().ToString();
        return serviceCollection
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = subjectSelector(sp);
                var attributeBasedSourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, "/", pathPrefix);
                return new MqttSubjectServerSource(
                    subject, attributeBasedSourcePathProvider, sp.GetRequiredService<ILogger<MqttSubjectServerSource>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttSubjectServerSource>(key))
            .AddSingleton<IHostedService>(sp =>
            {
                var subject = subjectSelector(sp);
                return new SubjectSourceBackgroundService(
                    sp.GetRequiredKeyedService<MqttSubjectServerSource>(key),
                    subject.Context,
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>());
            });
    }
}