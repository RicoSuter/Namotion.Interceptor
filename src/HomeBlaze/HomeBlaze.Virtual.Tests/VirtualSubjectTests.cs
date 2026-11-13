using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Virtual.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Virtual.Tests;

public class VirtualSubjectTests
{
    private static Dictionary<string, VirtualProperty> Entries(params (string Name, VirtualProperty Property)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Property);

    private static VirtualProperty Expression(string text, VirtualDataType dataType = VirtualDataType.Number) => new() { Expression = text, DataType = dataType };

    private static VirtualProperty Binding(string path, VirtualDataType dataType = VirtualDataType.Number) => new() { Path = path, DataType = dataType };

    // Delivered on the thread that publishes the change, so a change made by the test is recorded when the write returns.
    private static ConcurrentQueue<object?> RecordChanges(VirtualTestGraph graph, string propertyName, out IDisposable subscription)
    {
        var changes = new ConcurrentQueue<object?>();
        subscription = graph.Context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == propertyName)
            {
                changes.Enqueue(change.GetNewValue<object?>());
            }
        });
        return changes;
    }

    [Fact]
    public void WhenAnExpressionReadsAProperty_ThenAChangeOfThePropertyPublishesTheNewValue()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 100 };
        graph.Add("Meter", meter);
        graph.Add("Solar", graph.CreateVirtualSubject(Entries(("Double", Expression("/Meter/Power * 2")))));
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        meter.Power = 150;

        // Assert
        Assert.Contains(300m, changes);
    }

    [Fact]
    public async Task WhenTheTargetAppearsAfterTheVirtualSubject_ThenTheValueRecalculates()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Solar", graph.CreateVirtualSubject(Entries(("Double", Expression("/Meter/Power * 2")))));
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        graph.Add("Meter", new TestMeter { Power = 21 });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => changes.Contains(42m), message: "The virtual property did not recalculate");
    }

    [Fact]
    public async Task WhenTheTargetIsReplaced_ThenTheValueFollowsTheNewTarget()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 1 });
        graph.Add("Solar", graph.CreateVirtualSubject(Entries(("Double", Expression("/Meter/Power * 2")))));
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        graph.Add("Meter", new TestMeter { Power = 5 });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => changes.Contains(10m), message: "The virtual property did not follow the new target");
    }

    [Fact]
    public async Task WhenAPropertyIsAddedToTheTargetLater_ThenTheValueRecalculates()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        graph.Add("Solar", graph.CreateVirtualSubject(Entries(("Wind", Expression("/Meter/Wind * 2")))));
        var changes = RecordChanges(graph, "Wind", out var subscription);
        using var _ = subscription;
        decimal? wind = 4;

        // Act
        meter.TryGetRegisteredSubject()!.AddProperty("Wind", typeof(decimal?), _ => wind, null);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => changes.Contains(8m), message: "The virtual property did not find the added property");
    }

    [Fact]
    public void WhenABindingIsWritten_ThenTheTargetTakesTheValue()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 1 };
        graph.Add("Meter", meter);
        var solar = graph.CreateVirtualSubject(Entries(("Power", Binding("/Meter/Power"))));
        graph.Add("Solar", solar);

        // Act
        solar.TryGetRegisteredProperty("Power")!.SetValue(42m);

        // Assert
        Assert.Equal(42m, meter.Power);
        Assert.Equal(42m, solar.TryGetRegisteredProperty("Power")!.GetValue());
    }

    [Fact]
    public void WhenABindingToAnIntegerIsWritten_ThenTheNumberIsConverted()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        var solar = graph.CreateVirtualSubject(Entries(("Mode", Binding("/Meter/Mode"))));
        graph.Add("Solar", solar);

        // Act
        solar.TryGetRegisteredProperty("Mode")!.SetValue(3m);

        // Assert
        Assert.Equal(3, meter.Mode);
    }

    [Fact]
    public void WhenTheTargetOfABindingIsMissing_ThenAWriteThrows()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("Power", Binding("/Meter/Power"))));
        graph.Add("Solar", solar);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => solar.TryGetRegisteredProperty("Power")!.SetValue(1m));
    }

    [Fact]
    public void WhenAnEntryIsAnExpression_ThenItsPropertyCannotBeWritten()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("One", Expression("1"))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.False(solar.TryGetRegisteredProperty("One")!.HasSetter);
    }

    [Fact]
    public void WhenAnEntryHasState_ThenItsPropertyHasTheStateAttribute()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("One", Expression("1") with { State = new VirtualState { Title = "Uno", Unit = StateUnit.Watt } })));

        // Act
        graph.Add("Solar", solar);

        // Assert
        var state = Assert.IsType<StateMetadata>(solar.TryGetRegisteredProperty("One")!.TryGetAttribute(KnownAttributes.State)?.GetValue());
        Assert.Equal("Uno", state.Title);
        Assert.Equal(StateUnit.Watt, state.Unit);
    }

    [Fact]
    public void WhenAnEntryHasNoState_ThenItsPropertyIsAHelperWithoutStateAttribute()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("One", Expression("1"))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Null(solar.TryGetRegisteredProperty("One")!.TryGetAttribute(KnownAttributes.State));
    }

    [Fact]
    public void WhenEntriesAreNestedWithAttributes_ThenPathsReachEachOfThem()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 3 });
        var maximum = Expression("10000") with { Attributes = Entries(("IsEnabled", Expression("true", VirtualDataType.Boolean))) };
        var solar = graph.CreateVirtualSubject(Entries(("Pv", new VirtualProperty
        {
            Properties = Entries(
                ("Power", Binding("/Meter/Power") with { Attributes = Entries(("Maximum", maximum)) }),
                ("Double", Expression("Power * 2")))
        })));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Equal(3m, VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Power", graph.Root)?.GetValue());
        Assert.Equal(6m, VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Double", graph.Root)?.GetValue());
        Assert.Equal(10000m, VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Power@Maximum", graph.Root)?.GetValue());
        Assert.True(Assert.IsType<bool>(VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Power@Maximum@IsEnabled", graph.Root)?.GetValue()));
    }

    [Fact]
    public void WhenAnEntryIsInvalid_ThenItIsSkippedAndTheStatusReportsIt()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("Bad", Expression("(1")), ("Good", Expression("1"))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Equal(ServiceStatus.Error, solar.Status);
        Assert.StartsWith("Bad: ", solar.StatusMessage);
        Assert.Null(solar.TryGetRegisteredProperty("Bad"));
        Assert.Equal(1m, solar.TryGetRegisteredProperty("Good")!.GetValue());
    }

    [Fact]
    public void WhenEveryEntryApplies_ThenTheStatusIsRunning()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("Good", Expression("1"))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Equal(ServiceStatus.Running, solar.Status);
        Assert.Null(solar.StatusMessage);
    }

    [Fact]
    public void WhenEntriesReadEachOther_ThenTheyAreRejectedAndTheStatusReportsThem()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("A", Expression("B + 1")), ("B", Expression("A + 1")), ("C", Expression("1"))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Equal(ServiceStatus.Error, solar.Status);
        Assert.Contains("reads itself", solar.StatusMessage);
        Assert.Null(solar.TryGetRegisteredProperty("A"));
        Assert.Null(solar.TryGetRegisteredProperty("B"));
        Assert.Equal(1m, solar.TryGetRegisteredProperty("C")!.GetValue());
    }

    [Fact]
    public void WhenEntriesReadEachOtherThroughTheirAbsolutePaths_ThenTheyAreRejected()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = graph.CreateVirtualSubject(Entries(("A", Expression("/Solar/B + 1")), ("B", Expression("/Solar/A + 1"))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Equal(ServiceStatus.Error, solar.Status);
        Assert.Null(solar.TryGetRegisteredProperty("A"));
        Assert.Null(solar.TryGetRegisteredProperty("B"));
    }

    [Fact]
    public void WhenTwoVirtualSubjectsReadEachOther_ThenReadingGivesNoValueInsteadOfOverflowing()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var first = graph.CreateVirtualSubject(Entries(("A", Expression("/Second/B + 1"))));
        var second = graph.CreateVirtualSubject(Entries(("B", Expression("/First/A + 1"))));
        graph.Add("First", first);

        // Act
        graph.Add("Second", second);

        // Assert
        Assert.Null(first.TryGetRegisteredProperty("A")!.GetValue());
    }

    [Fact]
    public void WhenAnExpressionReadsASecret_ThenItHasNoValue()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { ApiKey = "secret" });
        var solar = graph.CreateVirtualSubject(Entries(("Key", Expression("/Meter/ApiKey", VirtualDataType.Text))));

        // Act
        graph.Add("Solar", solar);

        // Assert
        Assert.Null(solar.TryGetRegisteredProperty("Key")!.GetValue());
    }

    [Fact]
    public void WhenTheVirtualSubjectIsAttachedAgain_ThenItStillPublishesChanges()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 1 };
        graph.Add("Meter", meter);
        var solar = graph.CreateVirtualSubject(Entries(("Double", Expression("/Meter/Power * 2"))));
        graph.Add("Solar", solar);
        graph.Remove("Solar");
        graph.Add("Solar", solar);
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        meter.Power = 4;

        // Assert
        Assert.Contains(8m, changes);
    }
}
