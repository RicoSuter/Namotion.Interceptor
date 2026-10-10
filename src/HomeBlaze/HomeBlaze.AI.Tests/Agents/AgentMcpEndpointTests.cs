using System.Net;
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
using Namotion.Interceptor.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public sealed class AgentMcpEndpointTests : IAsyncLifetime
{
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
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [] }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [] }));

        _app = builder.Build();
        _app.MapHomeBlazeAgentMcp();
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

    private async Task<McpClient> ConnectAsync(string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_baseAddress + AgentMcpEndpoint.Route),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
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
        using var response = await httpClient.PostAsync(_baseAddress + AgentMcpEndpoint.Route, new StringContent("{}"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheTurnEnded_ThenItsTokenIsRejected()
    {
        // Arrange
        var registration = _registry.Register(CreateToolSet());
        var token = registration.Token;
        registration.Dispose();
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseAddress + AgentMcpEndpoint.Route) { Content = new StringContent("{}") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Act
        using var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
