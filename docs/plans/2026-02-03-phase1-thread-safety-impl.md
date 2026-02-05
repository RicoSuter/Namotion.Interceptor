# Phase 1: Thread Safety Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate 6 critical race conditions by merging `ConnectorReferenceCounter` and `ConnectorSubjectMapping` into a unified `SubjectConnectorRegistry`.

**Architecture:** Single registry with one lock for atomic registration/unregistration. OPC UA client extends via inheritance to add recently-deleted tracking. Server uses base class directly.

**Tech Stack:** C# 13, .NET 9, xUnit, System.Threading.Lock

---

## Task 1: Create SubjectConnectorRegistry - Storage and Constructor

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Create the file with storage and constructor**

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Unified registry for tracking subjects with external IDs and associated data.
/// Provides atomic registration/unregistration with reference counting.
/// Thread-safe for concurrent access.
/// </summary>
/// <typeparam name="TExternalId">External identifier type (e.g., NodeId).</typeparam>
/// <typeparam name="TData">Associated data type (e.g., List&lt;MonitoredItem&gt;).</typeparam>
public class SubjectConnectorRegistry<TExternalId, TData>
    where TExternalId : notnull
{
    private readonly record struct Entry(TExternalId ExternalId, int RefCount, TData Data);

    private readonly Dictionary<IInterceptorSubject, Entry> _subjects = new();
    private readonly Dictionary<TExternalId, IInterceptorSubject> _idToSubject = new();

    /// <summary>
    /// Lock for synchronization. Protected to allow subclasses to extend atomically.
    /// </summary>
    protected Lock Lock { get; } = new();
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs
git commit -m "feat(connectors): add SubjectConnectorRegistry skeleton"
```

---

## Task 2: Implement Register Methods

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Add Register and RegisterCore methods**

Add after the Lock property:

```csharp
    /// <summary>
    /// Registers a subject with an external ID. If already registered, increments ref count.
    /// </summary>
    /// <param name="subject">The subject to register.</param>
    /// <param name="externalId">The external identifier.</param>
    /// <param name="dataFactory">Factory to create data on first reference.</param>
    /// <param name="data">The associated data (existing or newly created).</param>
    /// <param name="isFirstReference">True if this is the first reference.</param>
    /// <returns>True if registered successfully, false if subject is null.</returns>
    public bool Register(
        IInterceptorSubject subject,
        TExternalId externalId,
        Func<TData> dataFactory,
        out TData data,
        out bool isFirstReference)
    {
        lock (Lock)
        {
            return RegisterCore(subject, externalId, dataFactory, out data, out isFirstReference);
        }
    }

    /// <summary>
    /// Core registration logic. Called inside lock.
    /// </summary>
    protected virtual bool RegisterCore(
        IInterceptorSubject subject,
        TExternalId externalId,
        Func<TData> dataFactory,
        out TData data,
        out bool isFirstReference)
    {
        if (subject is null)
        {
            data = default!;
            isFirstReference = false;
            return false;
        }

        if (_subjects.TryGetValue(subject, out var entry))
        {
            _subjects[subject] = entry with { RefCount = entry.RefCount + 1 };
            data = entry.Data;
            isFirstReference = false;
            return true;
        }

        data = dataFactory();
        _subjects[subject] = new Entry(externalId, 1, data);
        _idToSubject[externalId] = subject;
        isFirstReference = true;
        return true;
    }
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs
git commit -m "feat(connectors): add Register methods to SubjectConnectorRegistry"
```

---

## Task 3: Write Failing Tests for Register

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs`

**Step 1: Create test file with first tests**

```csharp
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for SubjectConnectorRegistry covering registration, unregistration,
/// lookups, modifications, and thread-safety.
/// </summary>
public class SubjectConnectorRegistryTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create().WithRegistry();

    [Fact]
    public void Register_FirstReference_ReturnsIsFirstTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.Register(subject, "node-1", () => [], out var data, out var isFirst);

        // Assert
        Assert.True(result);
        Assert.True(isFirst);
        Assert.NotNull(data);
    }

    [Fact]
    public void Register_SecondReference_ReturnsIsFirstFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => [1], out _, out _);

        // Act
        var result = registry.Register(subject, "node-1", () => [2], out var data, out var isFirst);

        // Assert
        Assert.True(result);
        Assert.False(isFirst);
        Assert.Single(data); // Should return existing data, not new
        Assert.Equal(1, data[0]);
    }

    [Fact]
    public void Register_NullSubject_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();

        // Act
        var result = registry.Register(null!, "node-1", () => [], out var data, out var isFirst);

        // Assert
        Assert.False(result);
        Assert.False(isFirst);
    }

    [Fact]
    public void Register_CallsDataFactoryOnlyOnFirstReference()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        var callCount = 0;

        // Act
        registry.Register(subject, "node-1", () => { callCount++; return 100; }, out var data1, out _);
        registry.Register(subject, "node-1", () => { callCount++; return 200; }, out var data2, out _);

        // Assert
        Assert.Equal(1, callCount);
        Assert.Equal(100, data1);
        Assert.Equal(100, data2);
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests.Register" -v n`
Expected: All 4 tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "test(connectors): add Register tests for SubjectConnectorRegistry"
```

---

## Task 4: Implement Unregister Methods

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Add Unregister and UnregisterCore methods**

Add after RegisterCore:

```csharp
    /// <summary>
    /// Decrements ref count. Removes entry when last reference is released.
    /// </summary>
    /// <param name="subject">The subject to unregister.</param>
    /// <param name="externalId">The external ID if this was the last reference.</param>
    /// <param name="data">The associated data if this was the last reference.</param>
    /// <param name="wasLastReference">True if this was the last reference.</param>
    /// <returns>True if subject was registered, false if not found.</returns>
    public bool Unregister(
        IInterceptorSubject subject,
        out TExternalId? externalId,
        out TData? data,
        out bool wasLastReference)
    {
        lock (Lock)
        {
            return UnregisterCore(subject, out externalId, out data, out wasLastReference);
        }
    }

    /// <summary>
    /// Core unregistration logic. Called inside lock.
    /// </summary>
    protected virtual bool UnregisterCore(
        IInterceptorSubject subject,
        out TExternalId? externalId,
        out TData? data,
        out bool wasLastReference)
    {
        if (subject is null || !_subjects.TryGetValue(subject, out var entry))
        {
            externalId = default;
            data = default;
            wasLastReference = false;
            return false;
        }

        if (entry.RefCount == 1)
        {
            _subjects.Remove(subject);
            _idToSubject.Remove(entry.ExternalId);
            externalId = entry.ExternalId;
            data = entry.Data;
            wasLastReference = true;
            return true;
        }

        _subjects[subject] = entry with { RefCount = entry.RefCount - 1 };
        externalId = default;
        data = default;
        wasLastReference = false;
        return true;
    }
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs
git commit -m "feat(connectors): add Unregister methods to SubjectConnectorRegistry"
```

---

## Task 5: Write Tests for Unregister

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs`

**Step 1: Add Unregister tests**

Add to the test class:

```csharp
    [Fact]
    public void Unregister_LastReference_ReturnsWasLastTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.Unregister(subject, out var externalId, out var data, out var wasLast);

        // Assert
        Assert.True(result);
        Assert.True(wasLast);
        Assert.Equal("node-1", externalId);
        Assert.Equal(42, data);
    }

    [Fact]
    public void Unregister_NotLastReference_ReturnsWasLastFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);
        registry.Register(subject, "node-1", () => 99, out _, out _);

        // Act
        var result = registry.Unregister(subject, out var externalId, out var data, out var wasLast);

        // Assert
        Assert.True(result);
        Assert.False(wasLast);
        Assert.Null(externalId);
        Assert.Equal(default, data);
    }

    [Fact]
    public void Unregister_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.Unregister(subject, out var externalId, out var data, out var wasLast);

        // Assert
        Assert.False(result);
        Assert.False(wasLast);
    }

    [Fact]
    public void Unregister_RemovesMappingOnlyWhenLast()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act - first unregister
        registry.Unregister(subject, out _, out _, out _);

        // Assert - still registered
        Assert.True(registry.TryGetExternalId(subject, out _));

        // Act - second unregister
        registry.Unregister(subject, out _, out _, out _);

        // Assert - now removed
        Assert.False(registry.TryGetExternalId(subject, out _));
    }
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests.Unregister" -v n`
Expected: All 4 tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "test(connectors): add Unregister tests for SubjectConnectorRegistry"
```

---

## Task 6: Implement UnregisterByExternalId

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Add UnregisterByExternalId methods**

Add after UnregisterCore:

```csharp
    /// <summary>
    /// Unregisters by external ID. Used for external deletions.
    /// </summary>
    public bool UnregisterByExternalId(
        TExternalId externalId,
        out IInterceptorSubject? subject,
        out TData? data,
        out bool wasLastReference)
    {
        lock (Lock)
        {
            return UnregisterByExternalIdCore(externalId, out subject, out data, out wasLastReference);
        }
    }

    /// <summary>
    /// Core logic for unregistering by external ID. Called inside lock.
    /// </summary>
    protected virtual bool UnregisterByExternalIdCore(
        TExternalId externalId,
        out IInterceptorSubject? subject,
        out TData? data,
        out bool wasLastReference)
    {
        if (!_idToSubject.TryGetValue(externalId, out subject))
        {
            data = default;
            wasLastReference = false;
            return false;
        }

        var entry = _subjects[subject];
        if (entry.RefCount == 1)
        {
            _subjects.Remove(subject);
            _idToSubject.Remove(externalId);
            data = entry.Data;
            wasLastReference = true;
        }
        else
        {
            _subjects[subject] = entry with { RefCount = entry.RefCount - 1 };
            data = default;
            wasLastReference = false;
        }
        return true;
    }
```

**Step 2: Add tests**

Add to test file:

```csharp
    [Fact]
    public void UnregisterByExternalId_Registered_ReturnsSubject()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.UnregisterByExternalId("node-1", out var foundSubject, out var data, out var wasLast);

        // Assert
        Assert.True(result);
        Assert.Same(subject, foundSubject);
        Assert.Equal(42, data);
        Assert.True(wasLast);
    }

    [Fact]
    public void UnregisterByExternalId_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();

        // Act
        var result = registry.UnregisterByExternalId("node-1", out var subject, out _, out _);

        // Assert
        Assert.False(result);
        Assert.Null(subject);
    }
