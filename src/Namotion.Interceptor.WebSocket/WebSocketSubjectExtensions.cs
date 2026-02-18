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
                configuration.Validate();
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
    /// The path is used as both the endpoint route and the keyed service identifier,
    /// allowing multiple handlers to be registered on different paths.
    /// Call MapWebSocketSubjectHandler with the same path to map the endpoint after building the app.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectHandler<TSubject>(
        this IServiceCollection services,
        string path,
        Action<WebSocketServerConfiguration>? configure = null)
        where TSubject : IInterceptorSubject
    {
        return services.AddWebSocketSubjectHandler(
            path,
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            configure);
    }

    /// <summary>
    /// Adds a WebSocket subject handler with custom subject selector.
    /// The path is used as both the endpoint route and the keyed service identifier,
    /// allowing multiple handlers to be registered on different paths.
    /// Call MapWebSocketSubjectHandler with the same path to map the endpoint after building the app.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectHandler(
        this IServiceCollection services,
        string path,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Action<WebSocketServerConfiguration>? configure = null)
    {
        return services
            .AddKeyedSingleton(path, (serviceProvider, _) =>
            {
                var configuration = new WebSocketServerConfiguration();
                configure?.Invoke(configuration);
                configuration.Validate();
                return configuration;
            })
            .AddKeyedSingleton(path, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(path, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(path);
                return new WebSocketSubjectHandler(
                    subject,
                    serviceProvider.GetRequiredKeyedService<WebSocketServerConfiguration>(path),
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectHandler>>());
            })
            .AddSingleton<IHostedService>(serviceProvider =>
            {
                var handler = serviceProvider.GetRequiredKeyedService<WebSocketSubjectHandler>(path);
                return new WebSocketSubjectChangeProcessor(
                    handler,
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectChangeProcessor>>());
            });
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
    /// builder.Services.AddWebSocketSubjectHandler&lt;Device&gt;("/ws");
    ///
    /// var app = builder.Build();
    /// app.UseWebSockets();
    /// app.MapWebSocketSubjectHandler("/ws");
    /// app.Run();
    /// </code>
    /// </example>
    public static IEndpointRouteBuilder MapWebSocketSubjectHandler(
        this IEndpointRouteBuilder endpoints,
        string path = "/ws")
    {
        endpoints.Map(path, async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var handler = context.RequestServices.GetRequiredKeyedService<WebSocketSubjectHandler>(path);
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
