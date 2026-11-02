using System.Runtime.InteropServices;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;

namespace HomeBlaze.Game.World;

/// <summary>
/// An entity whose indexed state changed, with the area and visibility it had in the index before its first change
/// since the last <see cref="ChangedEntities.Clear"/>.
/// </summary>
/// <param name="Entity">The entity; it may have left the index since.</param>
/// <param name="OldArea">The area it had in the index before, meaningless unless <paramref name="WasVisible"/>.</param>
/// <param name="WasVisible">Whether it was in the index and visible before.</param>
internal readonly record struct GameEntityChange(GameEntity Entity, GameArea OldArea, bool WasVisible);

/// <summary>
/// The entities whose indexed state changed since the last <see cref="Clear"/>, mostly once each. Not thread-safe: only
/// the simulation thread uses it.
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
