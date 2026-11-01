namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// What an object or tool may ask of the game it is part of; reached through <see cref="GameObject.World"/> and
/// <see cref="GameTool.World"/>.
/// </summary>
public interface IGameWorld
{
    /// <summary>
    /// Gets the area right in front of the caller's player, <paramref name="across"/> tiles across its facing direction
    /// and <paramref name="along"/> tiles along it, centered on the player across the facing (an odd remainder goes east
    /// or north). Does not check whether the area is free.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is outside 1 to <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a connected player of this game, faces diagonally, or the area
    /// would leave the <see cref="int"/> range.</exception>
    GameSpot GetSpotInFront(GameCaller? caller, int across, int along);

    /// <summary>
    /// Whether no movement blocking object other than <paramref name="except"/> overlaps the area. Callable from any
    /// thread; outside the game's own processing it answers from the state of the last tick.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The area is wider or deeper than <see cref="GameObject.MaximumSize"/>.</exception>
    bool IsFree(GameArea area, GameObject? except = null);

    /// <summary>
    /// Adds the object under a name of its type name in lowercase, a dash and 8 random hex digits. Canceling before the
    /// game processed the command drops it; after that the object stays placed.
    /// </summary>
    /// <exception cref="ArgumentException">The object is already part of this game, or its size is outside 1 to
    /// <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The object blocks movement and overlaps a movement blocking object.</exception>
    Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the object. Canceling before the game processed the command drops it; after that the object stays removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the object by the replacements in one step; no player sees an intermediate state. Canceling before the
    /// game processed the command drops it; after that the replacement stays applied.
    /// </summary>
    /// <exception cref="ArgumentException">A replacement is listed twice, already part of this game, or of invalid size.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or a movement blocking replacement
    /// overlaps a movement blocking object other than the replaced one or another replacement; nothing changes then.</exception>
    Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken);
}
