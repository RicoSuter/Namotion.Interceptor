using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Serves the MCP route of CLI agents on a web server of its own, bound to 127.0.0.1 on a port the operating system
/// assigns, so the route is not reachable through the bindings, proxies or published ports of the main web server.
/// Each request must carry the bearer token of a running turn and then sees exactly that turn's tools.
/// </summary>
internal sealed class AgentMcpListener : IHostedService, IAgentEndpointAddress, IAsyncDisposable
{
    /// <summary>The route of the agent MCP endpoint.</summary>
    internal const string Route = "/mcp/agents";

    private const string BearerScheme = "Bearer ";

    private static readonly object ToolSetItemKey = new();

    private readonly AgentTurnTokenRegistry _tokenRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentMcpListener> _logger;

    private WebApplication? _application;
    private volatile Uri? _endpoint;

    public AgentMcpListener(AgentTurnTokenRegistry tokenRegistry, ILoggerFactory loggerFactory)
    {
        _tokenRegistry = tokenRegistry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentMcpListener>();
    }

    /// <summary>The addresses the listener is bound to, empty until it started.</summary>
    internal IReadOnlyCollection<string> BoundAddresses { get; private set; } = [];

    /// <inheritdoc />
    public Uri? GetAgentEndpoint() => _endpoint;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The empty builder reads no configuration, so Kestrel settings and URLs meant for the main server do not apply.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<ILoggerFactory>(new WithoutLifetimeLoggerFactory(_loggerFactory));
        // The main host owns the process signals; a console lifetime here would handle them a second time.
        builder.Services.AddSingleton<IHostLifetime, OwnedLifetime>();
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            // Stateless, so the session options are configured per request with the tool set of its token.
            options.Stateless = true;
            options.ConfigureSessionOptions = ConfigureSessionOptions;
        });

        var application = builder.Build();
        application.Use(AuthenticateAsync);
        application.MapMcp(Route);
        try
        {
            await application.StartAsync(cancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }

        _application = application;

        BoundAddresses = application.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.ToArray();
        var endpoint = new Uri(new Uri(BoundAddresses.Single()), Route);
        _endpoint = endpoint;
        _logger.LogInformation("The agent MCP listener for CLI agents serves {Endpoint}.", endpoint);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _endpoint = null;
        if (_application is { } application)
        {
            await application.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _endpoint = null;
        // The container disposes this instance once per registration that resolves it.
        if (Interlocked.Exchange(ref _application, null) is { } application)
        {
            await application.DisposeAsync();
        }
    }

    private Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) ||
            !_tokenRegistry.TryGetToolSet(authorization[BearerScheme.Length..], out var toolSet))
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
