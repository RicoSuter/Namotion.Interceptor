using Namotion.Devices.Tesla.Polling;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaVehiclePollingPolicyTests
{
    private static readonly TeslaPollingSettings Settings = new(
        ActiveInterval: TimeSpan.FromMinutes(1),
        IdleInterval: TimeSpan.FromMinutes(5),
        IdleTimeout: TimeSpan.FromMinutes(15),
        SleepWindow: TimeSpan.FromMinutes(21));

    private static readonly DateTimeOffset Start = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TeslaVehicleState.Asleep)]
    [InlineData(TeslaVehicleState.Offline)]
    [InlineData(TeslaVehicleState.Unknown)]
    public void WhenVehicleIsNotOnline_ThenItIsNeverRead(TeslaVehicleState state)
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();

        // Act & Assert
        Assert.False(policy.ShouldRead(state, Start, Settings));
    }

    [Fact]
    public void WhenVehicleComesOnline_ThenItIsReadImmediately()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();

        // Act & Assert
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start, Settings));
    }

    [Fact]
    public void WhenVehicleIsActive_ThenItIsReadEveryActiveInterval()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        policy.OnRead(Start, isActive: true, canSleep: false, Settings);

        // Act & Assert
        Assert.False(policy.ShouldRead(TeslaVehicleState.Online, Start.AddSeconds(59), Settings));
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(1), Settings));
    }

    [Fact]
    public void WhenVehicleIsIdle_ThenItIsReadEveryIdleInterval()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        policy.OnRead(Start, isActive: false, canSleep: true, Settings);

        // Act & Assert
        Assert.False(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(4), Settings));
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(5), Settings));
    }

    [Fact]
    public void WhenVehicleWasIdleForTheTimeout_ThenReadingPausesForTheSleepWindow()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        for (var minute = 0; minute <= 15; minute += 5)
        {
            policy.OnRead(Start.AddMinutes(minute), isActive: false, canSleep: true, Settings);
        }

        // Act & Assert
        Assert.False(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(20), Settings));
        Assert.False(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(35), Settings));
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(36), Settings));
    }

    [Fact]
    public void WhenSomethingKeepsTheVehicleAwake_ThenNoSleepWindowStarts()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        for (var minute = 0; minute <= 30; minute += 5)
        {
            policy.OnRead(Start.AddMinutes(minute), isActive: false, canSleep: false, Settings);
        }

        // Act & Assert
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(35), Settings));
    }

    [Fact]
    public void WhenVehicleCannotSleepInBetween_ThenTheIdleTimeoutStartsOver()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        policy.OnRead(Start, isActive: false, canSleep: true, Settings);
        policy.OnRead(Start.AddMinutes(5), isActive: false, canSleep: false, Settings);
        policy.OnRead(Start.AddMinutes(10), isActive: false, canSleep: true, Settings);
        policy.OnRead(Start.AddMinutes(15), isActive: false, canSleep: true, Settings);

        // Act & Assert
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(20), Settings));
    }

    [Fact]
    public void WhenActivityIsObservedInTheSleepWindow_ThenReadingResumes()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        for (var minute = 0; minute <= 15; minute += 5)
        {
            policy.OnRead(Start.AddMinutes(minute), isActive: false, canSleep: true, Settings);
        }

        // Act
        policy.OnActivityObserved();

        // Assert
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(20), Settings));
    }

    [Fact]
    public void WhenVehicleFellAsleep_ThenTheNextWakeIsReadImmediately()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        policy.OnRead(Start, isActive: false, canSleep: true, Settings);
        policy.ShouldRead(TeslaVehicleState.Asleep, Start.AddMinutes(1), Settings);

        // Act & Assert
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(2), Settings));
    }

    [Fact]
    public void WhenVehicleFellAsleepInTheSleepWindow_ThenTheNextWakeIsReadImmediately()
    {
        // Arrange
        var policy = new TeslaVehiclePollingPolicy();
        for (var minute = 0; minute <= 15; minute += 5)
        {
            policy.OnRead(Start.AddMinutes(minute), isActive: false, canSleep: true, Settings);
        }

        policy.ShouldRead(TeslaVehicleState.Asleep, Start.AddMinutes(25), Settings);

        // Act & Assert
        Assert.True(policy.ShouldRead(TeslaVehicleState.Online, Start.AddMinutes(30), Settings));
    }
}
