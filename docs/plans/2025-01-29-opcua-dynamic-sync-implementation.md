# OPC UA Dynamic Address Space Synchronization - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement bidirectional live synchronization between C# object model and OPC UA address space, enabling runtime structural changes (add/remove subjects) to propagate in both directions.

**Architecture:** Property-level change processing with connector-scoped reference counting. Symmetric design for client and server. Uses existing `CollectionDiffBuilder` for structural diffing. Loop prevention via `change.Source` mechanism.

**Tech Stack:** C# 13, .NET 9.0, OPC UA Foundation SDK, System.Reactive, xUnit

**Design Document:** `docs/plans/2025-12-17-opcua-dynamic-sync-design.md`

---

## Phase 1: Connectors Library Abstractions

Add reusable infrastructure for connector-scoped reference counting and structural change processing.

### Task 1.1: Create ConnectorReferenceCounter<TData>

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/ConnectorReferenceCounter.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ConnectorReferenceCounterTests.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.Connectors.Tests/ConnectorReferenceCounterTests.cs
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests;

public class ConnectorReferenceCounterTests
{
    [Fact]
    public void IncrementAndCheckFirst_FirstReference_ReturnsTrue()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();

        // Act
        var isFirst = counter.IncrementAndCheckFirst(subject, () => "data", out var data);

        // Assert
        Assert.True(isFirst);
        Assert.Equal("data", data);
    }

    [Fact]
    public void IncrementAndCheckFirst_SecondReference_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);

        // Act
        var isFirst = counter.IncrementAndCheckFirst(subject, () => "new-data", out var data);

        // Assert
        Assert.False(isFirst);
        Assert.Equal("data", data); // Original data, not new
    }

    [Fact]
    public void DecrementAndCheckLast_LastReference_ReturnsTrue()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);

        // Act
        var isLast = counter.DecrementAndCheckLast(subject, out var data);

        // Assert
        Assert.True(isLast);
        Assert.Equal("data", data);
    }

    [Fact]
    public void DecrementAndCheckLast_NotLastReference_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);
        counter.IncrementAndCheckFirst(subject, () => "data", out _); // Second ref

        // Act
        var isLast = counter.DecrementAndCheckLast(subject, out var data);

        // Assert
        Assert.False(isLast);
        Assert.Null(data);
    }

    [Fact]
    public void TryGetData_ExistingSubject_ReturnsData()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);

        // Act
        var found = counter.TryGetData(subject, out var data);

        // Assert
        Assert.True(found);
        Assert.Equal("data", data);
    }

    [Fact]
    public void TryGetData_NonExistingSubject_ReturnsFalse()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();

        // Act
        var found = counter.TryGetData(subject, out var data);

        // Assert
        Assert.False(found);
        Assert.Null(data);
    }

    [Fact]
    public void Clear_ReturnsAllDataForCleanup()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject1 = new TestSubject();
        var subject2 = new TestSubject();
        counter.IncrementAndCheckFirst(subject1, () => "data1", out _);
        counter.IncrementAndCheckFirst(subject2, () => "data2", out _);

        // Act
        var clearedData = counter.Clear().ToList();

        // Assert
        Assert.Equal(2, clearedData.Count);
        Assert.Contains("data1", clearedData);
        Assert.Contains("data2", clearedData);
    }

    [Fact]
    public void Clear_EmptiesCounter()
    {
        // Arrange
        var counter = new ConnectorReferenceCounter<string>();
        var subject = new TestSubject();
        counter.IncrementAndCheckFirst(subject, () => "data", out _);
        counter.Clear();

        // Act
        var found = counter.TryGetData(subject, out _);

        // Assert
        Assert.False(found);
    }

    private class TestSubject : IInterceptorSubject
    {
        public IInterceptorSubjectContext Context => throw new NotImplementedException();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ConnectorReferenceCounterTests" -v n`
Expected: FAIL with "type or namespace 'ConnectorReferenceCounter' could not be found"

**Step 3: Write minimal implementation**

```csharp
// src/Namotion.Interceptor.Connectors/ConnectorReferenceCounter.cs
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Tracks connector-scoped reference counts for subjects with associated data.
/// Thread-safe for concurrent access.
/// </summary>
/// <typeparam name="TData">Connector-specific data type (e.g., NodeState, List&lt;MonitoredItem&gt;).</typeparam>
public class ConnectorReferenceCounter<TData>
{
    private readonly Dictionary<IInterceptorSubject, (int Count, TData Data)> _entries = new();
    private readonly object _lock = new();

    /// <summary>
    /// Increments reference count. Returns true if this is the first reference.
    /// For first reference, dataFactory is called to create associated data.
    /// </summary>
    public bool IncrementAndCheckFirst(IInterceptorSubject subject, Func<TData> dataFactory, out TData data)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(subject, out var entry))
            {
                _entries[subject] = (entry.Count + 1, entry.Data);
                data = entry.Data;
                return false;
            }

            data = dataFactory();
            _entries[subject] = (1, data);
            return true;
        }
    }

    /// <summary>
    /// Decrements reference count. Returns true if this was the last reference.
    /// On last reference, data is returned for cleanup.
    /// </summary>
    public bool DecrementAndCheckLast(IInterceptorSubject subject, out TData? data)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(subject, out var entry))
            {
                data = default;
                return false;
            }

            if (entry.Count == 1)
            {
                _entries.Remove(subject);
                data = entry.Data;
                return true;
            }

            _entries[subject] = (entry.Count - 1, entry.Data);
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Gets data for subject if tracked, null otherwise.
    /// </summary>
    public bool TryGetData(IInterceptorSubject subject, out TData? data)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(subject, out var entry))
            {
                data = entry.Data;
                return true;
            }
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Clears all entries, returns all data for cleanup.
    /// </summary>
    public IEnumerable<TData> Clear()
    {
        lock (_lock)
        {
            var data = _entries.Values.Select(e => e.Data).ToList();
            _entries.Clear();
            return data;
        }
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ConnectorReferenceCounterTests" -v n`
Expected: PASS

---

### Task 1.2: Add Thread-Safety Tests for ConnectorReferenceCounter

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/ConnectorReferenceCounterTests.cs`

**Step 1: Write additional tests for thread safety**

```csharp
// Add to ConnectorReferenceCounterTests.cs

[Fact]
public async Task IncrementAndCheckFirst_ConcurrentAccess_CountsCorrectly()
{
    // Arrange
    var counter = new ConnectorReferenceCounter<int>();
    var subject = new TestSubject();
    var tasks = new List<Task>();
    var firstCount = 0;

    // Act - 100 concurrent increments
    for (int i = 0; i < 100; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            var isFirst = counter.IncrementAndCheckFirst(subject, () => 42, out _);
            if (isFirst) Interlocked.Increment(ref firstCount);
        }));
    }
    await Task.WhenAll(tasks);

    // Assert - exactly one should be first
    Assert.Equal(1, firstCount);
}

