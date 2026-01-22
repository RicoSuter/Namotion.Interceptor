using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            .AddKeyedSingleton(key, (_, _) =>
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

    /// <summary>
    /// Adds a WebSocket subject handler for embedding in an existing ASP.NET application.
    /// Call MapWebSocketSubject to map the endpoint after building the app.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectHandler<TSubject>(
        this IServiceCollection services,
        Action<WebSocketServerConfiguration>? configure = null)
        where TSubject : IInterceptorSubject
    {
        return services.AddWebSocketSubjectHandler(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            configure);
    }

    /// <summary>
    /// Adds a WebSocket subject handler with custom subject selector.
    /// Call MapWebSocketSubject to map the endpoint after building the app.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectHandler(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Action<WebSocketServerConfiguration>? configure = null)
    {
        return services
            .AddSingleton(serviceProvider =>
            {
                var configuration = new WebSocketServerConfiguration();
                configure?.Invoke(configuration);
                return configuration;
            })
            .AddSingleton(serviceProvider =>
            {
                var subject = subjectSelector(serviceProvider);
                var configuration = serviceProvider.GetRequiredService<WebSocketServerConfiguration>();
                return new WebSocketSubjectHandler(
                    subject,
                    configuration,
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectHandler>>());
            })
            .AddSingleton<IHostedService, WebSocketSubjectChangeProcessor>();
    }

    /// <summary>
    /// Maps a WebSocket subject endpoint into an existing ASP.NET application.
    /// Requires AddWebSocketSubjectHandler to be called during service registration.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The path to map the WebSocket endpoint to. Default: "/ws"</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    /// <example>
    /// <code>
    /// // In Program.cs:
    /// builder.Services.AddWebSocketSubjectHandler&lt;Device&gt;();
    ///
    /// var app = builder.Build();
    /// app.UseWebSockets();
    /// app.MapWebSocketSubject("/ws");
    /// app.Run();
    /// </code>
    /// </example>
    public static IEndpointRouteBuilder MapWebSocketSubject(
        this IEndpointRouteBuilder endpoints,
        string path = "/ws")
    {
        endpoints.Map(path, async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var handler = context.RequestServices.GetRequiredService<WebSocketSubjectHandler>();
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await handler.HandleClientAsync(webSocket, context.RequestAborted);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        return endpoints;
    }
}
