using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeMergeTests
{
    [Fact]
    public async Task WhenListIsMerged_ThenLocksAndOpenersAreCreatedFromIt()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.List);

        // Assert
        var smartLock = Assert.Single(test.Bridge.Locks).Value;
        Assert.Equal(1L, smartLock.NukiId);
        Assert.Equal(NukiDeviceType.SmartLock, smartLock.DeviceType);
        Assert.Equal("Home", smartLock.Title);
        Assert.Equal("2.8.15", smartLock.SoftwareVersion);
        Assert.Equal(DoorLockState.Locked, smartLock.LockState);
        Assert.Equal(DoorState.Closed, smartLock.DoorState);
        Assert.Equal(0.85m, smartLock.BatteryLevel);
        Assert.Equal(new DateTimeOffset(2018, 10, 3, 6, 49, 0, TimeSpan.Zero), smartLock.LastUpdated);

        var opener = Assert.Single(test.Bridge.Openers).Value;
        Assert.Equal("Community door", opener.Title);
        Assert.Equal(NukiOpenerState.RingToOpenActive, opener.OpenerState);
        Assert.True(opener.IsContinuousModeActive);
        Assert.False(opener.IsRinging);
        Assert.Equal(new DateTimeOffset(2020, 4, 27, 16, 13, 0, TimeSpan.Zero), opener.LastRingTime);
    }

    [Fact]
    public async Task WhenListIsMergedAgain_ThenTheExistingInstancesAndCollectionsAreKept()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.List);
        var locks = test.Bridge.Locks;
        var smartLock = test.Lock;

        // Act
        test.Merge(NukiFixtures.Combine(NukiFixtures.SmartLock(state: 3), NukiFixtures.Opener(state: 1)));

        // Assert
        Assert.Same(locks, test.Bridge.Locks);
        Assert.Same(smartLock, test.Lock);
        Assert.Equal(DoorLockState.Unlocked, smartLock.LockState);
        Assert.Equal(NukiOpenerState.Online, test.Opener.OpenerState);
    }

    [Fact]
    public async Task WhenADeviceIsMissingFromTheList_ThenItIsRemovedAndDetached()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.List);
        var opener = test.Opener;
        Assert.NotNull(opener.TryGetRegisteredSubject());

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 1));

        // Assert
        Assert.Single(test.Bridge.Locks);
        Assert.Empty(test.Bridge.Openers);
        Assert.Null(opener.TryGetRegisteredSubject());
    }

    [Fact]
    public async Task WhenADeviceIsAdded_ThenTheOtherInstancesAreKeptAndTheNewOneIsConnected()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.SmartLock(state: 1));
        test.Bridge.IsConnected = true;
        var smartLock = test.Lock;

        // Act
        test.Merge(NukiFixtures.Combine(NukiFixtures.SmartLock(state: 1), NukiFixtures.SmartLock(state: 3, nukiId: 3)));

        // Assert
        Assert.Same(smartLock, test.Lock);
        var addedLock = test.Bridge.Locks["3"];
        Assert.NotNull(addedLock.TryGetRegisteredSubject());
        Assert.True(addedLock.IsConnected);
        Assert.Equal(DoorLockState.Unlocked, addedLock.LockState);
    }

    [Fact]
    public async Task WhenBridgeReportsNoStateForADevice_ThenTheDeviceIsNotConnectedAndItsStateIsUnknown()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.SmartLockWithoutState());

        // Act
        test.Bridge.IsConnected = true;

        // Assert
        Assert.False(test.Lock.IsConnected);
        Assert.Equal("The bridge reports no state for the device.", test.Lock.StatusMessage);
        Assert.Null(test.Lock.LockState);
        Assert.Null(test.Lock.DoorState);
        Assert.Null(test.Lock.BatteryLevel);
        Assert.Equal("Back door", test.Lock.Title);
    }

    [Fact]
    public async Task WhenSomeoneRingsAtAnOpener_ThenItIsRingingAndItsIconIsAWarning()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.Opener(state: 1, ringing: true));

        // Assert
        Assert.True(test.Opener.IsRinging);
        Assert.Equal("NotificationsActive", test.Opener.IconName);
        Assert.Equal("Warning", test.Opener.IconColor);
    }

    [Theory]
    [InlineData(0, NukiDeviceType.SmartLock)]
    [InlineData(3, NukiDeviceType.SmartDoor)]
    [InlineData(4, NukiDeviceType.SmartLock3)]
    public async Task WhenDeviceIsALockType_ThenItIsALock(int deviceType, NukiDeviceType expected)
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 1, deviceType: deviceType));

        // Assert
        Assert.Equal(expected, test.Lock.DeviceType);
    }

    [Fact]
    public async Task WhenTheTypeOfALockChanges_ThenANewInstanceWithTheNewTypeReplacesIt()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.SmartLock(state: 1, deviceType: 0));
        var smartLock = test.Lock;

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 1, deviceType: 4));

        // Assert
        Assert.NotSame(smartLock, test.Lock);
        Assert.Equal(NukiDeviceType.SmartLock3, test.Lock.DeviceType);
        Assert.Null(smartLock.TryGetRegisteredSubject());
    }

    [Fact]
    public async Task WhenDeviceTypeIsUnknown_ThenItIsSkipped()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 1, deviceType: 9));

        // Assert
        Assert.Empty(test.Bridge.Locks);
        Assert.Empty(test.Bridge.Openers);
    }

    [Fact]
    public async Task WhenLockStateIsMerged_ThenBatteryAndKeypadFlagsAreTaken()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.SmartLock(state: 254, doorSensorState: 3));

        // Assert
        Assert.Equal(DoorLockState.Jammed, test.Lock.LockState);
        Assert.Equal(DoorState.Open, test.Lock.DoorState);
        Assert.Equal(0.42m, test.Lock.BatteryLevel);
        Assert.True(test.Lock.IsBatteryCritical);
        Assert.True(test.Lock.IsBatteryCharging);
        Assert.True(test.Lock.IsKeypadBatteryCritical);
    }

    [Fact]
    public async Task WhenBridgeIsNotConnected_ThenDevicesAreNotConnected()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        test.Merge(NukiFixtures.List);

        // Assert
        Assert.False(test.Lock.IsConnected);
        Assert.Equal("The bridge is not connected.", test.Lock.StatusMessage);
    }

    [Fact]
    public async Task WhenBridgeIsConnected_ThenDevicesWithAStateAreConnected()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.List);

        // Act
        test.Bridge.IsConnected = true;

        // Assert
        Assert.True(test.Lock.IsConnected);
        Assert.True(test.Opener.IsConnected);
    }
}
