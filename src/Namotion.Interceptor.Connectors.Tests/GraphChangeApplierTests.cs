using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for GraphChangeApplier - the symmetric counterpart to GraphChangePublisher.
/// Verifies correct application of external changes to collections, dictionaries, and references.
/// </summary>
public class GraphChangeApplierTests
{
    private readonly object _testSource = new();

    #region Collection Tests

    [Fact]
    public void AddToCollection_WithValidCollection_AddsSubject()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.AddToCollection(property, child, _testSource);

        // Assert
        Assert.True(result);
        Assert.Single(parent.Items);
        Assert.Same(child, parent.Items[0]);
    }

    [Fact]
    public void AddToCollection_WithNonCollectionProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = applier.AddToCollection(property, child, _testSource);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void AddToCollection_AppendsMultipleSubjects()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        applier.AddToCollection(property, child1, _testSource);
        applier.AddToCollection(property, child2, _testSource);

        // Assert
        Assert.Equal(2, parent.Items.Count);
        Assert.Same(child1, parent.Items[0]);
        Assert.Same(child2, parent.Items[1]);
    }

    [Fact]
    public void RemoveFromCollection_WithExistingSubject_RemovesSubject()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        parent.Items = new List<CycleTestNode> { child1, child2 };

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.RemoveFromCollection(property, child1, _testSource);

        // Assert
        Assert.True(result);
        Assert.Single(parent.Items);
        Assert.Same(child2, parent.Items[0]);
    }

    [Fact]
    public void RemoveFromCollection_WithNonExistingSubject_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        parent.Items = new List<CycleTestNode> { child1 };

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.RemoveFromCollection(property, child2, _testSource);

        // Assert
        Assert.False(result);
        Assert.Single(parent.Items);
    }

    [Fact]
    public void RemoveFromCollection_WithNonCollectionProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = applier.RemoveFromCollection(property, child, _testSource);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void RemoveFromCollectionByIndex_WithValidIndex_RemovesSubject()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        var child3 = new CycleTestNode(context);
        parent.Items = new List<CycleTestNode> { child1, child2, child3 };

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.RemoveFromCollectionByIndex(property, 1, _testSource);

        // Assert
        Assert.True(result);
        Assert.Equal(2, parent.Items.Count);
        Assert.Same(child1, parent.Items[0]);
        Assert.Same(child3, parent.Items[1]);
    }

    [Fact]
    public void RemoveFromCollectionByIndex_WithInvalidIndex_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        parent.Items = new List<CycleTestNode> { child };

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var resultNegative = applier.RemoveFromCollectionByIndex(property, -1, _testSource);
        var resultTooLarge = applier.RemoveFromCollectionByIndex(property, 5, _testSource);

        // Assert
        Assert.False(resultNegative);
        Assert.False(resultTooLarge);
        Assert.Single(parent.Items);
    }

    #endregion

    #region Dictionary Tests

    [Fact]
    public void AddToDictionary_WithValidDictionary_AddsEntry()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        var result = applier.AddToDictionary(property, "key1", child, _testSource);

        // Assert
        Assert.True(result);
        Assert.Single(parent.Lookup);
        Assert.Same(child, parent.Lookup["key1"]);
    }

    [Fact]
    public void AddToDictionary_WithNonDictionaryProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = applier.AddToDictionary(property, "key1", child, _testSource);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void AddToDictionary_AddsMultipleEntries()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        applier.AddToDictionary(property, "key1", child1, _testSource);
        applier.AddToDictionary(property, "key2", child2, _testSource);

        // Assert
        Assert.Equal(2, parent.Lookup.Count);
        Assert.Same(child1, parent.Lookup["key1"]);
        Assert.Same(child2, parent.Lookup["key2"]);
    }

    [Fact]
    public void RemoveFromDictionary_WithExistingKey_RemovesEntry()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        parent.Lookup = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child1,
            ["key2"] = child2
        };

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        var result = applier.RemoveFromDictionary(property, "key1", _testSource);

        // Assert
        Assert.True(result);
        Assert.Single(parent.Lookup);
        Assert.True(parent.Lookup.ContainsKey("key2"));
        Assert.False(parent.Lookup.ContainsKey("key1"));
    }

    [Fact]
    public void RemoveFromDictionary_WithNonExistingKey_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        parent.Lookup = new Dictionary<string, CycleTestNode>
        {
            ["key1"] = child
        };

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        var result = applier.RemoveFromDictionary(property, "nonexistent", _testSource);

        // Assert
        Assert.False(result);
        Assert.Single(parent.Lookup);
    }

    [Fact]
    public void RemoveFromDictionary_WithNonDictionaryProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = applier.RemoveFromDictionary(property, "key1", _testSource);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Reference Tests

    [Fact]
    public void SetReference_WithValidReferenceProperty_SetsSubject()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Child));

        // Act
        var result = applier.SetReference(property, child, _testSource);

        // Assert
        Assert.True(result);
        Assert.Same(child, parent.Child);
    }

    [Fact]
    public void SetReference_WithNull_ClearsReference()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        parent.Child = child;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Child));

        // Act
        var result = applier.SetReference(property, null, _testSource);

        // Assert
        Assert.True(result);
        Assert.Null(parent.Child);
    }

    [Fact]
    public void SetReference_WithNonReferenceProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = applier.SetReference(property, child, _testSource);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void SetReference_ReplacesExistingReference()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child1 = new CycleTestNode(context);
        var child2 = new CycleTestNode(context);
        parent.Child = child1;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Child));

        // Act
        var result = applier.SetReference(property, child2, _testSource);

        // Assert
        Assert.True(result);
        Assert.Same(child2, parent.Child);
    }

    [Fact]
    public void SetReference_WithCollectionProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.SetReference(property, child, _testSource);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void SetReference_WithDictionaryProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        var result = applier.SetReference(property, child, _testSource);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Source Parameter Tests

    [Fact]
    public void AddToCollection_UsesSourceForLoopPrevention()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeObservable();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var customSource = new object();

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Track changes to verify source is passed through
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act
        applier.AddToCollection(property, child, customSource);

        // Assert
        Assert.NotEmpty(changes);
        var itemsChange = changes.First(c => c.Property.Name == nameof(CycleTestNode.Items));
        Assert.Same(customSource, itemsChange.Source);
    }

    [Fact]
    public void SetReference_UsesSourceForLoopPrevention()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithPropertyChangeObservable();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var customSource = new object();

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Child));

        // Track changes to verify source is passed through
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change));

        // Act
        applier.SetReference(property, child, customSource);

        // Assert
        Assert.NotEmpty(changes);
        var childChange = changes.First(c => c.Property.Name == nameof(CycleTestNode.Child));
        Assert.Same(customSource, childChange.Source);
    }

    #endregion

    #region Custom SubjectFactory Tests

    [Fact]
    public void Constructor_WithCustomSubjectFactory_UsesCustomFactory()
    {
        // Arrange
        var customFactory = new DefaultSubjectFactory();
        var applier = new GraphChangeApplier(customFactory);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.AddToCollection(property, child, _testSource);

        // Assert
        Assert.True(result);
        Assert.Single(parent.Items);
    }

    [Fact]
    public void Constructor_WithNullSubjectFactory_UsesDefaultFactory()
    {
        // Arrange
        var applier = new GraphChangeApplier(null);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = applier.AddToCollection(property, child, _testSource);

        // Assert
        Assert.True(result);
        Assert.Single(parent.Items);
    }

    #endregion
}
