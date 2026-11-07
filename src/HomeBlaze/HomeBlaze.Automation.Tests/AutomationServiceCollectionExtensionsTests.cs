using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Tests;

public class AutomationServiceCollectionExtensionsTests
{
    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SubjectPathResolver(() => null));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    [Fact]
    public void WhenAutomationIsAdded_ThenAnAutomationCanBeCreatedFromTheServices()
    {
        // Arrange
        var services = CreateServices();

        // Act
        services.AddHomeBlazeAutomation();
        using var provider = services.BuildServiceProvider();
        using var automation = ActivatorUtilities.CreateInstance<Automation>(provider);

        // Assert
        Assert.IsType<AllowAllAutomationAuthorizer>(automation.Authorizer);
        Assert.Same(TimeProvider.System, automation.TimeProvider);
        Assert.True(automation.Enabled);
    }

    [Fact]
    public void WhenAutomationIsAdded_ThenEveryAutomationUsesTheRegisteredChangeDispatcher()
    {
        // Arrange
        var services = CreateServices();
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
        services.AddSingleton<IAutomationAuthorizer, DenyingAuthorizer>();

        // Act
        services.AddHomeBlazeAutomation();
        using var provider = services.BuildServiceProvider();
        using var automation = ActivatorUtilities.CreateInstance<Automation>(provider);

        // Assert
        Assert.IsType<DenyingAuthorizer>(automation.Authorizer);
    }
}