[Fact]
public async Task DecrementAndCheckLast_ConcurrentAccess_CountsCorrectly()
{
    // Arrange
    var counter = new ConnectorReferenceCounter<int>();
    var subject = new TestSubject();

    // Add 100 references
    for (int i = 0; i < 100; i++)
    {
        counter.IncrementAndCheckFirst(subject, () => 42, out _);
    }

    var tasks = new List<Task>();
    var lastCount = 0;

    // Act - 100 concurrent decrements
    for (int i = 0; i < 100; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            var isLast = counter.DecrementAndCheckLast(subject, out _);
            if (isLast) Interlocked.Increment(ref lastCount);
        }));
    }
    await Task.WhenAll(tasks);

    // Assert - exactly one should be last
    Assert.Equal(1, lastCount);

    // Counter should be empty
    Assert.False(counter.TryGetData(subject, out _));
}
```

**Step 2: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ConnectorReferenceCounterTests" -v n`
Expected: PASS

---

### Task 1.3: Add CollectionNodeStructure Enum

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Attributes/CollectionNodeStructure.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaReferenceAttribute.cs`

**Step 1: Create the enum**

```csharp
// src/Namotion.Interceptor.OpcUa/Attributes/CollectionNodeStructure.cs
namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Defines how collections map to OPC UA node structures.
/// </summary>
public enum CollectionNodeStructure
{
    /// <summary>
    /// Children are direct children of parent with BrowseName "PropertyName[index]".
    /// Results in: Parent/Machines[0], Parent/Machines[1]
    /// </summary>
    Flat,

    /// <summary>
    /// Children are placed under an intermediate container node.
    /// Results in: Parent/Machines/Machines[0], Parent/Machines/Machines[1]
    /// </summary>
    Container
}
```

**Step 2: Add property to OpcUaReferenceAttribute**

```csharp
// Modify: src/Namotion.Interceptor.OpcUa/Attributes/OpcUaReferenceAttribute.cs
// Add this property to the class:

