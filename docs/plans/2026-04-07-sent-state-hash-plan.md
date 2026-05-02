# Sent-State Structural Hash Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace live-graph hash (`StateHashComputer`) with sent-state hash (`SentStructuralState`) for guaranteed false-positive-free structural divergence detection on every update, regardless of concurrent mutation rate.

**Architecture:** Both server and client maintain a `SentStructuralState` — a lightweight dictionary tracking structural state implied by sent/received `SubjectUpdate` content. SHA256 hash computed from this dictionary. No dependency on live graph, registry, or interceptors. Pure WebSocket-internal concern.

> **Implementation note (2026-04-07):** The plan below specifies a single global `_sentState` on the server. The final implementation differs in several key ways:
>
> - **Per-connection state:** Each `WebSocketClientConnection` owns its own `SentStructuralState`, initialized from the Welcome it was sent and updated from each broadcast. A single global instance breaks when multiple clients connect — re-initializing from a new client's Welcome destroys the sent-state tracking for other connected clients. Even "initialize once" fails because the Welcome includes unflushed CQP mutations that the cumulative sent state doesn't have, creating false-positive hash mismatches and cascading reconnections. The hash is computed and serialized per-connection (each client gets its own hash in the broadcast).
> - **Thread safety:** All `SentStructuralState` access on the server — both `UpdateSentState` (in `CreateUpdateWithSequence`, broadcast path) and `ComputeSentStateHash` (in `BroadcastHeartbeatAsync`, heartbeat path) — happens under `_applyUpdateLock`. No unsynchronized access. The hash is pre-computed under the lock and passed as a dictionary of per-connection hashes to `BroadcastUpdateAsync`, which runs outside the lock.
> - **Eager PathProvider caching:** The CQP filter eagerly caches PathProvider decisions for ALL sibling properties on the first registered encounter. This eliminates the "never seen while registered" cache miss window. The "no cache → drop" default is safe because all known properties are eagerly cached before any unregistered access can occur.
> - **Per-instance cache prefix:** The filter cache uses a per-instance prefix (`_filterCachePrefix` = GUID) to isolate cache entries between server instances sharing the same subject graph, preventing stale cache hits or cross-instance interference.
>
> See the updated [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md), Layer 2 for the final architecture.

**Tech Stack:** C# 13, .NET 9, System.Security.Cryptography (SHA256), System.Text.Json

**Design doc:** `docs/plans/2026-04-06-registry-independent-pipeline-design.md` (Layer 2: Sent-State Structural Hash)

---

### Task 1: Create `SentStructuralState` class with tests

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Internal/SentStructuralState.cs`
- Create: `src/Namotion.Interceptor.WebSocket.Tests/SentStructuralStateTests.cs`

**Step 1: Write tests**

Create test file covering these scenarios:
- Empty state returns null hash
- Initialize from Welcome snapshot produces non-null hash
- Two identical snapshots produce the same hash
- Adding a subject changes the hash
- Removing a subject (orphan via structural property change) changes the hash
- Moving a subject between parents preserves it (ref counting)
- Value-only update doesn't change hash
- Root subject is never removed
- Dictionary items sorted by key for deterministic hash
- Partial update with structural changes updates hash correctly

```csharp
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Internal;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests;

public class SentStructuralStateTests
{
    [Fact]
    public void WhenEmpty_ThenReturnsNullHash()
    {
        // Arrange
        var state = new SentStructuralState();

        // Act
        var hash = state.ComputeHash();

        // Assert
        Assert.Null(hash);
    }

    [Fact]
    public void WhenInitializedFromSnapshot_ThenReturnsNonNullHash()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithRoot("root");

        // Act
        state.InitializeFromSnapshot(snapshot);
        var hash = state.ComputeHash();

