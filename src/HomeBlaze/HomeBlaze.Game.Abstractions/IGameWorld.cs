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
    /// Plays an animation of the person on the caller's player. <see cref="GameVisualAnimationMode.Once"/> returns to idle
    /// when the animation ends; <see cref="GameVisualAnimationMode.Loop"/> holds until the player moves. A new play
    /// replaces a running animation, and a step or a teleport of the player ends it. An animation the person does not have
    /// shows nothing. Callable from any thread: the animation starts on the game's own thread in the next tick and does
    /// nothing when the player left by then; outside the game's own thread the check for a player answers from the state
    /// of the last tick.
    /// </summary>
    /// <param name="caller">The acting player; only the game page provides one.</param>
    /// <param name="animation">The name of the animation in the person's model, such as <c>Clapping</c>.</param>
    /// <param name="mode">Whether the animation plays once or loops.</param>
    /// <exception cref="ArgumentException">The animation is empty, <c>Idle</c> or <c>Walk</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not a <see cref="GameVisualAnimationMode"/> value.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a connected player of this game.</exception>
    void PlayAnimation(GameCaller? caller, string animation, GameVisualAnimationMode mode);

    /// <summary>
    /// Whether no movement blocking object other than <paramref name="except"/> overlaps the area. Callable from any
    /// thread; on the game's own thread it includes what objects wrote earlier in the tick, elsewhere it answers from the
    /// state of the last tick.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The area is wider or deeper than <see cref="GameObject.MaximumSize"/>.</exception>
    bool IsFree(GameArea area, GameObject? except = null);

    /// <summary>
    /// Adds the object under a name of its type name in lowercase, a dash and 8 random hex digits. Called from another
    /// thread, the task completes once the object is one of the game's objects, so its <see cref="GameObject.World"/>
    /// resolves and, unless the game stopped meanwhile, its <see cref="GameObject.OnAddedToWorld"/> ran. Called on the
    /// game's own thread, as from a scheduled action or <see cref="GameObject.OnAddedToWorld"/>, the command runs at
    /// once and the task completes with it; the object joins the game's objects later in that tick or in the next, and
    /// its <see cref="GameObject.OnAddedToWorld"/> follows. Canceling before the game processed the command drops it;
    /// after that the object stays placed, and canceling no longer ends the wait, which ends once the game wrote its
    /// objects, at the latest when a save in progress completed. A stop that timed out while a save held that write back
    /// may still drop the object; the task then fails with <see cref="GameStoppedException"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The object is already part of this game, or its size is outside 1 to
    /// <see cref="GameObject.MaximumSize"/>.</exception>
    /// <exception cref="InvalidOperationException">The object blocks movement and overlaps a movement blocking object.</exception>
    /// <exception cref="GameStoppedException">The game stopped before the command completed.</exception>
    Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the object. Canceling before the game processed the command drops it; after that the object stays removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    /// <exception cref="GameStoppedException">The game stopped before the command completed.</exception>
    Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the object by the replacements in one step; no player sees an intermediate state. The task completes
    /// and the replacements join the game's objects as with <see cref="PlaceAsync"/>. Canceling before the game
    /// processed the command drops it; after that the replacement stays applied, unless a stop drops it as with
    /// <see cref="PlaceAsync"/>, and canceling no longer ends the wait.
    /// </summary>
    /// <exception cref="ArgumentException">A replacement is listed twice, already part of this game, or of invalid size.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of this game, or a movement blocking replacement
    /// overlaps a movement blocking object other than the replaced one or another replacement; nothing changes then.</exception>
    /// <exception cref="GameStoppedException">The game stopped before the command completed.</exception>
    Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the action synchronously on the game's own thread at the first tick at or after the delay, never in the tick
    /// the delay counts from. Called on the game's own thread, the delay counts from the current tick; called from another
    /// thread, from the tick that registers the schedule, the next one. Pass a synchronous action, not an async lambda,
    /// whose continuations would leave the game's thread; an exception it throws is logged and the game goes on.
    /// Disposing the result, from any thread, cancels it, and removing the object cancels all its schedules. Schedules
    /// are not saved; set them up again in <see cref="GameObject.OnAddedToWorld"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    /// <exception cref="InvalidOperationException">The object is not part of this game.</exception>
    IDisposable Schedule(GameObject gameObject, TimeSpan delay, Action action);
}