/// <summary>
/// Gets or sets the node structure for collections. Default is Flat.
/// Dictionaries always use Container structure (this property is ignored for dictionaries).
/// </summary>
public CollectionNodeStructure CollectionStructure { get; init; } = CollectionNodeStructure.Flat;
```

**Step 3: Run build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: Build succeeded

---

### Task 1.4: Create StructuralChangeProcessor Base Class

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/StructuralChangeProcessor.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/StructuralChangeProcessorTests.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.Connectors.Tests/StructuralChangeProcessorTests.cs
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests;

public class StructuralChangeProcessorTests
{
    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectReference_CallsOnSubjectAdded()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Parent));

        var change = new SubjectPropertyChange(
            property,
            oldValue: null,
            newValue: child,
            source: null);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.AddedSubjects);
        Assert.Equal(child, processor.AddedSubjects[0].Subject);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_SubjectReference_CallsOnSubjectRemoved()
    {
        // Arrange
        var processor = new TestStructuralChangeProcessor();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Parent));

        var change = new SubjectPropertyChange(
            property,
            oldValue: child,
            newValue: null,
            source: null);

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Single(processor.RemovedSubjects);
        Assert.Equal(child, processor.RemovedSubjects[0].Subject);
    }

    [Fact]
    public async Task ProcessPropertyChangeAsync_IgnoresSourceFromIgnoreSource()
    {
        // Arrange
        var ignoreSource = new object();
        var processor = new TestStructuralChangeProcessor { TestIgnoreSource = ignoreSource };
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person(context);

        var registered = parent.TryGetRegisteredSubject()!;
        var property = registered.Properties.First(p => p.Name == nameof(Person.Parent));

        var change = new SubjectPropertyChange(
            property,
            oldValue: null,
            newValue: child,
            source: ignoreSource); // Same as ignore source

        // Act
        await processor.ProcessPropertyChangeAsync(change, property);

        // Assert
        Assert.Empty(processor.AddedSubjects);
    }

    private class TestStructuralChangeProcessor : StructuralChangeProcessor
    {
        public object? TestIgnoreSource { get; set; }
        public List<(RegisteredSubjectProperty Property, IInterceptorSubject Subject, object? Index)> AddedSubjects { get; } = new();
        public List<(RegisteredSubjectProperty Property, IInterceptorSubject Subject, object? Index)> RemovedSubjects { get; } = new();
        public List<SubjectPropertyChange> ValueChanges { get; } = new();

        protected override object? IgnoreSource => TestIgnoreSource;

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

        protected override Task OnValueChangedAsync(SubjectPropertyChange change)
        {
            ValueChanges.Add(change);
            return Task.CompletedTask;
        }
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~StructuralChangeProcessorTests" -v n`
Expected: FAIL with "type or namespace 'StructuralChangeProcessor' could not be found"

**Step 3: Write minimal implementation**

