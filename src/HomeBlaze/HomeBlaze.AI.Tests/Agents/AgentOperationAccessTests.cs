using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Mcp;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentOperationAccessTests
{
    [Theory]
    [InlineData("RunNow", true)]
    [InlineData("NewConversation", true)]
    [InlineData("RunNow", false)]
    [InlineData("NewConversation", false)]
    public async Task WhenAnAgentOrAnExternalClientCallsAnOperationOfAnAgent_ThenItIsRefused(string methodName, bool isAgent)
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var invokeOperation = CreateTools(services, root, isAgent)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent", method = methodName }), CancellationToken.None));

        // Assert
        Assert.Equal(HomeBlazeMcpConfigurationFactory.AgentOperationMessage, result.GetProperty("error").GetString());
        Assert.Equal(0, agent.ResetCount);
    }

    [Fact]
    public async Task WhenAnExternalClientReadsAnAgent_ThenItIsNotFound()
    {
        // Arrange
        var (root, _, services) = CreateGraph();
        var tools = CreateTools(services, root, isAgent: false);

        // Act
        var browse = JsonSerializer.SerializeToElement(await tools["browse"].Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent", format = "json" }), CancellationToken.None));
        var getProperty = JsonSerializer.SerializeToElement(await tools["get_property"].Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent/Status" }), CancellationToken.None));
        var rootBrowse = JsonSerializer.SerializeToElement(await tools["browse"].Handler(
            JsonSerializer.SerializeToElement(new { format = "json", depth = 1 }), CancellationToken.None));

        // Assert
        Assert.StartsWith("Path not found", browse.GetProperty("error").GetString());
        Assert.StartsWith("Path not found", getProperty.GetProperty("error").GetString());
        Assert.False(rootBrowse.GetProperty("result").GetProperty("properties").TryGetProperty("Agent", out _));
        Assert.Equal(0, rootBrowse.GetProperty("subjectCount").GetInt32());
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("/Agent")]
    public async Task WhenAnAgentSetsTheMetadataOfAnAgent_ThenItIsRefused(string path)
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = new { path, values = new { navTitle = "x" } } }),
            CancellationToken.None));

        // Assert
        Assert.Equal(AgentMetadataAccess.AgentMetadataMessage, result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenAnAgentSetsTheMetadataOfAPropertyOfAnAgent_ThenItIsRefused()
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new
            {
                path = "/Storage", method = "SetMetadata", parameters = new { path = "Agent", property = "Instructions", values = new { comment = "x" } }
            }),
            CancellationToken.None));

        // Assert
        Assert.Equal(AgentMetadataAccess.AgentMetadataMessage, result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenAnAgentSetsTheMetadataOfASubjectInsideAnAgent_ThenItIsRefused()
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };
        var inner = new AgentTestRoot();
        agent.TryGetRegisteredSubject()!.AddProperty("Inner", typeof(AgentTestRoot), _ => inner, null);
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = new { path = "Agent/Inner", values = new { navTitle = "x" } } }),
            CancellationToken.None));

        // Assert
        Assert.Equal(AgentMetadataAccess.AgentMetadataMessage, result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenAnAgentSetsTheMetadataOfASubjectThatAnAgentHoldsAsAnotherParent_ThenItIsRefused()
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage, ["Agent"] = agent };
        var shared = new AgentTestRoot();
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Shared"] = shared };
        agent.TryGetRegisteredSubject()!.AddProperty("Shared", typeof(AgentTestRoot), _ => shared, null);
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = new { path = "Shared", values = new { navTitle = "x" } } }),
            CancellationToken.None));

        // Assert
        Assert.NotSame(agent, shared.TryGetRegisteredSubject()!.Parents[0].Property.Subject);
        Assert.Equal(AgentMetadataAccess.AgentMetadataMessage, result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false, new string[0])]
    [InlineData(true, new string[0])]
    public async Task WhenMetadataIsSearched_ThenNoClientFindsMetadataOfAgents(bool isAgent, string[] expectedPaths)
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };
        var metadata = ImmutableDictionary<string, JsonElement>.Empty.Add("tags", JsonElement.Parse("""["floor-2"]"""));
        agent.TryGetRegisteredSubject()!.AddProperty(KnownAttributes.Metadata, metadata.GetType(), _ => metadata, null);
        var invokeQuery = CreateTools(services, root, isAgent)["invoke_query"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeQuery.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SearchMetadata", parameters = new { tag = "floor-2" } }),
            CancellationToken.None));

        // Assert
        Assert.Equal(expectedPaths, result.GetProperty("result").EnumerateArray().Select(match => match.GetProperty("path").GetString()));
    }

    [Theory]
    [InlineData("Hidden")]
    [InlineData("Hidden/Inner")]
    [InlineData("Missing")]
    public async Task WhenAnAgentSetsTheMetadataOfASubjectHiddenFromAgents_ThenItIsRefusedAsAMissingOne(string path)
    {
        // Arrange
        var (root, _, services) = CreateGraph(typeof(AgentExcludedThing));
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        var hidden = new AgentExcludedThing();
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Hidden"] = hidden };
        var inner = new AgentTestRoot();
        hidden.TryGetRegisteredSubject()!.AddProperty("Inner", typeof(AgentTestRoot), _ => inner, null);
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = new { path, values = new { navTitle = "x" } } }),
            CancellationToken.None));

        // Assert
        Assert.Equal($"No subject at '{path}' in this storage.", result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("Hidden")]
    [InlineData("Missing")]
    public async Task WhenAnAgentSetsNoJsonObjectAsTheMetadataOfASubjectHiddenFromAgents_ThenItIsRefusedAsForAMissingOne(string path)
    {
        // Arrange
        var (root, _, services) = CreateGraph(typeof(AgentExcludedThing));
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Hidden"] = new AgentExcludedThing() };
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = new { path, values = new[] { "x" } } }),
            CancellationToken.None));

        // Assert
        Assert.Equal(HomeBlazeMcpConfigurationFactory.SetMetadataArgumentsMessage, result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{ "path": "Thing", "values": ["x"] }""")]
    [InlineData("""{ "path": "Thing", "values": null }""")]
    [InlineData("""{ "path": "Thing", "values": "x" }""")]
    [InlineData("""{ "values": { "navTitle": "x" } }""")]
    [InlineData("""{ "path": null, "values": { "navTitle": "x" } }""")]
    public async Task WhenAnAgentSetsMetadataWithArgumentsOfAnotherShape_ThenItIsRefusedBeforeTheMethodRuns(string parameters)
    {
        // Arrange
        var (root, _, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Thing"] = new AgentTestRoot() };
        var invokeOperation = CreateTools(services, root, isAgent: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = JsonElement.Parse(parameters) }),
            CancellationToken.None));

        // Assert
        Assert.Equal(HomeBlazeMcpConfigurationFactory.SetMetadataArgumentsMessage, result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenAnExternalClientSetsTheMetadataOfAnAgent_ThenItIsRefused()
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var storage = CreateStorage(services);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Storage"] = storage };
        storage.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };
        var invokeOperation = CreateTools(services, root, isAgent: false)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Storage", method = "SetMetadata", parameters = new { path = "Agent", values = new { navTitle = "x" } } }),
            CancellationToken.None));

        // Assert
        Assert.Equal(AgentMetadataAccess.AgentMetadataMessage, result.GetProperty("error").GetString());
    }

    private static (AgentTestRoot Root, FakeAgent Agent, IServiceProvider Services) CreateGraph(params Type[] types)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer)
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
        var root = new AgentTestRoot(context);
        context.AddService<ISubjectPathResolver>(new SubjectPathResolver(() => root));
        var agentServices = AgentTestContext.CreateServices(root);
        var agent = new FakeAgent(agentServices);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(types);
        var services = new ServiceCollection()
            .AddSingleton(new SubjectTypeRegistry(typeProvider))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IInterceptorSubjectContext>(context)
            .AddHomeBlazeAuthorization(McpAuthorization.PathProvider)
            .BuildServiceProvider();
        return (root, agent, services);
    }

    // A storage that is not connected, which holds subjects only as the test assigns them.
    private static FluentStorageContainer CreateStorage(IServiceProvider services)
    {
        var typeRegistry = services.GetRequiredService<SubjectTypeRegistry>();
        return new FluentStorageContainer(typeRegistry, new ConfigurableSubjectSerializer(new TypeProvider(), services), services);
    }

    // The tools of an agent, with the policy agents have today, or of the external MCP server.
    private static Dictionary<string, McpToolInfo> CreateTools(IServiceProvider services, AgentTestRoot root, bool isAgent)
    {
        var configuration = isAgent
            ? HomeBlazeMcpConfigurationFactory.Create(services, () => root, isReadOnly: false, McpAuthorization.GetAccessPolicy(services, [BuiltInRoles.OperatorName]))
            : McpBuilderExtensions.CreateServerConfiguration(services, () => root, isReadOnly: false);
        return new McpToolFactory(root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }
}
