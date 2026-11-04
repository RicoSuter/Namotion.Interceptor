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

    [Theory]
    [InlineData(10, false)]
    [InlineData(100, false)]
    [InlineData(10, true)]
    [InlineData(100, true)]
    public void WhenManyItemsAreAddedAndRemoved_ThenItemsMatchAfterEveryStep(int count, bool startWithConstructorItem)
    {
        // Arrange
        var properties = CreateProperties(count);
        var collection = startWithConstructorItem
            ? new PropertyReferenceCollection(properties[0])
            : new PropertyReferenceCollection();
        var expected = new HashSet<PropertyReference>();
        if (startWithConstructorItem)
        {
            expected.Add(properties[0]);
        }

        var removalOrder = properties
            .Where((_, index) => index % 2 == 0)
            .Concat(properties.Where((_, index) => index % 2 == 1))
            .ToArray();

        // Act & Assert
        AssertItemsMatch(expected, properties, collection);
        AddEach(collection, expected, properties, properties);
        foreach (var property in properties)
        {
            Assert.False(collection.Add(property));
        }

        RemoveEach(collection, expected, properties, removalOrder);
        Assert.Empty(expected);

        AddEach(collection, expected, properties, properties.Reverse().ToArray());
        RemoveEach(collection, expected, properties, properties);
        Assert.False(collection.Remove(properties[0]));
    }

    [Theory]
    [InlineData(100, 1, 1)]
    [InlineData(31, 0, 2)]
    [InlineData(33, 17, 0)]
    [InlineData(40, 30, 10)]
    public void WhenCollectionIsModifiedAfterSnapshot_ThenSnapshotsAreUnchanged(int initialCount, int removeCount, int addCount)
    {
        // Arrange
        var collection = new PropertyReferenceCollection();
        var properties = CreateProperties(initialCount + addCount);
        var expected = new HashSet<PropertyReference>();
        AddEach(collection, expected, properties, properties.AsSpan(0, initialCount).ToArray());

        var span = collection.Items;
        var spanCopy = span.ToArray();
        var array = collection.ItemsArray;
        var arrayCopy = array.ToArray();

        // Act
        foreach (var property in properties.AsSpan(0, removeCount))
        {
            Assert.True(collection.Remove(property));
            expected.Remove(property);
        }

        foreach (var property in properties.AsSpan(initialCount, addCount))
        {
            Assert.True(collection.Add(property));
            expected.Add(property);
        }

        // Assert
        Assert.True(span.SequenceEqual(spanCopy));
        Assert.Equal(arrayCopy, array);
        AssertItemsMatch(expected, properties, collection);
    }

    [Fact]
    public void WhenCheckingContains_ThenReturnsTrueOnlyForPresentItems()
    {
        // Arrange
        var collection = new PropertyReferenceCollection();
        var properties = CreateProperties(2);
        collection.Add(properties[0]);

        // Act
        var containsPresent = collection.Contains(properties[0]);
        var containsMissing = collection.Contains(properties[1]);

        // Assert
        Assert.True(containsPresent);
        Assert.False(containsMissing);
    }

    [Fact]
    public async Task WhenThreadsAddAndRemoveAcrossThreshold_ThenFinalItemsAreExact()
    {
        // Arrange
        const int threadCount = 8;
        const int itemsPerThread = 50;
        const int roundCount = 20;
        var collection = new PropertyReferenceCollection();
        var properties = CreateProperties(threadCount * itemsPerThread);
        var universe = properties.ToHashSet();
        using var start = new ManualResetEventSlim();
        using var barrier = new Barrier(threadCount);
        using var stop = new CancellationTokenSource();

        // Act
        var writers = Enumerable.Range(0, threadCount).Select(thread => Task.Factory.StartNew(() =>
        {
            start.Wait();
            var ownProperties = properties.AsSpan(thread * itemsPerThread, itemsPerThread);
            for (var round = 0; round < roundCount; round++)
            {
                RunRound(collection, barrier, ownProperties, threadCount * itemsPerThread);
            }

            foreach (var property in ownProperties[..(itemsPerThread / 2)])
            {
                Assert.True(collection.Add(property));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var reader = Task.Factory.StartNew(() =>
        {
            start.Wait();
            while (!stop.IsCancellationRequested)
            {
                AssertSnapshotIsValid(collection.Items, universe);
                AssertSnapshotIsValid(collection.ItemsArray, universe);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        start.Set();
        try
        {
            await Task.WhenAll(writers);
        }
        finally
        {
            await stop.CancelAsync();
        }

        await reader;

        // Assert
        var expected = Enumerable.Range(0, threadCount)
            .SelectMany(thread => properties.Skip(thread * itemsPerThread).Take(itemsPerThread / 2))
            .ToHashSet();
        AssertItemsMatch(expected, properties, collection);
    }

    private static void RunRound(PropertyReferenceCollection collection, Barrier barrier, ReadOnlySpan<PropertyReference> ownProperties, int totalCount)
    {
        foreach (var property in ownProperties)
        {
            Assert.True(collection.Add(property));
        }

        SignalAndWait(barrier);
        Assert.Equal(totalCount, collection.Count);
        SignalAndWait(barrier);

        foreach (var property in ownProperties)
        {
            Assert.True(collection.Remove(property));
        }

        SignalAndWait(barrier);
        Assert.Equal(0, collection.Count);
        SignalAndWait(barrier);
    }

    private static void SignalAndWait(Barrier barrier)
    {
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
    }

    private static void AssertSnapshotIsValid(ReadOnlySpan<PropertyReference> snapshot, HashSet<PropertyReference> universe)
    {
        var seen = new HashSet<PropertyReference>();
        foreach (var item in snapshot)
        {
            Assert.Contains(item, universe);
            Assert.True(seen.Add(item));
        }
    }

    private static void AddEach(
        PropertyReferenceCollection collection,
        HashSet<PropertyReference> expected,
        PropertyReference[] universe,
        PropertyReference[] order)
    {
        foreach (var property in order)
        {
            var isNew = !expected.Contains(property);
            Assert.Equal(isNew, collection.Add(property));
            expected.Add(property);
            AssertItemsMatch(expected, universe, collection);
        }
    }

    private static void RemoveEach(
        PropertyReferenceCollection collection,
        HashSet<PropertyReference> expected,
        PropertyReference[] universe,
        PropertyReference[] order)
    {
        foreach (var property in order)
        {
            Assert.True(collection.Remove(property));
            expected.Remove(property);
            AssertItemsMatch(expected, universe, collection);

            Assert.False(collection.Remove(property));
            Assert.Equal(expected.Count, collection.Count);
        }
    }

    private static PropertyReference[] CreateProperties(int count)
    {
        var properties = new PropertyReference[count];
        for (var index = 0; index < count; index++)
        {
            properties[index] = new PropertyReference(new Person(), nameof(Person.FirstName));
        }

        return properties;
    }

    private static void AssertItemsMatch(
        HashSet<PropertyReference> expected,
        PropertyReference[] universe,
        PropertyReferenceCollection collection)
    {
        var items = collection.Items.ToArray();
        var itemsArray = collection.ItemsArray;
        Assert.Equal(expected.Count, collection.Count);
        Assert.Equal(expected.Count, items.Length);
        Assert.Equal(expected.Count, itemsArray.Length);
        Assert.Equal(items.Length, items.Distinct().Count());
        Assert.True(expected.SetEquals(items));
        Assert.True(expected.SetEquals(itemsArray));
        foreach (var property in universe)
        {
            Assert.Equal(expected.Contains(property), collection.Contains(property));
        }
    }
}
