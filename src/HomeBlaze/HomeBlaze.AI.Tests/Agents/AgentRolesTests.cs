using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public sealed class AgentRolesTests : IAsyncLifetime
{
    private const string RefusalSentence = "The access policy does not allow it.";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-agent-roles-");
    private readonly List<IAsyncDisposable> _disposables = [];

    [Fact]
    public void WhenAnAgentIsCreated_ThenItsRolesAreOperator()
    {
        // Act
        var agent = new FakeAgent(AgentTestContext.CreateServices(new AgentTestRoot()));

        // Assert
        Assert.Equal([BuiltInRoles.OperatorName], agent.Roles);
    }

    [Fact]
    public void WhenTheRolesAreSetToNull_ThenTheyAreOperator()
    {
        // Arrange
        var agent = new FakeAgent(AgentTestContext.CreateServices(new AgentTestRoot()));

        // Act
        agent.Roles = null!;

        // Assert
        Assert.Equal([BuiltInRoles.OperatorName], agent.Roles);
    }

    [Fact]
    public async Task WhenAReaderAgentSetsAState_ThenTheWriteIsRefusedAndLoggedWithTheAgentAndThePath()
    {
        // Arrange
        var logger = new RecordingLogger();
        string? setPropertyResult = null;
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services, logger),
            agent =>
            {
                agent.Name = "Watcher";
                agent.Roles = [BuiltInRoles.ReaderName];
                agent.AllowedTools = ["get_property", "set_property", HomeBlazeMcpToolProvider.InvokeOperationToolName];
                agent.Handler = async (request, _, cancellationToken) =>
                {
                    var setProperty = request.Tools.Tools.First(tool => tool.Name == "set_property");
                    setPropertyResult = JsonSerializer.Serialize(await setProperty.Handler(
                        JsonSerializer.SerializeToElement(new { path = "/Temperature", value = 30 }), cancellationToken));
                    return new AgentTurnResult("Done.", null, null);
                };
            });
        test.Root.Temperature = 20;

        // Act
        await test.Agent.SendMessageAsync("Make it warmer.", CancellationToken.None);

        // Assert
        Assert.Contains(RefusalSentence, setPropertyResult);
        Assert.Equal(20, test.Root.Temperature);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("Watcher", StringComparison.Ordinal) &&
            entry.Message.Contains("/Temperature", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BuiltInRoles.ReaderName, false)]
    [InlineData(BuiltInRoles.OperatorName, true)]
    public async Task WhenAnAgentRunsATurn_ThenItsToolsGetThePolicyOfItsRolesWhateverItsAllowedTools(string roleName, bool isAllowed)
    {
        // Arrange
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent =>
            {
                agent.Roles = [roleName];
                agent.AllowedTools = ["set_property", HomeBlazeMcpToolProvider.InvokeOperationToolName];
            });
        var root = test.Root.TryGetRegisteredSubject()!;
        var operation = root.AddProperty<MethodMetadata>("Restart", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            Title = "Restart",
            PropertyName = "Restart"
        });

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert: invoke_operation asks the policy for execute.
        var policy = ((TestAgentToolSource)test.Services.ToolSource).LastAccessPolicy!;
        Assert.Equal(isAllowed, policy.IsAllowed(operation, AuthorizationActions.Execute.Name));
        Assert.Equal(isAllowed, policy.IsAllowed(root.TryGetProperty(nameof(AgentTestRoot.Temperature))!, McpAccessActions.Write));
    }

    [Fact]
    public async Task WhenARoleIsUnknown_ThenTheAgentRunsItsTurnsWithDenyAllAndShowsTheError()
    {
        // Arrange
        string? getPropertyResult = null;
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent =>
            {
                agent.Roles = ["Missing"];
                agent.AllowedTools = ["get_property"];
                agent.Handler = async (request, _, cancellationToken) =>
                {
                    var getProperty = request.Tools.Tools.First(tool => tool.Name == "get_property");
                    getPropertyResult = JsonSerializer.Serialize(await getProperty.Handler(
                        JsonSerializer.SerializeToElement(new { path = "/Temperature" }), cancellationToken));
                    return new AgentTurnResult("Done.", null, null);
                };
            });
        var statusAfterStart = (test.Agent.Status, test.Agent.StatusMessage);

        // Act
        await test.Agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterStart.Status);
        Assert.Contains("'Missing'", statusAfterStart.StatusMessage);
        Assert.Equal(AgentTimelineEntryKind.Assistant, test.Agent.Timeline.Entries[^1].Kind);
        Assert.Contains("Path not found", getPropertyResult);
        var policy = Assert.IsType<AuthorizationMcpAccessPolicy>(((TestAgentToolSource)test.Services.ToolSource).LastAccessPolicy);
        Assert.Same(AuthorizationPolicy.DenyAll, policy.Policy);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Contains("'Missing'", test.Agent.StatusMessage);
    }

    [Fact]
    public async Task WhenAnUnknownRoleIsCorrected_ThenTheErrorIsCleared()
    {
        // Arrange
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent => agent.Roles = ["Missing"]);

        // Act
        test.Agent.Roles = [BuiltInRoles.ReaderName];
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.DoesNotContain("'Missing'", test.Agent.StatusMessage ?? string.Empty);
    }

    [Fact]
    public async Task WhenAToolCallReadsAnEvaluatedPage_ThenAPropertyTheRolesDenyRendersEmpty()
    {
        // Arrange
        var graph = await CreateGraphAsync(
            "Temperature: [{{ /Temperature }}], humidity: [{{ /Humidity }}]",
            ("PageReader", new Role
            {
                Includes = [BuiltInRoles.ReaderName],
                Deny = [new PermissionRule { Members = [nameof(AgentTestRoot.Humidity)], Actions = [PermissionAction.Read] }]
            }));
        string? evaluated = null;
        var agent = new FakeAgent(graph.AgentServices)
        {
            Roles = ["PageReader"],
            AllowedTools = [HomeBlazeMcpToolProvider.InvokeQueryToolName],
            Handler = async (request, _, cancellationToken) =>
            {
                var invokeQuery = request.Tools.Tools.First(tool => tool.Name == HomeBlazeMcpToolProvider.InvokeQueryToolName);
                var result = JsonSerializer.SerializeToElement(await invokeQuery.Handler(
                    JsonSerializer.SerializeToElement(new { path = "/Storage/Notes.md", method = "ReadEvaluatedContent" }), cancellationToken));
                evaluated = result.GetProperty("result").GetString();
                return new AgentTurnResult("Done.", null, null);
            }
        };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        await agent.SendMessageAsync("Read the notes.", CancellationToken.None);

        // Assert
        var page = Assert.IsType<MarkdownFile>(graph.Storage.Children["Notes.md"]);
        Assert.Equal("Temperature: [21.5], humidity: [40]", page.ReadEvaluatedContent()?.Trim());
        Assert.Equal("Temperature: [21.5], humidity: []", evaluated?.Trim());
    }

    [Fact]
    public async Task WhenAnAgentNamesACustomRole_ThenItsUsedByAgentsFollowsTheRolesAndResetsWhenTheAgentIsRemoved()
    {
        // Arrange
        var role = new Role { Includes = [BuiltInRoles.ReaderName] };
        var graph = await CreateGraphAsync("# Notes", ("Custom", role));
        var agent = new FakeAgent(graph.AgentServices) { Roles = ["custom"] };

        // Act
        await AttachAndWaitForStartAsync(graph, agent);
        var countWhenStarted = role.UsedByAgents;
        agent.Roles = [BuiltInRoles.OperatorName];
        var countAfterChange = role.UsedByAgents;
        agent.Roles = ["Custom", BuiltInRoles.ReaderName];
        var countAfterChangeBack = role.UsedByAgents;
        graph.Root.Children = graph.Root.Children
            .Where(child => child.Key != "Agent")
            .ToDictionary(child => child.Key, child => child.Value);

        // Assert
        Assert.Equal(1, countWhenStarted);
        Assert.Equal(0, countAfterChange);
        Assert.Equal(1, countAfterChangeBack);
        await AsyncTestHelpers.WaitUntilAsync(() => role.UsedByAgents == 0, message: "The removed agent should no longer count.");
        Assert.Equal(ServiceStatus.Stopped, agent.Status);
    }

    private static async Task AttachAndWaitForStartAsync(Graph graph, FakeAgent agent)
    {
        // The hosted service handler starts the agent once it is attached.
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Agent"] = agent };
        await AsyncTestHelpers.WaitUntilAsync(
            () => agent.Status is not (ServiceStatus.Stopped or ServiceStatus.Starting), message: "The agent should finish starting.");
    }

    private async Task<Graph> CreateGraphAsync(string notes, params (string Name, Role Role)[] roles)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory.FullName, "Notes.md"), notes);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddLogging();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        AgentTestRoot? root = null;
        services.AddSingleton(new SubjectPathResolver(() => root));
        services.AddSingleton<MarkdownContentParser>();
        services.AddHomeBlazeAuthorization(McpAuthorization.PathProvider);
        var serviceProvider = services.BuildServiceProvider();
        _disposables.Add(serviceProvider);

        var hostedServices = serviceProvider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        _disposables.Add(new AsyncDisposable(async () =>
        {
            foreach (var hostedService in hostedServices.Reverse())
            {
                await hostedService.StopAsync(CancellationToken.None);
            }
        }));

        var storage = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _directory.FullName,
            EnableFileWatching = false
        };
        _disposables.Add(new AsyncDisposable(() =>
        {
            storage.Dispose();
            return ValueTask.CompletedTask;
        }));

        var authorization = new Authorization.Authorization
        {
            Roles = roles.ToDictionary(entry => entry.Name, entry => entry.Role)
        };
        root = new AgentTestRoot(context)
        {
            Temperature = 21.5m,
            Humidity = 40m,
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Storage"] = storage,
                ["Authorization"] = authorization
            }
        };
        await storage.ConnectAsync(CancellationToken.None);

        var agentServices = new AgentServices(
            new FactoryToolSource(serviceProvider, () => root),
            new AgentTurnTokenRegistry(),
            new FixedEndpointAddress(null),
            TimeProvider.System,
            serviceProvider.GetRequiredService<AuthorizationService>());
        return new Graph(root, storage, agentServices);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        for (var index = _disposables.Count - 1; index >= 0; index--)
        {
            await _disposables[index].DisposeAsync();
        }

        _directory.Delete(recursive: true);
    }

    private sealed record Graph(AgentTestRoot Root, FluentStorageContainer Storage, AgentServices AgentServices);

    /// <summary>The HomeBlaze tools of agents over a fixed root.</summary>
    private sealed class FactoryToolSource(IServiceProvider serviceProvider, Func<IInterceptorSubject> root) : IAgentToolSource
    {
        public IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly, IMcpAccessPolicy accessPolicy)
        {
            var configuration = HomeBlazeMcpConfigurationFactory.Create(serviceProvider, root, isReadOnly, accessPolicy);
            return new McpToolFactory(root, configuration).CreateTools();
        }
    }

    private sealed class AsyncDisposable(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
