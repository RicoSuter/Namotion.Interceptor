namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceGateTests
{
    [Fact]
    public void WhenOpenIsCalledTwice_ThenStateIsOpen()
    {
        // Arrange
        var gate = new HostedServiceGate();

        // Act
        gate.Open();
        gate.Open();

        // Assert
        Assert.Equal(HostedServiceGateState.Open, gate.State);
    }

    [Fact]
    public void WhenOpenIsCalledWhileDraining_ThenStateStaysDraining()
    {
        // Arrange
        var gate = new HostedServiceGate();
        gate.Open();
        gate.BeginDraining();

        // Act
        gate.Open();

        // Assert - a plain assignment here would reopen the shutdown race the Draining state closes
        Assert.Equal(HostedServiceGateState.Draining, gate.State);
    }

    [Fact]
    public async Task WhenGateIsClosed_ThenWaitDoesNotComplete()
    {
        // Arrange
        var gate = new HostedServiceGate();

        // Act
        var wait = gate.WaitForOpenAsync();

        // Assert
        Assert.False(wait.IsCompleted);
        gate.Open();
        await wait;
    }

    [Fact]
    public async Task WhenDrainingStartsFromClosed_ThenParkedWaitersAreReleased()
    {
        // Arrange - a host that aborts startup never opens the gate; parked transitions must not hang
        var gate = new HostedServiceGate();
        var wait = gate.WaitForOpenAsync();
        Assert.False(wait.IsCompleted);

        // Act
        gate.BeginDraining();

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostedServiceGateState.Draining, gate.State);
    }
}
