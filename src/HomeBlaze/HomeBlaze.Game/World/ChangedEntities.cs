using System.Runtime.InteropServices;

namespace HomeBlaze.Game.World;

/// <summary>
/// The entities whose indexed state changed since the last <see cref="Clear"/>, mostly once each, in the order of their
/// first change; an entity removed and added again shows twice, its earlier change first. Not thread-safe: only the
/// simulation thread uses it.
/// </summary>
internal sealed class ChangedEntities
{
    private readonly List<GameEntityChange> _changes = [];

    public int Count => _changes.Count;

    /// <summary>
    /// The changes; valid until the next <see cref="Add"/> or <see cref="Clear"/>.
    /// </summary>
    public ReadOnlySpan<GameEntityChange> AsSpan() => CollectionsMarshal.AsSpan(_changes);

    public void Add(GameEntityChange change) => _changes.Add(change);

    public void Clear() => _changes.Clear();
}
