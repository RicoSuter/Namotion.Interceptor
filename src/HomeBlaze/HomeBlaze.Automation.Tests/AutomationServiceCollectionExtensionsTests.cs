using HomeBlaze.Authorization;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Automation.Tests;

public class AutomationServiceCollectionExtensionsTests
{
    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SubjectPathResolver(() => null));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new TestGraph().Context);
        return services;
    }

    [Fact]
    public void WhenAutomationIsAdded_ThenTheRolesDecideAndAnAutomationCanBeCreatedFromTheServices()
    {
        // Arrange
        var services = CreateServices();
        services.AddHomeBlazeAuthorization(new DefaultPathProvider('/'));

        // Act
        services.AddHomeBlazeAutomation();
        using var provider = services.BuildServiceProvider();
        using var automation = ActivatorUtilities.CreateInstance<Automation>(provider);

        // Assert
        Assert.IsType<RoleAutomationAuthorizer>(automation.Authorizer);
        Assert.Same(TimeProvider.System, automation.TimeProvider);
        Assert.True(automation.Enabled);
        Assert.Empty(automation.Roles);
    }

    [Fact]
    public void WhenAutomationIsAdded_ThenEveryAutomationUsesTheRegisteredChangeDispatcher()
    {
        // Arrange
        var services = CreateServices();
        services.AddSingleton<IAutomationAuthorizer, AllowAllAutomationAuthorizer>();
        services.AddHomeBlazeAutomation();
        using var provider = services.BuildServiceProvider();

        // Act
        using var first = ActivatorUtilities.CreateInstance<Automation>(provider);
        using var second = ActivatorUtilities.CreateInstance<Automation>(provider);

        // Assert
        Assert.Same(provider.GetRequiredService<AutomationChangeDispatcher>(), first.ChangeDispatcher);
        Assert.Same(first.ChangeDispatcher, second.ChangeDispatcher);
    }

    [Fact]
    public void WhenNoChangeDispatcherIsRegistered_ThenTheAutomationHasOneOfItsOwn()
    {
        // Arrange
        using var provider = new ServiceCollection().BuildServiceProvider();

        // Act
        using var automation = new Automation(
            new SubjectPathResolver(() => null), TimeProvider.System, new AllowAllAutomationAuthorizer(), provider, NullLogger<Automation>.Instance);

        // Assert
        Assert.NotNull(automation.ChangeDispatcher);
    }

    [Fact]
    public void WhenAnAuthorizerIsRegisteredFirst_ThenItIsKept()
    {
        // Arrange
        var services = CreateServices();
        services.AddSingleton<IAutomationAuthorizer, AllowAllAutomationAuthorizer>();

        // Act
        services.AddHomeBlazeAutomation();
        using var provider = services.BuildServiceProvider();
        using var automation = ActivatorUtilities.CreateInstance<Automation>(provider);

        // Assert
        Assert.IsType<AllowAllAutomationAuthorizer>(automation.Authorizer);
    }
}
