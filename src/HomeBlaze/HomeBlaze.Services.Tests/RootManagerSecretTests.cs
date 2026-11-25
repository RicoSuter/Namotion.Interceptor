using HomeBlaze.Services.Secrets;
using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class RootManagerSecretTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("homeblaze-root-secrets-").FullName;

    private string RootPath => Path.Combine(_directory, "Root.json");

    private string SecretsPath => Path.Combine(_directory, "Secrets.json");

    [Fact]
    public async Task WhenRootJsonHoldsAPlaintextSecret_ThenItIsConvertedOnLoad()
    {
        // Arrange
        await File.WriteAllTextAsync(RootPath, RootJson("s3cr3t"));
        var (rootManager, _) = CreateRootManager(new SecretStore(SecretsPath));

        // Act
        await rootManager.StartAsync(CancellationToken.None);
        var root = (SecretTestDevice)await rootManager.RootLoaded.WaitAsync(WaitTimeout);

        // Assert
        var text = await File.ReadAllTextAsync(RootPath);
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("$secret:", text);
        Assert.Equal("s3cr3t", root.Password);
    }

    [Fact]
    public async Task WhenRootJsonHoldsNoPlaintextSecret_ThenItIsNotRewritten()
    {
        // Arrange
        var original = RootJson("$secret:" + SecretStore.CreateKey());
        await File.WriteAllTextAsync(RootPath, original);
        var originalBytes = await File.ReadAllBytesAsync(RootPath);
        var (rootManager, _) = CreateRootManager(new SecretStore(SecretsPath));

        // Act
        await rootManager.StartAsync(CancellationToken.None);
        await rootManager.RootLoaded.WaitAsync(WaitTimeout);

        // Assert
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(RootPath));
    }

    [Fact]
    public async Task WhenTheSecretStoreCannotBeWritten_ThenTheRootLoadsFromTheOriginalTextAndWarns()
    {
        // Arrange
        Directory.CreateDirectory(SecretsPath);
        var original = RootJson("s3cr3t");
        await File.WriteAllTextAsync(RootPath, original);
        var (rootManager, logger) = CreateRootManager(new SecretStore(SecretsPath));

        // Act
        await rootManager.StartAsync(CancellationToken.None);
        var root = (SecretTestDevice)await rootManager.RootLoaded.WaitAsync(WaitTimeout);

        // Assert
        Assert.Equal(original, await File.ReadAllTextAsync(RootPath));
        Assert.Equal("s3cr3t", root.Password);
        logger.Verify(instance => instance.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private static string RootJson(string password) =>
        $$"""{ "$type": "{{typeof(SecretTestDevice).FullName}}", "password": "{{password}}" }""";

    private (RootManager RootManager, Mock<ILogger<RootManager>> Logger) CreateRootManager(SecretStore store)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(SecretTestDevice)]);
        var serviceProvider = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(instance => instance[HomeBlazePaths.RootConfigurationFileKey]).Returns(RootPath);

        var logger = new Mock<ILogger<RootManager>>();
        logger.Setup(instance => instance.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(context, () => rootManager!.Root);
        rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider), serializer, context, pathResolver, configuration.Object, logger.Object);

        _disposables.Add(serviceProvider);
        _disposables.Add(rootManager);
        return (rootManager, logger);
    }

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }
}
