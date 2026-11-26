using HomeBlaze.Abstractions;
using HomeBlaze.Services.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

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
        // RootManager takes the resolver as a dependency and puts it on the subject context, so
        // subjects attached while it loads (the history stores) only find it once RootManager has
        // been constructed. Nothing else registers it during startup.
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

    [Fact]
    public void WhenHomeBlazeServicesAreResolved_ThenPathResolverIsRegisteredAsLifecycleHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHomeBlazeServices();
        using var serviceProvider = services.BuildServiceProvider();
        var context = serviceProvider.GetRequiredService<IInterceptorSubjectContext>();

        // Act
        _ = serviceProvider.GetRequiredService<RootManager>();
        var resolver = serviceProvider.GetRequiredService<SubjectPathResolver>();
        var lifecycleHandlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.Contains(resolver, lifecycleHandlers);
        Assert.Single(lifecycleHandlers.OfType<SubjectPathResolver>());
    }

    [Fact]
    public void WhenHomeBlazeServicesAreAdded_ThenTheSecretStoreIsInTheDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HomeBlazePaths.RootConfigurationFileKey] = Path.Combine(dataDirectory, "Root.json")
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHomeBlazeServices();

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<SecretStore>();

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "Secrets.json"), store.Path);
    }
}
