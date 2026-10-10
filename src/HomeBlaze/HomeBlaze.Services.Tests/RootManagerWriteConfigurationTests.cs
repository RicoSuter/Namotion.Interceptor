using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

public class RootManagerWriteConfigurationTests : IDisposable
{
    private readonly string _configurationFilePath =
        Path.Combine(Path.GetTempPath(), $"homeblaze-root-{Guid.NewGuid():N}.json");

    private readonly ServiceProvider _serviceProvider = new ServiceCollection().BuildServiceProvider();
    private readonly IInterceptorSubjectContext _context = SubjectContextFactory.Create(new ServiceCollection());
    private readonly RootManager _rootManager;

    public RootManagerWriteConfigurationTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ParentWithChildSubject), typeof(ChildSubject)]);

        var configuration = new Mock<IConfiguration>();
        configuration
            .Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey])
            .Returns(_configurationFilePath);

        RootManager? rootManager = null;
        _rootManager = rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _serviceProvider),
            _context,
            new SubjectPathResolver(() => rootManager!.Root),
            configuration.Object);
    }

    [Fact]
    public async Task WhenSubjectNestedInRootConfigurationIsWritten_ThenRootConfigurationIsWritten()
    {
        // Arrange
        var child = new ChildSubject(_context) { ChildConfig = "changed" };
        _rootManager.Root = new ParentWithChildSubject(_context) { Child = child };

        // Act
        var isWritten = await _rootManager.WriteConfigurationAsync(child, CancellationToken.None);

        // Assert
        Assert.True(isWritten);
        Assert.Contains("\"childConfig\": \"changed\"", await File.ReadAllTextAsync(_configurationFilePath));
    }

    [Fact]
    public async Task WhenSubjectOutsideRootConfigurationIsWritten_ThenNothingIsWritten()
    {
        // Arrange
        _rootManager.Root = new ParentWithChildSubject(_context);
        var outside = new ChildSubject(_context);

        // Act
        var isWritten = await _rootManager.WriteConfigurationAsync(outside, CancellationToken.None);

        // Assert
        Assert.False(isWritten);
        Assert.False(File.Exists(_configurationFilePath));
    }

    public void Dispose()
    {
        _rootManager.Dispose();
        _serviceProvider.Dispose();
        File.Delete(_configurationFilePath);
    }
}
