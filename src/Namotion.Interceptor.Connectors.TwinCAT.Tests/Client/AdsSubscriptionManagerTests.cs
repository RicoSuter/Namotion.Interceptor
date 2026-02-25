using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
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
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var manager = new AdsSubscriptionManager(configuration, logger);

        Assert.NotNull(manager);
    }

    [Fact]
    public void NotificationCount_Initially_Zero()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Equal(0, manager.NotificationCount);
    }

    [Fact]
    public void PolledCount_Initially_Zero()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void GetSymbolPath_UnknownProperty_ReturnsNull()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        var result = manager.GetSymbolPath(property.Reference);

        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_NoSymbolLoader_ReturnsNull()
    {
        var result = AdsSubscriptionManager.TryGetSymbol(null, "GVL.SomeSymbol");

        Assert.Null(result);
    }

    [Fact]
    public void ClearAll_ResetsAllCaches()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        // ClearAll should not throw on empty state
        manager.ClearAll();

        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_SetsPollingDirtyFlag()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        manager.ClearAll();

        Assert.True(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void OnPropertyReleasing_UnknownProperty_DoesNotThrow()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);
        var context = CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Should not throw even for unknown property
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnSubjectDetaching_UnknownSubject_DoesNotThrow()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);
        var context = CreateContext();
        var model = new TestPlcModel(context);

        // Should not throw even for unknown subject
        manager.OnSubjectDetaching(model);
    }

    [Fact]
    public void RegisterSubscriptions_EmptyMappings_NoOp()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);
        var mappings = Array.Empty<(RegisteredSubjectProperty, string)>();

        // We can't easily call RegisterSubscriptions without a real connection,
        // but we verify the static DetermineEffectiveReadModes works with empty input
        var result = AdsSubscriptionManager.DetermineEffectiveReadModes(
            mappings, AdsReadMode.Auto, 100, 500);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var manager = new AdsSubscriptionManager(CreateConfiguration(), new Mock<ILogger>().Object);

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }
}
