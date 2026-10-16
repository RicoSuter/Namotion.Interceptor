using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Services.Tests;

public class RootManagerWriteConfigurationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    [Fact]
    public async Task WhenRootIsWrittenTwiceAtOnce_ThenTheFileHoldsTheLaterChange()
    {
        // Arrange
        var child = new ChildSubject(_context) { ChildConfig = "first" };
        _rootManager.Root = new ParentWithChildSubject(_context) { Child = child };
        using var blockingRead = new BlockingReadAfterValue(child, nameof(ChildSubject.ChildConfig));
        _context.AddService<IReadInterceptor>(blockingRead);
        blockingRead.Arm();

        // Act: the first write stops after it has read the old value, and the second starts with the new one then.
        var firstWrite = Task.Run(() => _rootManager.WriteConfigurationAsync(child, CancellationToken.None));
        await blockingRead.Reached.WaitAsync(Timeout);
        child.ChildConfig = "second";
        var secondWrite = _rootManager.WriteConfigurationAsync(child, CancellationToken.None);
        blockingRead.Release();
        var results = await Task.WhenAll(firstWrite, secondWrite).WaitAsync(Timeout);

        // Assert
        Assert.Equal([true, true], results);
        Assert.Contains("\"childConfig\": \"second\"", await File.ReadAllTextAsync(_configurationFilePath));
    }

    /// <summary>Blocks the thread of the next read of one property of one subject, after the value was read, until it is released.</summary>
    private sealed class BlockingReadAfterValue(IInterceptorSubject subject, string propertyName) : IReadInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isArmed;

        public Task Reached => _reached.Task;

        public void Arm() => Volatile.Write(ref _isArmed, 1);

        public void Release() => _released.Set();

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            if (ReferenceEquals(context.Property.Subject, subject) &&
                context.Property.Name == propertyName &&
                Interlocked.Exchange(ref _isArmed, 0) == 1)
            {
                _reached.TrySetResult();
                _released.Wait(Timeout);
            }

            return value;
        }

        public void Dispose()
        {
            _released.Set();
            _released.Dispose();
        }
    }

    public void Dispose()
    {
        _rootManager.Dispose();
        _serviceProvider.Dispose();
        File.Delete(_configurationFilePath);
    }
}
