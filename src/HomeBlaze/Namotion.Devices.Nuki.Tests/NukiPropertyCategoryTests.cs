using System.Text.Json;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Nuki.Model;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

/// <summary>
/// Reads the property panel categories of representative Nuki members, the way the panel resolves them:
/// <c>RegisteredSubjectProperty.GetStateMetadata()</c> for state properties and
/// <c>RegisteredSubjectProperty.TryGetMethodMetadata()</c> for operations.
/// </summary>
public class NukiPropertyCategoryTests
{
    [Theory]
    [InlineData(nameof(NukiSmartLock.LockState), "Lock")]
    [InlineData("IsLocked", "Lock")]
    [InlineData(nameof(NukiSmartLock.DoorState), "Door")]
    [InlineData(nameof(NukiSmartLock.BatteryLevel), "Battery")]
    public void WhenALockStatePropertyHasACategory_ThenThePanelReadsIt(string propertyName, string expectedCategory)
    {
        // Arrange
        var bridge = CreateBridgeWithDevices();
        var smartLock = bridge.Locks["1"];
        var property = smartLock.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

        // Act
        var category = property.GetStateMetadata()?.Category;

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    [Fact]
    public void WhenTheUnlockOperationHasACategory_ThenThePanelReadsIt()
    {
        // Arrange
        var bridge = CreateBridgeWithDevices();
        var smartLock = bridge.Locks["1"];
        var property = smartLock.TryGetRegisteredSubject()!.TryGetProperty("Unlock")!;

        // Act
        var category = property.TryGetMethodMetadata()?.Category;

        // Assert
        Assert.Equal("Lock", category);
    }

    [Fact]
    public void WhenTheOpenerOpenDoorOperationHasACategory_ThenThePanelReadsIt()
    {
        // Arrange
        var bridge = CreateBridgeWithDevices();
        var opener = bridge.Openers["2"];
        var property = opener.TryGetRegisteredSubject()!.TryGetProperty("OpenDoor")!;

        // Act
        var category = property.TryGetMethodMetadata()?.Category;

        // Assert
        Assert.Equal("Opener", category);
    }

    /// <summary>Builds a bridge with a smart lock and an Opener merged in, wired like the host wires its subjects.</summary>
    private static NukiBridge CreateBridgeWithDevices()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer)
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);

        var bridge = new NukiBridge(new FakeNukiBridge(), NullLogger<NukiBridge>.Instance);
        ((IInterceptorSubject)bridge).Context.AddFallbackContext(context);

        bridge.MergeDevices(JsonSerializer.Deserialize(
            NukiFixtures.List, NukiJsonContext.Default.NukiDeviceResponseArray)!);

        return bridge;
    }
}
