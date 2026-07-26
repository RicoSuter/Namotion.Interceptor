using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests the startup wiring contract of <see cref="ServiceCollectionExtensions.AddHomeBlazeServices"/>.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void WhenHostedServicesAreResolved_ThenPathResolverIsRegisteredBeforeTheGraphLoads()
    {
        // Arrange
        // SubjectPathResolver adds itself to the subject context in its constructor, so subjects
        // attached by RootManager (the history stores) only find it if the singleton already exists
        // when RootManager starts. Nothing else resolves it during startup.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHomeBlazeServices();
        var serviceProvider = services.BuildServiceProvider();
        var context = serviceProvider.GetRequiredService<IInterceptorSubjectContext>();

        // Act
        _ = serviceProvider.GetServices<IHostedService>().ToArray();

        // Assert
        Assert.NotNull(context.TryGetService<ISubjectPathResolver>());
    }
}
