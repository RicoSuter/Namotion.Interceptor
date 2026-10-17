using HomeBlaze.AI.Agents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenASessionOptionsCallbackIsAlreadyConfigured_ThenItStillRuns(bool addAgentsFirst)
    {
        // Arrange
        var previousCallCount = 0;
        var services = new ServiceCollection();
        if (addAgentsFirst)
        {
            services.AddHomeBlazeAgents();
        }

        services.AddMcpServer().WithHttpTransport(options =>
        {
            options.Stateless = true;
            options.ConfigureSessionOptions = (_, _, _) =>
            {
                previousCallCount++;
                return Task.CompletedTask;
            };
        });

        if (!addAgentsFirst)
        {
            services.AddHomeBlazeAgents();
        }

        await using var serviceProvider = services.BuildServiceProvider();
        var transportOptions = serviceProvider.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
        Assert.NotNull(transportOptions.ConfigureSessionOptions);

        // Act
        await transportOptions.ConfigureSessionOptions(new DefaultHttpContext(), new McpServerOptions(), CancellationToken.None);

        // Assert
        Assert.Equal(1, previousCallCount);
    }

    [Fact]
    public void WhenAddedTwice_ThenTheAgentServicesAreRegisteredOnce()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddHomeBlazeAgents();
        services.AddHomeBlazeAgents();

        // Assert
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AgentServices));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPostConfigureOptions<HttpServerTransportOptions>));
    }
}
