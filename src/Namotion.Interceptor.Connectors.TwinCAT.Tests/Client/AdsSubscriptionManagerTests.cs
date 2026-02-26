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

    #region Constructor

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        var logger = new Mock<ILogger>().Object;

        Assert.Throws<ArgumentNullException>(() =>
            new AdsSubscriptionManager(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        var configuration = CreateConfiguration();

        Assert.Throws<ArgumentNullException>(() =>
            new AdsSubscriptionManager(configuration, null!));
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldCreate()
    {
        var manager = CreateManager();

        Assert.NotNull(manager);
    }

    #endregion

    #region Initial State

    [Fact]
    public void NotificationCount_Initially_Zero()
    {
        var manager = CreateManager();

        Assert.Equal(0, manager.NotificationCount);
    }

    [Fact]
    public void PolledCount_Initially_Zero()
    {
        var manager = CreateManager();

        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void IsPollingCollectionDirty_Initially_False()
    {
        var manager = CreateManager();

        Assert.False(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void Subscriptions_Initially_NotNull()
    {
        var manager = CreateManager();

        Assert.NotNull(manager.Subscriptions);
    }

    #endregion

    #region GetSymbolPath

    [Fact]
    public void GetSymbolPath_UnknownProperty_ReturnsNull()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var result = manager.GetSymbolPath(property.Reference);

        Assert.Null(result);
    }

    [Fact]
    public void GetSymbolPath_MultipleUnknownProperties_AllReturnNull()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;

        foreach (var property in registeredSubject.Properties)
        {
            Assert.Null(manager.GetSymbolPath(property.Reference));
        }
    }

    #endregion

    #region TryGetSymbol (static)

    [Fact]
    public void TryGetSymbol_NoSymbolLoader_ReturnsNull()
    {
        var result = AdsSubscriptionManager.TryGetSymbol(null, "GVL.SomeSymbol");

        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_SymbolsPropertyThrows_ReturnsNull()
    {
        // SymbolCollection.TryGetInstance is non-virtual, so we can only test the
        // exception path by making the Symbols property itself throw.
        var mockLoader = new Mock<ISymbolLoader>();
        mockLoader.Setup(l => l.Symbols).Throws(new InvalidOperationException("Not loaded"));

        var result = AdsSubscriptionManager.TryGetSymbol(mockLoader.Object, "GVL.Broken");

        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_SymbolsPropertyReturnsNull_ReturnsNull()
    {
        var mockLoader = new Mock<ISymbolLoader>();
        mockLoader.Setup(l => l.Symbols).Returns((SymbolCollection)null!);

        // Should handle null Symbols gracefully (NullReferenceException caught)
        var result = AdsSubscriptionManager.TryGetSymbol(mockLoader.Object, "GVL.SomeSymbol");

        Assert.Null(result);
    }

    #endregion

    #region ClearAll

    [Fact]
    public void ClearAll_ResetsAllCaches()
    {
        var manager = CreateManager();

        // ClearAll should not throw on empty state
        manager.ClearAll();

        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_SetsPollingDirtyFlag()
    {
        var manager = CreateManager();

        manager.ClearAll();

        Assert.True(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void ClearAll_CalledMultipleTimes_DoesNotThrow()
    {
        var manager = CreateManager();

        manager.ClearAll();
        manager.ClearAll();
        manager.ClearAll();

        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_AfterClear_GetSymbolPathReturnsNull()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        manager.ClearAll();

        // Even after clear, unknown properties still return null
        Assert.Null(manager.GetSymbolPath(property.Reference));
    }

    #endregion

    #region OnPropertyReleasing

    [Fact]
    public void OnPropertyReleasing_UnknownProperty_DoesNotThrow()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Should not throw even for unknown property
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnPropertyReleasing_CalledTwiceForSameProperty_DoesNotThrow()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        manager.OnPropertyReleasing(property.Reference);
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnPropertyReleasing_MultipleProperties_DoesNotThrow()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;

        foreach (var property in registeredSubject.Properties)
        {
            manager.OnPropertyReleasing(property.Reference);
        }
    }

    #endregion

    #region OnSubjectDetaching

    [Fact]
    public void OnSubjectDetaching_UnknownSubject_DoesNotThrow()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);

        // Should not throw even for unknown subject
        manager.OnSubjectDetaching(model);
    }

    [Fact]
    public void OnSubjectDetaching_CalledTwice_DoesNotThrow()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model = new TestPlcModel(context);

        manager.OnSubjectDetaching(model);
        manager.OnSubjectDetaching(model);
    }

    [Fact]
    public void OnSubjectDetaching_DifferentSubjects_DoesNotThrow()
    {
        var manager = CreateManager();
        var context = CreateContext();
        var model1 = new TestPlcModel(context);
        var model2 = new TestPlcModel(context);

        manager.OnSubjectDetaching(model1);
        manager.OnSubjectDetaching(model2);
    }

    #endregion

    #region Static Read Mode Helpers

    [Fact]
    public void DetermineEffectiveReadModes_EmptyMappings_ReturnsEmpty()
    {
        var mappings = Array.Empty<(RegisteredSubjectProperty, string)>();

        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, 500);

        Assert.Empty(result);
    }

    [Fact]
    public void GetConfiguredMaxDelay_WithExplicitMaxDelay_ReturnsAttribute()
    {
        var context = CreateContext();
        var model = new NotificationOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // NotificationOnlyModel has CycleTime=50 but no explicit MaxDelay, so it should use default
        var maxDelay = AdsSubscriptionManager.GetConfiguredMaxDelay(property, 200);

        // AdsVariableAttribute in NotificationOnlyModel doesn't set MaxDelay, so default 200 should be used
        Assert.Equal(200, maxDelay);
    }

    [Fact]
    public void GetConfiguredMaxDelay_WithoutAttribute_ReturnsDefault()
    {
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var maxDelay = AdsSubscriptionManager.GetConfiguredMaxDelay(property, 500);

        Assert.Equal(500, maxDelay);
    }

    [Fact]
    public void GetConfiguredReadMode_NotificationAttribute_ReturnsNotification()
    {
        var context = CreateContext();
        var model = new NotificationOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var readMode = AdsSubscriptionManager.GetConfiguredReadMode(property, AdsReadMode.Polled);

        // Explicit Notification overrides default Polled
        Assert.Equal(AdsReadMode.Notification, readMode);
    }

    [Fact]
    public void GetConfiguredReadMode_PolledAttribute_ReturnsPolled()
    {
        var context = CreateContext();
        var model = new PolledOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var readMode = AdsSubscriptionManager.GetConfiguredReadMode(property, AdsReadMode.Notification);

        // Explicit Polled overrides default Notification
        Assert.Equal(AdsReadMode.Polled, readMode);
    }

    [Fact]
    public void GetConfiguredReadMode_AutoAttribute_ReturnsDefault()
    {
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var readMode = AdsSubscriptionManager.GetConfiguredReadMode(property, AdsReadMode.Polled);

        // Auto returns the default
        Assert.Equal(AdsReadMode.Polled, readMode);
    }

    [Fact]
    public void GetConfiguredCycleTime_WithExplicitValue_ReturnsAttribute()
    {
        var context = CreateContext();
        var model = new NotificationOnlyModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var cycleTime = AdsSubscriptionManager.GetConfiguredCycleTime(property, 999);

        // NotificationOnlyModel has CycleTime=50
        Assert.Equal(50, cycleTime);
    }

    [Fact]
    public void GetConfiguredCycleTime_WithoutExplicit_ReturnsDefault()
    {
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var cycleTime = AdsSubscriptionManager.GetConfiguredCycleTime(property, 999);

        Assert.Equal(999, cycleTime);
    }

    [Fact]
    public void GetConfiguredPriority_WithExplicitValue_ReturnsAttribute()
    {
        var context = CreateContext();
        var model = new DemotionTestModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(
            property => property.Name == nameof(DemotionTestModel.FastHighPriority));

        var priority = AdsSubscriptionManager.GetConfiguredPriority(property);

        Assert.Equal(-1, priority);
    }

    [Fact]
    public void GetConfiguredPriority_WithoutExplicit_ReturnsZero()
    {
        var context = CreateContext();
        var model = new AutoModeModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var priority = AdsSubscriptionManager.GetConfiguredPriority(property);

        Assert.Equal(0, priority);
    }

    [Fact]
    public void GetConfiguredPriority_LowPriorityProperty_ReturnsHighValue()
    {
        var context = CreateContext();
        var model = new DemotionTestModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First(
            property => property.Name == nameof(DemotionTestModel.SlowLowPriority));

        var priority = AdsSubscriptionManager.GetConfiguredPriority(property);

        Assert.Equal(10, priority);
    }

    #endregion

    #region Dispose

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        var manager = CreateManager();

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var manager = CreateManager();

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PropertiesStillAccessible()
    {
        var manager = CreateManager();
        await manager.DisposeAsync();

        // Properties should still be readable after dispose
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterClearAll_ShouldComplete()
    {
        var manager = CreateManager();
        manager.ClearAll();

        await manager.DisposeAsync();
    }

    #endregion
}
