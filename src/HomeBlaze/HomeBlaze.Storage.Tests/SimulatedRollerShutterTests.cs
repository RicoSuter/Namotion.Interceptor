using HomeBlaze.Abstractions.Devices.Covers;
using HomeBlaze.Samples;

namespace HomeBlaze.Storage.Tests;

public class SimulatedRollerShutterTests
{
    // Long enough that a move does not take a step while the test runs.
    private static readonly TimeSpan PendingDuration = TimeSpan.FromHours(1);

    [Theory]
    [InlineData(RollerShutterState.Open, false, true, false, false, false)]
    [InlineData(RollerShutterState.Closed, true, false, false, false, false)]
    [InlineData(RollerShutterState.PartiallyOpen, true, true, false, false, false)]
    [InlineData(RollerShutterState.Opening, false, true, true, true, false)]
    [InlineData(RollerShutterState.Closing, true, false, true, false, true)]
    public void WhenShutterIsInAState_ThenItsOperationsAreEnabledAndActiveForIt(
        RollerShutterState state, bool openEnabled, bool closeEnabled, bool stopEnabled, bool openActive, bool closeActive)
    {
        // Arrange
        var shutter = new SimulatedRollerShutter();

        // Act
        shutter.ShutterState = state;

        // Assert
        Assert.Equal(openEnabled, shutter.Open_IsEnabled);
        Assert.Equal(closeEnabled, shutter.Close_IsEnabled);
        Assert.Equal(stopEnabled, shutter.Stop_IsEnabled);
        Assert.Equal(openActive, shutter.Open_IsActive);
        Assert.Equal(closeActive, shutter.Close_IsActive);
    }

    [Fact]
    public async Task WhenMoveEnds_ThenTheShutterReachesItsTarget()
    {
        // Arrange
        var shutter = new SimulatedRollerShutter { TravelDuration = TimeSpan.Zero };

        // Act
        await shutter.CloseAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1m, shutter.Position);
        Assert.Equal(RollerShutterState.Closed, shutter.ShutterState);
    }

    [Fact]
    public async Task WhenShutterIsStoppedWhileItMoves_ThenItStandsStillAndNoOperationIsActive()
    {
        // Arrange
        var shutter = new SimulatedRollerShutter { TravelDuration = PendingDuration };
        await shutter.CloseAsync(CancellationToken.None);
        var stateWhileMoving = shutter.ShutterState;

        // Act
        await shutter.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(RollerShutterState.Closing, stateWhileMoving);
        Assert.False(shutter.IsMoving);
        Assert.False(shutter.Close_IsActive);
        Assert.False(shutter.Stop_IsEnabled);
    }

    [Fact]
    public async Task WhenShutterIsSetToTheMiddle_ThenItEndsPartiallyOpen()
    {
        // Arrange
        var shutter = new SimulatedRollerShutter { TravelDuration = TimeSpan.Zero };

        // Act
        await shutter.SetPositionAsync(0.5m, CancellationToken.None);

        // Assert
        Assert.Equal(0.5m, shutter.Position);
        Assert.Equal(RollerShutterState.PartiallyOpen, shutter.ShutterState);
    }
}
