using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyDependenciesTests
{
    [Fact]
    public void Add_WhenItemNotPresent_AddsAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = deps.Add(prop);

        // Assert
        Assert.True(result);
        Assert.Equal(1, deps.Count);
        Assert.Contains(prop, deps.Items.ToArray());
    }

    [Fact]
    public void Add_WhenItemAlreadyPresent_ReturnsFalseAndDoesNotDuplicate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop);

        // Act
        var result = deps.Add(prop);

        // Assert
        Assert.False(result);
        Assert.Equal(1, deps.Count);
    }

    [Fact]
    public void Remove_WhenItemPresent_RemovesAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop);

        // Act
        var result = deps.Remove(prop);

        // Assert
        Assert.True(result);
        Assert.Equal(0, deps.Count);
    }

    [Fact]
    public void Remove_WhenItemNotPresent_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = deps.Remove(prop);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Remove_WhenOnlyItem_ReturnsEmptyArray()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop);

        // Act
        deps.Remove(prop);

        // Assert
        Assert.Equal(0, deps.Count);
        Assert.Empty(deps.Items.ToArray());
    }

    [Fact]
    public void Remove_FirstItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        var prop2 = new PropertyReference(person, nameof(Person.LastName));
        var prop3 = new PropertyReference(person, nameof(Person.Father));
        deps.Add(prop1);
        deps.Add(prop2);
        deps.Add(prop3);

        // Act
        deps.Remove(prop1);

        // Assert
        Assert.Equal(2, deps.Count);
        var items = deps.Items.ToArray();
        Assert.DoesNotContain(prop1, items);
        Assert.Contains(prop2, items);
        Assert.Contains(prop3, items);
    }

    [Fact]
    public void Remove_LastItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        var prop2 = new PropertyReference(person, nameof(Person.LastName));
        var prop3 = new PropertyReference(person, nameof(Person.Father));
        deps.Add(prop1);
        deps.Add(prop2);
        deps.Add(prop3);

        // Act
        deps.Remove(prop3);

        // Assert
        Assert.Equal(2, deps.Count);
        var items = deps.Items.ToArray();
        Assert.Contains(prop1, items);
        Assert.Contains(prop2, items);
        Assert.DoesNotContain(prop3, items);
    }

    [Fact]
    public void Remove_MiddleItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        var prop2 = new PropertyReference(person, nameof(Person.LastName));
        var prop3 = new PropertyReference(person, nameof(Person.Father));
        deps.Add(prop1);
        deps.Add(prop2);
        deps.Add(prop3);

        // Act
        deps.Remove(prop2);

        // Assert
        Assert.Equal(2, deps.Count);
        var items = deps.Items.ToArray();
        Assert.Contains(prop1, items);
        Assert.DoesNotContain(prop2, items);
        Assert.Contains(prop3, items);
    }

    [Fact]
    public void TryReplace_WhenVersionMatches_ReplacesAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop1);
        var version = deps.Version;

        var prop2 = new PropertyReference(person, nameof(Person.LastName));
        var newItems = new[] { prop2 };

        // Act
        var result = deps.TryReplace(newItems, version);

        // Assert
        Assert.True(result);
        Assert.Equal(1, deps.Count);
        var items = deps.Items.ToArray();
        Assert.DoesNotContain(prop1, items);
        Assert.Contains(prop2, items);
    }

    [Fact]
    public void TryReplace_WhenVersionMismatch_ReturnsFalseAndDoesNotReplace()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop1);
        var oldVersion = deps.Version;

        // Simulate concurrent modification
        var prop2 = new PropertyReference(person, nameof(Person.LastName));
        deps.Add(prop2);

        var prop3 = new PropertyReference(person, nameof(Person.Father));
        var newItems = new[] { prop3 };

        // Act - try to replace with old version
        var result = deps.TryReplace(newItems, oldVersion);

        // Assert
        Assert.False(result);
        Assert.Equal(2, deps.Count); // Original items preserved
        var items = deps.Items.ToArray();
        Assert.Contains(prop1, items);
        Assert.Contains(prop2, items);
        Assert.DoesNotContain(prop3, items);
    }

    [Fact]
    public void TryReplace_WithEmptyArray_ClearsItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop1);
        var version = deps.Version;

        // Act
        var result = deps.TryReplace(ReadOnlySpan<PropertyReference>.Empty, version);

        // Assert
        Assert.True(result);
        Assert.Equal(0, deps.Count);
    }

    [Fact]
    public void Version_IncrementsOnAdd()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var initialVersion = deps.Version;

        // Act
        deps.Add(new PropertyReference(person, nameof(Person.FirstName)));

        // Assert
        Assert.Equal(initialVersion + 1, deps.Version);
    }

    [Fact]
    public void Version_IncrementsOnRemove()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop);
        var versionAfterAdd = deps.Version;

        // Act
        deps.Remove(prop);

        // Assert
        Assert.Equal(versionAfterAdd + 1, deps.Version);
    }

    [Fact]
    public void Version_IncrementsOnTryReplace()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        deps.Add(new PropertyReference(person, nameof(Person.FirstName)));
        var version = deps.Version;

        // Act
        deps.TryReplace(new[] { new PropertyReference(person, nameof(Person.LastName)) }, version);

        // Assert
        Assert.Equal(version + 1, deps.Version);
    }

    [Fact]
    public void Items_ReturnsSnapshotThatDoesNotChangeOnModification()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var prop1 = new PropertyReference(person, nameof(Person.FirstName));
        deps.Add(prop1);

        // Act - get snapshot then modify
        var snapshot = deps.Items.ToArray();
        deps.Add(new PropertyReference(person, nameof(Person.LastName)));

        // Assert - snapshot unchanged
        Assert.Single(snapshot);
        Assert.Equal(2, deps.Count); // But collection changed
    }

    [Fact]
    public async Task ConcurrentAddRemove_MaintainsConsistency()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var deps = new DerivedPropertyDependencies();
        var properties = new[]
        {
            new PropertyReference(person, nameof(Person.FirstName)),
            new PropertyReference(person, nameof(Person.LastName)),
            new PropertyReference(person, nameof(Person.Father)),
            new PropertyReference(person, nameof(Person.Mother))
        };

        // Act - concurrent adds and removes
        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                var prop = properties[index % properties.Length];
                if (index % 2 == 0)
                    deps.Add(prop);
                else
                    deps.Remove(prop);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - should not throw, count should be valid
        Assert.True(deps.Count >= 0 && deps.Count <= properties.Length);
    }
}
