using System.Reactive.Concurrency;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Registry.Tests;

public class DerivedGetterReentrancyTests
{
    [Fact]
    public void WhenTwoDerivedPropertiesReadEachOther_ThenTheReEnteredGetterGivesTheDefaultInsteadOfOverflowing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registered = new Person(context).TryGetRegisteredSubject()!;
        registered.AddDerivedProperty("A", typeof(int), subject => (int)(subject.TryGetRegisteredProperty("B")?.GetValue() ?? 0) + 1, null);
        registered.AddDerivedProperty("B", typeof(int), subject => (int)(subject.TryGetRegisteredProperty("A")?.GetValue() ?? 0) + 1, null);

        // Act
        var value = registered.TryGetProperty("A")!.GetValue();

        // Assert: A reads B, B reads A again, which gives 0, so B is 1 and A is 2.
        Assert.Equal(2, value);
    }

    [Fact]
    public void WhenAGetterWritesASourceItReads_ThenTheNestedRecalculationRunsTheGetterAndKeepsItsDependencies()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        var person = new Person(context) { FirstName = "A", LastName = "B" };
        var registered = person.TryGetRegisteredSubject()!;
        var writeOnNextEvaluation = false;
        var name = registered.AddDerivedProperty("Name", typeof(string), subject =>
        {
            var source = (Person)subject;
            var value = source.FirstName + source.LastName;
            if (writeOnNextEvaluation)
            {
                writeOnNextEvaluation = false;
                source.LastName = "C";
            }

            return value;
        }, null);

        changes.Clear();

        // Act: the write in the plain read's evaluation recalculates Name in a nested evaluation.
        writeOnNextEvaluation = true;
        name.GetValue();
        person.FirstName = "D";

        // Assert
        var nameChanges = changes.Where(change => change.Property == name.Reference).ToList();
        Assert.Contains(nameChanges, change => change.GetNewValue<string?>() == "DC");
        Assert.DoesNotContain(nameChanges, change => change.GetNewValue<string?>() is null);
        Assert.Equal("DC", name.GetValue());
    }

    [Fact]
    public void WhenADerivedPropertyIsReadTwiceInOneEvaluation_ThenEachReadGivesItsValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registered = new Person(context).TryGetRegisteredSubject()!;
        registered.AddDerivedProperty("One", typeof(int), _ => 1, null);
        registered.AddDerivedProperty("Two", typeof(int), subject =>
            (int)subject.TryGetRegisteredProperty("One")!.GetValue()! + (int)subject.TryGetRegisteredProperty("One")!.GetValue()!, null);

        // Act
        var value = registered.TryGetProperty("Two")!.GetValue();

        // Assert
        Assert.Equal(2, value);
    }

    [Fact]
    public void WhenAGetterThrows_ThenTheNextReadEvaluatesItAgain()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registered = new Person(context).TryGetRegisteredSubject()!;
        var shouldThrow = false;
        registered.AddDerivedProperty("Flaky", typeof(int), _ => shouldThrow ? throw new InvalidOperationException("failed") : 7, null);

        // Act
        shouldThrow = true;
        var exception = Record.Exception(() => registered.TryGetProperty("Flaky")!.GetValue());
        shouldThrow = false;
        var value = registered.TryGetProperty("Flaky")!.GetValue();

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(7, value);
    }

    [Theory]
    [InlineData(typeof(int?), null)]
    [InlineData(typeof(DayOfWeek), DayOfWeek.Sunday)]
    public void WhenADerivedPropertyReadsItself_ThenTheInnerReadGivesTheDefaultOfItsType(Type type, object? expected)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registered = new Person(context).TryGetRegisteredSubject()!;
        registered.AddDerivedProperty("Self", type, subject => subject.TryGetRegisteredProperty("Self")!.GetValue(), null);

        // Act
        var value = registered.TryGetProperty("Self")!.GetValue();

        // Assert
        Assert.Equal(expected?.GetType(), value?.GetType());
        Assert.Equal(expected, value);
    }
}
