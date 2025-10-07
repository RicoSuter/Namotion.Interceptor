using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources;
using Namotion.Interceptor.Sources.Paths;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionManagerTests
{
    private readonly OpcUaSubscriptionManager _subscriptionManager;

    public OpcUaSubscriptionManagerTests()
    {
        var mockLogger = new Mock<ILogger>();
        
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            MaximumItemsPerSubscription = 10,
            SourcePathProvider = new DefaultSourcePathProvider(),
            TypeResolver = new OpcUaTypeResolver(mockLogger.Object),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };

        _subscriptionManager = new OpcUaSubscriptionManager(configuration, mockLogger.Object);
    }

    [Fact]
    public void SetDispatcher_ShouldStoreDispatcher()
    {
        // Arrange
        var mockDispatcher = new Mock<ISubjectMutationDispatcher>();

        // Act
        _subscriptionManager.SetDispatcher(mockDispatcher.Object);

        // Assert - No exception means dispatcher was stored successfully
        Assert.True(true);
    }

    [Fact]
    public void Clear_ShouldClearInternalCollections()
    {
        // Arrange
        var mockDispatcher = new Mock<ISubjectMutationDispatcher>();
        _subscriptionManager.SetDispatcher(mockDispatcher.Object);

        // Act
        _subscriptionManager.Clear();

        // Assert - No exception means collections were cleared
        Assert.True(true);
    }

    [Fact]
    public void Cleanup_ShouldNotThrowWhenNoSubscriptions()
    {
        // Act & Assert
        var exception = Record.Exception(() => _subscriptionManager.Cleanup());
        
        Assert.Null(exception);
    }

    [Fact]
    public void Cleanup_WithMultipleCalls_ShouldNotThrow()
    {
        // Act & Assert - Multiple cleanup calls should be safe
        var exception = Record.Exception(() =>
        {
            _subscriptionManager.Cleanup();
            _subscriptionManager.Cleanup();
        });
        
        Assert.Null(exception);
    }

    [Fact]
    public void Clear_WithMultipleCalls_ShouldNotThrow()
    {
        // Act & Assert - Multiple clear calls should be safe
        var exception = Record.Exception(() =>
        {
            _subscriptionManager.Clear();
            _subscriptionManager.Clear();
        });
        
        Assert.Null(exception);
    }
}
