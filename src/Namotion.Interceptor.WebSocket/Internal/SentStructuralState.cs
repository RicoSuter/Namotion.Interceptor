using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Abstractions;

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
    private bool _dirty;
    private string? _cachedHash;

    /// <summary>
    /// Number of subjects currently tracked (for diagnostics/testing).
    /// </summary>
    public int TrackedSubjectCount => _subjectStructure.Count;

    /// <summary>
    /// Computes SHA256 hash of the tracked structural state.
    /// Returns the cached hash when no structural changes occurred since the last computation.
    /// Returns null if no subjects are tracked.
    /// </summary>
    public string? ComputeHash()
    {
        if (_subjectStructure.Count == 0)
            return null;

        if (!_dirty && _cachedHash is not null)
            return _cachedHash;

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
        _cachedHash = Convert.ToHexString(hash);
        _dirty = false;
        return _cachedHash;
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
        _dirty = true;
        _cachedHash = null;

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
            var hasStructuralProperties = false;
            foreach (var property in properties.Values)
            {
                if (property.Kind is SubjectPropertyUpdateKind.Object
                    or SubjectPropertyUpdateKind.Collection
                    or SubjectPropertyUpdateKind.Dictionary)
                {
                    hasStructuralProperties = true;
                    break;
                }
            }

            if (!hasStructuralProperties)
            {
                // Value-only update — ensure subject is tracked but don't change structure.
                // TryAdd returns true when a new subject is added, which changes the hash.
                if (_subjectStructure.TryAdd(subjectId, string.Empty))
                {
                    _dirty = true;
                }
                continue;
            }

            _dirty = true;

            // Get previous children for this subject
            var previousChildren = _children.TryGetValue(subjectId, out var existing)
                ? existing
                : null;

            // Build new structural content and extract new children
            var newChildren = new HashSet<string>();
            var structuralContent = BuildStructuralContent(properties, newChildren);

            // Decrement ref counts for removed children
            if (previousChildren is not null)
            {
                foreach (var child in previousChildren)
                {
                    if (!newChildren.Contains(child))
                    {
                        DecrementReferenceCount(child);
                    }
                }
            }

            // Increment ref counts for added children
            foreach (var child in newChildren)
            {
                if (previousChildren is null || !previousChildren.Contains(child))
                {
                    IncrementReferenceCount(child);
                }
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
        // Collect structural properties without LINQ to avoid iterator allocations
        List<KeyValuePair<string, SubjectPropertyUpdate>>? structuralProperties = null;
        foreach (var kvp in properties)
        {
            if (kvp.Value.Kind is SubjectPropertyUpdateKind.Object
                or SubjectPropertyUpdateKind.Collection
                or SubjectPropertyUpdateKind.Dictionary)
            {
                structuralProperties ??= new List<KeyValuePair<string, SubjectPropertyUpdate>>();
                structuralProperties.Add(kvp);
            }
        }

        if (structuralProperties is null)
            return string.Empty;

        // Sort by property name for deterministic output
        structuralProperties.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

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
                        // Sort dictionary items by key for deterministic output
                        var sortedItems = new List<SubjectPropertyItemUpdate>(property.Items);
                        sortedItems.Sort(static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
                        foreach (var item in sortedItems)
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

    /// <summary>
    /// Checks whether the tracked sent-state subjects match the actual registry.
    /// Returns true if they match, false if divergence is detected.
    /// Only checks subject presence/absence (not structural content) —
    /// catches the common case where a CQP-dropped mutation added or removed
    /// a subject that the server never learned about.
    /// </summary>
    public bool MatchesRegistry(ISubjectIdRegistry idRegistry, int registrySubjectCount)
    {
        // Quick count check (O(1))
        if (_subjectStructure.Count != registrySubjectCount)
            return false;

        // Check each tracked subject exists in registry (O(N))
        foreach (var subjectId in _subjectStructure.Keys)
        {
            if (!idRegistry.TryGetSubjectById(subjectId, out _))
                return false;
        }

        return true;
    }
}