```

**Step 3: Run tests and commit**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests.UnregisterByExternalId" -v n`
Expected: 2 tests pass

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "feat(connectors): add UnregisterByExternalId to SubjectConnectorRegistry"
```

---

## Task 7: Implement Lookup Methods

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Add all lookup methods**

Add after UnregisterByExternalIdCore:

```csharp
    /// <summary>
    /// Gets the external ID for a registered subject.
    /// </summary>
    public bool TryGetExternalId(IInterceptorSubject subject, out TExternalId? externalId)
    {
        lock (Lock)
        {
            if (subject is not null && _subjects.TryGetValue(subject, out var entry))
            {
                externalId = entry.ExternalId;
                return true;
            }
            externalId = default;
            return false;
        }
    }

    /// <summary>
    /// Gets the subject for a given external ID.
    /// </summary>
    public bool TryGetSubject(TExternalId externalId, out IInterceptorSubject? subject)
    {
        lock (Lock)
        {
            return _idToSubject.TryGetValue(externalId, out subject);
        }
    }

    /// <summary>
    /// Gets the data for a registered subject.
    /// </summary>
    public bool TryGetData(IInterceptorSubject subject, out TData? data)
    {
        lock (Lock)
        {
            if (subject is not null && _subjects.TryGetValue(subject, out var entry))
            {
                data = entry.Data;
                return true;
            }
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Checks if a subject is registered.
    /// </summary>
    public bool IsRegistered(IInterceptorSubject subject)
    {
        lock (Lock)
        {
            return subject is not null && _subjects.ContainsKey(subject);
        }
    }
```

**Step 2: Add tests**

Add to test file:

```csharp
    [Fact]
    public void TryGetExternalId_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.TryGetExternalId(subject, out var externalId);

        // Assert
        Assert.True(result);
        Assert.Equal("node-1", externalId);
    }

    [Fact]
    public void TryGetExternalId_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.TryGetExternalId(subject, out var externalId);

        // Assert
        Assert.False(result);
        Assert.Null(externalId);
    }

    [Fact]
    public void TryGetSubject_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.TryGetSubject("node-1", out var foundSubject);

        // Assert
        Assert.True(result);
        Assert.Same(subject, foundSubject);
    }

    [Fact]
    public void TryGetData_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.TryGetData(subject, out var data);

        // Assert
        Assert.True(result);
        Assert.Equal(42, data);
    }

    [Fact]
    public void IsRegistered_Registered_ReturnsTrue()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act & Assert
        Assert.True(registry.IsRegistered(subject));
    }

    [Fact]
    public void IsRegistered_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Act & Assert
        Assert.False(registry.IsRegistered(subject));
    }
