using HomeBlaze.Virtual.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Virtual.Tests;

public class VirtualPropertyAwaiterTests
{
    [Fact]
    public async Task WhenTheAwaitedPropertyIsAdded_ThenTheWaitingPropertyRecalculates()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        var (waiting, getCount) = AddCountingProperty(graph);
        graph.Awaiter.Await(meter, "Wind", waiting);
        var countBefore = getCount();

        // Act
        meter.TryGetRegisteredSubject()!.AddProperty("Wind", typeof(decimal?), _ => 1m, null);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => getCount() > countBefore, message: "The waiting property did not recalculate");
    }

    [Fact]
    public async Task WhenThePropertyWasAddedBeforeTheMissIsAwaited_ThenTheWaitingPropertyRecalculates()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        var (waiting, getCount) = AddCountingProperty(graph);
        meter.TryGetRegisteredSubject()!.AddProperty("Wind", typeof(decimal?), _ => 1m, null);
        var countBefore = getCount();

        // Act
        graph.Awaiter.Await(meter, "Wind", waiting);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => getCount() > countBefore, message: "The waiting property did not recalculate");
    }

    private static (PropertyReference Property, Func<int> GetCount) AddCountingProperty(VirtualTestGraph graph)
    {
        var holder = new TestMeter();
        graph.Add("Holder", holder);
        var count = 0;
        var property = holder.TryGetRegisteredSubject()!
            .AddDerivedProperty("Count", typeof(int), _ => Interlocked.Increment(ref count), null);
        return (property.Reference, () => Volatile.Read(ref count));
    }
}
