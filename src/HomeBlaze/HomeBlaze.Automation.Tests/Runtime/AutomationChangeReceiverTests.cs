using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationChangeReceiverTests
{
    // The only listener of the stream is unbounded, so that the dispatcher leaves out every graph change until a
    // listener releases the one it was given.
    [Fact]
    public async Task WhenTheWorkerTakesTheQueuedGraphChange_ThenTheNextGraphChangeIsQueuedAgain()
    {
        // Arrange
        var graph = new TestGraph();
        var holder = new TestSensorHolder();
        graph.Add("Holder", holder);
        var queue = new AutomationInputQueue();
        var receiver = new AutomationChangeReceiver(queue);
        using var registration = receiver.Register(new AutomationChangeDispatcher(), graph.Context);
        holder.Sensor = new TestSensor();
        var first = await queue.ReadAsync(CancellationToken.None);

        // Act
        receiver.OnGraphChangeTaken();
        holder.Sensor = new TestSensor();

        // Assert
        Assert.IsType<GraphChangedInput>(first);
        Assert.True(queue.HasWork);
        Assert.IsType<GraphChangedInput>(await queue.ReadAsync(CancellationToken.None));
    }
}
