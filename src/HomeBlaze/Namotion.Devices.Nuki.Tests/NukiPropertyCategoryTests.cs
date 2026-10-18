using HomeBlaze.Services;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
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
    public async Task WhenALockStatePropertyHasACategory_ThenThePanelReadsIt(string propertyName, string expectedCategory)
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.List);
        var property = test.Lock.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;

        // Act
        var category = property.GetStateMetadata()?.Category;

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    [Theory]
    [InlineData("Lock", "Unlock", "Lock")]
    [InlineData("Opener", "OpenDoor", "Opener")]
    public async Task WhenAnOperationHasACategory_ThenThePanelReadsIt(string device, string operation, string expectedCategory)
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.List);
        IInterceptorSubject subject = device == "Opener" ? test.Opener : test.Lock;
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(operation)!;

        // Act
        var category = property.TryGetMethodMetadata()?.Category;

        // Assert
        Assert.Equal(expectedCategory, category);
    }
}
