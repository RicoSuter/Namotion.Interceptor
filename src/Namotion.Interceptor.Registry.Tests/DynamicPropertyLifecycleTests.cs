using System.Reactive.Linq;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Tests that dynamic properties added via AddProperty/AddDerivedProperty
/// are correctly tracked by the lifecycle interceptor and registry.
/// </summary>
public class DynamicPropertyLifecycleTests
{
    [Fact]
    public void WhenWritingToDynamicDerivedPropertyWithSetter_ThenPropertyIsRecalculated()
    {
        // Arrange: Dynamic derived property with a setter.
        // The getter computes "FirstName (override)" or "FirstName" based on internal state.
        // The setter modifies internal state, then the handler should recalculate via the getter.
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "John" };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        string? overrideValue = null;

        var property = registeredRoot.AddDerivedProperty<string>(
            "DisplayName",
            getValue: _ => overrideValue ?? root.FirstName ?? "NA",
            setValue: (_, value) => overrideValue = value);

        context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "DisplayName")
            .Subscribe(changes.Add);

        // Verify initial value
        var propertyReference = property.Reference;
        var initialValue = propertyReference.Metadata.GetValue?.Invoke(root);
        Assert.Equal("John", initialValue);

        // Act - Write to the derived-with-setter property via the interceptor
        propertyReference.Metadata.SetValue?.Invoke(root, "Custom");

        // Assert - The override was applied
        Assert.Equal("Custom", overrideValue);

        // The getter should now return the override value
        var newValue = propertyReference.Metadata.GetValue?.Invoke(root);
        Assert.Equal("Custom", newValue);

        // The observable should have fired with the recalculated value
        Assert.NotEmpty(changes);
        Assert.Contains(changes, c =>
            c.GetNewValue<string?>() == "Custom");
    }

    [Fact]
    public void WhenSourceChanges_ThenDynamicDerivedPropertyIsRecalculated()
    {
        // Arrange: Dynamic derived property depending on FirstName.
        // When FirstName changes, the derived property should recalculate.
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "John" };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        registeredRoot.AddDerivedProperty<string>(
            "Greeting",
            getValue: _ => $"Hello, {root.FirstName}!",
            setValue: (_, _) => { });

        context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "Greeting")
            .Subscribe(changes.Add);

        // Act - Change the source property
        root.FirstName = "Jane";

        // Assert - Greeting should have been recalculated
        Assert.NotEmpty(changes);
        Assert.Contains(changes, c =>
            c.GetNewValue<string?>() == "Hello, Jane!");
    }

    [Fact]
    public void WhenDynamicDerivedPropertyReturnsSubject_ThenSubjectIsTrackedInRegistry()
    {
        // Arrange: Dynamic derived property that returns a subject reference (e.g., computed "first child").
        // Verifies that lifecycle tracking correctly attaches/detaches subjects discovered
        // through dynamic derived properties (IsDerived=true, IsIntercepted=true).
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [child1, child2]
        };

        var registry = context.GetService<ISubjectRegistry>();
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act: Add a dynamic derived property that returns the first child
        var derivedProperty = registeredRoot.AddDerivedProperty<Person>(
            "FirstChild",
            _ => root.Children.Length > 0 ? root.Children[0] : null);

        // Assert: child1 is now referenced from both Children (index 0) and FirstChild.
        // Ref count should be 2 (one from Children, one from FirstChild).
        Assert.Equal(2, child1.GetReferenceCount());
        Assert.Equal(1, child2.GetReferenceCount());

        // All subjects tracked: root + child1 + child2
        Assert.Equal(3, registry.KnownSubjects.Count);

        // Act: Change Children so that child2 is first — derived property should update
        root.Children = [child2];

        // Assert: child1 fully detached (removed from Children and no longer FirstChild)
        Assert.Equal(0, child1.GetReferenceCount());

        // child2 referenced from both Children and FirstChild
        Assert.Equal(2, child2.GetReferenceCount());

        // root + child2
        Assert.Equal(2, registry.KnownSubjects.Count);

        // Act: Set Children to empty — derived property returns null
        root.Children = [];

        // Assert: child2 fully detached
        Assert.Equal(0, child2.GetReferenceCount());

        // Only root remains
        Assert.Single(registry.KnownSubjects);
    }
}
