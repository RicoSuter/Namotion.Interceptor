using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class RootManagerWriteConfigurationTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _configurationFilePath =
        Path.Combine(Path.GetTempPath(), $"homeblaze-root-{Guid.NewGuid():N}.json");

    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

    private readonly List<IDisposable> _disposables = [];

    [Fact]
    public async Task WhenSubjectNestedInRootConfigurationIsWritten_ThenRootConfigurationIsWritten()
    {
        // Arrange
        var rootManager = await LoadRootAsync();
        var child = ((ParentWithChildSubject)rootManager.Root!).Child!;
        child.ChildConfig = "changed";

        // Act
        var isWritten = await rootManager.WriteConfigurationAsync(child, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"childConfig\": \"changed\"", await File.ReadAllTextAsync(_configurationFilePath));
    }

    [Fact]
    public async Task WhenSubjectOutsideRootConfigurationIsWritten_ThenNothingIsWritten()
    {
        // Arrange
        var rootManager = await LoadRootAsync();
        var originalContent = await File.ReadAllTextAsync(_configurationFilePath);
        ((ParentWithChildSubject)rootManager.Root!).Name = "changed";
        var unrelated = new ChildSubject(_context);

        // Act
        var isWritten = await rootManager.WriteConfigurationAsync(unrelated, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.Equal(originalContent, await File.ReadAllTextAsync(_configurationFilePath));
    }

    private async Task<RootManager> LoadRootAsync()
    {
        await File.WriteAllTextAsync(_configurationFilePath, $$"""
            {
              "$type": "{{typeof(ParentWithChildSubject).FullName}}",
              "name": "root",
              "child": {
                "$type": "{{typeof(ChildSubject).FullName}}",
                "childConfig": "loaded"
              }
            }
            """);

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ParentWithChildSubject), typeof(ChildSubject)]);

        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        var configuration = new Mock<IConfiguration>();
        configuration
            .Setup(instance => instance["HomeBlaze:RootConfigFile"])
            .Returns(_configurationFilePath);

        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, _context, pathResolver, configuration.Object);

        _disposables.Add(serviceProvider);
        _disposables.Add(rootManager);

        await rootManager.StartAsync(CancellationToken.None);
        await rootManager.RootLoaded.WaitAsync(WaitTimeout);
        return rootManager;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        File.Delete(_configurationFilePath);
    }
}
