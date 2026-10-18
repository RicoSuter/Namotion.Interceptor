using System.Net;
using System.Web;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiActionTests
{
    [Fact]
    public async Task WhenLockRuns_ThenTheTransitionShowsUntilTheBridgeReportsTheResult()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;
        fake.OnAction = () => fake.ListJson = NukiFixtures.SmartLock(state: 1);

        // Act
        var action = test.Lock.LockAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Lock.LockState == DoorLockState.Locking);
        gate.SetResult();
        await action;

        // Assert
        Assert.Equal(DoorLockState.Locked, test.Lock.LockState);
        var query = HttpUtility.ParseQueryString(fake.Requests.Single(uri => uri.AbsolutePath == "/lockAction").Query);
        Assert.Equal("2", query["action"]);
        Assert.Equal("1", query["nukiId"]);
        Assert.Equal("0", query["deviceType"]);
        Assert.Equal("0", query["nowait"]);
    }

    [Theory]
    [InlineData("Unlock", 1, 1, DoorLockState.Unlocking)]
    [InlineData("Unlatch", 3, 3, DoorLockState.Unlatching)]
    [InlineData("LockNGo", 4, 1, DoorLockState.Unlocking)]
    [InlineData("LockNGoWithUnlatch", 5, 1, DoorLockState.Unlatching)]
    public async Task WhenAnActionRuns_ThenItSendsItsNumberAndShowsItsTransition(string operation, int action, int initialState, DoorLockState transition)
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: initialState) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;

        // Act
        var task = operation switch
        {
            "Unlock" => test.Lock.UnlockAsync(CancellationToken.None),
            "Unlatch" => test.Lock.UnlatchAsync(CancellationToken.None),
            "LockNGo" => test.Lock.LockNGoAsync(CancellationToken.None),
            _ => test.Lock.LockNGoWithUnlatchAsync(CancellationToken.None)
        };
        await AsyncTestHelpers.WaitUntilAsync(() => test.Lock.LockState == transition);
        gate.SetResult();
        await task;

        // Assert
        var query = HttpUtility.ParseQueryString(fake.Requests.Single(uri => uri.AbsolutePath == "/lockAction").Query);
        Assert.Equal(action.ToString(System.Globalization.CultureInfo.InvariantCulture), query["action"]);
    }

    [Fact]
    public async Task WhenTheDeviceReportsAFailure_ThenTheActionThrowsAndTheReportedStateReturns()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3), ActionJson = NukiFixtures.ActionFailed };
        await using var test = new TestBridge(fake);
        await test.StartAsync();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Lock.LockAsync(CancellationToken.None));
        Assert.Equal("The lock could not lock the door.", exception.Message);
        Assert.Equal(DoorLockState.Unlocked, test.Lock.LockState);
    }

    [Fact]
    public async Task WhenTheListCannotBeReadAfterTheAction_ThenTheLockStateIsUnknown()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        fake.OnAction = () => fake.StatusOverride = path => path == "list" ? HttpStatusCode.InternalServerError : null;

        // Act
        await test.Lock.LockAsync(CancellationToken.None);

        // Assert
        Assert.Null(test.Lock.LockState);
    }

    [Fact]
    public async Task WhenBridgeIsNotConnected_ThenTheActionThrowsAndTheStateStays()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Merge(NukiFixtures.SmartLock(state: 3));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Lock.LockAsync(CancellationToken.None));
        Assert.Equal(DoorLockState.Unlocked, test.Lock.LockState);
        Assert.Empty(test.Fake.Requests);
    }

    [Fact]
    public async Task WhenAPollRunsDuringAnAction_ThenTheRequestsDoNotOverlap()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;

        // Act
        var action = test.Lock.LockAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Lock.LockState == DoorLockState.Locking);
        var refresh = test.Bridge.RefreshAsync(CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(action, refresh);

        // Assert
        Assert.Equal(1, fake.MaxConcurrentRequests);
    }

    [Fact]
    public async Task WhenOpenerOpensTheDoor_ThenItSendsTheElectricStrikeActuationAndShowsOpening()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.Opener(state: 1) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;

        // Act
        var action = test.Opener.OpenDoorAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Opener.OpenerState == NukiOpenerState.Opening);
        gate.SetResult();
        await action;

        // Assert
        var query = HttpUtility.ParseQueryString(fake.Requests.Single(uri => uri.AbsolutePath == "/lockAction").Query);
        Assert.Equal("3", query["action"]);
        Assert.Equal("2", query["deviceType"]);
        Assert.Equal(NukiOpenerState.Online, test.Opener.OpenerState);
    }
}
