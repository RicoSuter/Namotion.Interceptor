using System;
using System.Collections.Generic;
using System.Linq;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Internal;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Internal;

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

    [Fact]
    public void WhenValueOnlyUpdateOnExistingSubject_ThenCachedHashReturned()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot);
        var hash1 = state.ComputeHash();

        // Act — value-only update on existing subject, compute hash twice
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
        var hash2 = state.ComputeHash();
        var hash3 = state.ComputeHash();

        // Assert — all hashes identical (value-only doesn't invalidate cache)
        Assert.Equal(hash1, hash2);
        Assert.Equal(hash2, hash3);
    }

    [Fact]
    public void WhenStructuralUpdateFollowedByValueOnly_ThenCacheInvalidatedOnceOnly()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot);
        var hashAfterInit = state.ComputeHash();

        // Act — structural update adds child2
        var structuralUpdate = CreatePartialUpdateWithDictionary("root", "Items",
            new[] { ("key1", "child1"), ("key2", "child2") },
            completeSubjectIds: new[] { "child2" });
        state.UpdateFromBroadcast(structuralUpdate);
        var hashAfterStructural = state.ComputeHash();

        // Act — value-only update (should use cache)
        var valueUpdate = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["child1"] = new()
                {
                    ["Temp"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = 99
                    }
                }
            }
        };
        state.UpdateFromBroadcast(valueUpdate);
        var hashAfterValue = state.ComputeHash();

        // Assert
        Assert.NotEqual(hashAfterInit, hashAfterStructural); // structural change invalidated
        Assert.Equal(hashAfterStructural, hashAfterValue);    // value-only returned cache
    }

    [Fact]
    public void WhenValueOnlyUpdateAddsNewSubject_ThenHashChanges()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithRoot("root");
        state.InitializeFromSnapshot(snapshot);
        var hashBefore = state.ComputeHash();

        // Act — value-only update for a previously untracked subject
        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["newSubject"] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "hello"
                    }
                }
            }
        };
        state.UpdateFromBroadcast(update);
        var hashAfter = state.ComputeHash();

        // Assert — new subject in tracked set changes hash
        Assert.NotEqual(hashBefore, hashAfter);
        Assert.Equal(2, state.TrackedSubjectCount);
    }

    [Fact]
    public void WhenReInitializedFromSnapshot_ThenCacheInvalidated()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot1 = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot1);
        var hash1 = state.ComputeHash();

        // Act — re-initialize with different snapshot
        var snapshot2 = CreateSnapshotWithDictionary("root", "Items",
            new[] { ("key1", "child1"), ("key2", "child2") });
        state.InitializeFromSnapshot(snapshot2);
        var hash2 = state.ComputeHash();

        // Assert — hash changes after re-initialization
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void WhenMultipleConsecutiveValueOnlyUpdates_ThenHashComputedOnce()
    {
        // Arrange
        var state = new SentStructuralState();
        var snapshot = CreateSnapshotWithDictionary("root", "Items", new[] { ("key1", "child1") });
        state.InitializeFromSnapshot(snapshot);
        var hash1 = state.ComputeHash(); // first computation

        // Act — several value-only updates on existing subjects
        for (var i = 0; i < 10; i++)
        {
            var update = new SubjectUpdate
            {
                Root = "root",
                Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
                {
                    ["child1"] = new()
                    {
                        ["Value"] = new SubjectPropertyUpdate
                        {
                            Kind = SubjectPropertyUpdateKind.Value,
                            Value = i
                        }
                    }
                }
            };
            state.UpdateFromBroadcast(update);
        }

        var hash2 = state.ComputeHash();

        // Assert — same hash, no structural changes occurred
        Assert.Equal(hash1, hash2);
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
