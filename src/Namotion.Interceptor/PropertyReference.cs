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

    // One holder per property, created on its first write and reused for the property's lifetime. Both
    // slots are written by the same terminal on the same write, so keeping them in one array is what
    // holds the hot path to a single dictionary lookup. The slots are independent: nothing reads them
    // as a pair, so the two exchanges below need no mutual ordering.
    // Short by convention, and load-bearing here: the revision slot is read on every delivered change,
    // string hash codes are not cached, so key length is per-call work. Matches the "ni.*" keys the
    // tracking and connector layers use.
    private const string WriteStateKey = "ni.wstate";
    private const int TimestampSlot = 0;
    private const int RevisionSlot = 1;

    /// <summary>
    /// Gets the write timestamp, or null if no timestamp has been set.
    /// </summary>
    public DateTimeOffset? TryGetWriteTimestamp()
    {
        if (Subject.Data.TryGetValue((Name, WriteStateKey), out var value) && value is long[] holder)
        {
            var ticks = Interlocked.Read(ref holder[TimestampSlot]);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Gets the revision of the last write to this property that reached a write terminal.
    /// </summary>
    /// <remarks>
    /// Comparable against the revision carried by a change to the same property, because both are
    /// stamped by the same terminal under the subject's lock. Revisions of different subjects are not
    /// comparable, and neither are revisions of two properties, so a caller may only compare a change
    /// against the property it belongs to.
    /// <para>
    /// The store happens after the value store and inside the lock, so a reader that observes revision
    /// N is guaranteed to observe the value committed at N. Returns false when the property has never
    /// been written through a terminal, and 0 when only a path that stamps no revision has written it
    /// (a derived recomputation, for instance).
    /// </para>
    /// </remarks>
    public bool TryGetCommittedRevision(out long revision)
    {
        if (Subject.Data.TryGetValue((Name, WriteStateKey), out var value) && value is long[] holder)
        {
            revision = Interlocked.Read(ref holder[RevisionSlot]);
            return true;
        }

        revision = 0;
        return false;
    }

    /// <summary>
    /// Records what the terminal just committed: the write timestamp as raw UTC ticks, avoiding
    /// DateTimeOffset conversion on the hot path, and the commit revision.
    /// Uses <see cref="Interlocked.Exchange(ref long, long)"/> to guarantee atomic 64-bit writes
    /// on 32-bit runtimes (the library targets netstandard2.0, which includes x86 .NET Framework;
    /// ECMA-335 only guarantees atomicity for writes up to <c>native int</c> size, so plain stores
    /// of a <c>long</c> can tear on 32-bit). Paired with <see cref="Interlocked.Read(ref long)"/>
    /// on the read side for symmetric atomicity. The exchange's return value is intentionally
    /// unused.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteState(long timestamp, long revision)
    {
        var holder = RentWriteState();
        Interlocked.Exchange(ref holder[TimestampSlot], timestamp);
        Interlocked.Exchange(ref holder[RevisionSlot], revision);
    }

    /// <summary>
    /// Sets the write timestamp alone, for the paths that produce a change without committing a write
    /// through a terminal and therefore have no revision to stamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetWriteTimestamp(long timestamp)
    {
        Interlocked.Exchange(ref RentWriteState()[TimestampSlot], timestamp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long[] RentWriteState()
    {
        return (long[])Subject.Data.GetOrAdd((Name, WriteStateKey), static _ => new long[2])!;
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