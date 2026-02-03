using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for GraphChangeApplier - the symmetric counterpart to GraphChangePublisher.
/// Verifies correct application of external changes to collections, dictionaries, and references.
/// Uses factory pattern for add operations to avoid creating subjects when validation fails.
/// </summary>
public class GraphChangeApplierTests
{
    private readonly object _testSource = new();

    #region Collection Tests

    [Fact]
    public async Task AddToCollectionAsync_WithValidCollection_AddsSubject()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = await applier.AddToCollectionAsync(property, () => Task.FromResult<IInterceptorSubject>(child), _testSource);

        // Assert
        Assert.Same(child, result);
        Assert.Single(parent.Items);
        Assert.Same(child, parent.Items[0]);
    }

    [Fact]
    public async Task AddToCollectionAsync_WithNonCollectionProperty_ReturnsNull()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var factoryCalled = false;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = await applier.AddToCollectionAsync(
            property,
            () => { factoryCalled = true; return Task.FromResult<IInterceptorSubject>(child); },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.False(factoryCalled); // Factory should NOT be called when validation fails
    }

    [Fact]
    public async Task AddToCollectionAsync_AppendsMultipleSubjects()
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
        await applier.AddToCollectionAsync(property, () => Task.FromResult<IInterceptorSubject>(child1), _testSource);
        await applier.AddToCollectionAsync(property, () => Task.FromResult<IInterceptorSubject>(child2), _testSource);

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
    public async Task AddToDictionaryAsync_WithValidDictionary_AddsEntry()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        var result = await applier.AddToDictionaryAsync(property, "key1", () => Task.FromResult<IInterceptorSubject>(child), _testSource);

        // Assert
        Assert.Same(child, result);
        Assert.Single(parent.Lookup);
        Assert.Same(child, parent.Lookup["key1"]);
    }

    [Fact]
    public async Task AddToDictionaryAsync_WithNonDictionaryProperty_ReturnsNull()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var factoryCalled = false;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = await applier.AddToDictionaryAsync(
            property,
            "key1",
            () => { factoryCalled = true; return Task.FromResult<IInterceptorSubject>(child); },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.False(factoryCalled); // Factory should NOT be called when validation fails
    }

    [Fact]
    public async Task AddToDictionaryAsync_AddsMultipleEntries()
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
        await applier.AddToDictionaryAsync(property, "key1", () => Task.FromResult<IInterceptorSubject>(child1), _testSource);
        await applier.AddToDictionaryAsync(property, "key2", () => Task.FromResult<IInterceptorSubject>(child2), _testSource);

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
    public async Task SetReferenceAsync_WithValidReferenceProperty_SetsSubject()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Child));

        // Act
        var result = await applier.SetReferenceAsync(property, () => Task.FromResult<IInterceptorSubject>(child), _testSource);

        // Assert
        Assert.Same(child, result);
        Assert.Same(child, parent.Child);
    }

    [Fact]
    public void RemoveReference_ClearsReference()
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
        var result = applier.RemoveReference(property, _testSource);

        // Assert
        Assert.True(result);
        Assert.Null(parent.Child);
    }

    [Fact]
    public async Task SetReferenceAsync_WithNonReferenceProperty_ReturnsNull()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var factoryCalled = false;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = await applier.SetReferenceAsync(
            property,
            () => { factoryCalled = true; return Task.FromResult<IInterceptorSubject>(child); },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.False(factoryCalled); // Factory should NOT be called when validation fails
    }

    [Fact]
    public async Task SetReferenceAsync_ReplacesExistingReference()
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
        var result = await applier.SetReferenceAsync(property, () => Task.FromResult<IInterceptorSubject>(child2), _testSource);

        // Assert
        Assert.Same(child2, result);
        Assert.Same(child2, parent.Child);
    }

    [Fact]
    public async Task SetReferenceAsync_WithCollectionProperty_ReturnsNull()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var factoryCalled = false;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = await applier.SetReferenceAsync(
            property,
            () => { factoryCalled = true; return Task.FromResult<IInterceptorSubject>(child); },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task SetReferenceAsync_WithDictionaryProperty_ReturnsNull()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);
        var factoryCalled = false;

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Lookup));

        // Act
        var result = await applier.SetReferenceAsync(
            property,
            () => { factoryCalled = true; return Task.FromResult<IInterceptorSubject>(child); },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.False(factoryCalled);
    }

    [Fact]
    public void RemoveReference_WithNonReferenceProperty_ReturnsFalse()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = applier.RemoveReference(property, _testSource);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Source Parameter Tests

    [Fact]
    public async Task AddToCollectionAsync_UsesSourceForLoopPrevention()
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
        await applier.AddToCollectionAsync(property, () => Task.FromResult<IInterceptorSubject>(child), customSource);

        // Assert
        Assert.NotEmpty(changes);
        var itemsChange = changes.First(c => c.Property.Name == nameof(CycleTestNode.Items));
        Assert.Same(customSource, itemsChange.Source);
    }

    [Fact]
    public async Task SetReferenceAsync_UsesSourceForLoopPrevention()
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
        await applier.SetReferenceAsync(property, () => Task.FromResult<IInterceptorSubject>(child), customSource);

        // Assert
        Assert.NotEmpty(changes);
        var childChange = changes.First(c => c.Property.Name == nameof(CycleTestNode.Child));
        Assert.Same(customSource, childChange.Source);
    }

    #endregion

    #region Custom SubjectFactory Tests

    [Fact]
    public async Task Constructor_WithCustomSubjectFactory_UsesCustomFactory()
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
        var result = await applier.AddToCollectionAsync(property, () => Task.FromResult<IInterceptorSubject>(child), _testSource);

        // Assert
        Assert.NotNull(result);
        Assert.Single(parent.Items);
    }

    [Fact]
    public async Task Constructor_WithNullSubjectFactory_UsesDefaultFactory()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var child = new CycleTestNode(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Items));

        // Act
        var result = await applier.AddToCollectionAsync(property, () => Task.FromResult<IInterceptorSubject>(child), _testSource);

        // Assert
        Assert.NotNull(result);
        Assert.Single(parent.Items);
    }

    #endregion

    #region Factory Pattern Tests

    [Fact]
    public async Task AddToCollectionAsync_FactoryNotCalledWhenValidationFails()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var factoryCallCount = 0;

        var registered = parent.TryGetRegisteredSubject()!;
        // Use a non-collection property to trigger validation failure
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = await applier.AddToCollectionAsync(
            property,
            () =>
            {
                factoryCallCount++;
                return Task.FromResult<IInterceptorSubject>(new CycleTestNode(context));
            },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.Equal(0, factoryCallCount); // Factory should never be called
    }

    [Fact]
    public async Task AddToDictionaryAsync_FactoryNotCalledWhenValidationFails()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var factoryCallCount = 0;

        var registered = parent.TryGetRegisteredSubject()!;
        // Use a non-dictionary property to trigger validation failure
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = await applier.AddToDictionaryAsync(
            property,
            "key",
            () =>
            {
                factoryCallCount++;
                return Task.FromResult<IInterceptorSubject>(new CycleTestNode(context));
            },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.Equal(0, factoryCallCount); // Factory should never be called
    }

    [Fact]
    public async Task SetReferenceAsync_FactoryNotCalledWhenValidationFails()
    {
        // Arrange
        var applier = new GraphChangeApplier();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new CycleTestNode(context);
        var factoryCallCount = 0;

        var registered = parent.TryGetRegisteredSubject()!;
        // Use a non-reference property to trigger validation failure
        var property = registered.Properties.First(p => p.Name == nameof(CycleTestNode.Name));

        // Act
        var result = await applier.SetReferenceAsync(
            property,
            () =>
            {
                factoryCallCount++;
                return Task.FromResult<IInterceptorSubject>(new CycleTestNode(context));
            },
            _testSource);

        // Assert
        Assert.Null(result);
        Assert.Equal(0, factoryCallCount); // Factory should never be called
    }

    #endregion
}