```

**Step 3: Run tests and commit**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests.TryGet|FullyQualifiedName~SubjectConnectorRegistryTests.IsRegistered" -v n`
Expected: 6 tests pass

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "feat(connectors): add lookup methods to SubjectConnectorRegistry"
```

---

## Task 8: Implement UpdateExternalId and ModifyData

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Add UpdateExternalId methods**

```csharp
    /// <summary>
    /// Updates the external ID for a subject. Used for collection reindexing.
    /// </summary>
    /// <returns>True if updated, false if subject not found or newId already exists for different subject.</returns>
    public bool UpdateExternalId(IInterceptorSubject subject, TExternalId newExternalId)
    {
        lock (Lock)
        {
            return UpdateExternalIdCore(subject, newExternalId);
        }
    }

    /// <summary>
    /// Core logic for updating external ID. Called inside lock.
    /// </summary>
    protected virtual bool UpdateExternalIdCore(IInterceptorSubject subject, TExternalId newExternalId)
    {
        if (subject is null || !_subjects.TryGetValue(subject, out var entry))
        {
            return false;
        }

        // Check for collision (different subject already has this ID)
        if (_idToSubject.TryGetValue(newExternalId, out var existingSubject) &&
            !ReferenceEquals(existingSubject, subject))
        {
            return false;
        }

        // Remove old ID mapping
        _idToSubject.Remove(entry.ExternalId);

        // Update to new ID
        _subjects[subject] = entry with { ExternalId = newExternalId };
        _idToSubject[newExternalId] = subject;
        return true;
    }

    /// <summary>
    /// Modifies the data associated with a subject while holding the lock.
    /// </summary>
    public bool ModifyData(IInterceptorSubject subject, Action<TData> modifier)
    {
        lock (Lock)
        {
            if (subject is null || !_subjects.TryGetValue(subject, out var entry))
            {
                return false;
            }
            modifier(entry.Data);
            return true;
        }
    }
