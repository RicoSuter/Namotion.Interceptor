using System.Reactive.Concurrency;
using HomeBlaze.Virtual.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

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

    [Fact]
    public async Task WhenARecalculationThrows_ThenTheAwaiterLogsAWarningAndRecalculatesTheOthers()
    {
        // Arrange
        var logger = new RecordingLogger<VirtualPropertyAwaiter>();
        var graph = new VirtualTestGraph(logger);
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        var (failing, _) = AddCountingProperty(graph, "Failing");
        var (waiting, getCount) = AddCountingProperty(graph, "Count");
        using var _ = ThrowOnChangeOf(graph, "Failing");
        graph.Awaiter.Await(meter, "Wind", failing);
        graph.Awaiter.Await(meter, "Wind", waiting);
        var countBefore = getCount();

        // Act
        meter.TryGetRegisteredSubject()!.AddProperty("Wind", typeof(decimal?), _ => 1m, null);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Warnings.Count == 1 && getCount() > countBefore, message: "The recalculations did not complete");
        Assert.Contains("Failing", logger.Warnings[0]);
    }

    [Fact]
    public async Task WhenTheRecalculationForAPropertyAddedEarlierThrows_ThenTheAwaiterLogsAWarning()
    {
        // Arrange
        var logger = new RecordingLogger<VirtualPropertyAwaiter>();
        var graph = new VirtualTestGraph(logger);
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        var (failing, _) = AddCountingProperty(graph, "Failing");
        meter.TryGetRegisteredSubject()!.AddProperty("Wind", typeof(decimal?), _ => 1m, null);
        using var _ = ThrowOnChangeOf(graph, "Failing");

        // Act
        graph.Awaiter.Await(meter, "Wind", failing);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Warnings.Count == 1, message: "The failed recalculation was not logged");
    }

    [Fact]
    public void WhenAPropertyOfADetachedVirtualSubjectMisses_ThenNoWaitIsKept()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter());
        var solar = graph.CreateVirtualSubject(new Dictionary<string, VirtualProperty>
        {
            ["Double"] = new() { Expression = "/Meter/Wind * 2", DataType = VirtualDataType.Number }
        });
        graph.Add("Solar", solar);
        graph.Remove("Solar");

        // Act
        var value = ((IInterceptorSubject)solar).Properties["Double"].GetValue?.Invoke(solar);

        // Assert
        Assert.Null(value);
        Assert.Equal(0, graph.Awaiter.WaitCount);
    }

    // A synchronous observer that throws fails the recalculation that publishes the change.
    private static IDisposable ThrowOnChangeOf(VirtualTestGraph graph, string propertyName) =>
        graph.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == propertyName)
            {
                throw new InvalidOperationException("The observer failed.");
            }
        });

    // The getter gives a new value with each evaluation, so each recalculation publishes a change.
    private static (PropertyReference Property, Func<int> GetCount) AddCountingProperty(VirtualTestGraph graph, string name = "Count")
    {
        if (!graph.Root.Children.TryGetValue("Holder", out var holder))
        {
            holder = new TestMeter();
            graph.Add("Holder", holder);
        }

        var count = 0;
        var property = holder.TryGetRegisteredSubject()!
            .AddDerivedProperty(name, typeof(int), _ => Interlocked.Increment(ref count), null);
        return (property.Reference, () => Volatile.Read(ref count));
    }
}
