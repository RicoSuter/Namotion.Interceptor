using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Mcp;
using HomeBlaze.Components;
using HomeBlaze.Authorization;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
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
    public async Task WhenAReaderAgentInvokesAnOperation_ThenItIsRefused()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        string? error = null;
        var agent = new FakeAgent(graph.AgentServices)
        {
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
    }

    [Fact]
    public async Task WhenAReaderAgentSetsAState_ThenTheWriteIsRefused()
    {
        // Arrange
        string? setPropertyResult = null;
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(
            services => new FakeAgent(services),
            agent =>
            {
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

    [Fact]
    public async Task WhenAWatchedMemberIsDeniedByTheRolesOfTheAgent_ThenItsStateIsLeftOutOfTheWatchedState()
    {
        // Arrange
        var graph = await CreateGraphAsync(
            "# Notes",
            new AuthorizationRole
            {
                Name = "no-humidity",
                Includes = [BuiltInRoles.ReaderName],
                Deny = [new PermissionRule { Members = [nameof(AgentTestRoot.Humidity)], Actions = [PermissionAction.Read] }]
            });
        var agent = new FakeAgent(graph.AgentServices)
        {
            Roles = ["no-humidity"],
            WatchPaths = ["/Temperature", "/Humidity"],
            MinRunInterval = TimeSpan.Zero
        };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => agent.RunCount > 0 && !agent.IsBusy);

        // Assert
        var stateLines = agent.Requests[^1].UserMessage.Split('\n')
            .SkipWhile(line => line != "```jsonl").Skip(1).TakeWhile(line => line != "```")
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        var temperature = Assert.Single(stateLines, line => line.GetProperty("path").GetString() == "/Temperature");
        Assert.Equal(21.5m, temperature.GetProperty("property").GetProperty("value").GetDecimal());
        var humidity = Assert.Single(stateLines, line => line.GetProperty("path").GetString() == "/Humidity");
        Assert.False(humidity.TryGetProperty("property", out _));
    }

    [Fact]
    public async Task WhenTheRolesHideAPartOfAWatchedBranch_ThenTheBackgroundMessageLeavesItOut()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes", new AuthorizationRole
        {
            Name = "nocellar",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Paths = ["/Devices/Cellar/**"], Actions = [PermissionAction.Read] }]
        });
        AddDevices(graph);
        var agent = new FakeAgent(graph.AgentServices) { Roles = ["nocellar"], WatchPaths = ["/Devices"], MinRunInterval = TimeSpan.Zero };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        var runCount = agent.RunCount;
        agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => agent.RunCount > runCount && !agent.IsBusy, message: "The background run should finish.");

        // Assert
        var message = agent.Requests[^1].UserMessage;
        Assert.Contains("/Devices/Kitchen", message);
        Assert.DoesNotContain("Cellar", message);
    }

    [Fact]
    public async Task WhenAWatchedChangeIsHiddenByTheRoles_ThenItStartsNoRunWhileAReadableOneDoes()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes", new AuthorizationRole
        {
            Name = "nocellar",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Paths = ["/Devices/Cellar/**"], Actions = [PermissionAction.Read] }]
        });
        AddDevices(graph);
        var logger = new RecordingLogger();
        var agent = new FakeAgent(graph.AgentServices, logger) { Roles = ["nocellar"], WatchPaths = ["/Devices"], MinRunInterval = TimeSpan.Zero };
        await AttachAndWaitForStartAsync(graph, agent);
        var devices = (AgentTestRoot)graph.Root.Children["Devices"];

        // Act
        ((TestThing)devices.Children["Cellar"]).Temperature = 5;
        ((TestThing)devices.Children["Kitchen"]).Temperature = 25;
        await AsyncTestHelpers.WaitUntilAsync(() => agent.RunCount == 1, message: "The readable change should start a run.");

        // Assert
        // Changes are evaluated in order, so once the later readable change has started a run, the hidden one was evaluated too.
        var runRequest = Assert.Single(logger.Entries, entry => entry.Message.Contains("requests a background run", StringComparison.Ordinal));
        Assert.Contains("/Devices/Kitchen", runRequest.Message);
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
    public async Task WhenAnAgentWithTheDefaultAllowedToolsHasARoleAllowingExecute_ThenItsToolSetHasNoStateChangingTool()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        string[]? turnToolNames = null;
        var agent = new FakeAgent(graph.AgentServices)
        {
            Roles = [BuiltInRoles.OperatorName],
            Handler = (request, _, _) =>
            {
                turnToolNames = request.Tools.Tools.Select(tool => tool.Name).ToArray();
                return Task.FromResult(new AgentTurnResult("Done.", null, null));
            }
        };
        await AttachAndWaitForStartAsync(graph, agent);
        var offeredToolNames = graph.AgentServices.ToolSource
            .CreateTools(McpAuthorization.GetAccessPolicy(graph.Services.GetRequiredService<AuthorizationService>(), [BuiltInRoles.OperatorName]))
            .Select(tool => tool.Name)
            .ToArray();

        // Act
        await agent.SendMessageAsync("Hello", CancellationToken.None);

        // Assert
        Assert.Equal(AgentToolSet.DefaultAllowedTools, agent.AllowedTools);
        Assert.Contains(HomeBlazeMcpToolProvider.InvokeOperationToolName, offeredToolNames);
        Assert.Contains("set_property", offeredToolNames);
        Assert.NotNull(turnToolNames);
        Assert.Contains(HomeBlazeMcpToolProvider.InvokeQueryToolName, turnToolNames);
        Assert.All(AgentToolSet.StateChangingTools, name => Assert.DoesNotContain(name, turnToolNames));
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
                agent.Roles = ["missing"];
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
        Assert.Contains("'missing'", statusAfterStart.StatusMessage);
        Assert.Equal(AgentTimelineEntryKind.Assistant, test.Agent.Timeline.Entries[^1].Kind);
        Assert.Contains("Path not found", getPropertyResult);
        var policy = Assert.IsType<AuthorizationMcpAccessPolicy>(((TestAgentToolSource)test.Services.ToolSource).LastAccessPolicy);
        Assert.Same(AuthorizationPolicy.DenyAll, policy.Policy);
        Assert.Equal(ServiceStatus.Error, test.Agent.Status);
        Assert.Contains("'missing'", test.Agent.StatusMessage);
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
            agent => agent.Roles = ["missing"]);

        // Act
        test.Agent.Roles = [BuiltInRoles.ReaderName];
        await test.Agent.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
        Assert.DoesNotContain("'missing'", test.Agent.StatusMessage ?? string.Empty);
    }

    [Fact]
    public async Task WhenAnUnknownRoleIsAddedToTheAuthorization_ThenTheErrorClearsWithoutATurn()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        var agent = new FakeAgent(graph.AgentServices) { Roles = ["late"] };
        await AttachAndWaitForStartAsync(graph, agent);
        var statusBefore = (agent.Status, agent.StatusMessage);
        var authorization = Assert.IsType<Authorization.Authorization>(graph.Root.Children["Authorization"]);

        // Act
        authorization.ReplaceRoles([new AuthorizationRole { Name = "late", Includes = [BuiltInRoles.ReaderName] }]);

        // Assert
        Assert.Equal(ServiceStatus.Error, statusBefore.Status);
        Assert.Contains("'late'", statusBefore.StatusMessage);
        // Status and message are written one after the other, so both are waited for.
        await AsyncTestHelpers.WaitUntilAsync(
            () => agent.Status == ServiceStatus.Running && !(agent.StatusMessage ?? string.Empty).Contains("'late'"),
            message: "The roles error should clear.");
        Assert.Empty(agent.Requests);
    }

    [Fact]
    public async Task WhenARoleIsRemovedDuringATurn_ThenTheNextToolCallIsRefused()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes", new AuthorizationRole { Name = "custom", Includes = [BuiltInRoles.ReaderName] });
        var authorization = Assert.IsType<Authorization.Authorization>(graph.Root.Children["Authorization"]);
        var results = new List<string>();
        var agent = new FakeAgent(graph.AgentServices)
        {
            Roles = ["custom"],
            AllowedTools = ["get_property"],
            Handler = async (request, _, cancellationToken) =>
            {
                var getProperty = request.Tools.Tools.First(tool => tool.Name == "get_property");
                var input = JsonSerializer.SerializeToElement(new { path = "/Temperature" });
                results.Add(JsonSerializer.Serialize(await getProperty.Handler(input, cancellationToken)));
                authorization.ReplaceRoles([]);
                results.Add(JsonSerializer.Serialize(await getProperty.Handler(input, cancellationToken)));
                return new AgentTurnResult("Done.", null, null);
            }
        };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        await agent.SendMessageAsync("Read the temperature twice.", CancellationToken.None);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains("21.5", results[0]);
        Assert.Contains("Path not found", results[1]);
    }

    [Fact]
    public async Task WhenARoleHidesAMethodDuringATurn_ThenTheNextListMethodsLeavesItOutAlsoWhenItsResultIsReadLater()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes", new AuthorizationRole { Name = "custom", Includes = [BuiltInRoles.ReaderName] });
        var authorization = Assert.IsType<Authorization.Authorization>(graph.Root.Children["Authorization"]);
        var results = new List<string>();
        var agent = new FakeAgent(graph.AgentServices)
        {
            Roles = ["custom"],
            AllowedTools = ["list_methods"],
            Handler = async (request, _, cancellationToken) =>
            {
                var listMethods = request.Tools.Tools.First(tool => tool.Name == "list_methods");
                var input = JsonSerializer.SerializeToElement(new { path = "/Storage" });
                results.Add(JsonSerializer.Serialize(await listMethods.Handler(input, cancellationToken)));
                authorization.ReplaceRoles(
                [
                    new AuthorizationRole
                    {
                        Name = "custom",
                        Includes = [BuiltInRoles.ReaderName],
                        Deny = [new PermissionRule { Members = [nameof(FluentStorageContainer.GetOrder)], Actions = [PermissionAction.Read] }]
                    }
                ]);

                // Serialized after the call returned, as a backend serializes a result.
                var result = await listMethods.Handler(input, cancellationToken);
                results.Add(JsonSerializer.Serialize(result));
                return new AgentTurnResult("Done.", null, null);
            }
        };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        await agent.SendMessageAsync("List the methods twice.", CancellationToken.None);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains("\"GetOrder\"", results[0]);
        Assert.DoesNotContain("\"GetOrder\"", results[1]);
        Assert.Contains("\"SearchMetadata\"", results[1]);
    }

    [Fact]
    public async Task WhenAUsedRoleIsRemovedFromTheAuthorization_ThenTheAgentShowsTheErrorWithoutATurn()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes", new AuthorizationRole { Name = "custom", Includes = [BuiltInRoles.ReaderName] });
        var agent = new FakeAgent(graph.AgentServices) { Roles = ["custom"] };
        await AttachAndWaitForStartAsync(graph, agent);
        var statusBefore = agent.Status;
        var authorization = Assert.IsType<Authorization.Authorization>(graph.Root.Children["Authorization"]);

        // Act
        authorization.ReplaceRoles([]);

        // Assert
        Assert.Equal(ServiceStatus.Running, statusBefore);
        // Status and message are written one after the other, so both are waited for.
        await AsyncTestHelpers.WaitUntilAsync(
            () => agent.Status == ServiceStatus.Error && (agent.StatusMessage ?? string.Empty).Contains("'custom'"),
            message: "The roles error should be shown.");
    }

    [Fact]
    public async Task WhenAToolCallReadsAnEvaluatedPage_ThenAPropertyTheRolesDenyRendersEmpty()
    {
        // Arrange
        var graph = await CreateGraphAsync(
            "Temperature: [{{ /Temperature }}], humidity: [{{ /Humidity }}]",
            new AuthorizationRole
            {
                Name = "page-reader",
                Includes = [BuiltInRoles.ReaderName],
                Deny = [new PermissionRule { Members = [nameof(AgentTestRoot.Humidity)], Actions = [PermissionAction.Read] }]
            });
        string? evaluated = null;
        var agent = new FakeAgent(graph.AgentServices)
        {
            Roles = ["page-reader"],
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
        Assert.Equal("Temperature: [21.5], humidity: [40]", page.ReadEvaluatedContent(UnrestrictedPermissions.Instance)?.Trim());
        Assert.Equal("Temperature: [21.5], humidity: []", evaluated?.Trim());
    }

    [Fact]
    public async Task WhenAnAgentWithTheDefaultRolesReadsAnEvaluatedPage_ThenADerivedTitleRenders()
    {
        // Arrange
        var graph = await CreateGraphAsync("Device: [{{ /Device/Title }}]");
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Device"] = new TitledTestDevice { Name = "Pump" } };
        string? evaluated = null;
        var agent = new FakeAgent(graph.AgentServices)
        {
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
        Assert.Equal([BuiltInRoles.OperatorName], agent.Roles);
        Assert.Equal("Device: [Pump]", evaluated?.Trim());
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
        Assert.DoesNotContain("agent: []", page.ReadEvaluatedContent(UnrestrictedPermissions.Instance));
        Assert.Equal("Temperature: [21.5], agent: []", result.GetProperty("result").GetString()?.Trim());
    }

    [Fact]
    public async Task WhenARoleDeniesReadOnAHoldingProperty_ThenAPageExpressionBelowItRendersEmptyWithoutItsSourcePath()
    {
        // Arrange
        var graph = await CreateGraphAsync("Kitchen: [{{ /Devices/Kitchen/Temperature }}], active: [{{ /Devices/Kitchen/Child/IsActive }}]");
        AddDevices(graph);
        var role = new AuthorizationRole
        {
            Name = "no-children",
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
    public async Task WhenARoleMakesOnlyOneBranchReadable_ThenPageExpressionsRenderItsValuesAndNotThoseOfOtherBranches()
    {
        // Arrange
        var graph = await CreateGraphAsync(
            "Kitchen: [{{ /Devices/Kitchen/Child/IsActive }}], cellar: [{{ /Devices/Cellar/Child/IsActive }}], temperature: [{{ /Devices/Cellar/Temperature }}]");
        AddDevices(graph);
        var role = new AuthorizationRole
        {
            Name = "kitchen-only",
            Allow =
            [
                new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Read] },
                new PermissionRule { Members = [nameof(AgentTestRoot.Children)], Actions = [PermissionAction.Read] },
                new PermissionRule { Tags = [MemberTags.Subjects], Paths = ["/Devices/Kitchen/**"], Actions = [PermissionAction.Read] },
                new PermissionRule { Tags = [MemberTags.Query], Actions = [PermissionAction.Read, AuthorizationActions.Execute] }
            ]
        };
        var tools = CreateClientTools(graph, TestAccessPolicies.Create(role.Name, role));

        // Act
        var evaluated = await ReadEvaluatedContentAsync(tools, includeSourcePaths: false);

        // Assert
        Assert.Equal("Kitchen: [Yes], cellar: [], temperature: [18]", evaluated);
    }

    public static TheoryData<string> AuthorizationBlocks => new()
    {
        $":: {typeof(Authorization.Authorization).FullName}#auth\n::",
        $":: {typeof(Role).FullName}#role\n::",
        $"```subject(auth)\n{{ \"$type\": \"{typeof(Authorization.Authorization).FullName}\" }}\n```",
        $":: {typeof(GridLayout).FullName}\n::: Cell\n:::: {typeof(Authorization.Authorization).FullName}#auth\n::::\n:::\n::"
    };

    [Theory]
    [MemberData(nameof(AuthorizationBlocks))]
    public async Task WhenAnAgentWritesAnAuthorizationOrRoleBlock_ThenEveryPageWriteRefusesItAlsoWithThePageLayoutPermission(string block)
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        var results = new List<string>();
        var agent = new FakeAgent(graph.AgentServices)
        {
            AllowedTools = [.. HomeBlazeMarkdownToolProvider.ToolNames, HomeBlazeMcpToolProvider.InvokeOperationToolName, AgentToolSet.EditPageLayoutPermission],
            Handler = async (request, _, cancellationToken) =>
            {
                var tools = request.Tools.Tools.ToDictionary(tool => tool.Name);
                async Task CallAsync(string name, object input) => results.Add(JsonSerializer.Serialize(
                    await tools[name].Handler(JsonSerializer.SerializeToElement(input), cancellationToken)));

                await CallAsync(HomeBlazeMcpToolProvider.InvokeOperationToolName, new
                {
                    path = "/Storage", method = "CreateMarkdownFile", parameters = new { name = "Roles.md", content = "# Roles\n\n" + block }
                });
                await CallAsync(HomeBlazeMarkdownToolProvider.WriteMarkdownToolName, new { path = "/Storage/Notes.md", content = "# Notes\n\n" + block });
                await CallAsync(HomeBlazeMarkdownToolProvider.EditMarkdownToolName, new { path = "/Storage/Notes.md", old_text = "# Notes", new_text = "# Notes\n\n" + block });
                await CallAsync(HomeBlazeMcpToolProvider.InvokeOperationToolName, new
                {
                    path = "/Storage/Notes.md", method = "ReplaceContent", parameters = new { content = "# Notes\n\n" + block }
                });
                return new AgentTurnResult("Done.", null, null);
            }
        };
        await AttachAndWaitForStartAsync(graph, agent);

        // Act
        await agent.SendMessageAsync("Add a role.", CancellationToken.None);

        // Assert
        Assert.Equal([BuiltInRoles.OperatorName], agent.Roles);
        Assert.Equal(4, results.Count);

        // Creating a page is administration, which agents never have; the other writes refuse the block.
        Assert.Contains("CreateMarkdownFile", results[0]);
        Assert.Contains("is not permitted", results[0]);
        Assert.All(results.Skip(1), result => Assert.Contains("With the permission to edit page layout", result));
        var knownSubjects = ((IInterceptorSubject)graph.Root).Context.GetService<ISubjectRegistry>().KnownSubjects.Keys;
        var authorization = Assert.Single(knownSubjects.OfType<Authorization.Authorization>());
        Assert.True(knownSubjects.OfType<Role>().ToHashSet().SetEquals(authorization.Roles.Values), "Only the roles of the Authorization exist.");
        Assert.Equal("# Notes", await File.ReadAllTextAsync(Path.Combine(_directory.FullName, "Notes.md")));
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "Roles.md")));
    }

    [Theory]
    [InlineData(false, "State: [warm]")]
    [InlineData(true, "State: [Cannot resolve map: busy]")]
    public async Task WhenARoleHidesAValueMap_ThenAPageExpressionCannotApplyIt(bool hidesMap, string expected)
    {
        // Arrange
        var graph = await CreateGraphAsync($$$"""
            :: {{{typeof(ValueMap).FullName}}}#busy
            ::: Case When="value > 20" Text="warm"
            ::

            State: [{{ /Temperature | map:busy }}]
            """);
        var role = new AuthorizationRole
        {
            Name = "nomap",
            Includes = [BuiltInRoles.OperatorName],
            Deny = hidesMap ? [new PermissionRule { Paths = ["/Storage/Notes.md/busy/**"], Actions = [PermissionAction.Read] }] : []
        };
        var tools = CreateClientTools(graph, TestAccessPolicies.Create(role.Name, role));

        // Act
        var evaluated = await ReadEvaluatedContentAsync(tools, includeSourcePaths: false);

        // Assert
        Assert.Equal(expected, evaluated?.Split('\n')[^1]);
    }

    [Fact]
    public async Task WhenAnOperationTakesThePermissionsOfItsReader_ThenTheyDenyEverything()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        IUserPermissions? permissions = null;
        AddMethod(graph, "Heat", MethodKind.Operation, resolved => permissions = (IUserPermissions?)resolved[0]);
        var tools = CreateClientTools(graph, TestAccessPolicies.Create(BuiltInRoles.OperatorName));

        // Act
        var result = JsonSerializer.Serialize(await tools[HomeBlazeMcpToolProvider.InvokeOperationToolName].Handler(
            JsonSerializer.SerializeToElement(new { path = "/", method = "Heat" }), CancellationToken.None));

        // Assert
        Assert.Contains("\"success\":true", result);
        Assert.Same(DenyAllPermissions.Instance, permissions);
    }

    [Fact]
    public async Task WhenAQueryTakesThePermissionsOfItsReader_ThenTheyFollowThePolicyOfTheCaller()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        var root = graph.Root.TryGetRegisteredSubject()!;
        var humidity = root.TryGetProperty(nameof(AgentTestRoot.Humidity))!;
        var temperature = root.TryGetProperty(nameof(AgentTestRoot.Temperature))!;
        IUserPermissions? permissions = null;
        AddMethod(graph, "Check", MethodKind.Query, resolved => permissions = (IUserPermissions?)resolved[0]);
        var tools = CreateClientTools(graph, TestAccessPolicies.Create("no-humidity", CreateNoHumidityRole()));

        // Act
        var result = JsonSerializer.Serialize(await tools[HomeBlazeMcpToolProvider.InvokeQueryToolName].Handler(
            JsonSerializer.SerializeToElement(new { path = "/", method = "Check" }), CancellationToken.None));

        // Assert
        Assert.Contains("\"success\":true", result);
        Assert.NotNull(permissions);
        Assert.False(permissions.CanRead(humidity));
        Assert.True(permissions.CanRead(temperature));
        Assert.True(permissions.CanShow(graph.Storage.TryGetRegisteredSubject()!));
        Assert.False(permissions.CanAdminister(temperature));
        Assert.False(permissions.HoldsAllRoles([BuiltInRoles.ReaderName]));
    }

    [Fact]
    public async Task WhenAQueryTakesThePermissionsOfItsReader_ThenTheyNeitherWriteNorInvoke()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes");
        var root = graph.Root.TryGetRegisteredSubject()!;
        var temperature = root.TryGetProperty(nameof(AgentTestRoot.Temperature))!;
        IUserPermissions? permissions = null;
        AddMethod(graph, "Check", MethodKind.Query, resolved => permissions = (IUserPermissions?)resolved[0]);
        AddMethod(graph, "Heat", MethodKind.Operation, _ => { });
        var tools = CreateClientTools(graph, TestAccessPolicies.Create(BuiltInRoles.OperatorName));

        // Act
        await tools[HomeBlazeMcpToolProvider.InvokeQueryToolName].Handler(
            JsonSerializer.SerializeToElement(new { path = "/", method = "Check" }), CancellationToken.None);

        // Assert
        Assert.NotNull(permissions);
        Assert.True(permissions.CanRead(temperature));
        Assert.False(permissions.CanWrite(temperature));
        Assert.False(permissions.CanInvoke(root.TryGetProperty("Heat")!));
    }

    // A method of the root that takes the permissions of its reader as a service.
    private static void AddMethod(Graph graph, string name, MethodKind kind, Action<object?[]> invoked) =>
        graph.Root.TryGetRegisteredSubject()!.AddProperty<MethodMetadata>(name, _ => new MethodMetadata(resolved =>
        {
            invoked(resolved ?? []);
            return null;
        })
        {
            Kind = kind,
            Title = name,
            PropertyName = name,
            Parameters = [new MethodParameter { Name = "permissions", Type = typeof(IUserPermissions), IsFromServices = true }]
        });

    private static AuthorizationRole CreateNoHumidityRole() => new()
    {
        Name = "no-humidity",
        Includes = [BuiltInRoles.OperatorName],
        Deny = [new PermissionRule { Members = [nameof(AgentTestRoot.Humidity)], Actions = [PermissionAction.Read] }]
    };

    [Fact]
    public async Task WhenAnAgentNamesACustomRole_ThenItsUsedByAgentsFollowsTheRolesAndResetsWhenTheAgentIsRemoved()
    {
        // Arrange
        var graph = await CreateGraphAsync("# Notes", new AuthorizationRole { Name = "custom", Includes = [BuiltInRoles.ReaderName] });
        var role = Assert.IsType<Authorization.Authorization>(graph.Root.Children["Authorization"]).Roles["custom"];
        var agent = new FakeAgent(graph.AgentServices) { Roles = ["custom"] };

        // Act
        await AttachAndWaitForStartAsync(graph, agent);
        var countWhenStarted = role.UsedByAgents;
        agent.Roles = [BuiltInRoles.OperatorName];
        var countAfterChange = role.UsedByAgents;
        agent.Roles = ["custom", BuiltInRoles.ReaderName];
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
        var configuration = HomeBlazeMcpConfigurationFactory.Create(graph.Services, () => graph.Root, accessPolicy);
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
        var configuration = McpBuilderExtensions.CreateServerConfiguration(graph.Services, () => graph.Root);
        return new McpToolFactory(graph.Root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }

    private static async Task AttachAndWaitForStartAsync(Graph graph, FakeAgent agent)
    {
        // The hosted service handler starts the agent once it is attached.
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>(graph.Root.Children) { ["Agent"] = agent };
        await AsyncTestHelpers.WaitUntilAsync(
            () => agent.Status is not (ServiceStatus.Stopped or ServiceStatus.Starting), message: "The agent should finish starting.");
    }

    private async Task<Graph> CreateGraphAsync(string notes, params AuthorizationRole[] roles)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory.FullName, "Notes.md"), notes);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(ValueMap).Assembly);
        typeProvider.AddAssembly(typeof(Authorization.Authorization).Assembly);
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
        services.AddSingleton<IMcpClientAccess>(new TestMcpClientAccess(BuiltInRoles.ReaderName));
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

        var authorization = new Authorization.Authorization();
        authorization.ReplaceRoles(roles);
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
        // The hosted service handler starts the storage once it is attached, which connects it. A second connect from
        // here would rebuild the subjects of the storage, possibly while a test already reads them.
        await AsyncTestHelpers.WaitUntilAsync(
            () => storage.Status == StorageStatus.Connected, message: "The storage should connect.");

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
        public IReadOnlyList<McpToolInfo> CreateTools(IMcpAccessPolicy accessPolicy)
        {
            var configuration = HomeBlazeMcpConfigurationFactory.Create(serviceProvider, root, accessPolicy);
            return new McpToolFactory(root, configuration).CreateTools();
        }
    }

    private sealed class AsyncDisposable(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}

/// <summary>A device whose title is derived from a configuration property, as most devices have it.</summary>
[InterceptorSubject]
public partial class TitledTestDevice : ITitleProvider
{
    [Configuration]
    public partial string? Name { get; set; }

    [Derived]
    public string? Title => Name;
}