```csharp
// src/Namotion.Interceptor.Connectors/StructuralChangeProcessor.cs
using System.Collections;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base class for processing structural property changes (add/remove subjects).
/// Handles branching on property type and collection diffing.
/// </summary>
public abstract class StructuralChangeProcessor
{
    private readonly CollectionDiffBuilder _diffBuilder = new();

    /// <summary>
    /// Source to ignore (prevents sync loops).
    /// </summary>
    protected abstract object? IgnoreSource { get; }

    /// <summary>
    /// Process a property change, branching on property type.
    /// </summary>
    public async Task ProcessPropertyChangeAsync(SubjectPropertyChange change, RegisteredSubjectProperty property)
    {
        if (change.Source == IgnoreSource && IgnoreSource is not null)
            return;

        if (property.IsSubjectReference)
        {
            var oldSubject = change.OldValue as IInterceptorSubject;
            var newSubject = change.NewValue as IInterceptorSubject;

            if (oldSubject is not null && !ReferenceEquals(oldSubject, newSubject))
                await OnSubjectRemovedAsync(property, oldSubject, index: null);
            if (newSubject is not null && !ReferenceEquals(oldSubject, newSubject))
                await OnSubjectAddedAsync(property, newSubject, index: null);
        }
        else if (property.IsSubjectCollection)
        {
            var oldCollection = change.OldValue as IReadOnlyList<IInterceptorSubject> ?? Array.Empty<IInterceptorSubject>();
            var newCollection = change.NewValue as IReadOnlyList<IInterceptorSubject> ?? Array.Empty<IInterceptorSubject>();

            _diffBuilder.GetCollectionChanges(oldCollection, newCollection,
                out var operations, out var newItems, out _);

            // Process removes (descending order)
            if (operations is not null)
            {
                foreach (var op in operations)
                {
                    if (op.Action == SubjectCollectionOperationType.Remove)
                        await OnSubjectRemovedAsync(property, oldCollection[(int)op.Index], op.Index);
                }
            }

            // Process adds
            if (newItems is not null)
            {
                foreach (var (index, subject) in newItems)
                    await OnSubjectAddedAsync(property, subject, index);
            }

            // Reorders ignored - order is connector-specific (OPC UA: no-op)
        }
        else if (property.IsSubjectDictionary)
        {
            var oldDict = change.OldValue as IDictionary;
            var newDict = change.NewValue as IDictionary ?? new Dictionary<object, object>();

            _diffBuilder.GetDictionaryChanges(oldDict, newDict,
                out _, out var newItems, out var removedKeys);

            var oldChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
            if (removedKeys is not null)
            {
                foreach (var key in removedKeys)
                {
                    if (oldChildren.TryGetValue(key, out var subject))
                        await OnSubjectRemovedAsync(property, subject, key);
                }
            }

            if (newItems is not null)
            {
                foreach (var (key, subject) in newItems)
                    await OnSubjectAddedAsync(property, (IInterceptorSubject)subject, key);
            }
        }
        else
        {
            await OnValueChangedAsync(change);
        }
    }

    /// <summary>
    /// Called when a subject is added to a property.
    /// </summary>
    protected abstract Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);

    /// <summary>
    /// Called when a subject is removed from a property.
    /// </summary>
    protected abstract Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);

    /// <summary>
    /// Called when a value property changes (non-structural).
    /// </summary>
    protected abstract Task OnValueChangedAsync(SubjectPropertyChange change);
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~StructuralChangeProcessorTests" -v n`
Expected: PASS

---

### Task 1.5: Add Collection and Dictionary Tests for StructuralChangeProcessor

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/StructuralChangeProcessorTests.cs`
- May need: Create test model with collections if not existing

**Step 1: Check for existing collection test models**

Check if `Person` model has collection properties. If not, we need a model with collections. The existing `CycleTestNode` might work, or we may need to create one.

**Step 2: Add collection and dictionary tests**

```csharp
// Add to StructuralChangeProcessorTests.cs

[Fact]
public async Task ProcessPropertyChangeAsync_Collection_Add_CallsOnSubjectAdded()
{
    // Arrange
    var processor = new TestStructuralChangeProcessor();
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

    // Need a model with collection property - use appropriate test model
    // This test validates collection add detection

    var child1 = new Person(context);
    var child2 = new Person(context);

    // Simulate collection change: [] -> [child1, child2]
    var oldCollection = Array.Empty<IInterceptorSubject>();
    var newCollection = new IInterceptorSubject[] { child1, child2 };

    // Create a mock property that reports IsSubjectCollection = true
    // (Implementation depends on how test infrastructure allows this)

    // For now, test the diff builder directly
    var diffBuilder = new CollectionDiffBuilder();
    diffBuilder.GetCollectionChanges(oldCollection, newCollection, out _, out var newItems, out _);

    Assert.Equal(2, newItems!.Count);
}

[Fact]
public async Task ProcessPropertyChangeAsync_Collection_Remove_CallsOnSubjectRemoved()
{
    // Arrange
    var processor = new TestStructuralChangeProcessor();
    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

    var child1 = new Person(context);
    var child2 = new Person(context);

    // Simulate collection change: [child1, child2] -> [child1]
    var oldCollection = new IInterceptorSubject[] { child1, child2 };
    var newCollection = new IInterceptorSubject[] { child1 };

    var diffBuilder = new CollectionDiffBuilder();
    diffBuilder.GetCollectionChanges(oldCollection, newCollection, out var operations, out _, out _);

    Assert.Single(operations!);
    Assert.Equal(SubjectCollectionOperationType.Remove, operations[0].Action);
}
```

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~StructuralChangeProcessorTests" -v n`
Expected: PASS

---

### Task 1.6: Run Full Connectors Test Suite