        // Assert
        Assert.NotNull(hash);
    }

    [Fact]
    public void WhenInitializedWithSameSnapshot_ThenHashesMatch()
    {
        // Arrange
        var state1 = new SentStructuralState();
        var state2 = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });

        // Act
        state1.InitializeFromSnapshot(snapshot);
        state2.InitializeFromSnapshot(snapshot);

        // Assert
        Assert.Equal(state1.ComputeHash(), state2.ComputeHash());
    }

    [Fact]
    public void WhenSubjectAddedViaDictionary_ThenHashChanges()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot);
        var hashBefore = state.ComputeHash();

        // Act — partial update adds child2
        var update = CreatePartialUpdateWithDictionary("root", "Items",
            new[] { ("key1", "child1"), ("key2", "child2") },
            completeSubjectIds: new[] { "child2" });
        state.UpdateFromBroadcast(update);
        var hashAfter = state.ComputeHash();

        // Assert
        Assert.NotEqual(hashBefore, hashAfter);
    }

    [Fact]
    public void WhenSubjectRemovedViaDictionary_ThenHashChangesAndOrphanRemoved()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items",
            new[] { ("key1", "child1"), ("key2", "child2") });
        state.InitializeFromSnapshot(snapshot);
        var hashBefore = state.ComputeHash();

        // Act — partial update removes child2
        var update = CreatePartialUpdateWithDictionary("root", "Items",
            new[] { ("key1", "child1") });
        state.UpdateFromBroadcast(update);
        var hashAfter = state.ComputeHash();

        // Assert
        Assert.NotEqual(hashBefore, hashAfter);
        Assert.Equal(2, state.TrackedSubjectCount); // root + child1 only
    }

    [Fact]
    public void WhenSubjectMovedBetweenParents_ThenSubjectPreserved()
    {
        // Arrange — root has parentA and parentB, parentA has child
        var state = new SentStructuralState();
        var snapshot = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["ParentA"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "parentA"
                    },
                    ["ParentB"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "parentB"
                    }
                },
                ["parentA"] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "theChild"
                    }
                },
                ["parentB"] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = null
                    }
                },
                ["theChild"] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "test"
                    }
                }
            }
        };
        state.InitializeFromSnapshot(snapshot);

        // Act — move child from parentA to parentB
        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["parentA"] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = null
                    }
                },
                ["parentB"] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "theChild"
                    }
                }
            }
        };
        state.UpdateFromBroadcast(update);

        // Assert — child still tracked (moved, not removed)
        Assert.Equal(4, state.TrackedSubjectCount); // root, parentA, parentB, theChild
    }

    [Fact]
    public void WhenValueOnlyUpdate_ThenHashUnchanged()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot);
        var hashBefore = state.ComputeHash();

        // Act — value-only update for child1
        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["child1"] = new()
                {
                    ["Temperature"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = 42.0
                    }
                }
            }
        };
        state.UpdateFromBroadcast(update);
        var hashAfter = state.ComputeHash();

        // Assert
        Assert.Equal(hashBefore, hashAfter);
    }

    [Fact]
    public void WhenRootSubject_ThenNeverRemoved()
    {
        // Arrange — root with one child
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot);

        // Act — remove all children from root
        var update = CreatePartialUpdateWithDictionary("root", "Items", Array.Empty<(string, string)>());
        state.UpdateFromBroadcast(update);

        // Assert — root still tracked
        Assert.Equal(1, state.TrackedSubjectCount); // root only
        Assert.NotNull(state.ComputeHash());
    }

    [Fact]
    public void WhenCollectionOrderChanges_ThenHashChanges()
    {
        // Arrange
        var state1 = new SentStructuralState();
        var state2 = new SentStructuralState();

        var snapshot1 = CreateSnapshotWithCollection("root", "Items", new[] { "child1", "child2" });
        var snapshot2 = CreateSnapshotWithCollection("root", "Items", new[] { "child2", "child1" });

        state1.InitializeFromSnapshot(snapshot1);
        state2.InitializeFromSnapshot(snapshot2);

        // Assert — different order = different hash (order matters for collections)
        Assert.NotEqual(state1.ComputeHash(), state2.ComputeHash());
    }

    [Fact]
    public void WhenDictionaryKeysUnordered_ThenHashDeterministic()
    {
        // Arrange — same entries, different insertion order
        var state1 = new SentStructuralState();
        var state2 = new SentStructuralState();

        var snapshot1 = CreateSnapshotWithDictionary("root", "Items",
            new[] { ("b", "child2"), ("a", "child1") });
        var snapshot2 = CreateSnapshotWithDictionary("root", "Items",
            new[] { ("a", "child1"), ("b", "child2") });

        state1.InitializeFromSnapshot(snapshot1);
        state2.InitializeFromSnapshot(snapshot2);

        // Assert — same hash regardless of insertion order (sorted by key)
        Assert.Equal(state1.ComputeHash(), state2.ComputeHash());
    }

    // --- Helper methods ---

    private static SubjectUpdate CreateSnapshotWithRoot(string rootId)
    {
        return new SubjectUpdate
        {
            Root = rootId,
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                [rootId] = new()
            }
        };
    }

    private static SubjectUpdate CreateSnapshotWithDictionary(
        string rootId, string propertyName, (string Key, string ChildId)[] items)
    {
        var subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
        {
            [rootId] = new()
            {
                [propertyName] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Dictionary,
                    Items = items.Select(i => new SubjectPropertyItemUpdate
                    {
                        Id = i.ChildId,
                        Key = i.Key
                    }).ToList()
                }
            }
        };

        // Add child subjects with empty properties
        foreach (var item in items)
        {
            subjects[item.ChildId] = new();
        }

        return new SubjectUpdate { Root = rootId, Subjects = subjects };
    }

    private static SubjectUpdate CreateSnapshotWithCollection(
        string rootId, string propertyName, string[] childIds)
    {
        var subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
        {
            [rootId] = new()
            {
                [propertyName] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Collection,
                    Items = childIds.Select(id => new SubjectPropertyItemUpdate
                    {
                        Id = id
                    }).ToList()
                }
            }
        };

        foreach (var childId in childIds)
        {
            subjects[childId] = new();
        }

        return new SubjectUpdate { Root = rootId, Subjects = subjects };
    }

    private static SubjectUpdate CreatePartialUpdateWithDictionary(
        string rootId, string propertyName, (string Key, string ChildId)[] items,
        string[]? completeSubjectIds = null)
    {
        var subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
        {
            [rootId] = new()
            {
                [propertyName] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Dictionary,
                    Items = items.Select(i => new SubjectPropertyItemUpdate
                    {
                        Id = i.ChildId,
                        Key = i.Key
                    }).ToList()
                }
            }
        };

        // Add complete subjects (new subjects)
        if (completeSubjectIds is not null)
        {
            foreach (var id in completeSubjectIds)
            {
                subjects[id] = new();
            }
        }

        return new SubjectUpdate
        {
            Root = rootId,
            Subjects = subjects,
            CompleteSubjectIds = completeSubjectIds?.ToHashSet()
        };
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~SentStructuralState" -v minimal`
Expected: Compilation error (SentStructuralState doesn't exist yet)

**Step 3: Implement `SentStructuralState`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.WebSocket.Internal;

/// <summary>
/// Tracks the structural state implied by sent/received SubjectUpdate messages.
/// Computes a deterministic SHA256 hash from this tracked state rather than the
/// live graph, eliminating false-positive hash mismatches caused by concurrent
/// mutations from the mutation engine.
/// </summary>
internal sealed class SentStructuralState
{
    // subjectId -> deterministic string of structural property content
    private readonly SortedDictionary<string, string> _subjectStructure
        = new(StringComparer.Ordinal);

    // subjectId -> set of child subject IDs referenced by structural properties
    private readonly Dictionary<string, HashSet<string>> _children = new();

    // subjectId -> number of parents referencing this subject
    private readonly Dictionary<string, int> _referenceCount = new();

    private string? _rootSubjectId;

    /// <summary>
    /// Number of subjects currently tracked (for diagnostics/testing).
    /// </summary>
    public int TrackedSubjectCount => _subjectStructure.Count;

    /// <summary>
    /// Computes SHA256 hash of the tracked structural state.
    /// Returns null if no subjects are tracked.
    /// </summary>
    public string? ComputeHash()
    {
        if (_subjectStructure.Count == 0)
            return null;

        var builder = new StringBuilder(_subjectStructure.Count * 64);
        foreach (var (subjectId, structuralContent) in _subjectStructure)
        {
            builder.Append(subjectId);
            if (structuralContent.Length > 0)
            {
                builder.Append(structuralContent);
            }
            builder.Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Initializes the tracked state from a complete snapshot (Welcome).
    /// Clears any previously tracked state.
    /// </summary>
    public void InitializeFromSnapshot(SubjectUpdate completeUpdate)
    {
        _subjectStructure.Clear();
        _children.Clear();
        _referenceCount.Clear();
        _rootSubjectId = completeUpdate.Root;

        foreach (var (subjectId, properties) in completeUpdate.Subjects)
        {
            var childrenSet = new HashSet<string>();
            var structuralContent = BuildStructuralContent(properties, childrenSet);

            _subjectStructure[subjectId] = structuralContent;
            _children[subjectId] = childrenSet;

            foreach (var child in childrenSet)
            {
                _referenceCount[child] = _referenceCount.GetValueOrDefault(child) + 1;
            }
        }
    }

    /// <summary>
    /// Updates the tracked state from a partial or complete broadcast update.
    /// Only processes structural properties — value-only changes are ignored.
    /// </summary>
    public void UpdateFromBroadcast(SubjectUpdate update)
    {
        foreach (var (subjectId, properties) in update.Subjects)
        {
            var hasStructuralProperties = properties.Values.Any(p =>
                p.Kind is SubjectPropertyUpdateKind.Object
                    or SubjectPropertyUpdateKind.Collection
                    or SubjectPropertyUpdateKind.Dictionary);

            if (!hasStructuralProperties)
            {
                // Value-only update — ensure subject is tracked but don't change structure
                _subjectStructure.TryAdd(subjectId, string.Empty);
                continue;
            }

            // Get previous children for this subject
            var previousChildren = _children.TryGetValue(subjectId, out var existing)
                ? existing
                : new HashSet<string>();

            // Build new structural content and extract new children
            var newChildren = new HashSet<string>();
            var structuralContent = BuildStructuralContent(properties, newChildren);

            // Decrement ref counts for removed children
            foreach (var removed in previousChildren.Except(newChildren))
            {
                DecrementReferenceCount(removed);
            }

            // Increment ref counts for added children
            foreach (var added in newChildren.Except(previousChildren))
            {
                IncrementReferenceCount(added);
            }

            // Update tracked state
            _subjectStructure[subjectId] = structuralContent;
            _children[subjectId] = newChildren;
        }
    }

    private void IncrementReferenceCount(string subjectId)
    {
        _referenceCount[subjectId] = _referenceCount.GetValueOrDefault(subjectId) + 1;

        // Ensure subject is tracked even if not yet in update.Subjects
        _subjectStructure.TryAdd(subjectId, string.Empty);
        if (!_children.ContainsKey(subjectId))
        {
            _children[subjectId] = new HashSet<string>();
        }
    }

    private void DecrementReferenceCount(string subjectId)
    {
        // Never remove the root subject
        if (subjectId == _rootSubjectId)
            return;

        if (!_referenceCount.TryGetValue(subjectId, out var count))
            return;

        count--;
        if (count <= 0)
        {
            _referenceCount.Remove(subjectId);
            RemoveSubject(subjectId);
        }
        else
        {
            _referenceCount[subjectId] = count;
        }
    }

    private void RemoveSubject(string subjectId)
    {
        _subjectStructure.Remove(subjectId);

        if (_children.TryGetValue(subjectId, out var children))
        {
            _children.Remove(subjectId);
            foreach (var child in children)
            {
                DecrementReferenceCount(child);
            }
        }
    }

    private static string BuildStructuralContent(
        Dictionary<string, SubjectPropertyUpdate> properties,
        HashSet<string> childrenOutput)
    {
        var structuralProperties = properties
            .Where(p => p.Value.Kind is SubjectPropertyUpdateKind.Object
                or SubjectPropertyUpdateKind.Collection
                or SubjectPropertyUpdateKind.Dictionary)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        if (structuralProperties.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var (propertyName, property) in structuralProperties)
        {
            builder.Append('|').Append(propertyName).Append(':');

            switch (property.Kind)
            {
                case SubjectPropertyUpdateKind.Object:
                    if (property.Id is not null)
                    {
                        builder.Append(property.Id);
                        childrenOutput.Add(property.Id);
                    }
                    else
                    {
                        builder.Append('-');
                    }
                    break;

                case SubjectPropertyUpdateKind.Collection:
                    builder.Append('[');
                    if (property.Items is not null)
                    {
                        foreach (var item in property.Items)
                        {
                            builder.Append(item.Id).Append(',');
                            childrenOutput.Add(item.Id);
                        }
                    }
                    builder.Append(']');
                    break;

                case SubjectPropertyUpdateKind.Dictionary:
                    builder.Append('{');
                    if (property.Items is not null)
                    {
                        foreach (var item in property.Items
                            .OrderBy(i => i.Key, StringComparer.Ordinal))
                        {
                            builder.Append(item.Key).Append('=')
                                .Append(item.Id).Append(',');
                            childrenOutput.Add(item.Id);
                        }
                    }
                    builder.Append('}');
                    break;
            }
        }

        return builder.ToString();
    }
}
```

**Step 4: Run tests**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~SentStructuralState" -v minimal`
Expected: All tests pass

**Step 5: Run full unit test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

---

### Task 2: Server — replace `StateHashComputer` with `SentStructuralState`

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`

**Step 1: Add `_sentState` field and initialize from Welcome**

Add field after existing fields:

```csharp
private readonly SentStructuralState _sentState = new();
```

In `HandleClientAsync`, after creating the Welcome snapshot under `_applyUpdateLock`, initialize the sent state:

```csharp
lock (_applyUpdateLock)
{
    welcomeSequence = Volatile.Read(ref _sequence);
    initialState = SubjectUpdate.CreateCompleteUpdate(_subject, _processors);
    _sentState.InitializeFromSnapshot(initialState);
}
```

Note: `InitializeFromSnapshot` is called on every new client Welcome. This re-initializes the global sent state to match the current live snapshot. This is correct because the Welcome captures the complete state, and subsequent broadcasts will update from there. For multi-client scenarios, all clients receive the same broadcasts, so they stay in sync.

**Step 2: Update `CreateUpdateWithSequence` to use sent state**

Replace the current implementation:

```csharp
private (SubjectUpdate Update, long Sequence, string? StructuralHash) CreateUpdateWithSequence(ReadOnlySpan<SubjectPropertyChange> changes)
{
    lock (_applyUpdateLock)
    {
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes, _processors);
        var sequence = Interlocked.Increment(ref _sequence);

        _sentState.UpdateFromBroadcast(update);
        var hash = _sentState.ComputeHash();

        return (update, sequence, hash);
    }
}
```

This replaces the `StateHashComputer.ComputeStructuralHash()` call with `_sentState.UpdateFromBroadcast()` + `_sentState.ComputeHash()`. The hash is guaranteed consistent with the update content.

**Step 3: Update `BroadcastHeartbeatAsync` to use sent state**

Replace the hash computation section:

```csharp
private async Task BroadcastHeartbeatAsync(CancellationToken cancellationToken)
{
    if (_connections.IsEmpty) return;

    // Only send heartbeat if no update was broadcast recently (idle detection)
    var timeSinceLastBroadcast = Environment.TickCount64 - Volatile.Read(ref _lastBroadcastTicks);
    if (timeSinceLastBroadcast < _configuration.HeartbeatInterval.TotalMilliseconds)
        return;

    string? stateHash;
    long sequence;
    lock (_applyUpdateLock)
    {
        sequence = Volatile.Read(ref _sequence);
        stateHash = _sentState.ComputeHash();
    }

    var heartbeat = new HeartbeatPayload
    {
        Sequence = sequence,
        StateHash = stateHash
    };

    var serializedMessage = _serializer.SerializeMessage(MessageType.Heartbeat, heartbeat);

    await BroadcastToAllAsync(
        connection => connection.SendHeartbeatAsync(serializedMessage, cancellationToken),
        cancellationToken).ConfigureAwait(false);
}
```

**Step 4: Remove `StateHashComputer` import**

Remove `using Namotion.Interceptor.WebSocket.Internal;` if `StateHashComputer` was the only usage from that namespace. Keep it if `SentStructuralState` is in the same namespace (it is — `Namotion.Interceptor.WebSocket.Internal`).

**Step 5: Verify build and tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

---

### Task 3: Client — replace `StateHashComputer` with `SentStructuralState`

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

**Step 1: Add `_clientState` field**

Add field after existing fields:

```csharp
private readonly SentStructuralState _clientState = new();
```

**Step 2: Initialize from Welcome**

In `ConnectCoreAsync`, after receiving and deserializing the Welcome, initialize the client state:

```csharp
_initialState = welcome.State;
_sequenceTracker.InitializeFromWelcome(welcome.Sequence);
_clientState.InitializeFromSnapshot(welcome.State);
```

**Step 3: Update `HandleUpdate` to use sent state**

Replace the hash comparison logic:

```csharp
/// <summary>
/// Returns true if the update was applied successfully and hashes match (or no hash present).
/// Returns false if a structural hash mismatch was detected (caller should trigger reconnection).
/// </summary>
private bool HandleUpdate(UpdatePayload update)
{
    var propertyWriter = _propertyWriter;
    if (propertyWriter is null) return true;

    propertyWriter.Write(
        (update, subject: _subject, source: this, factory: _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance),
        static state =>
        {
            using (SubjectChangeContext.WithSource(state.source))
            {
                state.subject.ApplySubjectUpdate(state.update, state.factory);
            }
        });

    // Update client-side sent state from received update content
    _clientState.UpdateFromBroadcast(update);

    return !HasStructuralHashMismatch(update.StructuralHash);
}
```

**Step 4: Simplify `HasStructuralHashMismatch`**

Replace the method to use sent state instead of live graph hash:

```csharp
/// <summary>
/// Compares the server's structural hash against the client's sent-state hash.
/// Returns true if a mismatch was detected (caller should trigger reconnection).
/// Returns false if hashes match or server hash is null.
/// </summary>
private bool HasStructuralHashMismatch(string? serverHash)
{
    if (serverHash is null)
        return false;

    var clientHash = _clientState.ComputeHash();
    if (clientHash is not null && clientHash != serverHash)
    {
        _logger.LogWarning(
            "Structural hash mismatch: server={ServerHash}, client={ClientHash}. Triggering reconnection.",
            serverHash[..Math.Min(8, serverHash.Length)],
            clientHash[..Math.Min(8, clientHash.Length)]);
        return true;
    }

    return false;
}
```

Key changes vs previous implementation:
- No `_subject.GetApplyLock()` — no lock needed, hash is from update content
- No `StateHashComputer.ComputeStructuralHash()` — no live graph walk
- No try/catch for hash computation failures (plain dictionary walk, no exceptions expected)

**Step 5: Update heartbeat handler to also update client state**

The heartbeat doesn't carry update content, so `_clientState` is already up to date. The heartbeat comparison uses `_clientState.ComputeHash()` which is the same as what `HasStructuralHashMismatch` does. No change needed to the heartbeat handler — it already calls `HasStructuralHashMismatch(heartbeat.StateHash)`.

**Step 6: Verify build and tests**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeds, all tests pass

---

### Task 4: Delete `StateHashComputer.cs`

**Files:**
- Delete: `src/Namotion.Interceptor.WebSocket/Internal/StateHashComputer.cs`

**Step 1: Verify no remaining references**

Search for `StateHashComputer` in the codebase. Should have zero references after Tasks 2 and 3.

**Step 2: Delete the file**

Remove `src/Namotion.Interceptor.WebSocket/Internal/StateHashComputer.cs`.

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds with zero errors

---

### Task 5: Update documentation

**Files:**
- Modify: `docs/connectors-websocket.md`
- Modify: `docs/plans/fixes.md`
- Modify: `docs/plans/followups.md`

**Step 1: Update heartbeat section in `connectors-websocket.md`**

Update the heartbeat section (around line 392) to mention idle-only behavior and structural hash:

After the existing heartbeat content, add a subsection about structural divergence detection:

```markdown
### Structural Divergence Detection

Every broadcast update includes a structural hash (`UpdatePayload.StructuralHash`) computed from the sent-state model — a lightweight dictionary tracking the structural state implied by all updates sent so far. The client maintains an identical model, updated from received updates.

After applying each update, the client compares its hash against the server's. A mismatch indicates structural divergence (dropped update, failed apply, or pipeline bug) and triggers reconnection via the existing recovery flow (Welcome with complete state).

**Why sent-state hash instead of live graph hash:**
The live graph contains mutations from the mutation engine that haven't been flushed by the change queue processor yet. Hashing the live graph produces false-positive mismatches during concurrent structural mutations. The sent-state hash only reflects what was actually sent/received, eliminating false positives regardless of mutation rate.

**Idle heartbeat:** When no updates are broadcast for 10 seconds, the server sends a heartbeat carrying the same sent-state hash. This covers the edge case where the last update is lost and the system goes idle. During active operation, the heartbeat is suppressed — updates already carry the hash.

**What the hash covers:**
- Subject IDs (sorted lexicographically)
- Structural property content: collection items (order-preserving), dictionary entries (sorted by key), object references
- Does NOT hash value properties — structural divergence is the critical detection target. Value divergence self-heals via continued mutations or is fixed as a side effect of structural resync.
```

**Step 2: Update Fix 21 description in `fixes.md`**

Update the Fix 21 section (around line 758) to note it's been superseded:

Change the Fix 21 summary entry to:

```markdown
### Fix 21: Heartbeat structural hash
**Files:** `HeartbeatPayload.cs`, `StateHashComputer.cs` (new, later replaced), `WebSocketSubjectHandler.cs`, `WebSocketSubjectClientSource.cs`, `Program.cs` (tester heartbeat config)

Structural state hash in heartbeats detects and auto-heals any divergence via reconnection + Welcome. **Superseded by Fix 24 (sent-state hash):** live graph hash replaced by sent-state hash to eliminate false positives during concurrent structural mutations. `StateHashComputer.cs` removed.
```

**Step 3: Update Fix 23 entry in `fixes.md`**

Update the Fix 23 summary entry (around line 768) to note the hash approach changed:

```markdown
### Fix 23: CQP filter cache + hash-in-update
**Files:** `WebSocketSubjectHandler.cs`, `UpdatePayload.cs`, `WebSocketSubjectClientSource.cs`

CQP filter caches PathProvider decisions in `subject.Data` — no PathProvider bypass during momentary unregistration. Structural hash embedded in every broadcast via `UpdatePayload.StructuralHash`. Client compares after each apply. Heartbeat only fires during idle periods (no broadcasts for 10s). **Hash source changed in Fix 24** from live graph (`StateHashComputer`) to sent-state model (`SentStructuralState`). Design: [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md).
```

**Step 4: Add Fix 24 entry to `fixes.md`**

Append after Fix 23:

```markdown
---

## Fix 24: Sent-state structural hash

**Files changed:** `SentStructuralState.cs` (new), `WebSocketSubjectHandler.cs`, `WebSocketSubjectClientSource.cs`, `StateHashComputer.cs` (deleted)

**Cause:** Fix 23's per-update hash comparison used `StateHashComputer` to walk the live graph for the hash. The live graph always contains unflushed mutations from the mutation engine — structural mutations that CQP hasn't flushed yet. The `_applyUpdateLock` blocks other CQP flushes but not the mutation engine. The hash captured state not in the update, causing false-positive mismatches (~120 per cycle in ConnectorTester at 500+ structural mutations/sec).

**Fix:** Replaced live graph hash with sent-state hash. `SentStructuralState` maintains a lightweight dictionary tracking structural state implied by sent/received `SubjectUpdate` content. SHA256 hash computed from this dictionary. Both server and client maintain identical instances updated from the same update content. The hash is consistent by construction — no false positives regardless of concurrent mutation rate.

**What was removed:** `StateHashComputer.cs` (106 lines of live graph walking). Client no longer needs apply lock for hash computation. Server no longer walks the graph under `_applyUpdateLock`.

**What was added:** `SentStructuralState.cs` — ~150 lines, self-contained, no dependency on registry/interceptors/core library. Pure WebSocket-internal concern.

**Design:** See [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md), Layer 2: Sent-State Structural Hash.

**Status:** Applied.
```

**Step 5: Update `followups.md`**

Update the "Structural hash: lazy recomputation" follow-up (around line 29) to note it's no longer applicable:

```markdown
## Structural hash: lazy recomputation instead of per-heartbeat graph walk

**Priority:** Medium (performance) — **SUPERSEDED** (Fix 24)
**Context:** The sent-state hash approach (Fix 24) eliminated the live graph walk entirely. The hash is computed from a plain `SortedDictionary`, which is significantly faster than the interceptor-chain-based graph walk. Lazy recomputation is no longer needed.
```

Also update the "Incremental structural hash" follow-up (around line 51):

```markdown
## Incremental structural hash

**Priority:** Low (optimization) — **SUPERSEDED** (Fix 24)
**Context:** The sent-state hash approach (Fix 24) already provides O(changed subjects) per update for dictionary maintenance. SHA256 is recomputed from the dictionary on each broadcast, but this is a plain data walk — no interceptor chain, no registry access. For graphs where SHA256 recomputation becomes a bottleneck, XOR-based incremental hashing can be added to `SentStructuralState` as a future optimization.
```

---

### Task 6: Build and run ConnectorTester

**Step 1: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds with zero errors

**Step 2: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

**Step 3: Run ConnectorTester for initial validation (50 cycles)**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester -- --ConnectorTester:Connector=websocket --ConnectorTester:Cycles=50`
Expected: 50 cycles without convergence failure. Watch for:
- Zero `Structural hash mismatch` warnings caused by false positives
- Hash mismatches detected and resolved if real structural divergence occurs
- Idle heartbeats sent during convergence check (when mutations stop)

**Step 4: Run long ConnectorTester (1000+ cycles) in background**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester -- --ConnectorTester:Connector=websocket --ConnectorTester:Cycles=2000`
Expected: 2000 cycles without failure. This validates the sent-state hash under sustained load with continuous structural + value mutations.
