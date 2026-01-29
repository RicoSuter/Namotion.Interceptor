using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for StructuralChangeProcessor focusing on property change routing and subject lifecycle callbacks.
/// Verifies correct branching on property type (reference, collection, dictionary) and loop prevention.
/// </summary>
public class StructuralChangeProcessorTests
{
    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectReference_CallsOnSubjectAdded()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Person(context);
        var child = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Father));

        var change = SubjectPropertyChange.Create<Person?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: null,
            newValue: child);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.AddedSubjects);
        Assert.Equal(child, processor.AddedSubjects[0].Subject);
        Assert.Null(processor.AddedSubjects[0].Index);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectReference_CallsOnSubjectRemoved()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Person(context);
        var child = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Father));

        var change = SubjectPropertyChange.Create<Person?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: child,
            newValue: null);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(child, processor.RemovedSubjects[0].Subject);
        Assert.Null(processor.RemovedSubjects[0].Index);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectReferenceReplacement_CallsBothAddedAndRemoved()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Person(context);
        var oldChild = new Person(context);
        var newChild = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Father));

        var change = SubjectPropertyChange.Create<Person?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldChild,
            newValue: newChild);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(oldChild, processor.RemovedSubjects[0].Subject);
        Assert.Single(processor.AddedSubjects);
        Assert.Equal(newChild, processor.AddedSubjects[0].Subject);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_SameSubjectReference_NoCallbacks()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Person(context);
        var child = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Father));

        var change = SubjectPropertyChange.Create<Person?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: child,
            newValue: child); // Same reference

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Empty(processor.AddedSubjects);
        Assert.Empty(processor.RemovedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_NonStructuralProperty_ReturnsFalse()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Person(context);

        var registered = person.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.FirstName));

        var change = SubjectPropertyChange.Create<string?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: "Old",
            newValue: "New");

        // Act
        var result = await processor.ProcessPropertyChangeAsync(change, property);

        // Assert - Returns false for non-structural (value) changes; caller handles value processing
        Assert.False(result);
        Assert.Empty(processor.AddedSubjects);
        Assert.Empty(processor.RemovedSubjects);
    }

    #region Collection Tests

    [Fact]
    public async Task ProcessPropertyChangeAsync_Collection_EmptyToPopulated_CallsOnSubjectAddedForEachItem()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        var oldCollection = Array.Empty<IInterceptorSubject>();
        var newCollection = new List<CycleTestNode> { child1, child2 };

        var change = SubjectPropertyChange.Create<List<CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: [],
            newValue: newCollection);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.AddedSubjects.Count);
        Assert.Contains(processor.AddedSubjects, x => x.Subject == child1 && (int)x.Index! == 0);
        Assert.Contains(processor.AddedSubjects, x => x.Subject == child2 && (int)x.Index! == 1);
        Assert.Empty(processor.RemovedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Collection_PopulatedToFewer_CallsOnSubjectRemovedForRemovedItems()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        var child3 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        var oldCollection = new List<CycleTestNode> { child1, child2, child3 };
        var newCollection = new List<CycleTestNode> { child1 };

        var change = SubjectPropertyChange.Create<List<CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldCollection,
            newValue: newCollection);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.RemovedSubjects.Count);
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child2);
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child3);
        Assert.Empty(processor.AddedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Collection_AddAndRemoveInSingleChange_CallsBothCallbacks()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        var child3 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Old collection has child1 and child2; new collection has child1 and child3
        // So child2 is removed, child3 is added
        var oldCollection = new List<CycleTestNode> { child1, child2 };
        var newCollection = new List<CycleTestNode> { child1, child3 };

        var change = SubjectPropertyChange.Create<List<CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldCollection,
            newValue: newCollection);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(child2, processor.RemovedSubjects[0].Subject);

        Assert.Single(processor.AddedSubjects);
        Assert.Equal(child3, processor.AddedSubjects[0].Subject);
        Assert.Equal(1, processor.AddedSubjects[0].Index);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Collection_PopulatedToEmpty_CallsOnSubjectRemovedForAllItems()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        var oldCollection = new List<CycleTestNode> { child1, child2 };
        var newCollection = new List<CycleTestNode>();

        var change = SubjectPropertyChange.Create<List<CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldCollection,
            newValue: newCollection);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.RemovedSubjects.Count);
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child1);
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child2);
        Assert.Empty(processor.AddedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Collection_NullToPopulated_CallsOnSubjectAddedForEachItem()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        var newCollection = new List<CycleTestNode> { child1, child2 };

        // Create change with null old value (simulating null -> populated)
        var change = SubjectPropertyChange.Create<List<CycleTestNode>?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: null,
            newValue: newCollection);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.AddedSubjects.Count);
        Assert.Empty(processor.RemovedSubjects);
    }

    #endregion

    #region Dictionary Tests

    [Fact]
    public async Task ProcessPropertyChangeAsync_Dictionary_EmptyToPopulated_CallsOnSubjectAddedWithKeys()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        // Set up the parent to have an empty dictionary first so Children is populated correctly
        parent.Lookup = new Dictionary<string, CycleTestNode>();

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        var oldDictionary = new Dictionary<string, CycleTestNode>();
        var newDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key2"] = child2
        };

        var change = SubjectPropertyChange.Create<Dictionary<string, CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldDictionary,
            newValue: newDictionary);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.AddedSubjects.Count);
        Assert.Contains(processor.AddedSubjects, x => x.Subject == child1 && (string)x.Index! == "key1");
        Assert.Contains(processor.AddedSubjects, x => x.Subject == child2 && (string)x.Index! == "key2");
        Assert.Empty(processor.RemovedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Dictionary_PopulatedToFewer_CallsOnSubjectRemovedWithKeys()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        var child3 = new CycleTestNode(context);

        // Set up the parent with the old dictionary first so Children is populated
        var oldDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key2"] = child2,
            ["key3"] = child3
        };
        parent.Lookup = oldDictionary;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // New dictionary only has key1
        var newDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1
        };

        var change = SubjectPropertyChange.Create<Dictionary<string, CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldDictionary,
            newValue: newDictionary);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.RemovedSubjects.Count);
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child2 && (string)x.Index! == "key2");
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child3 && (string)x.Index! == "key3");
        Assert.Empty(processor.AddedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Dictionary_AddAndRemoveInSingleChange_CallsBothCallbacks()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        var child3 = new CycleTestNode(context);

        // Set up parent with old dictionary
        var oldDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key2"] = child2
        };
        parent.Lookup = oldDictionary;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // New dictionary removes key2, adds key3
        var newDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key3"] = child3
        };

        var change = SubjectPropertyChange.Create<Dictionary<string, CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldDictionary,
            newValue: newDictionary);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(child2, processor.RemovedSubjects[0].Subject);
        Assert.Equal("key2", processor.RemovedSubjects[0].Index);

        Assert.Single(processor.AddedSubjects);
        Assert.Equal(child3, processor.AddedSubjects[0].Subject);
        Assert.Equal("key3", processor.AddedSubjects[0].Index);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Dictionary_PopulatedToEmpty_CallsOnSubjectRemovedForAllItems()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        // Set up parent with dictionary
        var oldDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key2"] = child2
        };
        parent.Lookup = oldDictionary;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        var newDictionary = new Dictionary<string, CycleTestNode>();

        var change = SubjectPropertyChange.Create<Dictionary<string, CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldDictionary,
            newValue: newDictionary);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.RemovedSubjects.Count);
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child1 && (string)x.Index! == "key1");
        Assert.Contains(processor.RemovedSubjects, x => x.Subject == child2 && (string)x.Index! == "key2");
        Assert.Empty(processor.AddedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Dictionary_NullToPopulated_CallsOnSubjectAddedWithKeys()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        var newDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key2"] = child2
        };

        // Create change with null old value
        var change = SubjectPropertyChange.Create<Dictionary<string, CycleTestNode>?>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: null,
            newValue: newDictionary);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Equal(2, processor.AddedSubjects.Count);
        Assert.Contains(processor.AddedSubjects, x => x.Subject == child1 && (string)x.Index! == "key1");
        Assert.Contains(processor.AddedSubjects, x => x.Subject == child2 && (string)x.Index! == "key2");
        Assert.Empty(processor.RemovedSubjects);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_Dictionary_ValueReplacedAtSameKey_CallsBothAddedAndRemoved()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        // Set up parent with dictionary containing child1 at key1
        var oldDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1
        };
        parent.Lookup = oldDictionary;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Replace value at same key with different subject
        var newDictionary = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child2
        };

        var change = SubjectPropertyChange.Create<Dictionary<string, CycleTestNode>>(
            property.Reference,
            source: null,
            changedTimestamp: DateTimeOffset.UtcNow,
            receivedTimestamp: null,
            oldValue: oldDictionary,
            newValue: newDictionary);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert - replacing value at same key should trigger both remove and add
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(child1, processor.RemovedSubjects[0].Subject);
        Assert.Equal("key1", processor.RemovedSubjects[0].Index);

        Assert.Single(processor.AddedSubjects);
        Assert.Equal(child2, processor.AddedSubjects[0].Subject);
        Assert.Equal("key1", processor.AddedSubjects[0].Index);
    }

    #endregion

    private class TestStructuralChangeProcessor : StructuralChangeProcessor
    {
        public List<(RegisteredSubjectProperty Property, IInterceptorSubject Subject, object? Index)> AddedSubjects { get; } = new();
        public List<(RegisteredSubjectProperty Property, IInterceptorSubject Subject, object? Index)> RemovedSubjects { get; } = new();

        protected override Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
        {
            AddedSubjects.Add((property, subject, index));
            return Task.CompletedTask;
        }

        protected override Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
        {
            RemovedSubjects.Add((property, subject, index));
            return Task.CompletedTask;
        }
    }
}