**Step 1: Run all Connectors tests to ensure no regressions**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests -v n`
Expected: All tests PASS

---

## Phase 1 Review Checkpoint

**Stop here for review.** Verify:
1. `ConnectorReferenceCounter<TData>` works correctly with thread-safe operations
2. `CollectionNodeStructure` enum is added to OpcUa attributes
3. `StructuralChangeProcessor` base class handles all property types
4. All existing Connectors tests still pass

---

## Phase 2: Server Reference Counting Refactor

Refactor `CustomNodeManager` to use `ConnectorReferenceCounter<NodeState>` instead of the current `_subjects` dictionary.

### Task 2.1: Analyze Current CustomNodeManager Implementation

**Files:**
- Read: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

**Step 1: Document current state tracking**

Read the file to understand:
- How `_subjects` dictionary is currently used
- Where nodes are created/removed
- Current lifecycle event subscriptions (if any)

This is a research task to understand refactoring scope.

---

### Task 2.2: Add ConnectorReferenceCounter to CustomNodeManager

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

**Step 1: Add reference counter field**

```csharp
// Add to CustomNodeManager class fields:
private readonly ConnectorReferenceCounter<NodeState> _subjectRefCounter = new();
```

**Step 2: Update CreateChildObject to use ref counter**

Modify `CreateChildObject` method to:
1. Call `_subjectRefCounter.IncrementAndCheckFirst()`
2. Only create node if `isFirst` is true
3. Always add reference from parent to child node

**Step 3: Update RemoveSubjectNodes to use ref counter**

Modify `RemoveSubjectNodes` method to:
1. Call `_subjectRefCounter.DecrementAndCheckLast()`
2. Only delete node if `isLast` is true
3. Always remove reference from parent

**Step 4: Run OPC UA server tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~Server" -v n`
Expected: PASS

---

### Task 2.3: Add Server Reference Counting Integration Test

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Server/ServerReferenceCountingTests.cs`

**Step 1: Write integration test for shared subjects**

```csharp
// src/Namotion.Interceptor.OpcUa.Tests/Server/ServerReferenceCountingTests.cs
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Server;

public class ServerReferenceCountingTests
{
    [Fact]
    public async Task SharedSubject_ReferencedTwice_CreatesOneNode()
    {
        // Arrange: Create model where same subject is referenced from two properties
        // Act: Start server, browse address space
        // Assert: Only one node exists for the shared subject
    }

    [Fact]
    public async Task SharedSubject_OneReferenceRemoved_NodeStillExists()
    {
        // Arrange: Subject referenced from two places
        // Act: Remove one reference
        // Assert: Node still exists (ref count > 0)
    }

    [Fact]
    public async Task SharedSubject_AllReferencesRemoved_NodeDeleted()
    {
        // Arrange: Subject referenced from two places
        // Act: Remove both references
        // Assert: Node is deleted
    }
}
```

**Step 2: Implement tests with real server/client**

Use existing test infrastructure from `SharedOpcUaServerFixture`.

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ServerReferenceCountingTests" -v n`
Expected: PASS

---

### Task 2.4: Run Full OPC UA Server Test Suite

**Step 1: Run all server tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~Server" -v n`
Expected: All tests PASS

---

## Phase 2 Review Checkpoint

**Stop here for review.** Verify:
1. `CustomNodeManager` uses `ConnectorReferenceCounter<NodeState>`
2. Shared subjects create only one node
3. Reference counting cleanup works correctly
4. All existing server tests pass

---

## Phase 3: Server Model → OPC UA Incremental Sync

Add live synchronization from C# model changes to OPC UA address space.

### Task 3.1: Create OpcUaServerStructuralChangeProcessor

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerStructuralChangeProcessor.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Server/OpcUaServerStructuralChangeProcessorTests.cs`

**Step 1: Write failing test**

```csharp
// Test that structural changes trigger node creation/deletion
[Fact]
public async Task ProcessPropertyChange_SubjectAdded_CreatesNode()
{
    // Arrange: Server with empty collection property
    // Act: Add subject to collection
    // Assert: New node appears in address space
}
```

**Step 2: Implement processor**

Create class extending `StructuralChangeProcessor` that:
- Calls `CustomNodeManager.CreateChildObject()` on add
- Calls `CustomNodeManager.RemoveSubjectNodes()` on remove
- Sets `IgnoreSource` to prevent loops

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaServerStructuralChangeProcessorTests" -v n`
Expected: PASS

