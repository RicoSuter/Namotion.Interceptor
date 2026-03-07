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
        var dependencies = new DerivedPropertyDependencies();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = dependencies.Add(property);

        // Assert
        Assert.True(result);
        Assert.Equal(1, dependencies.Count);
        Assert.Contains(property, dependencies.Items.ToArray());
    }

    [Fact]
    public void Add_WhenItemAlreadyPresent_ReturnsFalseAndDoesNotDuplicate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(property);

        // Act
        var result = dependencies.Add(property);

        // Assert
        Assert.False(result);
        Assert.Equal(1, dependencies.Count);
    }

    [Fact]
    public void Remove_WhenItemPresent_RemovesAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(property);

        // Act
        var result = dependencies.Remove(property);

        // Assert
        Assert.True(result);
        Assert.Equal(0, dependencies.Count);
    }

    [Fact]
    public void Remove_WhenItemNotPresent_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = dependencies.Remove(property);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Remove_WhenOnlyItem_ReturnsEmptyArray()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(property);

        // Act
        dependencies.Remove(property);

        // Assert
        Assert.Equal(0, dependencies.Count);
        Assert.Empty(dependencies.Items.ToArray());
    }

    [Fact]
    public void Remove_FirstItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        dependencies.Add(firstNameProperty);
        dependencies.Add(lastNameProperty);
        dependencies.Add(fatherProperty);

        // Act
        dependencies.Remove(firstNameProperty);

        // Assert
        Assert.Equal(2, dependencies.Count);
        var items = dependencies.Items.ToArray();
        Assert.DoesNotContain(firstNameProperty, items);
        Assert.Contains(lastNameProperty, items);
        Assert.Contains(fatherProperty, items);
    }

    [Fact]
    public void Remove_LastItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        dependencies.Add(firstNameProperty);
        dependencies.Add(lastNameProperty);
        dependencies.Add(fatherProperty);

        // Act
        dependencies.Remove(fatherProperty);

        // Assert
        Assert.Equal(2, dependencies.Count);
        var items = dependencies.Items.ToArray();
        Assert.Contains(firstNameProperty, items);
        Assert.Contains(lastNameProperty, items);
        Assert.DoesNotContain(fatherProperty, items);
    }

    [Fact]
    public void Remove_MiddleItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        dependencies.Add(firstNameProperty);
        dependencies.Add(lastNameProperty);
        dependencies.Add(fatherProperty);

        // Act
        dependencies.Remove(lastNameProperty);

        // Assert
        Assert.Equal(2, dependencies.Count);
        var items = dependencies.Items.ToArray();
        Assert.Contains(firstNameProperty, items);
        Assert.DoesNotContain(lastNameProperty, items);
        Assert.Contains(fatherProperty, items);
    }

    [Fact]
    public void TryReplace_WhenVersionMatches_ReplacesAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(firstNameProperty);
        var version = dependencies.Version;

        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var newItems = new[] { lastNameProperty };

        // Act
        var result = dependencies.TryReplace(newItems, version);

        // Assert
        Assert.True(result);
        Assert.Equal(1, dependencies.Count);
        var items = dependencies.Items.ToArray();
        Assert.DoesNotContain(firstNameProperty, items);
        Assert.Contains(lastNameProperty, items);
    }

    [Fact]
    public void TryReplace_WhenVersionMismatch_ReturnsFalseAndDoesNotReplace()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(firstNameProperty);
        var oldVersion = dependencies.Version;

        // Simulate concurrent modification
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        dependencies.Add(lastNameProperty);

        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        var newItems = new[] { fatherProperty };

        // Act - try to replace with old version
        var result = dependencies.TryReplace(newItems, oldVersion);

        // Assert
        Assert.False(result);
        Assert.Equal(2, dependencies.Count); // Original items preserved
        var items = dependencies.Items.ToArray();
        Assert.Contains(firstNameProperty, items);
        Assert.Contains(lastNameProperty, items);
        Assert.DoesNotContain(fatherProperty, items);
    }

    [Fact]
    public void TryReplace_WithEmptyArray_ClearsItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(firstNameProperty);
        var version = dependencies.Version;

        // Act
        var result = dependencies.TryReplace(ReadOnlySpan<PropertyReference>.Empty, version);

        // Assert
        Assert.True(result);
        Assert.Equal(0, dependencies.Count);
    }

    [Fact]
    public void Version_IncrementsOnAdd()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var initialVersion = dependencies.Version;

        // Act
        dependencies.Add(new PropertyReference(person, nameof(Person.FirstName)));

        // Assert
        Assert.Equal(initialVersion + 1, dependencies.Version);
    }

    [Fact]
    public void Version_IncrementsOnRemove()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(property);
        var versionAfterAdd = dependencies.Version;

        // Act
        dependencies.Remove(property);

        // Assert
        Assert.Equal(versionAfterAdd + 1, dependencies.Version);
    }

    [Fact]
    public void Version_IncrementsOnTryReplace()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        dependencies.Add(new PropertyReference(person, nameof(Person.FirstName)));
        var version = dependencies.Version;

        // Act
        dependencies.TryReplace(new[] { new PropertyReference(person, nameof(Person.LastName)) }, version);

        // Assert
        Assert.Equal(version + 1, dependencies.Version);
    }

    [Fact]
    public void Items_ReturnsSnapshotThatDoesNotChangeOnModification()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        dependencies.Add(firstNameProperty);

        // Act - get snapshot then modify
        var snapshot = dependencies.Items.ToArray();
        dependencies.Add(new PropertyReference(person, nameof(Person.LastName)));

        // Assert - snapshot unchanged
        Assert.Single(snapshot);
        Assert.Equal(2, dependencies.Count); // But collection changed
    }

    [Fact]
    public async Task ConcurrentAddRemove_MaintainsConsistency()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var dependencies = new DerivedPropertyDependencies();
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
                var property = properties[index % properties.Length];
                if (index % 2 == 0)
                    dependencies.Add(property);
                else
                    dependencies.Remove(property);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - should not throw, count should be valid
        Assert.True(dependencies.Count >= 0 && dependencies.Count <= properties.Length);
    }
}
