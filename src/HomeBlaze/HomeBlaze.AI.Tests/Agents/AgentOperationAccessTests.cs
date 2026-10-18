using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
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
    [InlineData("RunNow")]
    [InlineData("NewConversation")]
    public async Task WhenAnAgentCallsAnOperationOfAnAgent_ThenItIsRefused(string methodName)
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var invokeOperation = CreateTools(services, root, forAgents: true)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent", method = methodName }), CancellationToken.None));

        // Assert
        Assert.Equal("Agents cannot run operations of agents.", result.GetProperty("error").GetString());
        Assert.Equal(0, agent.ResetCount);
    }

    [Fact]
    public async Task WhenAnAgentCallsAnOperationOfTheMetadataOfAnAgent_ThenItIsRefusedAndItsQueriesRun()
    {
        // Arrange
        var (root, agent, services) = CreateGraph();
        var metadataSubject = new FakeMetadataSubject(agent);
        agent.TryGetRegisteredSubject()!.AddProperty<IMetadataSubject>(IMetadataSubject.PropertyName, _ => metadataSubject);
        var tools = CreateTools(services, root, forAgents: true);

        // Act
        var operationResult = JsonSerializer.SerializeToElement(await tools["invoke_operation"].Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent/HomeBlaze:Metadata", method = "SetOrder", parameters = new { names = new[] { "a" } } }),
            CancellationToken.None));
        var queryResult = JsonSerializer.SerializeToElement(await tools["invoke_query"].Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent/HomeBlaze:Metadata", method = "GetOrder" }), CancellationToken.None));

        // Assert
        Assert.Equal("Agents cannot run operations of agents.", operationResult.GetProperty("error").GetString());
        Assert.Equal(0, metadataSubject.GetSetOrderCount());
        Assert.True(queryResult.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task WhenAnExternalClientCallsAnOperationOfAnAgent_ThenItRuns()
    {
        // Arrange
        var (root, _, services) = CreateGraph();
        var invokeOperation = CreateTools(services, root, forAgents: false)["invoke_operation"];

        // Act
        var result = JsonSerializer.SerializeToElement(await invokeOperation.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Agent", method = "RunNow" }), CancellationToken.None));

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
    }

    private static (AgentTestRoot Root, FakeAgent Agent, IServiceProvider Services) CreateGraph()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer)
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
        var root = new AgentTestRoot(context);
        var agentServices = AgentTestContext.CreateServices(root);
        var agent = new FakeAgent(agentServices);
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Agent"] = agent };

        var services = new ServiceCollection()
            .AddSingleton(new SubjectTypeRegistry(new TypeProvider()))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        return (root, agent, services);
    }

    private static Dictionary<string, McpToolInfo> CreateTools(IServiceProvider services, AgentTestRoot root, bool forAgents)
    {
        var configuration = HomeBlazeMcpConfigurationFactory.Create(services, () => root, isReadOnly: false, forAgents);
        return new McpToolFactory(root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }
}

[InterceptorSubject]
public partial class FakeMetadataSubject : IMetadataSubject
{
    private readonly IInterceptorSubject _owner;
    private int _setOrderCount;

    public FakeMetadataSubject(IInterceptorSubject owner)
    {
        _owner = owner;
    }

    public SubjectMetadata? SubjectMetadata => null;

    public int GetSetOrderCount() => _setOrderCount;

    public IInterceptorSubject GetOwner() => _owner;

    public Task SetMetadataAsync(JsonElement values, CancellationToken cancellationToken) => Task.CompletedTask;

    [Query]
    public Task<string[]> GetOrderAsync(CancellationToken cancellationToken) => Task.FromResult<string[]>([]);

    [Operation]
    public Task SetOrderAsync(string[] names, CancellationToken cancellationToken)
    {
        _setOrderCount++;
        return Task.CompletedTask;
    }
}
