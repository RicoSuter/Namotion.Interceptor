using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

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
        Assert.IsType<AllowAllAutomationAuthorizer>(provider.GetRequiredService<IAutomationAuthorizer>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<AutomationServices>().TimeProvider);
        Assert.True(automation.Enabled);
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

        // Assert
        Assert.IsType<DenyingAuthorizer>(provider.GetRequiredService<AutomationServices>().Authorizer);
    }

    private sealed class DenyingAuthorizer : IAutomationAuthorizer
    {
        public string? GetOperationDenial(IInterceptorSubject automation, RegisteredSubjectProperty operation) => "denied";

        public string? GetPropertyWriteDenial(IInterceptorSubject automation, RegisteredSubjectProperty property) => "denied";
    }
}
