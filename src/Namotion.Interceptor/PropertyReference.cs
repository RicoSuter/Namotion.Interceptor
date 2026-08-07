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
    /// Gets the revision of the last non-source write to this property that reached a write terminal,
    /// and whether a sink has already published its value. Returns false when the property has never
    /// been written.
    /// </summary>
    /// <remarks>
    /// The revision is only comparable against a change to the same property: revisions are per subject,
    /// and two properties of one subject draw from the same counter. Both values come from one lookup
    /// because this runs per delivered change.
    /// </remarks>
    public bool TryGetWriteState(out long lastNonSourceCommitRevision, out bool published)
    {
        if (TryGetWriteState(out var state))
        {
            lastNonSourceCommitRevision = Interlocked.Read(ref state.LastNonSourceCommitRevision);
            published = state.Published;
            return true;
        }

        lastNonSourceCommitRevision = 0;
        published = false;
        return false;
    }

    /// <summary>
    /// Records that a sink has published this property's value. Sticky; see
    /// <see cref="PropertyWriteState.Published"/>.
    /// </summary>
    public void MarkPublished()
    {
        GetOrAddWriteState().Published = true;
    }

    /// <summary>
    /// Records what the terminal just committed: the write timestamp as raw UTC ticks, avoiding a
    /// DateTimeOffset conversion on the hot path, and the commit revision. The revision is recorded only
    /// for commits that did not come from a source; see
    /// <see cref="PropertyWriteState.LastNonSourceCommitRevision"/> for why that exclusion is required.
    /// The timestamp is recorded either way, preserving what <see cref="TryGetWriteTimestamp"/> reports.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteState(long timestamp, long revision, bool isFromSource)
    {
        var state = GetOrAddWriteState();
        Interlocked.Exchange(ref state.TimestampTicks, timestamp);

        if (!isFromSource)
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