---

### Task 3.2: Wire Structural Change Processor to Server Background Service

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServerBackgroundService.cs`

**Step 1: Add structural change queue**

Add separate queue/processing for structural changes (reference, collection, dictionary properties).

**Step 2: Add configuration option**

Add `EnableLiveSync` to `OpcUaServerConfiguration` (default: false).

**Step 3: Run integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests/Integration -v n`
Expected: PASS

---

### Task 3.3: Add ModelChangeEvent Emission

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

**Step 1: Add batched ModelChangeEvent emission**

```csharp
private readonly List<ModelChangeStructureDataType> _pendingModelChanges = new();

private void QueueModelChange(NodeId affectedNodeId, ModelChangeStructureVerbMask verb)
{
    _pendingModelChanges.Add(new ModelChangeStructureDataType
    {
        Affected = affectedNodeId,
        AffectedType = null,
        Verb = (byte)verb
    });
}

private void FlushModelChangeEvents()
{
    if (_pendingModelChanges.Count == 0)
        return;

    var e = new GeneralModelChangeEventState(null);
    // ... configure and emit event
    _pendingModelChanges.Clear();
}
```

**Step 2: Integrate with structural change processing**

Call `QueueModelChange()` after node add/remove, `FlushModelChangeEvents()` after batch.

---

### Task 3.4: Add Collection BrowseName Re-indexing

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaCollectionReindexTests.cs`

**Step 1: Write failing integration test**

```csharp
[Fact]
public async Task Server_CollectionItemRemoved_BrowseNamesReindexed()
{
    // Arrange: Server with collection [A, B, C] → BrowseNames: Items[0], Items[1], Items[2]
    // Act: Remove B from collection
    // Assert: Browse server, verify BrowseNames are now Items[0], Items[1] (not Items[0], Items[2])
}
```

**Step 2: Implement re-indexing**

```csharp
private void ReindexCollectionBrowseNames(RegisteredSubjectProperty property)
{
    var children = property.Children.ToList();
    for (var i = 0; i < children.Count; i++)
    {
        if (_subjectRefCounter.TryGetData(children[i].Subject, out var node) && node is not null)
        {
            node.BrowseName = new QualifiedName($"{property.Name}[{i}]", NamespaceIndex);
        }
    }
}
```

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~CollectionReindex" -v n`
Expected: PASS

---

### Task 3.5: Add Server Live Sync Integration Test

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaServerLiveSyncTests.cs`

**Step 1: Write comprehensive integration tests**

```csharp
public class OpcUaServerLiveSyncTests : IClassFixture<SharedOpcUaServerFixture>
{
    [Fact]
    public async Task AddSubjectToCollection_ClientSeesBrowseChange()
    {
        // Full round-trip: model change → server → client browse
    }

    [Fact]
    public async Task RemoveSubjectFromCollection_ClientSeesBrowseChange()
    {
        // Full round-trip: model change → server → client browse
    }