```

**Step 2: Add tests**

```csharp
    [Fact]
    public void UpdateExternalId_Registered_UpdatesBothDirections()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var result = registry.UpdateExternalId(subject, "node-2");

        // Assert
        Assert.True(result);
        Assert.True(registry.TryGetExternalId(subject, out var newId));
        Assert.Equal("node-2", newId);
        Assert.True(registry.TryGetSubject("node-2", out var foundSubject));
        Assert.Same(subject, foundSubject);
        Assert.False(registry.TryGetSubject("node-1", out _)); // Old ID removed
    }

    [Fact]
    public void UpdateExternalId_Collision_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject1 = new Person(CreateContext());
        var subject2 = new Person(CreateContext());
        registry.Register(subject1, "node-1", () => 1, out _, out _);
        registry.Register(subject2, "node-2", () => 2, out _, out _);

        // Act - try to give subject1 the same ID as subject2
        var result = registry.UpdateExternalId(subject1, "node-2");

        // Assert
        Assert.False(result);
        // Original mappings unchanged
        Assert.True(registry.TryGetExternalId(subject1, out var id1));
        Assert.Equal("node-1", id1);
    }

    [Fact]
    public void ModifyData_Registered_ExecutesModifier()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => [1, 2], out _, out _);

        // Act
        var result = registry.ModifyData(subject, list => list.Add(3));

        // Assert
        Assert.True(result);
        Assert.True(registry.TryGetData(subject, out var data));
        Assert.Equal([1, 2, 3], data);
    }

    [Fact]
    public void ModifyData_NotRegistered_ReturnsFalse()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());

        // Act
        var result = registry.ModifyData(subject, list => list.Add(1));

        // Assert
        Assert.False(result);
    }
