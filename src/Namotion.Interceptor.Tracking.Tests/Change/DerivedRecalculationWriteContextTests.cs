using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedRecalculationWriteContextTests
{
    [Fact]
    public void WhenPlainPropertyIsWritten_ThenOnlyTheDerivedRecalculationIsFlagged()
    {
        // Arrange
        var (context, probe) = CreateContext();
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        person.FirstName = "Ada";

        // Assert
        Assert.Contains((nameof(Person.FirstName), false), probe.Writes);
        Assert.Contains((nameof(Person.FullName), true), probe.Writes);
        Assert.All(probe.Writes, write => Assert.Equal(write.Property != nameof(Person.FirstName), write.IsDerivedRecalculation));
    }

    [Fact]
    public void WhenSettableDerivedPropertyIsWritten_ThenTheWriteIsNotFlaggedAndItsRecalculationsAre()
    {
        // Arrange
        var (context, probe) = CreateContext();
        var person = new DerivedSetterPerson(context);
        probe.Writes.Clear();

        // Act
        person.Nickname = "Bob";

        // Assert
        Assert.Single(probe.Writes, write => write == (nameof(DerivedSetterPerson.Nickname), false));
        Assert.Contains((nameof(DerivedSetterPerson.Nickname), true), probe.Writes);
        Assert.Contains((nameof(DerivedSetterPerson.NicknameWithPrefix), true), probe.Writes);
        Assert.DoesNotContain((nameof(DerivedSetterPerson.NicknameWithPrefix), false), probe.Writes);
    }

    [Fact]
    public void WhenDerivedPropertyIsRecalculatedManually_ThenTheWriteIsFlagged()
    {
        // Arrange
        var (context, probe) = CreateContext();
        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => 5.0;
        probe.Writes.Clear();

        // Act
        new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature)).RecalculateDerivedProperty();

        // Assert
        Assert.Equal([(nameof(ExternalSensor.CalibratedTemperature), true)], probe.Writes);
    }

    [Fact]
    public void WhenDynamicSettableDerivedPropertyIsWritten_ThenTheWriteIsNotFlaggedAndItsRecalculationIs()
    {
        // Arrange
        var (context, probe) = CreateContext();
        var person = new Person(context);
        string? backing = null;
        var derived = person.TryGetRegisteredSubject()!.AddDerivedProperty<string?>(
            "Trimmed",
            getValue: _ => backing?.Trim(),
            setValue: (_, value) => backing = value);
        probe.Writes.Clear();

        // Act
        derived.SetValue("  hello  ");

        // Assert
        Assert.Single(probe.Writes, write => write == ("Trimmed", false));
        Assert.Contains(("Trimmed", true), probe.Writes);
    }

    [Fact]
    public void WhenDerivedPropertyIsAddedAtRuntime_ThenItsInitialPublishIsFlagged()
    {
        // Arrange
        var (context, probe) = CreateContext();
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        person.TryGetRegisteredSubject()!.AddDerivedProperty<string?>("Computed", getValue: _ => "value");

        // Assert
        Assert.Equal([("Computed", true)], probe.Writes);
    }

    [Fact]
    public void WhenDependencyChangesWhileRuntimeDerivedPropertyIsPublished_ThenLastPublishedValueIsCurrentValue()
    {
        // Arrange
        var (context, _) = CreateContext();
        var interleaver = new InterleavingWriter();
        context.AddService(interleaver);
        var person = new Person(context) { FirstName = "a" };
        var published = new List<string?>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name == "Upper")
                {
                    published.Add(change.GetNewValue<string?>());
                }
            });
        interleaver.Pending = ("Upper", () => person.FirstName = "b");

        // Act
        var upper = person.TryGetRegisteredSubject()!.AddDerivedProperty<string?>(
            "Upper", getValue: subject => ((Person)subject).FirstName?.ToUpperInvariant());

        // Assert
        Assert.Equal("B", upper.GetValue());
        Assert.Equal("B", published[^1]);
    }

    private static (IInterceptorSubjectContext Context, RecalculationFlagProbe Probe) CreateContext()
    {
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();
        context.AddService(probe);
        return (context, probe);
    }

    private sealed class RecalculationFlagProbe : IWriteInterceptor
    {
        public List<(string Property, bool IsDerivedRecalculation)> Writes { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((context.Property.Name, context.IsDerivedRecalculation));
            next(ref context);
        }
    }

    // Stands in for a dependency write that lands between reading the initial value and publishing it.
    [RunsBefore(typeof(PropertyChangeInterceptor))]
    private sealed class InterleavingWriter : IWriteInterceptor
    {
        public (string Name, Action Write)? Pending;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Pending is { } pending && context.Property.Name == pending.Name)
            {
                Pending = null;
                pending.Write();
            }

            next(ref context);
        }
    }
}
