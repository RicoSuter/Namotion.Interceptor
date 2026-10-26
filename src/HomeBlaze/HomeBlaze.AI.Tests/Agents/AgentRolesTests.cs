using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Mcp;
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
    public async Task WhenAReaderAgentInvokesAnOperation_ThenItIsRefusedAndLoggedWithTheAgentAndTheMethodPath()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        var logger = new RecordingLogger();
        string? error = null;
        var agent = new FakeAgent(graph.AgentServices, logger)
        {
            Name = "Watcher",
            Roles = [BuiltInRoles.ReaderName],
            AllowedTools = [HomeBlazeMcpToolProvider.InvokeOperationToolName],
            Handler = async (request, _, cancellationToken) =>
            {
                var invokeOperation = request.Tools.Tools.First(tool => tool.Name == HomeBlazeMcpToolProvider.InvokeOperationToolName);
                var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
                    JsonSerializer.SerializeToElement(new { path = "/Storage/Notes.md", method = "ReplaceContent", parameters = new { content = "# Changed" } }),
                    cancellationToken));
                error = result.GetProperty("error").GetString();
                return new AgentTurnResult("Done.", null, null);
            }
        };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        await agent.SendMessageAsync("Rewrite the notes.", CancellationToken.None);

        // Assert
        Assert.Equal("Operation 'ReplaceContent' is not permitted. The access policy does not allow it.", error);
        Assert.Equal("# Notes", await File.ReadAllTextAsync(Path.Combine(_directory.FullName, "Notes.md")));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("Watcher", StringComparison.Ordinal) &&
            entry.Message.Contains("/Storage/Notes.md/ReplaceContent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenAReaderAgentSetsAState_ThenTheWriteIsRefusedWithoutALog()
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
                agent.AllowedTools = ["get_property", "set_property"];
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
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("/Temperature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenAWatchPathPointsAtTheStateOfAnotherAgent_ThenItIsNotDeliveredWhileAnOrdinaryStateIs()
    {
        // Arrange
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent => agent.WatchPaths = ["/Humidity", "/Other/Status"]);
        var other = new FakeAgent(test.Services);
        test.Root.Children = new Dictionary<string, IInterceptorSubject>(test.Root.Children) { ["Other"] = other };
        test.Root.Humidity = 55;

        // Act
        await test.RunNowAndWaitAsync();

        // Assert
        var lines = test.Agent.Requests[^1].UserMessage.Split('\n');
        var stateLines = lines.SkipWhile(line => line != "```jsonl").Skip(1).TakeWhile(line => line != "```")
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        Assert.Equal(2, stateLines.Count);
        Assert.Equal("/Humidity", stateLines[0].GetProperty("path").GetString());
        Assert.Equal(55, stateLines[0].GetProperty("property").GetProperty("value").GetDecimal());
        Assert.Equal("/Other/Status", stateLines[1].GetProperty("path").GetString());
        Assert.False(stateLines[1].TryGetProperty("property", out _));
        Assert.Contains("not found", stateLines[1].GetProperty("error").GetString());
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
    public async Task WhenNoRolesAreAssigned_ThenTheAgentShowsTheError()
    {
        // Act
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent => agent.Roles = []);

        // Assert
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Contains("Invalid roles: No roles are assigned.", test.Agent.StatusMessage);
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
    public async Task WhenAnMcpClientReadsAnEvaluatedPage_ThenTheStateOfAnAgentRendersEmptyWhileAnOrdinaryStateRenders()
    {
        // Arrange
        var graph = await CreateGraphAsync("Temperature: [{{ /Temperature }}], agent: [{{ /Agent/Status }}]");
        await AttachAndWaitForStartAsync(graph, new FakeAgent(graph.AgentServices));
        var invokeQuery = CreateMcpServerTools(graph)[HomeBlazeMcpToolProvider.InvokeQueryToolName];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeQuery.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage/Notes.md", method = "ReadEvaluatedContent" }), CancellationToken.None));

        // Assert
        var page = Assert.IsType<MarkdownFile>(graph.Storage.Children["Notes.md"]);
        Assert.DoesNotContain("agent: []", page.ReadEvaluatedContent());
        Assert.Equal("Temperature: [21.5], agent: []", result.GetProperty("result").GetString()?.Trim());
    }

    [Fact]
    public async Task WhenAnMcpClientReadsAnEvaluatedPageOnAFlowWithAReadCheck_ThenTheCheckOfTheFlowDoesNotApply()
    {
        // Arrange
        var graph = await CreateGraphAsync("Temperature: [{{ /Temperature }}]");
        var invokeQuery = CreateMcpServerTools(graph)[HomeBlazeMcpToolProvider.InvokeQueryToolName];

        // Act
        JsonElement result;
        using (PropertyReadAccess.Enter(_ => false))
        {
            result = JsonSerializer.SerializeToElement(await invokeQuery.Handler(
                JsonSerializer.SerializeToElement(new { path = "/Storage/Notes.md", method = "ReadEvaluatedContent" }), CancellationToken.None));
        }

        // Assert
        Assert.Equal("Temperature: [21.5]", result.GetProperty("result").GetString()?.Trim());
    }

    [Fact]
    public async Task WhenARoleDeniesReadOnAHoldingProperty_ThenAPageExpressionBelowItRendersEmptyWithoutItsSourcePath()
    {
        // Arrange
        var graph = await CreateGraphAsync("Kitchen: [{{ /Devices/Kitchen/Temperature }}], active: [{{ /Devices/Kitchen/Child/IsActive }}]");
        AddDevices(graph);
        var role = new AuthorizationRole
        {
            Name = "NoChildren",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Members = [nameof(TestThing.Child)], Actions = [PermissionAction.Read] }]
        };
        var tools = CreateClientTools(graph, TestAccessPolicies.Create(role.Name, role));

        // Act
        var browse = JsonSerializer.Serialize(await tools["get_property"].Handler(
            JsonSerializer.SerializeToElement(new { path = "/Devices/Kitchen/Child/IsActive" }), CancellationToken.None));
        var evaluated = await ReadEvaluatedContentAsync(tools, includeSourcePaths: true);

        // Assert
        Assert.Contains("Path not found", browse);
        Assert.Equal("Kitchen: [21 (/Devices/Kitchen/Temperature)], active: []", evaluated);
    }

    [Fact]
    public async Task WhenARoleShowsOnlyOneBranch_ThenPageExpressionsRenderItsValuesAndNotThoseOfOtherBranches()
    {
        // Arrange
        var graph = await CreateGraphAsync(
            "Kitchen: [{{ /Devices/Kitchen/Child/IsActive }}], cellar: [{{ /Devices/Cellar/Child/IsActive }}], temperature: [{{ /Devices/Cellar/Temperature }}]");
        AddDevices(graph);
        var role = new AuthorizationRole
        {
            Name = "KitchenOnly",
            Allow =
            [
                new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Read] },
                new PermissionRule { Members = [nameof(AgentTestRoot.Children)], Actions = [PermissionAction.Read] },
                new PermissionRule { Tags = [MemberTags.Subjects], Paths = ["/Devices/Kitchen"], Actions = [PermissionAction.Read] },
                new PermissionRule { Tags = [MemberTags.Query], Actions = [PermissionAction.Read, AuthorizationActions.Execute] }
            ]
        };
        var tools = CreateClientTools(graph, TestAccessPolicies.Create(role.Name, role));

        // Act
        var evaluated = await ReadEvaluatedContentAsync(tools, includeSourcePaths: false);

        // Assert
        Assert.Equal("Kitchen: [Yes], cellar: [], temperature: [18]", evaluated);
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

    // Devices/Kitchen and Devices/Cellar, each with a child.
    private static void AddDevices(Graph graph)
    {
        var devices = new AgentTestRoot
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Kitchen"] = new TestThing { Name = "Kitchen", Temperature = 21, Child = new TestChildThing { ChildName = "Light", IsActive = true } },
                ["Cellar"] = new TestThing { Name = "Cellar", Temperature = 18, Child = new TestChildThing { ChildName = "Pump", IsActive = true } }
            }
        };
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Devices"] = devices };
    }

    // The tools of a client with the policy over the graph.
    private static Dictionary<string, McpToolInfo> CreateClientTools(Graph graph, IMcpAccessPolicy accessPolicy)
    {
        var configuration = HomeBlazeMcpConfigurationFactory.Create(graph.Services, () => graph.Root, isReadOnly: true, accessPolicy);
        return new McpToolFactory(graph.Root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }

    private static async Task<string?> ReadEvaluatedContentAsync(Dictionary<string, McpToolInfo> tools, bool includeSourcePaths)
    {
        var result = JsonSerializer.SerializeToElement(await tools[HomeBlazeMcpToolProvider.InvokeQueryToolName].Handler(
            JsonSerializer.SerializeToElement(new
            {
                path = "/Storage/Notes.md",
                method = "ReadEvaluatedContent",
                parameters = new { includeSourcePaths }
            }),
            CancellationToken.None));
        return result.GetProperty("result").GetString()?.Trim();
    }

    // The tools of the external MCP server over the graph.
    private static Dictionary<string, McpToolInfo> CreateMcpServerTools(Graph graph)
    {
        var configuration = McpBuilderExtensions.CreateServerConfiguration(graph.Services, () => graph.Root, isReadOnly: true);
        return new McpToolFactory(graph.Root, configuration).CreateTools().ToDictionary(tool => tool.Name);
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
        // In the context as well, so that its paths follow the changes of the graph, such as an attached agent.
        var pathResolver = new SubjectPathResolver(() => root);
        context.AddService(pathResolver);
        services.AddSingleton(pathResolver);
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
        return new Graph(root, storage, agentServices, serviceProvider);
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

    private sealed record Graph(AgentTestRoot Root, FluentStorageContainer Storage, AgentServices AgentServices, IServiceProvider Services);

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
