using System.Reactive.Concurrency;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

public class DerivedAttributeReattachTests
{
    public static TheoryData<int> ExtraAttributeCounts()
    {
        // Each count gives the subject a different property key set, and so a different attach order.
        var data = new TheoryData<int>();
        for (var count = 0; count <= 40; count++)
        {
            data.Add(count);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExtraAttributeCounts))]
    public void WhenSubjectWithTwoDerivedAttributesIsMoved_ThenBothKeepTheirSiblingDependencies(int extraAttributeCount)
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithRegistry();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var root = new Person(context);
        var child = new Person { FirstName = "John" };
        root.Mother = child;

        var firstName = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        var values = new Dictionary<string, object?> { ["A"] = 1, ["B"] = 1 };
        var attributeA = firstName.AddAttribute("A", typeof(int), _ => values["A"], (_, value) => values["A"] = value);
        var attributeB = firstName.AddAttribute("B", typeof(int), _ => values["B"], (_, value) => values["B"] = value);
        var computedA = firstName.AddDerivedAttribute("ComputedA", typeof(int), _ => (int)attributeA.GetValue()! * 10, null);
        var computedB = firstName.AddDerivedAttribute("ComputedB", typeof(int), _ => (int)attributeB.GetValue()! * 10, null);

        for (var i = 0; i < extraAttributeCount; i++)
        {
            firstName.AddAttribute($"Extra{i}", typeof(int), _ => 0, null);
        }

        // Act
        root.Mother = null;
        root.Father = child;

        changes.Clear();
        attributeA.SetValue(2);
        attributeB.SetValue(3);

        // Assert
        Assert.Contains(attributeA.Reference, computedA.Reference.GetRequiredProperties().ToArray());
        Assert.Contains(attributeB.Reference, computedB.Reference.GetRequiredProperties().ToArray());
        Assert.Contains(changes, change => change.Property == computedA.Reference && change.GetNewValue<int>() == 20);
        Assert.Contains(changes, change => change.Property == computedB.Reference && change.GetNewValue<int>() == 30);
    }
}
