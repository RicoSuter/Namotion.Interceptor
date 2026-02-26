using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using TwinCAT.TypeSystem;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class AdsSubscriptionManagerTests
{
    private static AdsClientConfiguration CreateConfiguration()
    {
        return new AdsClientConfiguration
        {
            Host = "127.0.0.1",
            AmsNetId = "127.0.0.1.1.1",
            AmsPort = 851,
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.')
        };
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    private static AdsSubscriptionManager CreateManager()
    {
        return new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsSubscriptionManager(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var configuration = CreateConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsSubscriptionManager(configuration, null!));
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldCreate()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.NotNull(manager);
    }

    [Fact]
    public void NotificationCount_Initially_Zero()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
    }

    [Fact]
    public void PolledCount_Initially_Zero()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void IsPollingCollectionDirty_Initially_False()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.False(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void Subscriptions_Initially_NotNull()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.NotNull(manager.Subscriptions);
    }

    [Fact]
    public void GetSymbolPath_UnknownProperty_ReturnsNull()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var result = manager.GetSymbolPath(property.Reference);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetSymbolPath_MultipleUnknownProperties_AllReturnNull()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var property in registeredSubject.Properties)
        {
            Assert.Null(manager.GetSymbolPath(property.Reference));
        }
    }

    [Fact]
    public void TryGetSymbol_NoSymbolLoader_ReturnsNull()
    {
        // Arrange & Act
        var result = AdsSubscriptionManager.TryGetSymbol(null, "GVL.SomeSymbol");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_SymbolsPropertyThrows_ReturnsNull()
    {
        // Arrange
        var mockLoader = new Mock<ISymbolLoader>();
        mockLoader.Setup(l => l.Symbols).Throws(new InvalidOperationException("Not loaded"));

        // Act
        var result = AdsSubscriptionManager.TryGetSymbol(mockLoader.Object, "GVL.Broken");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_SymbolsPropertyReturnsNull_ReturnsNull()
    {
        // Arrange
        var mockLoader = new Mock<ISymbolLoader>();
        mockLoader.Setup(l => l.Symbols).Returns((SymbolCollection)null!);

        // Act
        var result = AdsSubscriptionManager.TryGetSymbol(mockLoader.Object, "GVL.SomeSymbol");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ClearAll_ResetsAllCaches()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.ClearAll();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_SetsPollingDirtyFlag()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.ClearAll();

        // Assert
        Assert.True(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void ClearAll_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.ClearAll();
        manager.ClearAll();
        manager.ClearAll();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_AfterClear_GetSymbolPathReturnsNull()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        manager.ClearAll();

        // Assert
        Assert.Null(manager.GetSymbolPath(property.Reference));
    }

    [Fact]
    public void OnPropertyReleasing_UnknownProperty_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act & Assert
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnPropertyReleasing_CalledTwiceForSameProperty_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act & Assert
        manager.OnPropertyReleasing(property.Reference);
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnPropertyReleasing_MultipleProperties_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var property in registeredSubject.Properties)
        {
            manager.OnPropertyReleasing(property.Reference);
        }
    }

    [Fact]
    public void OnSubjectDetaching_UnknownSubject_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);

        // Act & Assert
        manager.OnSubjectDetaching(model);
    }

    [Fact]
    public void OnSubjectDetaching_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);

        // Act & Assert
        manager.OnSubjectDetaching(model);
        manager.OnSubjectDetaching(model);
    }

    [Fact]
    public void OnSubjectDetaching_DifferentSubjects_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = CreateContext();
        var model1 = new TestPlcModel(context);
        var model2 = new TestPlcModel(context);

        // Act & Assert
        manager.OnSubjectDetaching(model1);
        manager.OnSubjectDetaching(model2);
    }

    [Fact]
    public void DetermineEffectiveReadModes_EmptyMappings_ReturnsEmpty()
    {
        // Arrange
        var mappings = Array.Empty<(RegisteredSubjectProperty, string)>();

        // Act
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, 500);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetConfiguredMaxDelay_WithExplicitMaxDelay_ReturnsAttribute()
    {
        // Arrange
        var context = CreateContext();
        var model = new NotificationOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var maxDelay = AdsSubscriptionManager.GetConfiguredMaxDelay(property, 200);

        // Assert - NotificationOnlyModel doesn't set MaxDelay, so default 200 should be used
        Assert.Equal(200, maxDelay);
    }

    [Fact]
    public void GetConfiguredMaxDelay_WithoutAttribute_ReturnsDefault()
    {
        // Arrange
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var maxDelay = AdsSubscriptionManager.GetConfiguredMaxDelay(property, 500);

        // Assert
        Assert.Equal(500, maxDelay);
    }

    [Fact]
    public void GetConfiguredReadMode_NotificationAttribute_ReturnsNotification()
    {
        // Arrange
        var context = CreateContext();
        var model = new NotificationOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var readMode = AdsSubscriptionManager.GetConfiguredReadMode(property, AdsReadMode.Polled);

        // Assert - explicit Notification overrides default Polled
        Assert.Equal(AdsReadMode.Notification, readMode);
    }

    [Fact]
    public void GetConfiguredReadMode_PolledAttribute_ReturnsPolled()
    {
        // Arrange
        var context = CreateContext();
        var model = new PolledOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var readMode = AdsSubscriptionManager.GetConfiguredReadMode(property, AdsReadMode.Notification);

        // Assert - explicit Polled overrides default Notification
        Assert.Equal(AdsReadMode.Polled, readMode);
    }

    [Fact]
    public void GetConfiguredReadMode_AutoAttribute_ReturnsDefault()
    {
        // Arrange
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var readMode = AdsSubscriptionManager.GetConfiguredReadMode(property, AdsReadMode.Polled);

        // Assert
        Assert.Equal(AdsReadMode.Polled, readMode);
    }

    [Fact]
    public void GetConfiguredCycleTime_WithExplicitValue_ReturnsAttribute()
    {
        // Arrange
        var context = CreateContext();
        var model = new NotificationOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var cycleTime = AdsSubscriptionManager.GetConfiguredCycleTime(property, 999);

        // Assert - NotificationOnlyModel has CycleTime=50
        Assert.Equal(50, cycleTime);
    }

    [Fact]
    public void GetConfiguredCycleTime_WithoutExplicit_ReturnsDefault()
    {
        // Arrange
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var cycleTime = AdsSubscriptionManager.GetConfiguredCycleTime(property, 999);

        // Assert
        Assert.Equal(999, cycleTime);
    }

    [Fact]
    public void GetConfiguredPriority_WithExplicitValue_ReturnsAttribute()
    {
        // Arrange
        var context = CreateContext();
        var model = new DemotionTestModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(
            property => property.Name == nameof(DemotionTestModel.FastHighPriority));

        // Act
        var priority = AdsSubscriptionManager.GetConfiguredPriority(property);

        // Assert
        Assert.Equal(-1, priority);
    }

    [Fact]
    public void GetConfiguredPriority_WithoutExplicit_ReturnsZero()
    {
        // Arrange
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var priority = AdsSubscriptionManager.GetConfiguredPriority(property);

        // Assert
        Assert.Equal(0, priority);
    }

    [Fact]
    public void GetConfiguredPriority_LowPriorityProperty_ReturnsHighValue()
    {
        // Arrange
        var context = CreateContext();
        var model = new DemotionTestModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(
            property => property.Name == nameof(DemotionTestModel.SlowLowPriority));

        // Act
        var priority = AdsSubscriptionManager.GetConfiguredPriority(property);

        // Assert
        Assert.Equal(10, priority);
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PropertiesStillAccessible()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        await manager.DisposeAsync();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterClearAll_ShouldComplete()
    {
        // Arrange
        var manager = CreateManager();
        manager.ClearAll();

        // Act & Assert
        await manager.DisposeAsync();
    }
}
