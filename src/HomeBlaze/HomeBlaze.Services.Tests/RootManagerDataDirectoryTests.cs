using HomeBlaze.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Tests;

public class RootManagerDataDirectoryTests
{
    [Fact]
    public void WhenRootConfigFileIsSet_ThenConfigurationPathAndDataDirectoryFollowIt()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "homeblaze-instance");
        var rootFile = Path.Combine(dataDirectory, "Root.json");

        // Act
        var (rootManager, _) = CreateRootManager(rootFile);

        // Assert
        Assert.Equal(rootFile, rootManager.ConfigurationPath);
        Assert.Equal(dataDirectory, rootManager.DataDirectory);
    }

    [Fact]
    public void WhenRootManagerIsCreated_ThenItIsRegisteredAsDataDirectoryProvider()
    {
        // Arrange
        var rootFile = Path.Combine(Path.GetTempPath(), "homeblaze-instance", "Root.json");

        // Act
        var (rootManager, context) = CreateRootManager(rootFile);

        // Assert
        Assert.Same(rootManager, context.TryGetService<IDataDirectoryProvider>());
    }

    private static (RootManager RootManager, IInterceptorSubjectContext Context) CreateRootManager(string rootFile)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
        var context = InterceptorSubjectContext.Create();

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(rootFile);

        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, context, pathResolver, configuration.Object);
        return (rootManager, context);
    }
}
