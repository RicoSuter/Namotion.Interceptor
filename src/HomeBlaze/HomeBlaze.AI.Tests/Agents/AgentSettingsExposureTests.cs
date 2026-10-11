using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Anthropic;
using HomeBlaze.AI.Agents.Cli;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentSettingsExposureTests
{
    [Fact]
    public void WhenAgentsAreInTheGraph_ThenNoneOfTheirConfigurationPropertiesIsReachableThroughTheMcpPathProvider()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
        var root = new AgentTestRoot(context);
        var services = new AgentServices(new TestAgentToolSource(() => root), new AgentTurnTokenRegistry(), new FixedEndpointAddress(null), TimeProvider.System);
        root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Cli"] = new ClaudeCliAgent(services, NullLogger<ClaudeCliAgent>.Instance),
            ["Api"] = new AnthropicAgent(services, NullLogger<AnthropicAgent>.Instance)
        };
        var pathProvider = new StateAttributePathProvider();
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var configurationProperties = root.Children
            .SelectMany(child => child.Value.TryGetRegisteredSubject()!.Properties
                .Where(property => property.TryGetAttribute(KnownAttributes.Configuration) is not null)
                .Select(property => (Path: $"/{child.Key}/{property.Name}", Property: property)))
            .ToList();

        // Assert
        Assert.Contains(configurationProperties, entry => entry.Path == "/Cli/AllowedTools");
        Assert.Contains(configurationProperties, entry => entry.Path == "/Api/Instructions");
        Assert.NotNull(pathProvider.TryGetPropertyFromPath(rootRegistered, "/Cli/RunCount"));
        Assert.All(configurationProperties, entry =>
        {
            Assert.False(pathProvider.IsPropertyIncluded(entry.Property), $"{entry.Path} is exposed through MCP.");
            Assert.Null(pathProvider.TryGetPropertyFromPath(rootRegistered, entry.Path));
        });
    }
}
