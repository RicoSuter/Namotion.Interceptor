using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Serves the MCP route of CLI agents on a web server of its own, bound to 127.0.0.1 on a port the operating system
/// assigns, so the route is not reachable through the bindings, proxies or published ports of the main web server.
/// Requests are handled by <see cref="AgentMcpApplication"/>. When the listener cannot start, it logs an error and has no
/// address, so CLI agent turns fail while the rest of the host runs.
/// </summary>
internal sealed class AgentMcpListener : IHostedService, IAgentEndpointAddress, IAsyncDisposable, IDisposable
{
    /// <summary>The route of the agent MCP endpoint.</summary>
    internal const string Route = "/mcp/agents";

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

    /// <summary>The loopback port to bind, 0 for one the operating system assigns.</summary>
    internal int Port { get; init; }

    /// <summary>The addresses the listener is bound to, empty until it started.</summary>
    internal IReadOnlyCollection<string> BoundAddresses { get; private set; } = [];

    /// <inheritdoc />
    public Uri? GetAgentEndpoint() => _endpoint;

    /// <exception cref="InvalidOperationException">The listener was already started.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_application is not null)
        {
            throw new InvalidOperationException("The agent MCP listener was already started.");
        }

        var application = AgentMcpApplication.Create(_tokenRegistry, _loggerFactory, Port);
        Uri endpoint;
        try
        {
            await application.StartAsync(cancellationToken);
            var boundAddresses = application.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.ToArray();
            endpoint = new Uri(new Uri(boundAddresses.Single()), Route);
            BoundAddresses = boundAddresses;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await application.DisposeAsync();
            _logger.LogError(exception, "The agent MCP listener could not start on 127.0.0.1, so CLI agents cannot run.");
            return;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }

        _application = application;
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

    public void Dispose()
    {
        _endpoint = null;
        if (Interlocked.Exchange(ref _application, null) is { } application)
        {
            ((IDisposable)application).Dispose();
        }
    }
}
