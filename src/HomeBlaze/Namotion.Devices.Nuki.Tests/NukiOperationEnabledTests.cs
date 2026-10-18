using HomeBlaze.Services;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

/// <summary>
/// Reads the enabled state of the operations as the property panel and the widgets do.
/// </summary>
public class NukiOperationEnabledTests
{
    [Theory]
    [InlineData(3, "Lock", true)]
    [InlineData(5, "Lock", true)]
    [InlineData(1, "Lock", false)]
    [InlineData(4, "Lock", false)]
    [InlineData(1, "Unlock", true)]
    [InlineData(3, "Unlock", false)]
    [InlineData(2, "Unlock", false)]
    [InlineData(1, "Unlatch", true)]
    [InlineData(3, "Unlatch", true)]
    [InlineData(254, "Unlatch", false)]
    [InlineData(1, "LockNGo", true)]
    [InlineData(3, "LockNGo", false)]
    [InlineData(1, "LockNGoWithUnlatch", true)]
    [InlineData(1, "ToggleLock", true)]
    [InlineData(3, "ToggleLock", true)]
    [InlineData(7, "ToggleLock", false)]
    public async Task WhenLockIsInAState_ThenOnlyItsPossibleOperationsAreEnabled(int state, string operation, bool expected)
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.SmartLock(state));

        // Act
        test.Bridge.IsConnected = true;

        // Assert
        var property = test.Lock.TryGetRegisteredSubject()!.TryGetProperty(operation)!;
        Assert.Equal(expected, property.IsMethodEnabled());
    }

    [Fact]
    public async Task WhenBridgeIsNotConnected_ThenNoLockOperationIsEnabled()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 3));

        // Assert
        var registered = test.Lock.TryGetRegisteredSubject()!;
        Assert.All(
            ["Lock", "Unlock", "Unlatch", "LockNGo", "LockNGoWithUnlatch", "ToggleLock"],
            operation => Assert.False(registered.TryGetProperty(operation)!.IsMethodEnabled(), operation));
    }

    [Theory]
    [InlineData(1, 2, "OpenDoor", true)]
    [InlineData(7, 2, "OpenDoor", false)]
    [InlineData(1, 2, "ActivateRingToOpen", true)]
    [InlineData(3, 2, "ActivateRingToOpen", false)]
    [InlineData(3, 2, "DeactivateRingToOpen", true)]
    [InlineData(1, 2, "ActivateContinuousMode", true)]
    [InlineData(1, 3, "ActivateContinuousMode", false)]
    [InlineData(3, 3, "DeactivateContinuousMode", true)]
    public async Task WhenOpenerIsInAState_ThenOnlyItsPossibleOperationsAreEnabled(int state, int mode, string operation, bool expected)
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.Opener(state, mode));

        // Act
        test.Bridge.IsConnected = true;

        // Assert
        var property = test.Opener.TryGetRegisteredSubject()!.TryGetProperty(operation)!;
        Assert.Equal(expected, property.IsMethodEnabled());
    }

    [Theory]
    [InlineData("Unlock")]
    [InlineData("Unlatch")]
    [InlineData("LockNGoWithUnlatch")]
    public async Task WhenAnOperationLetsSomeoneIn_ThenItRequiresConfirmation(string operation)
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 1));

        // Assert
        var method = (HomeBlaze.Abstractions.Metadata.MethodMetadata)test.Lock.TryGetRegisteredSubject()!.TryGetProperty(operation)!.GetValue()!;
        Assert.True(method.RequiresConfirmation);
    }
}
