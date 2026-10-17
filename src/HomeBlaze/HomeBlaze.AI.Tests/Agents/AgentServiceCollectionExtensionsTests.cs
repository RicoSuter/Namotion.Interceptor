using HomeBlaze.AI.Agents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentServiceCollectionExtensionsTests
{
    [Fact]
    public async Task WhenTheHostHasAnMcpServer_ThenAddingTheAgentsLeavesItsTransportAsConfigured()
    {
        // Arrange
        Func<HttpContext, McpServerOptions, CancellationToken, Task> configureSessionOptions = (_, _, _) => Task.CompletedTask;
        var services = new ServiceCollection();
        services.AddMcpServer().WithHttpTransport(options => options.ConfigureSessionOptions = configureSessionOptions);

        // Act
        services.AddHomeBlazeAgents();

        // Assert
        await using var serviceProvider = services.BuildServiceProvider();
        var transportOptions = serviceProvider.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
        Assert.Same(configureSessionOptions, transportOptions.ConfigureSessionOptions);
    }

    [Fact]
    public void WhenAddedTwice_ThenTheAgentServicesAndTheListenerAreRegisteredOnce()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddHomeBlazeAgents();
        services.AddHomeBlazeAgents();

        // Assert
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AgentServices));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AgentMcpListener));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }
}
