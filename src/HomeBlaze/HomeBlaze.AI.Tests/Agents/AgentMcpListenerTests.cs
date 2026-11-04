using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HomeBlaze.AI.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Namotion.Interceptor.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public sealed class AgentMcpListenerTests : IAsyncLifetime
{
    private const string ExternalRoute = "/mcp";
    private const string ExternalServerInstructions = "Read the documentation pages first.";

    private WebApplication? _app;
    private string _baseAddress = string.Empty;
    private string _agentEndpoint = string.Empty;
    private readonly AgentTurnTokenRegistry _registry = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_registry);
        builder.Services.AddHomeBlazeAgents();
        builder.Services.AddMcpServer(options => options.ServerInstructions = ExternalServerInstructions)
            .WithHttpTransport(options => options.Stateless = true)
            // The external MCP server of a host has its own handlers, tools, prompts and resources; the agent route must see none of them.
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool { Name = "global_tool", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
            }))
            .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult { IsError = true, Content = [] }))
            .WithTools([McpServerTool.Create(() => "registered", new McpServerToolCreateOptions { Name = "registered_tool" })])
            .WithPrompts([McpServerPrompt.Create(() => "registered", new McpServerPromptCreateOptions { Name = "registered_prompt" })])
            .WithResources([McpServerResource.Create(() => "registered", new McpServerResourceCreateOptions { Name = "registered_resource", UriTemplate = "test://registered" })]);

        _app = builder.Build();
        _app.MapMcp(ExternalRoute);
        await _app.StartAsync();
        _baseAddress = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _agentEndpoint = _app.Services.GetRequiredService<IAgentEndpointAddress>().GetAgentEndpoint()!.ToString();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static AgentToolSet CreateToolSet()
    {
        var browse = new McpToolInfo
        {
            Name = "browse",
            Description = "Browse",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = (input, _) => Task.FromResult<object?>("browsed " + input.GetProperty("path").GetString())
        };
        var memory = new AgentMemory(filePath: null, TimeProvider.System);
        return AgentToolSet.Create([browse], ["browse"], memory.CreateUpdateTool(), maxToolCalls: 10, progress: null);
    }

    private static async Task<HttpResponseMessage> PostAsync(string address, string? authorization)
    {
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return await httpClient.SendAsync(request);
    }

    private Task<McpClient> ConnectToAgentRouteAsync(string authorization) =>
        ConnectAsync(new Uri(_agentEndpoint), authorization);

    private Task<McpClient> ConnectToExternalRouteAsync() =>
        ConnectAsync(new Uri(_baseAddress + ExternalRoute), authorization: null);

    private static async Task<McpClient> ConnectAsync(Uri endpoint, string? authorization)
    {
        var headers = new Dictionary<string, string>();
        if (authorization is not null)
        {
            headers["Authorization"] = authorization;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
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
        await using var client = await ConnectToAgentRouteAsync("Bearer " + registration.Token);

        // Act
        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("browse", new Dictionary<string, object?> { ["path"] = "/Kitchen" });

        // Assert
        Assert.Equal(["browse", "update_memory"], tools.Select(tool => tool.Name).Order());
        Assert.Equal("browsed /Kitchen", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Single(toolSet.Calls);
    }

    [Fact]
    public async Task WhenConnectingToTheAgentRoute_ThenTheServerInstructionsOfTheExternalRouteAreNotSent()
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());
        await using var externalClient = await ConnectToExternalRouteAsync();

        // Act
        await using var agentClient = await ConnectToAgentRouteAsync("Bearer " + registration.Token);

        // Assert
        Assert.Equal(ExternalServerInstructions, externalClient.ServerInstructions);
        Assert.Null(agentClient.ServerInstructions);
    }

    [Fact]
    public async Task WhenNoTokenIsSent_ThenTheRequestIsRejected()
    {
        // Act
        using var response = await PostAsync(_agentEndpoint, authorization: null);

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

        // Act
        using var response = await PostAsync(_agentEndpoint, "Bearer " + token);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheSchemeIsLowercase_ThenTheTokenIsAccepted()
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());
        await using var client = await ConnectToAgentRouteAsync("bearer " + registration.Token);

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
        await using var externalClient = await ConnectToExternalRouteAsync();
        await using var agentClient = await ConnectToAgentRouteAsync("Bearer " + registration.Token);

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
        await using var externalClient = await ConnectToExternalRouteAsync();
        await using var agentClient = await ConnectToAgentRouteAsync("Bearer " + registration.Token);

        // Assert
        Assert.NotNull(externalClient.ServerCapabilities.Prompts);
        Assert.NotNull(externalClient.ServerCapabilities.Resources);
        Assert.Null(agentClient.ServerCapabilities.Prompts);
        Assert.Null(agentClient.ServerCapabilities.Resources);
        Assert.Null(agentClient.ServerCapabilities.Completions);
    }

    [Theory]
    [InlineData("Bearer 0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("Bearer")]
    public async Task WhenTheTokenIsUnknownOrNotABearerToken_ThenTheRequestIsRejected(string authorization)
    {
        // Arrange
        using var registration = _registry.Register(CreateToolSet());

        // Act
        using var response = await PostAsync(_agentEndpoint, authorization);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void WhenTheListenerRuns_ThenItIsBoundToLoopbackOnlyOnAPortOfItsOwn()
    {
        // Arrange
        var listener = _app!.Services.GetRequiredService<AgentMcpListener>();

        // Act
        var endpoint = new Uri(_agentEndpoint);

        // Assert
        Assert.Equal("127.0.0.1", endpoint.Host);
        Assert.Equal(AgentMcpListener.Route, endpoint.AbsolutePath);
        Assert.NotEqual(new Uri(_baseAddress).Port, endpoint.Port);
        var boundAddress = new Uri(Assert.Single(listener.BoundAddresses));
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(boundAddress.Host)));
        Assert.Equal(endpoint.Port, boundAddress.Port);
    }

    [Fact]
    public void WhenAgentServicesAreResolved_ThenTheyUseTheRegisteredRegistryAndTheListenerAddress()
    {
        // Act
        var services = _app!.Services.GetRequiredService<AgentServices>();

        // Assert
        Assert.Same(_registry, services.TokenRegistry);
        Assert.Same(TimeProvider.System, services.TimeProvider);
        Assert.IsType<HomeBlazeAgentToolSource>(services.ToolSource);
        Assert.Same(_app.Services.GetRequiredService<AgentMcpListener>(), services.EndpointAddress);
    }

    [Fact]
    public async Task WhenTheHostHasNoWebServerOrMcpServer_ThenTheListenerStillServesTheTurn()
    {
        // Arrange
        var registry = new AgentTurnTokenRegistry();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton(registry);
        builder.Services.AddHomeBlazeAgents();
        using var host = builder.Build();
        await host.StartAsync();
        using var registration = registry.Register(CreateToolSet());
        var endpoint = host.Services.GetRequiredService<IAgentEndpointAddress>().GetAgentEndpoint()!;

        // Act
        await using var client = await ConnectAsync(endpoint, "Bearer " + registration.Token);
        var tools = await client.ListToolsAsync();
        await host.StopAsync();

        // Assert
        Assert.Equal(["browse", "update_memory"], tools.Select(tool => tool.Name).Order());
    }

    [Theory]
    [InlineData("GET", AgentMcpListener.Route)]
    [InlineData("DELETE", AgentMcpListener.Route)]
    [InlineData("POST", "/other")]
    [InlineData("GET", "/")]
    public async Task WhenAnyRequestHasNoToken_ThenTheListenerRejectsIt(string method, string path)
    {
        // Arrange
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(new Uri(_agentEndpoint), path));

        // Act
        using var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheListenerStarts_ThenItLogsItsEndpointButNotTheListeningLineOfAWebServer()
    {
        // Arrange
        var logs = new RecordingLoggerProvider();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        builder.Services.AddHomeBlazeAgents();
        using var host = builder.Build();

        // Act
        await host.StartAsync();
        var endpoint = host.Services.GetRequiredService<IAgentEndpointAddress>().GetAgentEndpoint()!;
        await host.StopAsync();

        // Assert
        // The test host logs its own lifetime messages; the listener's must not name its address.
        Assert.DoesNotContain(logs.Messages, message => message.Category == "Microsoft.Hosting.Lifetime" && message.Text.Contains(endpoint.Authority));
        var listenerMessage = Assert.Single(logs.Messages, message => message.Category == typeof(AgentMcpListener).FullName);
        Assert.Equal(LogLevel.Information, listenerMessage.Level);
        Assert.Contains(endpoint.ToString(), listenerMessage.Text);
    }

    [Fact]
    public async Task WhenTheLoopbackPortCannotBeBound_ThenStartingLogsAnErrorAndLeavesTheListenerWithoutAnAddress()
    {
        // Arrange
        var occupyingListener = new TcpListener(IPAddress.Loopback, 0);
        occupyingListener.Start();
        try
        {
            var logs = new RecordingLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
            await using var listener = new AgentMcpListener(new AgentTurnTokenRegistry(), loggerFactory)
            {
                Port = ((IPEndPoint)occupyingListener.LocalEndpoint).Port
            };

            // Act
            await listener.StartAsync(CancellationToken.None);

            // Assert
            Assert.Null(listener.GetAgentEndpoint());
            var error = Assert.Single(logs.Messages, message => message.Level >= LogLevel.Error);
            Assert.Equal(typeof(AgentMcpListener).FullName, error.Category);
        }
        finally
        {
            occupyingListener.Stop();
        }
    }

    [Fact]
    public async Task WhenTheStartIsCancelled_ThenStartingThrowsAndTheListenerHasNoAddress()
    {
        // Arrange
        await using var listener = new AgentMcpListener(new AgentTurnTokenRegistry(), NullLoggerFactory.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.StartAsync(cancellation.Token));
        Assert.Null(listener.GetAgentEndpoint());
    }

    [Fact]
    public async Task WhenStartedAfterDispose_ThenStartingThrows()
    {
        // Arrange
        var listener = new AgentMcpListener(new AgentTurnTokenRegistry(), NullLoggerFactory.Instance);
        await listener.DisposeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => listener.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenStartedTwice_ThenTheSecondStartThrows()
    {
        // Arrange
        await using var listener = new AgentMcpListener(new AgentTurnTokenRegistry(), NullLoggerFactory.Instance);
        await listener.StartAsync(CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheServiceProviderIsDisposedSynchronously_ThenTheStartedListenerIsDisposedWithoutThrowing()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeBlazeAgents();
        using var serviceProvider = services.BuildServiceProvider();
        var listener = serviceProvider.GetRequiredService<AgentMcpListener>();
        await listener.StartAsync(CancellationToken.None);
        var endpoint = listener.GetAgentEndpoint()!;

        // Act
        var exception = Record.Exception(serviceProvider.Dispose);

        // Assert
        Assert.Null(exception);
        Assert.Null(listener.GetAgentEndpoint());
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(endpoint.Host, endpoint.Port));
    }

    [Fact]
    public async Task WhenTheHostStops_ThenTheListenerHasNoAddress()
    {
        // Arrange
        var endpointAddress = _app!.Services.GetRequiredService<IAgentEndpointAddress>();

        // Act
        await _app.StopAsync();

        // Assert
        Assert.Null(endpointAddress.GetAgentEndpoint());
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, LogLevel Level, string Text)> _messages = [];

        public IReadOnlyList<(string Category, LogLevel Level, string Text)> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider._messages)
                {
                    provider._messages.Add((category, logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
