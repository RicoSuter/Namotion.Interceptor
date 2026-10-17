using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Builds the web application of <see cref="AgentMcpListener"/>: the agent route alone on 127.0.0.1, where each request
/// must carry the bearer token of a running turn and then sees exactly that turn's tools.
/// </summary>
internal static class AgentMcpApplication
{
    private const string BearerScheme = "Bearer ";

    private static readonly object ToolSetItemKey = new();

    /// <summary>Builds the application, not yet started.</summary>
    /// <param name="tokenRegistry">Resolves the tool set of a request's token.</param>
    /// <param name="loggerFactory">The logger factory of the main host, which keeps owning it.</param>
    /// <param name="port">The loopback port, 0 for one the operating system assigns.</param>
    public static WebApplication Create(AgentTurnTokenRegistry tokenRegistry, ILoggerFactory loggerFactory, int port)
    {
        // The empty builder reads no configuration, so Kestrel settings and URLs meant for the main server do not apply.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<ILoggerFactory>(new WithoutLifetimeLoggerFactory(loggerFactory));
        // The main host owns the process signals; a console lifetime here would handle them a second time.
        builder.Services.AddSingleton<IHostLifetime, OwnedLifetime>();
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            // Stateless, so the session options are configured per request with the tool set of its token.
            options.Stateless = true;
            options.ConfigureSessionOptions = ConfigureSessionOptions;
        });

        var application = builder.Build();
        application.Use((context, next) => AuthenticateAsync(tokenRegistry, context, next));
        application.MapMcp(AgentMcpListener.Route);
        return application;
    }

    private static Task AuthenticateAsync(AgentTurnTokenRegistry tokenRegistry, HttpContext context, RequestDelegate next)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) ||
            !tokenRegistry.TryGetToolSet(authorization[BearerScheme.Length..], out var toolSet))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }

        context.Items[ToolSetItemKey] = toolSet;
        return next(context);
    }

    private static Task ConfigureSessionOptions(HttpContext context, McpServerOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<McpToolInfo> tools = context.Items[ToolSetItemKey] is AgentToolSet toolSet ? toolSet.Tools : [];
        var handlers = options.Handlers;
        handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(McpToolDispatcher.ListTools(tools));
        handlers.CallToolHandler = (request, token) => McpToolDispatcher.CallToolAsync(tools, request.Params, logger: null, token);
        return Task.CompletedTask;
    }

    // The inner host would log "Now listening on" with its loopback address, which reads like the address of the UI.
    private sealed class WithoutLifetimeLoggerFactory(ILoggerFactory loggerFactory) : ILoggerFactory
    {
        private const string LifetimeCategory = "Microsoft.Hosting.Lifetime";

        public ILogger CreateLogger(string categoryName) =>
            categoryName == LifetimeCategory ? NullLogger.Instance : loggerFactory.CreateLogger(categoryName);

        public void AddProvider(ILoggerProvider provider) => loggerFactory.AddProvider(provider);

        public void Dispose()
        {
            // The main host owns the wrapped factory.
        }
    }

    private sealed class OwnedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