```

**Step 3: Run tests and commit**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests.UpdateExternalId|FullyQualifiedName~SubjectConnectorRegistryTests.ModifyData" -v n`
Expected: 4 tests pass

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "feat(connectors): add UpdateExternalId and ModifyData to SubjectConnectorRegistry"
```

---

## Task 9: Implement Enumeration and Clear

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

**Step 1: Add enumeration and Clear methods**

```csharp
    /// <summary>
    /// Returns a snapshot of all registered subjects.
    /// </summary>
    public List<IInterceptorSubject> GetAllSubjects()
    {
        lock (Lock)
        {
            return [.. _subjects.Keys];
        }
    }

    /// <summary>
    /// Returns a snapshot of all entries.
    /// </summary>
    public List<(IInterceptorSubject Subject, TExternalId ExternalId, TData Data)> GetAllEntries()
    {
        lock (Lock)
        {
            return _subjects.Select(kvp =>
                (kvp.Key, kvp.Value.ExternalId, kvp.Value.Data)).ToList();
        }
    }

    /// <summary>
    /// Removes all entries.
    /// </summary>
    public void Clear()
    {
        lock (Lock)
        {
            ClearCore();
        }
    }

    /// <summary>
    /// Core logic for clearing. Called inside lock.
    /// </summary>
    protected virtual void ClearCore()
    {
        _subjects.Clear();
        _idToSubject.Clear();
    }
```

**Step 2: Add tests**

```csharp
    [Fact]
    public void GetAllSubjects_ReturnsAllRegistered()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject1 = new Person(CreateContext());
        var subject2 = new Person(CreateContext());
        registry.Register(subject1, "node-1", () => 1, out _, out _);
        registry.Register(subject2, "node-2", () => 2, out _, out _);

        // Act
        var subjects = registry.GetAllSubjects();

        // Assert
        Assert.Equal(2, subjects.Count);
        Assert.Contains(subject1, subjects);
        Assert.Contains(subject2, subjects);
    }

    [Fact]
    public void GetAllEntries_ReturnsAllData()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        var entries = registry.GetAllEntries();

        // Assert
        Assert.Single(entries);
        Assert.Same(subject, entries[0].Subject);
        Assert.Equal("node-1", entries[0].ExternalId);
        Assert.Equal(42, entries[0].Data);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => 42, out _, out _);

        // Act
        registry.Clear();

        // Assert
        Assert.False(registry.IsRegistered(subject));
        Assert.False(registry.TryGetSubject("node-1", out _));
        Assert.Empty(registry.GetAllSubjects());
    }
```

**Step 3: Run tests and commit**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests.GetAll|FullyQualifiedName~SubjectConnectorRegistryTests.Clear" -v n`
Expected: 3 tests pass

```bash
git add src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "feat(connectors): add enumeration and Clear to SubjectConnectorRegistry"
```

---

## Task 10: Add Thread-Safety Tests

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs`

**Step 1: Add concurrent access tests**

```csharp
    [Fact]
    public async Task Register_ConcurrentAccess_ExactlyOneFirst()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());
        var firstCount = 0;
        var tasks = new List<Task>();

        // Act - 100 concurrent registrations
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                registry.Register(subject, "node-1", () => 42, out _, out var isFirst);
                if (isFirst) Interlocked.Increment(ref firstCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be first
        Assert.Equal(1, firstCount);
    }

    [Fact]
    public async Task Unregister_ConcurrentAccess_ExactlyOneLast()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, int>();
        var subject = new Person(CreateContext());

        // Add 100 references
        for (int i = 0; i < 100; i++)
        {
            registry.Register(subject, "node-1", () => 42, out _, out _);
        }

        var lastCount = 0;
        var tasks = new List<Task>();

        // Act - 100 concurrent unregistrations
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                registry.Unregister(subject, out _, out _, out var wasLast);
                if (wasLast) Interlocked.Increment(ref lastCount);
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - exactly one should be last
        Assert.Equal(1, lastCount);
        Assert.False(registry.IsRegistered(subject));
    }

    [Fact]
    public async Task ModifyData_ConcurrentAccess_AllModificationsApplied()
    {
        // Arrange
        var registry = new SubjectConnectorRegistry<string, List<int>>();
        var subject = new Person(CreateContext());
        registry.Register(subject, "node-1", () => [], out _, out _);
        var tasks = new List<Task>();

        // Act - 100 concurrent modifications
        for (int i = 0; i < 100; i++)
        {
            var value = i;
            tasks.Add(Task.Run(() =>
            {
                registry.ModifyData(subject, list => list.Add(value));
            }));
        }
        await Task.WhenAll(tasks);

        // Assert - all modifications applied
        Assert.True(registry.TryGetData(subject, out var data));
        Assert.Equal(100, data!.Count);
    }
