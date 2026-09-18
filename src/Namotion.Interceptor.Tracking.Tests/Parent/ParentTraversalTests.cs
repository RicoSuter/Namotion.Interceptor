using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

public class ParentTraversalTests
{
    [Fact]
    public void WhenParentIsValueEqualToChild_ThenFindsDistinctParent()
    {
        // Arrange
        var child = new ValueEqualParentSubject { Key = 1 };
        var parent = new ValueEqualParentSubject { Key = 1 };
        child.AddParent(new PropertyReference(parent, nameof(ValueEqualParentSubject.Child)), null);

        // Act
        var result = child.TryGetFirstParent<ValueEqualParentSubject>();

        // Assert
        Assert.Same(parent, result);
    }

    [Fact]
    public void WhenIntermediateParentsAreValueEqual_ThenFindsAncestorBeyondBoth()
    {
        // Arrange
        var child = new Person();
        var parent = new ValueEqualParentSubject { Key = 1 };
        var grandparent = new ValueEqualParentSubject { Key = 1 };
        var ancestor = new Person();
        child.AddParent(new PropertyReference(parent, nameof(ValueEqualParentSubject.Child)), null);
        parent.AddParent(new PropertyReference(grandparent, nameof(ValueEqualParentSubject.Child)), null);
        grandparent.AddParent(new PropertyReference(ancestor, nameof(Person.Children)), 0);

        // Act
        var result = child.TryGetFirstParent<Person>();

        // Assert
        Assert.Same(ancestor, result);
    }

    [Fact]
    public void WhenOnlyMatchingSubjectIsSelfInCycle_ThenReturnsNull()
    {
        // Arrange
        var child = new Person();
        var parent = new ValueEqualParentSubject { Key = 1 };
        var grandparent = new ValueEqualParentSubject { Key = 1 };
        child.AddParent(new PropertyReference(parent, nameof(ValueEqualParentSubject.Child)), null);
        parent.AddParent(new PropertyReference(grandparent, nameof(ValueEqualParentSubject.Child)), null);
        grandparent.AddParent(new PropertyReference(child, nameof(Person.Children)), 0);

        // Act
        var result = child.TryGetFirstParent<Person>();

        // Assert
        Assert.Null(result);
    }
}

[InterceptorSubject]
public sealed partial class ValueEqualParentSubject : IEquatable<ValueEqualParentSubject>
{
    public int Key { get; init; }

    public partial IInterceptorSubject? Child { get; set; }

    public bool Equals(ValueEqualParentSubject? other) => other is not null && Key == other.Key;

    public override bool Equals(object? obj) => obj is ValueEqualParentSubject other && Equals(other);

    public override int GetHashCode() => Key;
}
