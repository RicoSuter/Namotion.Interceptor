using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyReferenceCollectionTests
{
    [Fact]
    public void Add_WhenItemNotPresent_AddsAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = collection.Add(property);

        // Assert
        Assert.True(result);
        Assert.Equal(1, collection.Count);
        Assert.Contains(property, collection.Items.ToArray());
    }

    [Fact]
    public void Add_WhenItemAlreadyPresent_ReturnsFalseAndDoesNotDuplicate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        collection.Add(property);

        // Act
        var result = collection.Add(property);

        // Assert
        Assert.False(result);
        Assert.Equal(1, collection.Count);
    }

    [Fact]
    public void Remove_WhenItemPresent_RemovesAndReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        collection.Add(property);

        // Act
        var result = collection.Remove(property);

        // Assert
        Assert.True(result);
        Assert.Equal(0, collection.Count);
    }

    [Fact]
    public void Remove_WhenItemNotPresent_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = collection.Remove(property);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Remove_WhenOnlyItem_ReturnsEmptyArray()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        collection.Add(property);

        // Act
        collection.Remove(property);

        // Assert
        Assert.Equal(0, collection.Count);
        Assert.Empty(collection.Items.ToArray());
    }

    [Fact]
    public void Remove_FirstItem_PreservesOtherItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        collection.Add(firstNameProperty);
        collection.Add(lastNameProperty);
        collection.Add(fatherProperty);

        // Act
        collection.Remove(firstNameProperty);

        // Assert
        Assert.Equal(2, collection.Count);
        var items = collection.Items.ToArray();
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
        var collection = new PropertyReferenceCollection();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        collection.Add(firstNameProperty);
        collection.Add(lastNameProperty);
        collection.Add(fatherProperty);

        // Act
        collection.Remove(fatherProperty);

        // Assert
        Assert.Equal(2, collection.Count);
        var items = collection.Items.ToArray();
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
        var collection = new PropertyReferenceCollection();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));
        collection.Add(firstNameProperty);
        collection.Add(lastNameProperty);
        collection.Add(fatherProperty);

        // Act
        collection.Remove(lastNameProperty);

        // Assert
        Assert.Equal(2, collection.Count);
        var items = collection.Items.ToArray();
        Assert.Contains(firstNameProperty, items);
        Assert.DoesNotContain(lastNameProperty, items);
        Assert.Contains(fatherProperty, items);
    }

    [Fact]
    public void Items_ReturnsSnapshotThatDoesNotChangeOnModification()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        collection.Add(firstNameProperty);

        // Act - get snapshot then modify
        var snapshot = collection.Items.ToArray();
        collection.Add(new PropertyReference(person, nameof(Person.LastName)));

        // Assert - snapshot unchanged
        Assert.Single(snapshot);
        Assert.Equal(2, collection.Count); // But collection changed
    }

    [Fact]
    public async Task ConcurrentAddRemove_MaintainsConsistency()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var collection = new PropertyReferenceCollection();
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
                    collection.Add(property);
                else
                    collection.Remove(property);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - should not throw, count should be valid
        Assert.True(collection.Count >= 0 && collection.Count <= properties.Length);
    }
}
