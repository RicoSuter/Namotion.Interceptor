using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Covers derived values that read the parents of their subject. The parents are not a tracked
/// property, so such a value is computed when its subject enters the graph, and a subject that a
/// context-taking constructor anchored enters it before it has a parent.
/// </summary>
public class DerivedPropertyGraphPositionTests
{
    [Fact]
    public void WhenSubjectAnchoredByItsConstructorGetsAParent_ThenDerivedValuesThatReadTheParentsAreRecalculated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new ScopeHolder(context) { Name = "Holder" };
        var child = new ScopedChild(context);
        var changes = new List<string>();
        child.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName!);

        // Act
        holder.Child = child;

        // Assert
        Assert.Contains(nameof(ScopedChild.ScopeName), changes);
        Assert.Contains(nameof(ScopedChild.Scope), changes);
        Assert.Same(holder, child.Scope);
    }

    [Fact]
    public void WhenSubjectAnchoredByItsConstructorGetsAParent_ThenDerivedValuesFollowTheParentAfterwards()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new ScopeHolder(context) { Name = "Holder" };
        var child = new ScopedChild(context);
        holder.Child = child;
        var changes = new List<string>();
        child.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName!);

        // Act
        holder.Name = "Renamed";

        // Assert
        Assert.Equal([nameof(ScopedChild.ScopeName)], changes);
        Assert.Equal("Renamed", child.ScopeName);
    }

    [Fact]
    public void WhenSubjectAnchoredByItsConstructorGetsAParent_ThenDerivedValuesThatDidNotChangeAreNotPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person(context) { FirstName = "Child" };
        var timestampBefore = new PropertyReference(child, nameof(Person.FullName)).TryGetWriteTimestamp();
        var propertyChanges = new List<string>();
        child.PropertyChanged += (_, arguments) => propertyChanges.Add(arguments.PropertyName!);
        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        parent.Children = [child];

        // Assert
        Assert.DoesNotContain(nameof(Person.FullName), propertyChanges);
        Assert.DoesNotContain(changes, change => ReferenceEquals(change.Property.Subject, child));
        Assert.Equal(timestampBefore, new PropertyReference(child, nameof(Person.FullName)).TryGetWriteTimestamp());
    }

    [Fact]
    public void WhenUnattachedSubjectGetsAParent_ThenDerivedValuesThatReadTheParentsAreComputedOnAttach()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var holder = new ScopeHolder(context) { Name = "Holder" };
        var child = new ScopedChild();
        holder.Child = child;
        var changes = new List<string>();
        child.PropertyChanged += (_, arguments) => changes.Add(arguments.PropertyName!);

        // Act
        holder.Name = "Renamed";

        // Assert
        Assert.Equal([nameof(ScopedChild.ScopeName)], changes);
        Assert.Equal("Renamed", child.ScopeName);
    }
}
