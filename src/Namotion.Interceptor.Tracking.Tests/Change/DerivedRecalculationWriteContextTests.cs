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
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        person.FirstName = "Ada";

        // Assert
        Assert.Contains((nameof(Person.FirstName), false), probe.Writes);
        Assert.Contains((nameof(Person.FullName), true), probe.Writes);
        Assert.DoesNotContain((nameof(Person.FirstName), true), probe.Writes);
        Assert.DoesNotContain((nameof(Person.FullName), false), probe.Writes);
    }

    [Fact]
    public void WhenSettableDerivedPropertyIsWritten_ThenTheWriteIsNotFlaggedAndItsRecalculationsAre()
    {
        // Arrange
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.AddService(probe);
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
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        context.AddService(probe);
        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => 5.0;
        probe.Writes.Clear();

        // Act
        new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature)).RecalculateDerivedProperty();

        // Assert
        Assert.Contains((nameof(ExternalSensor.CalibratedTemperature), true), probe.Writes);
        Assert.DoesNotContain((nameof(ExternalSensor.CalibratedTemperature), false), probe.Writes);
    }

    [Fact]
    public void WhenDynamicSettableDerivedPropertyIsWritten_ThenTheWriteIsNotFlaggedAndItsRecalculationIs()
    {
        // Arrange
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();
        context.AddService(probe);
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
        var probe = new RecalculationFlagProbe();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        person.TryGetRegisteredSubject()!.AddDerivedProperty<string?>("Computed", getValue: _ => "value");

        // Assert
        Assert.Contains(("Computed", true), probe.Writes);
        Assert.DoesNotContain(("Computed", false), probe.Writes);
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
}
