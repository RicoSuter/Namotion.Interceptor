using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The MCP route used by CLI agents. Each request must come from the same host and carry the bearer token
/// of a running turn; it then sees exactly that turn's tools.
/// </summary>
public static class AgentMcpEndpoint
{
    /// <summary>The route of the agent MCP endpoint.</summary>
    public const string Route = "/mcp/agents";

    private const string BearerScheme = "Bearer ";

    private static readonly object ToolSetItemKey = new();

    /// <summary>
    /// Maps the agent route and its token check. The services must be registered with
    /// <see cref="AgentServiceCollectionExtensions.AddHomeBlazeAgents"/>, which hooks
    /// <see cref="ConfigureSessionOptions"/> into the MCP HTTP transport, and the transport must be stateless.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AgentServiceCollectionExtensions.AddHomeBlazeAgents"/> was not called, or the HTTP transport is not
    /// stateless.
    /// </exception>
    public static WebApplication MapHomeBlazeAgentMcp(this WebApplication app)
    {
        if (app.Services.GetService<AgentServicesMarker>() is null)
        {
            throw new InvalidOperationException(
                $"The agent MCP route requires the agent services; call {nameof(AgentServiceCollectionExtensions.AddHomeBlazeAgents)} on the service collection.");
        }

        var transportOptions = app.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
        if (!transportOptions.Stateless)
        {
            throw new InvalidOperationException(
                "The agent MCP route requires the MCP HTTP transport to be stateless; otherwise requests to the route would see the global tools.");
        }

        app.Use(AuthenticateAsync);
        app.MapMcp(Route).WithMetadata(AgentRouteMarker.Instance);
        return app;
    }

    /// <summary>
    /// Replaces the tools of a request to the agent route with only the turn's tools and removes all prompts,
    /// resources and completions. Stateless transport calls this per request with a fresh options copy, so other
    /// routes are unaffected.
    /// </summary>
    public static Task ConfigureSessionOptions(HttpContext context, McpServerOptions options, CancellationToken cancellationToken)
    {
        if (!IsAgentRoute(context))
        {
            return Task.CompletedTask;
        }

        // A request that bypassed the gate carries no tool set and gets no tools.
        IReadOnlyList<McpToolInfo> tools = context.Items[ToolSetItemKey] is AgentToolSet toolSet ? toolSet.Tools : [];
        options.ToolCollection = null;
        options.PromptCollection = null;
        options.ResourceCollection = null;

        var handlers = options.Handlers;
        handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(McpToolDispatcher.ListTools(tools));
        handlers.CallToolHandler = (request, token) => McpToolDispatcher.CallToolAsync(tools, request.Params!, logger: null, token);
        handlers.ListPromptsHandler = null;
        handlers.GetPromptHandler = null;
        handlers.ListResourcesHandler = null;
        handlers.ListResourceTemplatesHandler = null;
        handlers.ReadResourceHandler = null;
        handlers.SubscribeToResourcesHandler = null;
        handlers.UnsubscribeFromResourcesHandler = null;
        handlers.CompleteHandler = null;
        return Task.CompletedTask;
    }

    private static Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsAgentRoute(context))
        {
            return next(context);
        }

        var registry = context.RequestServices.GetRequiredService<AgentTurnTokenRegistry>();
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!IsSameHost(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress) ||
            !authorization.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase) ||
            !registry.TryGetToolSet(authorization[BearerScheme.Length..], out var toolSet))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }

        context.Items[ToolSetItemKey] = toolSet;
        return next(context);
    }

    private static bool IsSameHost(IPAddress? remoteAddress, IPAddress? localAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        remoteAddress = Normalize(remoteAddress);
        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        // A local client of a server bound only to a LAN address connects from that address. An off-host peer
        // cannot complete a TCP handshake with the server's own address as source, so this does not open the route.
        return localAddress is not null && remoteAddress.Equals(Normalize(localAddress));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsAgentRoute(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<AgentRouteMarker>() is not null;

    private sealed class AgentRouteMarker
    {
        public static readonly AgentRouteMarker Instance = new();
    }
}