    [Fact]
    public async Task ReplaceSubjectReference_ClientSeesBrowseChange()
    {
        // Full round-trip: model change → server → client browse
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ServerLiveSync" -v n`
Expected: PASS

---

### Task 3.6: Run Full OPC UA Test Suite

**Step 1: Run all OPC UA tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n`
Expected: All tests PASS

---

## Phase 3 Review Checkpoint

**Stop here for review.** Verify:
1. Server responds to model changes by updating address space
2. ModelChangeEvents are emitted correctly
3. Collection BrowseNames are re-indexed after changes
4. All OPC UA tests pass

---

## Phase 4: Client Reference Counting Refactor

Refactor client to use `ConnectorReferenceCounter<List<MonitoredItem>>` for tracked subjects.

### Task 4.1: Add Reference Counter to OpcUaSubjectClientSource

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

**Step 1: Add reference counter field**

```csharp
private readonly ConnectorReferenceCounter<List<MonitoredItem>> _subjectRefCounter = new();
```

**Step 2: Add subject-to-NodeId mapping**

```csharp
private readonly Dictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();
```

**Step 3: Update subject loading to use ref counter**

Modify loading logic to:
1. Call `IncrementAndCheckFirst()` when subject first encountered
2. Create MonitoredItems only on first reference
3. Track NodeId mapping for collection identity

---

### Task 4.2: Update Client Cleanup Logic

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

**Step 1: Update cleanup to use ref counter**

When subject is removed from model:
1. Call `DecrementAndCheckLast()`
2. Remove MonitoredItems only on last reference
3. Clean up NodeId mapping

**Step 2: Update reconnection logic**

On reconnect:
1. Call `Clear()` to get all MonitoredItems for cleanup
2. Clear NodeId mapping
3. Perform full resync

---

### Task 4.3: Add Client Reference Counting Tests

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Client/ClientReferenceCountingTests.cs`

**Step 1: Write tests**

```csharp
public class ClientReferenceCountingTests
{
    [Fact]
    public async Task SharedSubject_ReferencedTwice_CreatesOneMonitoredItemSet()
    {
        // Assert only one set of MonitoredItems for shared subject
    }

    [Fact]
    public async Task SharedSubject_OneReferenceRemoved_MonitoredItemsStillActive()
    {
        // Assert MonitoredItems remain when ref count > 0
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ClientReferenceCountingTests" -v n`
Expected: PASS

---

## Phase 4 Review Checkpoint

**Stop here for review.** Verify:
1. Client uses `ConnectorReferenceCounter<List<MonitoredItem>>`
2. Subject-to-NodeId mapping works for collection identity
3. MonitoredItem cleanup respects reference counting
4. All client tests pass

---

## Phase 5: Client Model → OPC UA Incremental Sync

Add live synchronization from C# model changes to OPC UA server via AddNodes/DeleteNodes.

### Task 5.1: Create OpcUaClientStructuralChangeProcessor

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs`

**Step 1: Implement processor**

Create class extending `StructuralChangeProcessor` that:
- Creates MonitoredItems on add
- Optionally calls AddNodes on server
- Removes MonitoredItems on remove
- Optionally calls DeleteNodes on server

---

### Task 5.2: Add Client Configuration Options

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs`

**Step 1: Add configuration options**

```csharp
public bool EnableLiveSync { get; set; } = false;
public bool EnableRemoteNodeManagement { get; set; } = false;
public bool EnableModelChangeEvents { get; set; } = false;
public bool EnablePeriodicResync { get; set; } = false;
public TimeSpan PeriodicResyncInterval { get; set; } = TimeSpan.FromSeconds(30);
```

---

### Task 5.3: Add Client Live Sync Integration Tests

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaClientLiveSyncTests.cs`

**Step 1: Write tests**

```csharp
public class OpcUaClientLiveSyncTests
{
    [Fact]
    public async Task AddSubjectToCollection_MonitoredItemsCreated()
    {
        // Model change → new MonitoredItems
    }

    [Fact]
    public async Task RemoveSubjectFromCollection_MonitoredItemsRemoved()
    {
        // Model change → MonitoredItems cleaned up
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ClientLiveSync" -v n`
Expected: PASS

---

## Phase 5 Review Checkpoint

**Stop here for review.** Verify:
1. Client creates/removes MonitoredItems dynamically
2. Configuration options work correctly
3. Optional AddNodes/DeleteNodes integration works
4. All tests pass

---

## Phase 6: Client OPC UA → Model Incremental Sync

Add synchronization from OPC UA server changes to C# model.

### Task 6.1: Implement ProcessNodeChangeAsync

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaNodeChangeProcessor.cs`

**Step 1: Implement node change processing**

```csharp
public class OpcUaNodeChangeProcessor
{
    public async Task ProcessNodeChangeAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        ISession session,
        CancellationToken ct)
    {
        // Browse remote nodes
        // Compare with local model
        // Create/remove subjects as needed
    }
}
```

---

### Task 6.2: Add ModelChangeEvent Subscription

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

**Step 1: Add optional event subscription**

When `EnableModelChangeEvents` is true:
1. Subscribe to `GeneralModelChangeEventType` on Server node
2. Process changes via `ProcessNodeChangeAsync`

---

### Task 6.3: Add Periodic Resync Fallback

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

**Step 1: Add periodic resync timer**

When `EnablePeriodicResync` is true:
1. Start timer with `PeriodicResyncInterval`
2. On tick, call `ProcessNodeChangeAsync` for root subject

---

### Task 6.4: Add Client OPC UA → Model Integration Tests

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaClientRemoteSyncTests.cs`

**Step 1: Write tests**

```csharp
public class OpcUaClientRemoteSyncTests
{
    [Fact]
    public async Task ServerAddsNode_ClientModelUpdated()
    {
        // Server structural change → client model update
    }

    [Fact]
    public async Task PeriodicResync_DetectsServerChanges()
    {
        // Timer-based resync works
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ClientRemoteSync" -v n`
Expected: PASS

---

## Phase 6 Review Checkpoint

**Stop here for review.** Verify:
1. Client responds to server ModelChangeEvents
2. Periodic resync detects structural changes
3. Source tracking prevents loops
4. All tests pass

---

## Phase 7: Server OPC UA → Model Sync

Add support for external clients to modify server structure via AddNodes/DeleteNodes.

### Task 7.1: Create InterceptorOpcUaServer

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Server/InterceptorOpcUaServer.cs`

**Step 1: Implement custom server**

```csharp
public class InterceptorOpcUaServer : StandardServer
{
    public override async Task<AddNodesResponse> AddNodesAsync(...)
    {
        if (!_configuration.EnableExternalNodeManagement)
            return BadServiceUnsupported;

        // Process AddNodes → create subjects in model
    }

    public override async Task<DeleteNodesResponse> DeleteNodesAsync(...)
    {
        if (!_configuration.EnableExternalNodeManagement)
            return BadServiceUnsupported;

        // Process DeleteNodes → remove subjects from model
    }
}
```

---

### Task 7.2: Add Server Configuration Options

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs`

**Step 1: Add configuration options**

```csharp
public bool EnableLiveSync { get; set; } = false;
public bool EnableExternalNodeManagement { get; set; } = false;
```

---

### Task 7.3: Add TypeDefinition → C# Type Resolution

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Server/OpcUaTypeRegistry.cs`

**Step 1: Implement type registry**

```csharp
public class OpcUaTypeRegistry
{
    // Map TypeDefinition NodeId → C# Type
    public void RegisterType<T>(NodeId typeDefinitionId) where T : IInterceptorSubject;
    public Type? ResolveType(NodeId typeDefinitionId);
}
```

---

### Task 7.4: Add Server External Management Integration Tests

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaServerExternalManagementTests.cs`

**Step 1: Write tests**

```csharp
public class OpcUaServerExternalManagementTests
{
    [Fact]
    public async Task ExternalAddNodes_CreatesSubjectInModel()
    {
        // External client calls AddNodes → subject appears in C# model
    }

    [Fact]
    public async Task ExternalDeleteNodes_RemovesSubjectFromModel()
    {
        // External client calls DeleteNodes → subject removed from C# model
    }

    [Fact]
    public async Task ExternalNodeManagementDisabled_ReturnsBadServiceUnsupported()
    {
        // Default behavior rejects AddNodes/DeleteNodes
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ExternalManagement" -v n`
Expected: PASS

---

## Phase 7 Review Checkpoint

**Stop here for review.** Verify:
1. `InterceptorOpcUaServer` handles AddNodes/DeleteNodes
2. External management disabled by default
3. TypeDefinition resolution works
4. Source tracking prevents loops
5. All tests pass

---

## Final Integration Testing

### Task Final.1: Run Full Test Suite

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx -v n`
Expected: All tests PASS

### Task Final.2: Test Sample Applications

**Step 1: Run console sample**

Run: `dotnet run --project src/Namotion.Interceptor.SampleConsole`
Expected: Sample runs without errors

**Step 2: Run Blazor sample**

Run: `dotnet run --project src/Extensions/Namotion.Interceptor.SampleBlazor`
Expected: Sample runs without errors

---

## Documentation Updates

### Task Doc.1: Update opcua-mapping.md

Add documentation for:
- `CollectionNodeStructure.Flat` vs `Container`
- Dictionary node structure
- Collection BrowseName patterns

### Task Doc.2: Update opcua.md

Add "Structural Synchronization" section covering:
- Live sync configuration
- Null vs empty semantics
- Eventual consistency model
- Error handling

---

## Summary

This plan implements OPC UA dynamic address space synchronization in 7 phases:

1. **Phase 1:** Connectors library abstractions (reference counter, structural change processor)
2. **Phase 2:** Server reference counting refactor
3. **Phase 3:** Server Model → OPC UA incremental sync
4. **Phase 4:** Client reference counting refactor
5. **Phase 5:** Client Model → OPC UA incremental sync
6. **Phase 6:** Client OPC UA → Model incremental sync
7. **Phase 7:** Server OPC UA → Model sync (external AddNodes/DeleteNodes)

Each phase has review checkpoints with tests validating the implementation before proceeding.
