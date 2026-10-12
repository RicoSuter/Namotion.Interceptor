using System.Text.Json;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
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
        var configuration = forAgents
            ? HomeBlazeMcpConfigurationFactory.CreateForAgents(services, () => root, isReadOnly: false)
            : HomeBlazeMcpConfigurationFactory.Create(services, () => root, isReadOnly: false);
        return new McpToolFactory(root, configuration).CreateTools().ToDictionary(tool => tool.Name);
    }
}
