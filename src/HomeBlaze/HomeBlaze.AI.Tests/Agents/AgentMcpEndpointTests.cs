using System.Net;
using System.Text;
using System.Text.Json;
using HomeBlaze.AI.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public sealed class AgentMcpEndpointTests : IAsyncLifetime
{
    private const string ExternalRoute = "/mcp";
    private const string RemoteAddressTestHeader = "X-Test-Remote-Address";

    private WebApplication? _app;
    private string _baseAddress = string.Empty;
    private readonly AgentTurnTokenRegistry _registry = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_registry);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = AgentMcpEndpoint.ConfigureSessionOptions;
            })
            // Production always has global handlers (the external MCP server); mirror that.
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool { Name = "global_tool", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
            }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [] }))
            .WithTools([McpServerTool.Create(() => "registered", new McpServerToolCreateOptions { Name = "registered_tool" })])
            .WithPrompts([McpServerPrompt.Create(() => "registered", new McpServerPromptCreateOptions { Name = "registered_prompt" })])
            .WithResources([McpServerResource.Create(() => "registered", new McpServerResourceCreateOptions { Name = "registered_resource", UriTemplate = "test://registered" })]);

        _app = builder.Build();

        // Simulates a remote caller; registered before the agent gate so the gate sees the changed address.
        _app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey(RemoteAddressTestHeader))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            }

            return next(context);
        });

        _app.MapHomeBlazeAgentMcp();
        _app.MapMcp(ExternalRoute);
        await _app.StartAsync();
        _baseAddress = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static AgentToolSet CreateToolSet(int maxToolCalls = 10)
    {
        var browse = new McpToolInfo
        {
            Name = "browse",
            Description = "Browse",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = (input, _) => Task.FromResult<object?>("browsed " + input.GetProperty("path").GetString())
        };
        var memory = new AgentMemory(filePath: null, TimeProvider.System);
        return AgentToolSet.Create([browse], ["browse"], memory.CreateUpdateTool(), maxToolCalls, progress: null);
    }

    private Task<McpClient> ConnectAsync(string token) =>
        ConnectAsync(AgentMcpEndpoint.Route, "Bearer " + token);

    private async Task<McpClient> ConnectAsync(string route, string? authorization)
    {
        var headers = new Dictionary<string, string>();
        if (authorization is not null)
        {
            headers["Authorization"] = authorization;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_baseAddress + route),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers
        }, NullLoggerFactory.Instance);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task WhenATurnTokenIsValid_ThenOnlyTheTurnsToolsAreListedAndCallable()
    {
        // Arrange
        var toolSet = CreateToolSet();
        using var registration = _registry.Register(toolSet);
        await using var client = await ConnectAsync(registration.Token);

        // Act
        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("browse", new Dictionary<string, object?> { ["path"] = "/Kitchen" });

        // Assert
        Assert.Equal(["browse", "update_memory"], tools.Select(tool => tool.Name).Order());
        Assert.Equal("browsed /Kitchen", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Single(toolSet.Calls);
    }

    [Fact]
    public async Task WhenNoTokenIsSent_ThenTheRequestIsRejected()
    {
        // Arrange
        using var httpClient = new HttpClient();

        // Act
        using var response = await httpClient.PostAsync(_baseAddress + AgentMcpEndpoint.Route, new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task WhenTheTurnEnded_ThenItsTokenIsRejected()
    {
        // Arrange
        var registration = _registry.Register(CreateToolSet());
        var token = registration.Token;
        registration.Dispose();
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseAddress + AgentMcpEndpoint.Route) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Act
        using var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheRequestIsNotFromLoopback_ThenAValidTokenIsRejected()
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseAddress + AgentMcpEndpoint.Route) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registration.Token);
        request.Headers.Add(RemoteAddressTestHeader, "1");

        // Act
        using var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheSchemeIsLowercase_ThenTheTokenIsAccepted()
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());
        await using var client = await ConnectAsync(AgentMcpEndpoint.Route, "bearer " + registration.Token);

        // Act
        var tools = await client.ListToolsAsync();

        // Assert
        Assert.Equal(["browse", "update_memory"], tools.Select(tool => tool.Name).Order());
    }

    [Fact]
    public async Task WhenTheExternalRouteIsAlsoMapped_ThenItKeepsTheGlobalToolsWhileTheAgentRouteServesTheTurn()
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());
        await using var externalClient = await ConnectAsync(ExternalRoute, authorization: null);
        await using var agentClient = await ConnectAsync(registration.Token);

        // Act
        var externalTools = await externalClient.ListToolsAsync();
        var agentTools = await agentClient.ListToolsAsync();

        // Assert
        Assert.Equal(["global_tool", "registered_tool"], externalTools.Select(tool => tool.Name).Order());
        Assert.Equal(["browse", "update_memory"], agentTools.Select(tool => tool.Name).Order());
    }

    [Fact]
    public async Task WhenPromptsAndResourcesAreRegistered_ThenTheAgentRouteExposesNone()
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());

        // Act
        await using var externalClient = await ConnectAsync(ExternalRoute, authorization: null);
        await using var agentClient = await ConnectAsync(registration.Token);

        // Assert
        Assert.NotNull(externalClient.ServerCapabilities.Prompts);
        Assert.NotNull(externalClient.ServerCapabilities.Resources);
        Assert.Null(agentClient.ServerCapabilities.Prompts);
        Assert.Null(agentClient.ServerCapabilities.Resources);
        Assert.Null(agentClient.ServerCapabilities.Completions);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WhenTheTransportIsNotConfiguredForTheAgentRoute_ThenMappingThrows(bool stateless, bool configureSessionOptions)
    {
        // Arrange
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(new AgentTurnTokenRegistry());
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            options.Stateless = stateless;
            options.ConfigureSessionOptions = configureSessionOptions ? AgentMcpEndpoint.ConfigureSessionOptions : null;
        });
        await using var app = builder.Build();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => app.MapHomeBlazeAgentMcp());
    }
}
