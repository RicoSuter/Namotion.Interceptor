using System.Globalization;
using System.Net;
using System.Web;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiActionTests
{
    [Theory]
    [InlineData("Lock", 2, 3, DoorLockState.Locking, DoorLockState.Unlocked)]
    [InlineData("Unlock", 1, 1, DoorLockState.Unlocking, DoorLockState.Locked)]
    [InlineData("Unlatch", 3, 3, DoorLockState.Unlatching, DoorLockState.Unlocked)]
    [InlineData("LockNGo", 4, 1, DoorLockState.Unlocking, DoorLockState.Locked)]
    [InlineData("LockNGoWithUnlatch", 5, 1, DoorLockState.Unlatching, DoorLockState.Locked)]
    public async Task WhenAnActionRuns_ThenItSendsItsNumberAndShowsItsTransitionUntilTheBridgeReportsTheState(
        string operation, int action, int reportedState, DoorLockState transition, DoorLockState reported)
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: reportedState) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        Func<Task> startAction = operation switch
        {
            "Lock" => () => test.Lock.LockAsync(CancellationToken.None),
            "Unlock" => () => test.Lock.UnlockAsync(CancellationToken.None),
            "Unlatch" => () => test.Lock.UnlatchAsync(CancellationToken.None),
            "LockNGo" => () => test.Lock.LockNGoAsync(CancellationToken.None),
            "LockNGoWithUnlatch" => () => test.Lock.LockNGoWithUnlatchAsync(CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.")
        };

        // Act
        await RunHeldActionAsync(fake, startAction, () => test.Lock.LockState == transition);

        // Assert
        var query = HttpUtility.ParseQueryString(fake.Requests.Single(uri => uri.AbsolutePath == "/lockAction").Query);
        Assert.Equal(action.ToString(CultureInfo.InvariantCulture), query["action"]);
        Assert.Equal("1", query["nukiId"]);
        Assert.Equal("0", query["deviceType"]);
        Assert.Equal(reported, test.Lock.LockState);
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
    public async Task WhenAnActionIsCanceledWhileWaitingForAnotherAction_ThenItEndsWithoutReadingTheListOrChangingTheState()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;
        var heldAction = test.Lock.LockAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Lock.LockState == DoorLockState.Locking, message: "The first action was not sent.");
        var listRequests = fake.CountRequests("list");
        using var cancellation = new CancellationTokenSource();
        var waitingAction = test.Lock.UnlatchAsync(cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => waitingAction.IsCompleted, message: "The canceled action did not end while the other one runs.");

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAction);
        Assert.Equal(listRequests, fake.CountRequests("list"));
        Assert.Equal(DoorLockState.Locking, test.Lock.LockState);
        Assert.Equal(1, fake.CountRequests("lockAction"));

        gate.SetResult();
        await heldAction;
    }

    [Fact]
    public async Task WhenAPollRunsDuringAnAction_ThenTheRequestsDoNotOverlap()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();

        // Act
        await RunHeldActionAsync(
            fake,
            () => test.Lock.LockAsync(CancellationToken.None),
            () => test.Lock.LockState == DoorLockState.Locking,
            whileHeld: () => test.Bridge.RefreshAsync(CancellationToken.None));

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

        // Act
        await RunHeldActionAsync(
            fake,
            () => test.Opener.OpenDoorAsync(CancellationToken.None),
            () => test.Opener.OpenerState == NukiOpenerState.Opening);

        // Assert
        var query = HttpUtility.ParseQueryString(fake.Requests.Single(uri => uri.AbsolutePath == "/lockAction").Query);
        Assert.Equal("3", query["action"]);
        Assert.Equal("2", query["deviceType"]);
        Assert.Equal(NukiOpenerState.Online, test.Opener.OpenerState);
    }

    // Starts an action that the bridge holds, waits until it shows its transition, starts whileHeld, then lets the bridge
    // finish the action and waits for both.
    private static async Task RunHeldActionAsync(FakeNukiBridge fake, Func<Task> startAction, Func<bool> showsTransition, Func<Task>? whileHeld = null)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;
        var action = startAction();
        await AsyncTestHelpers.WaitUntilAsync(showsTransition);
        var held = whileHeld?.Invoke() ?? Task.CompletedTask;
        gate.SetResult();
        await Task.WhenAll(action, held);
    }
}
