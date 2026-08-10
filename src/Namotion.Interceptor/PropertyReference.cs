using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public readonly struct PropertyReference : IEquatable<PropertyReference>
{
    public static readonly PropertyReferenceComparer Comparer = new();

    public PropertyReference(IInterceptorSubject subject, string name)
    {
        Subject = subject;
        Name = name;
    }

    public IInterceptorSubject Subject { get; }

    public string Name { get; }

    /// <summary>
    /// Looks up the property metadata in the subject's property table on each access; the result is not
    /// cached, because PropertyReference is a value type copied throughout the codebase and an embedded
    /// cache would bloat every copy and force the struct to be mutable (see the readonly-struct
    /// declaration). The lookup is cheap, but hoist it to a local if you read it more than once in a hot path.
    /// </summary>
    public SubjectPropertyMetadata Metadata =>
        Subject.Properties.TryGetValue(Name, out var metadata) ? metadata :
            throw new InvalidOperationException(
                $"No metadata found for property '{Name}' on {Subject.GetType().Name}. " +
                $"Available properties ({Subject.Properties.Count}): [{string.Join(", ", Subject.Properties.Keys)}]");

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[(Name, key)] = value;
    }

    public void RemovePropertyData(string key)
    {
        Subject.Data.TryRemove((Name, key), out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue((Name, key), out value);
    }

    /// <summary>
    /// Gets an existing value for the specified key, or adds the value if the key doesn't exist.
    /// This operation is atomic and thread-safe.
    /// </summary>
    /// <param name="key">The key to look up or add.</param>
    /// <param name="value">The value to add if the key doesn't exist.</param>
    /// <returns>The existing value if found, or the newly added value.</returns>
    public object? GetOrSetPropertyData(string key, object? value)
    {
        return Subject.Data.GetOrAdd((Name, key), value);
    }

    /// <summary>
    /// Adds the property data for the specified key only if the key is not already present.
    /// This operation is atomic and thread-safe, and is the add-if-absent counterpart to
    /// <see cref="TryRemovePropertyData"/>. Use it when the caller must distinguish a first
    /// write from a subsequent one, which <see cref="GetOrSetPropertyData"/> cannot express.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value to store when the key is absent.</param>
    /// <returns><c>true</c> if the value was stored; <c>false</c> if a value was already present, which is left untouched.</returns>
    public bool TryAddPropertyData(string key, object? value)
    {
        return Subject.Data.TryAdd((Name, key), value);
    }

    /// <summary>
    /// Removes the property data for the specified key only if it matches the expected value.
    /// This operation is atomic and thread-safe.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="expectedValue">The value that must match for removal to succeed.</param>
    /// <returns><c>true</c> if the key-value pair was removed; <c>false</c> if the key didn't exist or the value didn't match.</returns>
    public bool TryRemovePropertyData(string key, object? expectedValue)
    {
        return ((ICollection<KeyValuePair<(string?, string), object?>>)Subject.Data)
            .Remove(new KeyValuePair<(string?, string), object?>((Name, key), expectedValue));
    }

    // Short by convention, and load-bearing here: the write state is read on every delivered change,
    // string hash codes are not cached, so key length is per-call work. Matches the "ni.*" keys the
    // tracking and connector layers use.
    private const string WriteStateKey = "ni.wstate";

    /// <summary>
    /// Gets the write timestamp, or null if no timestamp has been set.
    /// </summary>
    public DateTimeOffset? TryGetWriteTimestamp()
    {
        if (TryGetWriteState(out var state))
        {
            var ticks = Interlocked.Read(ref state.TimestampTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Gets the revision of the last write to this property that reached a write terminal, both counting
    /// source-originated commits and excluding them, and whether a sink has already published its value.
    /// Returns false when the property has never been written.
    /// </summary>
    /// <param name="includeSourceCommits">Whether commits applied from a source count. Only a sink that
    /// can prove such a value already reached its destination may say true; for anything talking over a
    /// wire the value was produced before the source saw our write, so it cannot rank against our
    /// commits. Excluding them is load-bearing rather than an optimization.</param>
    /// <param name="commitRevision">The revision of the last qualifying commit, or 0 if there is none.</param>
    /// <param name="published">Whether a sink has published this property's value.</param>
    /// <remarks>
    /// A revision is only comparable against a change to the same property: revisions are per subject,
    /// and two properties of one subject draw from the same counter. The caller states which commits
    /// count rather than receiving both markers, so the wrong one cannot be selected by argument
    /// position, and only the slot that is needed is read.
    /// </remarks>
    public bool TryGetWriteState(bool includeSourceCommits, out long commitRevision, out bool published)
    {
        if (TryGetWriteState(out var state))
        {
            var nonSourceCommitRevision = Interlocked.Read(ref state.LastNonSourceCommitRevision);

            // Each commit advances exactly one of the two, so the last of any kind is their maximum, and
            // the source slot is read only when it can count. A stale read of either can only lower the
            // result, which delivers a redundant change rather than dropping a live one.
            commitRevision = includeSourceCommits
                ? Math.Max(nonSourceCommitRevision, Interlocked.Read(ref state.LastSourceCommitRevision))
                : nonSourceCommitRevision;

            published = state.Published;
            return true;
        }

        commitRevision = 0;
        published = false;
        return false;
    }

    /// <summary>
    /// Records that a sink has published this property's value. One-way: the flag is never cleared, so
    /// calling this again on the same property has no effect. Read it back through the <c>published</c>
    /// output of <see cref="TryGetWriteState(bool, out long, out bool)"/>.
    /// </summary>
    public void MarkPublished()
    {
        GetOrAddWriteState().Published = true;
    }

    /// <summary>
    /// Records what the terminal just committed: the write timestamp as raw UTC ticks, avoiding a
    /// DateTimeOffset conversion on the hot path, and the commit revision. The revision goes to whichever
    /// of the two slots the origin selects, never both, so a commit costs one revision store on top of the
    /// timestamp store rather than two; see <see cref="PropertyWriteState.LastSourceCommitRevision"/>. The
    /// timestamp is recorded either way, preserving what <see cref="TryGetWriteTimestamp"/> reports.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteState(long timestamp, long revision, bool isFromSource)
    {
        var state = GetOrAddWriteState();
        Interlocked.Exchange(ref state.TimestampTicks, timestamp);

        if (isFromSource)
        {
            Interlocked.Exchange(ref state.LastSourceCommitRevision, revision);
        }
        else
        {
            Interlocked.Exchange(ref state.LastNonSourceCommitRevision, revision);
        }
    }

    /// <summary>
    /// Sets the write timestamp alone, for the paths that produce a change without committing a write
    /// through a terminal and therefore have no revision to stamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteTimestamp(long timestamp)
    {
        Interlocked.Exchange(ref GetOrAddWriteState().TimestampTicks, timestamp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetWriteState(out PropertyWriteState state)
    {
        if (Subject.Data.TryGetValue((Name, WriteStateKey), out var value) && value is PropertyWriteState existing)
        {
            state = existing;
            return true;
        }

        state = null!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PropertyWriteState GetOrAddWriteState()
    {
        return (PropertyWriteState)Subject.Data.GetOrAdd((Name, WriteStateKey), static _ => new PropertyWriteState())!;
    }

    #region Equality

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(PropertyReference other)
    {
        return Comparer.Equals(this, other);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj)
    {
        return obj is PropertyReference other && Comparer.Equals(this, other);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return Comparer.GetHashCode(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(PropertyReference left, PropertyReference right)
    {
        return left.Equals(right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(PropertyReference left, PropertyReference right)
    {
        return !left.Equals(right);
    }

    public sealed class PropertyReferenceComparer : IEqualityComparer<PropertyReference>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(PropertyReference x, PropertyReference y)
        {
            return ReferenceEquals(x.Subject, y.Subject) && string.Equals(x.Name, y.Name, StringComparison.Ordinal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetHashCode(PropertyReference obj)
        {
            var subject = obj.Subject;
            var name = obj.Name;
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            var h1 = subject is null ? 0 : RuntimeHelpers.GetHashCode(subject);
            var h2 = name is null ? 0 : StringComparer.Ordinal.GetHashCode(name);
            // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            return (h1 * 397) ^ h2;
        }
    }    

    #endregion
}