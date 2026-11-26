using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using HomeBlaze.Virtual.Tests.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
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

    private static VirtualSubject AddSolar(VirtualTestGraph graph, params (string Name, VirtualProperty Property)[] entries)
    {
        var solar = graph.CreateVirtualSubject(Entries(entries));
        graph.Add("Solar", solar);
        return solar;
    }

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
        AddSolar(graph, ("Double", Expression("/Meter/Power * 2")));
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        meter.Power = 150;

        // Assert
        Assert.Contains(300m, changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenATargetIsAddedAfterTheVirtualSubject_ThenTheValueFollowsIt(bool replacesATarget)
    {
        // Arrange
        var graph = new VirtualTestGraph();
        if (replacesATarget)
        {
            graph.Add("Meter", new TestMeter { Power = 1 });
        }

        AddSolar(graph, ("Double", Expression("/Meter/Power * 2")));
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        graph.Add("Meter", new TestMeter { Power = 21 });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => changes.Contains(42m), message: "The virtual property did not follow the added target");
    }

    [Fact]
    public async Task WhenAPropertyIsAddedToTheTargetLater_ThenTheValueRecalculates()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        AddSolar(graph, ("Wind", Expression("/Meter/Wind * 2")));
        var changes = RecordChanges(graph, "Wind", out var subscription);
        using var _ = subscription;
        decimal? wind = 4;

        // Act
        meter.TryGetRegisteredSubject()!.AddProperty("Wind", typeof(decimal?), _ => wind, null);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => changes.Contains(8m), message: "The virtual property did not find the added property");
    }

    [Theory]
    [InlineData("/Meter/Sub/Power * 2")]
    [InlineData("../Meter/Sub/Power * 2")]
    public async Task WhenAMiddleSegmentIsAddedToTheTargetLater_ThenTheValueRecalculates(string expression)
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        AddSolar(graph, ("Double", Expression(expression)));
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;
        var nested = new TestMeter { Power = 5 };

        // Act
        meter.TryGetRegisteredSubject()!.AddProperty("Sub", typeof(TestMeter), _ => nested, null);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => changes.Contains(10m), message: "The virtual property did not find the added segment");
    }

    [Fact]
    public void WhenABindingIsWritten_ThenTheTargetTakesTheValue()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 1 };
        graph.Add("Meter", meter);
        var solar = AddSolar(graph, ("Power", Binding("/Meter/Power")));

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
        var solar = AddSolar(graph, ("Mode", Binding("/Meter/Mode")));

        // Act
        solar.TryGetRegisteredProperty("Mode")!.SetValue(3m);

        // Assert
        Assert.Equal(3, meter.Mode);
    }

    [Theory]
    [InlineData("/Other/Power")]
    [InlineData("/Meter/Energy")]
    public void WhenTheTargetOfABindingIsMissingOrReadOnly_ThenAWriteThrows(string path)
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter());
        var solar = AddSolar(graph, ("Value", Binding(path)));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => solar.TryGetRegisteredProperty("Value")!.SetValue(1m));
    }

    [Fact]
    public void WhenAnEntryIsAnExpression_ThenItsPropertyCannotBeWritten()
    {
        // Arrange
        var graph = new VirtualTestGraph();

        // Act
        var solar = AddSolar(graph, ("One", Expression("1")));

        // Assert
        Assert.False(solar.TryGetRegisteredProperty("One")!.HasSetter);
    }

    [Fact]
    public void WhenAnEntryHasState_ThenItsPropertyHasTheStateAttribute()
    {
        // Arrange
        var graph = new VirtualTestGraph();

        // Act
        var solar = AddSolar(graph, ("One", Expression("1") with { State = new VirtualState { Title = "Uno", Unit = StateUnit.Watt } }));

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

        // Act
        var solar = AddSolar(graph, ("One", Expression("1")));

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

        // Act
        AddSolar(graph, ("Pv", new VirtualProperty
        {
            Properties = Entries(
                ("Power", Binding("/Meter/Power") with { Attributes = Entries(("Maximum", maximum)) }),
                ("Double", Expression("Power * 2")))
        }));

        // Assert
        Assert.Equal(3m, VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Power", graph.Root)?.GetValue());
        Assert.Equal(6m, VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Double", graph.Root)?.GetValue());
        Assert.Equal(10000m, VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Power@Maximum", graph.Root)?.GetValue());
        Assert.True(Assert.IsType<bool>(VirtualExpression.ResolveProperty(graph.Resolver, "/Solar/Pv/Power@Maximum@IsEnabled", graph.Root)?.GetValue()));
    }

    [Fact]
    public void WhenEntriesAreNested_ThenTheNestedSubjectIsTitledByTheNameOfItsEntry()
    {
        // Arrange
        var graph = new VirtualTestGraph();

        // Act
        AddSolar(graph, ("Pv", new VirtualProperty { Properties = Entries(("Double", Expression("2"))) }));

        // Assert
        var node = graph.Resolver.ResolveSubject("/Solar/Pv", PathStyle.Canonical);
        Assert.Equal("Pv", Assert.IsAssignableFrom<ITitleProvider>(node).Title);
    }

    [Fact]
    public void WhenAnEntryIsInvalid_ThenItIsSkippedAndTheStatusReportsIt()
    {
        // Arrange
        var graph = new VirtualTestGraph();

        // Act
        var solar = AddSolar(graph, ("Bad", Expression("(1")), ("Good", Expression("1")));

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

        // Act
        var solar = AddSolar(graph, ("Good", Expression("1")));

        // Assert
        Assert.Equal(ServiceStatus.Running, solar.Status);
        Assert.Null(solar.StatusMessage);
    }

    [Theory]
    [InlineData("B + 1", "A + 1")]
    [InlineData("/Solar/B + 1", "/Solar/A + 1")]
    [InlineData("../Solar/B + 1", "A + 1")]
    public void WhenEntriesReadEachOther_ThenTheyAreRejectedAndTheStatusReportsThem(string first, string second)
    {
        // Arrange
        var graph = new VirtualTestGraph();

        // Act
        var solar = AddSolar(graph, ("A", Expression(first)), ("B", Expression(second)), ("C", Expression("1")));

        // Assert
        Assert.Equal(ServiceStatus.Error, solar.Status);
        Assert.Contains("reads itself", solar.StatusMessage);
        Assert.Null(solar.TryGetRegisteredProperty("A"));
        Assert.Null(solar.TryGetRegisteredProperty("B"));
        Assert.Equal(1m, solar.TryGetRegisteredProperty("C")!.GetValue());
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
    public void WhenTwoBindingsPointAtEachOther_ThenAWriteThrowsInsteadOfOverflowing()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var first = graph.CreateVirtualSubject(Entries(("A", Binding("/Second/B"))));
        var second = graph.CreateVirtualSubject(Entries(("B", Binding("/First/A"))));
        graph.Add("First", first);
        graph.Add("Second", second);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => first.TryGetRegisteredProperty("A")!.SetValue(1m));
        Assert.Contains("writes itself through other bindings", exception.Message);
    }

    [Fact]
    public void WhenAnExpressionReadsASecret_ThenItHasNoValue()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { ApiKey = "secret" });

        // Act
        var solar = AddSolar(graph, ("Key", Expression("/Meter/ApiKey", VirtualDataType.Text)));

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
        var solar = AddSolar(graph, ("Double", Expression("/Meter/Power * 2")));
        graph.Remove("Solar");
        graph.Add("Solar", solar);
        var changes = RecordChanges(graph, "Double", out var subscription);
        using var _ = subscription;

        // Act
        meter.Power = 4;

        // Assert
        Assert.Contains(8m, changes);
    }

    [Fact]
    public void WhenTheVirtualSubjectWithNestedEntriesIsAttachedAgain_ThenItStillPublishesChanges()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 1 };
        graph.Add("Meter", meter);
        var solar = AddSolar(graph, ("Pv", new VirtualProperty
        {
            Properties = Entries(
                ("Power", Binding("/Meter/Power") with { Attributes = Entries(("Maximum", Expression("Power * 10"))) }),
                ("Double", Expression("Power * 2")))
        }));
        graph.Remove("Solar");
        graph.Add("Solar", solar);
        var doubles = RecordChanges(graph, "Double", out var doubleSubscription);
        using var _ = doubleSubscription;
        var maximums = RecordChanges(graph, "Power@Maximum", out var maximumSubscription);
        using var __ = maximumSubscription;

        // Act
        meter.Power = 4;

        // Assert
        Assert.Contains(8m, doubles);
        Assert.Contains(40m, maximums);
    }

    [Fact]
    public async Task WhenAnEntryReadsASiblingDeclaredAfterIt_ThenItGetsTheValue()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 3 });

        // Act
        var solar = AddSolar(graph, ("Double", Expression("Power * 2")), ("Power", Binding("/Meter/Power")));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => solar.TryGetRegisteredProperty("Double")!.GetValue() is 6m, message: "The entry did not get the value of its sibling");
    }

    [Fact]
    public void WhenTheContextHasNoRegistry_ThenTheStatusReportsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var root = new TestRoot(context);
        var resolver = new SubjectPathResolver(() => root);
        var solar = new VirtualSubject(resolver, NullLogger<VirtualSubject>.Instance)
        {
            Properties = Entries(("One", Expression("1")))
        };

        // Act
        root.Children = new Dictionary<string, IInterceptorSubject> { ["Solar"] = solar };

        // Assert
        Assert.Equal(ServiceStatus.Error, solar.Status);
        Assert.Equal("A virtual subject needs a context with a registry.", solar.StatusMessage);
    }

    [Fact]
    public void WhenTheVirtualSubjectIsDetached_ThenTheStatusMessageIsCleared()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var solar = AddSolar(graph, ("Bad", Expression("(1")));

        // Act
        graph.Remove("Solar");

        // Assert
        Assert.Equal(ServiceStatus.Stopped, solar.Status);
        Assert.Null(solar.StatusMessage);
    }

    [Fact]
    public void WhenEntriesAreSkipped_ThenOneWarningListsThem()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var logger = new RecordingLogger<VirtualSubject>();
        var solar = new VirtualSubject(graph.Resolver, logger)
        {
            Properties = Entries(("Bad", Expression("(1")), ("Worse", Expression("(2")), ("Good", Expression("1")))
        };

        // Act
        graph.Add("Solar", solar);

        // Assert
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Bad: ", warning);
        Assert.Contains("Worse: ", warning);
    }
}
