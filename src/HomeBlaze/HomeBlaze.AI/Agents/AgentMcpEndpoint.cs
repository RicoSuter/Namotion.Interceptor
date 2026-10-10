using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The MCP route used by CLI agents. Each request must come from a loopback address and carry the bearer token
/// of a running turn; it then sees exactly that turn's tools.
/// </summary>
public static class AgentMcpEndpoint
{
    /// <summary>The route of the agent MCP endpoint.</summary>
    public const string Route = "/mcp/agents";

    private const string ToolSetItemKey = "HomeBlaze.AI.AgentToolSet";
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Maps the agent route and its token check. The MCP server must use the stateless HTTP transport with
    /// <see cref="ConfigureSessionOptions"/>, which <c>AddHomeBlazeAgents</c> configures.
    /// </summary>
    public static WebApplication MapHomeBlazeAgentMcp(this WebApplication app)
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(Route),
            branch => branch.Use(AuthenticateAsync));
        app.MapMcp(Route);
        return app;
    }

    /// <summary>
    /// Replaces the tool handlers of a request to the agent route with ones serving only the turn's tools.
    /// Stateless transport calls this per request with a fresh options copy, so other routes are unaffected.
    /// </summary>
    public static Task ConfigureSessionOptions(HttpContext context, McpServerOptions options, CancellationToken cancellationToken)
    {
        if (!context.Request.Path.StartsWithSegments(Route))
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<McpToolInfo> tools = context.Items[ToolSetItemKey] is AgentToolSet toolSet ? toolSet.Tools : [];
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(McpToolDispatcher.ListTools(tools));
        options.Handlers.CallToolHandler = (request, token) => McpToolDispatcher.CallToolAsync(tools, request.Params!, logger: null, token);
        return Task.CompletedTask;
    }

    private static async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        var registry = context.RequestServices.GetRequiredService<AgentTurnTokenRegistry>();
        var remoteAddress = context.Connection.RemoteIpAddress;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (remoteAddress is null ||
            !IPAddress.IsLoopback(remoteAddress) ||
            !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal) ||
            !registry.TryGetToolSet(authorization[BearerPrefix.Length..], out var toolSet))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[ToolSetItemKey] = toolSet;
        await next(context);
    }
}
