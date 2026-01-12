using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Server;

namespace Namotion.Interceptor.WebSocket;

public static class WebSocketSubjectExtensions
{
    /// <summary>
    /// Adds a WebSocket subject server that exposes subjects to connected clients.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectServer<TSubject>(
        this IServiceCollection services,
        Action<WebSocketServerConfiguration> configure)
        where TSubject : IInterceptorSubject
    {
        return services.AddWebSocketSubjectServer(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            configure);
    }

    /// <summary>
    /// Adds a WebSocket subject server with custom subject selector.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectServer(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Action<WebSocketServerConfiguration> configure)
    {
        var key = Guid.NewGuid().ToString();

        return services
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var configuration = new WebSocketServerConfiguration();
                configure(configuration);
                return configuration;
            })
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new WebSocketSubjectServer(
                    subject,
                    serviceProvider.GetRequiredKeyedService<WebSocketServerConfiguration>(key),
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectServer>>());
            })
            .AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredKeyedService<WebSocketSubjectServer>(key));
    }

    /// <summary>
    /// Adds a WebSocket subject client source that connects to a server and synchronizes subjects.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectClientSource<TSubject>(
        this IServiceCollection services,
        Action<WebSocketClientConfiguration> configure)
        where TSubject : IInterceptorSubject
    {
        return services.AddWebSocketSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            configure);
    }

    /// <summary>
    /// Adds a WebSocket subject client source with custom subject selector.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Action<WebSocketClientConfiguration> configure)
    {
        var key = Guid.NewGuid().ToString();

        return services
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var configuration = new WebSocketClientConfiguration();
                configure(configuration);
                return configuration;
            })
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new WebSocketSubjectClientSource(
                    subject,
                    serviceProvider.GetRequiredKeyedService<WebSocketClientConfiguration>(key),
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredKeyedService<WebSocketSubjectClientSource>(key))
            .AddSingleton<IHostedService>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredKeyedService<WebSocketClientConfiguration>(key);
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    serviceProvider.GetRequiredKeyedService<WebSocketSubjectClientSource>(key),
                    subject.Context,
                    serviceProvider.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }
}