```

**Step 2: Run tests and commit**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectConnectorRegistryTests" -v n`
Expected: All tests pass

```bash
git add src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs
git commit -m "test(connectors): add thread-safety tests for SubjectConnectorRegistry"
```

---

## Task 11: Create OpcUaClientSubjectRegistry

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientSubjectRegistry.cs`

**Step 1: Create the subclass**

```csharp
using Namotion.Interceptor.Connectors;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// OPC UA client-specific subject registry that adds recently-deleted tracking.
/// Recently deleted subjects are tracked to prevent periodic resync from re-adding them.
/// </summary>
internal class OpcUaClientSubjectRegistry
    : SubjectConnectorRegistry<NodeId, List<MonitoredItem>>
{
    private readonly Dictionary<NodeId, DateTime> _recentlyDeleted = new();
    private readonly TimeSpan _recentlyDeletedExpiry = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Core unregistration that also marks the subject as recently deleted.
    /// </summary>
    protected override bool UnregisterCore(
        IInterceptorSubject subject,
        out NodeId? externalId,
        out List<MonitoredItem>? data,
        out bool wasLastReference)
    {
        var result = base.UnregisterCore(subject, out externalId, out data, out wasLastReference);

        if (result && wasLastReference && externalId is not null)
        {
            _recentlyDeleted[externalId] = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Checks if a node was recently deleted (within expiry window).
    /// Used to prevent periodic resync from re-adding deleted items.
    /// </summary>
    public bool WasRecentlyDeleted(NodeId nodeId)
    {
        lock (Lock)
        {
            if (!_recentlyDeleted.TryGetValue(nodeId, out var deletedAt))
            {
                return false;
            }

            if (DateTime.UtcNow - deletedAt > _recentlyDeletedExpiry)
            {
                _recentlyDeleted.Remove(nodeId);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Clears all entries including recently deleted tracking.
    /// </summary>
    protected override void ClearCore()
    {
        base.ClearCore();
        _recentlyDeleted.Clear();
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientSubjectRegistry.cs
git commit -m "feat(opcua): add OpcUaClientSubjectRegistry with recently-deleted tracking"
```

---

## Task 12: Run Full Test Suite - Checkpoint

**Step 1: Run all tests to ensure nothing is broken**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 2: Commit checkpoint if all tests pass**

```bash
git add -A
git commit -m "checkpoint: SubjectConnectorRegistry complete, ready for integration"
```

---

## Remaining Tasks (Integration)

The following tasks integrate the new registry into the OPC UA code. These are larger and will be detailed in a follow-up plan:

- **Task 13-16:** Update `OpcUaSubjectClientSource` to use `OpcUaClientSubjectRegistry`
- **Task 17-20:** Update `OpcUaClientGraphChangeReceiver` to use registry
- **Task 21-24:** Update server side (`CustomNodeManager`, `OpcUaSubjectServer`, etc.)
- **Task 25:** Delete old `ConnectorReferenceCounter` and `ConnectorSubjectMapping`
- **Task 26:** Final validation - run full test suite

---

## Summary

| Task | Description | Tests |
|------|-------------|-------|
| 1 | Create registry skeleton | - |
| 2 | Implement Register | - |
| 3 | Write Register tests | 4 |
| 4 | Implement Unregister | - |
| 5 | Write Unregister tests | 4 |
| 6 | Implement UnregisterByExternalId | 2 |
| 7 | Implement lookups | 6 |
| 8 | Implement UpdateExternalId, ModifyData | 4 |
| 9 | Implement enumeration, Clear | 3 |
| 10 | Thread-safety tests | 3 |
| 11 | Create OpcUaClientSubjectRegistry | - |
| 12 | Checkpoint - run all tests | - |
| **Total** | | **26** |
