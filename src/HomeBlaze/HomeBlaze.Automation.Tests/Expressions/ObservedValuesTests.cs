using System.Collections.Frozen;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Automation.Tests.Expressions;

public class ObservedValuesTests
{
    [Fact]
    public void WhenAValueWasObserved_ThenOnlyANewerChangeReplacesIt()
    {
        // Arrange
        var graph = new TestGraph();
        var sensor = new TestSensor();
        graph.Add("Weather", sensor);
        var property = sensor.TryGetRegisteredProperty(nameof(TestSensor.Gust))!;
        var values = new ObservedValues();
        sensor.Gust = 9;
        property.Reference.TryGetWriteState(includeSourceCommitsInRevision: true, out var observedRevision, out _);
        var first = values.GetValue(property);
        sensor.Gust = 3;
        property.Reference.TryGetWriteState(includeSourceCommitsInRevision: true, out var newerRevision, out _);

        // Act
        var isStaleApplied = values.TryApply(property.Reference, 1.0, observedRevision);
        var afterStale = values.GetValue(property);
        var isNewerApplied = values.TryApply(property.Reference, 3.0, newerRevision);
        var afterNewer = values.GetValue(property);

        // Assert
        Assert.True(newerRevision > observedRevision);
        Assert.Equal(9.0, first);
        Assert.False(isStaleApplied);
        Assert.Equal(9.0, afterStale);
        Assert.True(isNewerApplied);
        Assert.Equal(3.0, afterNewer);
    }

    [Fact]
    public void WhenAPropertyIsNoLongerRetained_ThenItsValueIsReadFromTheGraphAgain()
    {
        // Arrange
        var graph = new TestGraph();
        var sensor = new TestSensor();
        graph.Add("Weather", sensor);
        var property = sensor.TryGetRegisteredProperty(nameof(TestSensor.Gust))!;
        var values = new ObservedValues();
        sensor.Gust = 9;
        values.GetValue(property);
        sensor.Gust = 4;

        // Act
        values.Retain(FrozenSet<PropertyReference>.Empty);
        var value = values.GetValue(property);

        // Assert
        Assert.Equal(4.0, value);
    }
}
