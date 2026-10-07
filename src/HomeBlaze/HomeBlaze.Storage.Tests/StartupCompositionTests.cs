using HomeBlaze.Abstractions;
using HomeBlaze.Samples;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Storage.Tests;

public class StartupCompositionTests : IDisposable
{
    private readonly DirectoryInfo _rootDirectory = Directory.CreateTempSubdirectory("homeblaze-startup-root-");
    private readonly DirectoryInfo _nestedDirectory = Directory.CreateTempSubdirectory("homeblaze-startup-nested-");

    [Fact]
    public async Task WhenTreeStarts_ThenStartupCompletesOnlyOnceNestedStoragesScannedAndPlaceholdersAreUpgraded()
    {
        // Arrange
        WriteFile(_rootDirectory, "Loader.json", $$"""{ "$type": "{{typeof(TypeLoadingService).FullName}}" }""");
        WriteFile(_rootDirectory, "Nested.json", $$"""
            {
              "$type": "{{typeof(FluentStorageContainer).FullName}}",
              "storageType": "disk",
              "connectionString": {{System.Text.Json.JsonSerializer.Serialize(_nestedDirectory.FullName)}},
              "enableFileWatching": false
            }
            """);
        WriteFile(_nestedDirectory, "Motor1.json", """{ "$type": "HomeBlaze.Samples.Motor", "name": "Nested Motor" }""");

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(FluentStorageContainer), typeof(TypeLoadingService)]);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(context);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<ConfigurableSubjectSerializer>();
        await using var serviceProvider = services.BuildServiceProvider();
        var handler = Assert.Single(serviceProvider.GetServices<IHostedService>());
        var gate = context.GetService<StartupGate>();

        var root = new FluentStorageContainer(typeRegistry, serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(), serviceProvider)
        {
            ConnectionString = _rootDirectory.FullName,
            EnableFileWatching = false
        };

        // Act: attach the root like RootManager does, then let the host run the queued starts.
        ((IInterceptorSubject)root).Context.AddFallbackContext(context);
        gate.CompleteRootLoad();
        await handler.StartAsync(CancellationToken.None);

        try
        {
            // Assert
            await gate.Completed.WaitAsync(TimeSpan.FromSeconds(30));
            var nested = Assert.IsType<FluentStorageContainer>(root.Children["Nested"]);
            var motor = Assert.IsType<Motor>(nested.Children["Motor1"]);
            Assert.Equal("Nested Motor", motor.Name);
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
        }
    }

    private static void WriteFile(DirectoryInfo directory, string name, string content)
    {
        File.WriteAllText(Path.Combine(directory.FullName, name), content);
    }

    public void Dispose()
    {
        _rootDirectory.Delete(recursive: true);
        _nestedDirectory.Delete(recursive: true);
    }
}

/// <summary>
/// Stands in for a plugin provider: defers startup from its start until it added its types.
/// </summary>
[InterceptorSubject]
public partial class TypeLoadingService : BackgroundService, IConfigurable
{
    private readonly TypeProvider _typeProvider;

    public TypeLoadingService(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var startupDeferral = ((IInterceptorSubject)this).Context.DeferStartupCompletion();
        await base.StartAsync(cancellationToken);
        startupDeferral.ReleaseWhenCompleted(ExecuteTask);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lets the nested storage's scan run concurrently, as a download would.
        await Task.Yield();
        _typeProvider.AddAssembly(typeof(Motor).Assembly);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
