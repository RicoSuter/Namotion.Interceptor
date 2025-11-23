using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Sources.Paths;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttSourceExtensions
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
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var subject = sp.GetRequiredKeyedService<IInterceptorSubject>(key);
                var pathProvider = new AttributeBasedSourcePathProvider(sourceName, "/", pathPrefix);
                return new MqttSubjectServerBackgroundService(
                    subject, pathProvider, sp.GetRequiredService<ILogger<MqttSubjectServerBackgroundService>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<MqttSubjectServerBackgroundService>(key));
    }
}
