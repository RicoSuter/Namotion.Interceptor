using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeBlaze.Services.Tests;

public class HttpClientDefaultsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("any")]
    public void WhenHomeBlazeHttpClientsAreAdded_ThenEveryClientConnectsWithTenSecondTimeout(string name)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHomeBlazeHttpClients();
        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name);
        var builder = serviceProvider.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = name;

        // Act
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        // Assert
        var handler = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.NotNull(serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(name));
    }
}
