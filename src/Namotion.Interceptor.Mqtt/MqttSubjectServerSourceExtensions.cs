using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mqtt;
using Namotion.Interceptor.Sources;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class MqttSubjectServerSourceExtensions
{
    public static IServiceCollection AddMqttSubjectServerSource<TProxy>(
        this IServiceCollection serviceCollection, string sourceName, string? pathPrefix = null)
        where TProxy : IInterceptorSubject
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var sourcePathProvider = new AttributeBasedSourcePathProvider(sourceName, "/", pathPrefix);
                return new MqttSubjectServerSource<TProxy>(
                    sp.GetRequiredService<TProxy>(),
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<MqttSubjectServerSource<TProxy>>>());
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<MqttSubjectServerSource<TProxy>>())
            .AddSingleton<IHostedService>(sp =>
            {
                return new SubjectSourceBackgroundService<TProxy>(
                    sp.GetRequiredService<MqttSubjectServerSource<TProxy>>(),
                    sp.GetRequiredService<IInterceptorSubjectContext>(),
                    sp.GetRequiredService<ILogger<SubjectSourceBackgroundService<TProxy>>>());
            });
    }
}
