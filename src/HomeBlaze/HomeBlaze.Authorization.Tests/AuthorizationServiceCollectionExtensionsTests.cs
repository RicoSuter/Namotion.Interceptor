using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class AuthorizationServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenAddedTwice_ThenOneServiceResolvesOverTheRegisteredContext()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(TestContext.Create());

        // Act
        services.AddHomeBlazeAuthorization(new DefaultPathProvider('/'));
        services.AddHomeBlazeAuthorization(new DefaultPathProvider('/'));
        using var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetRequiredService<AuthorizationService>();

        // Assert
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AuthorizationService));
        Assert.Null(service.GetPolicy([BuiltInRoles.ReaderName]).Error);
    }
}
