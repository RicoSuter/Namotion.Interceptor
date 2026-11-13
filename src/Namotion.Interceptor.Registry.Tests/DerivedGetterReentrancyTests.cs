using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

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
        shouldThrow = true;
        Assert.Throws<InvalidOperationException>(() => registered.TryGetProperty("Flaky")!.GetValue());
        shouldThrow = false;

        // Act
        var value = registered.TryGetProperty("Flaky")!.GetValue();

        // Assert
        Assert.Equal(7, value);
    }

    [Fact]
    public void WhenANullableDerivedPropertyReadsItself_ThenItGivesNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registered = new Person(context).TryGetRegisteredSubject()!;
        registered.AddDerivedProperty("Self", typeof(int?), subject => (int?)subject.TryGetRegisteredProperty("Self")!.GetValue() + 1, null);

        // Act
        var value = registered.TryGetProperty("Self")!.GetValue();

        // Assert
        Assert.Null(value);
    }
}
