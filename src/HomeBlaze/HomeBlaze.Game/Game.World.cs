using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using HomeBlaze.Game.View;
using HomeBlaze.Game.World;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game;

/// <summary>
/// The verdict on a step and the position and facing of the player after it.
/// </summary>
internal readonly record struct MoveResult(bool IsAccepted, int X, int Y, GameDirection Facing);

public partial class Game
{
    /// <summary>
    /// The steps, and separately the turns, a player may take at once after a pause; one more each <see cref="StepInterval"/>.
    /// </summary>
    internal const int StepBurst = 3;

    /// <summary>
    /// The animations a player may start at once after a pause; one more each <see cref="AnimationInterval"/>.
    /// </summary>
    internal const int AnimationBurst = 3;

    /// <summary>
    /// The time a player waits between two animation starts it may make beyond the burst.
    /// </summary>
    internal static readonly TimeSpan AnimationInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The time a player waits between two messages it sends.
    /// </summary>
    internal static readonly TimeSpan MessageInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The message with which placing is refused for a caller that is not a player of this game.
    /// </summary>
    internal const string NoActorMessage = "Only a player in the game can place objects.";

    /// <summary>
    /// The message with which placing in front of a player that faces diagonally is refused.
    /// </summary>
    internal const string DiagonalMessage = "Face north, east, south or west to place objects.";

    /// <summary>
    /// The message with which placing in front of a player is refused when the spot would leave the <see cref="int"/> range.
    /// </summary>
    internal const string NoRoomInFrontMessage = "There is no room in front of you.";

    /// <summary>
    /// The message with which sending is refused for a caller that is not a connected player of this game.
    /// </summary>
    internal const string NoSenderMessage = "Only a player in the game can send messages.";

    /// <summary>
    /// The message with which playing an animation is refused for a caller that is not a connected player of this game.
    /// </summary>
    internal const string NoAnimationActorMessage = "Only a player in the game can play animations.";

    // Touched only on the simulation thread: the objects whose OnAddedToWorld runs.
    private readonly List<GameObject> _addedObjects = [];

    private readonly GameViewSessions _viewSessions;

    /// <summary>
    /// The view sessions of the pages.
    /// </summary>
    internal GameViewSessions Views => _viewSessions;

    // Touched only on the simulation thread: the places and replaces of other threads, which wait for a write of the set.
    private readonly GameSetWaiters _placementsAwaitingWrite = new();

    /// <inheritdoc />
    public GameSpot GetSpotInFront(GameCaller? caller, int across, int along)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(across, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(across, GameObject.MaximumSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(along, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(along, GameObject.MaximumSize);
        if (!TryGetActorPosition(caller, out var actorArea, out var facing))
        {
            throw new InvalidOperationException(NoActorMessage);
        }

        if (!FrontPlacement.TryGetArea(actorArea, facing, across, along, out var area))
        {
            throw new InvalidOperationException(!Enum.IsDefined(facing) || facing.IsDiagonal() ? DiagonalMessage : NoRoomInFrontMessage);
        }

        return new GameSpot(area.X, area.Y, area.Width, area.Depth, facing);
    }

    /// <inheritdoc />
    public bool IsFree(GameArea area, GameObject? except = null)
    {
        if (area.Width > GameObject.MaximumSize || area.Depth > GameObject.MaximumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(area), $"An area is at most {GameObject.MaximumSize} tiles wide and deep.");
        }

        if (!_simulation.IsCurrent)
        {
            return _world.Snapshot.IsFree(area, except);
        }

        // Indexes what earlier schedules of this tick wrote, so two objects stepping in one tick cannot both take a tile.
        _world.RefreshChangedSubjects();
        var ignored = except is null ? null : _world.FindEntity(except);
        return !_world.Index.IsBlocked(area, ignored);
    }

    /// <inheritdoc />
    public Task PlaceAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        return RunPlacementAsync(() => _world.Place(gameObject), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(GameObject gameObject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        return _simulation.InvokeAsync(() =>
        {
            _world.Remove(gameObject);
            _simulation.CancelSchedules(gameObject);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ReplaceAsync(GameObject gameObject, IReadOnlyList<GameObject> replacements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(replacements);

        // A copy, so a caller that changes its list afterwards does not change the queued command.
        GameObject[] copy = [.. replacements];
        return RunPlacementAsync(() =>
        {
            _world.Replace(gameObject, copy);
            _simulation.CancelSchedules(gameObject);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public IDisposable Schedule(GameObject gameObject, TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        if (_simulation.IsCurrent)
        {
            if (!_world.NamesBySubject.ContainsKey(gameObject))
            {
                throw new InvalidOperationException(GameWorldState.NotPartOfGameMessage);
            }

            return _simulation.Schedule(gameObject, delay, action);
        }

        // Outside the simulation only the facade answers, which holds every object whose place or replace completed.
        if (gameObject is GamePlayer || !ReferenceEquals(gameObject.TryGetFirstParent<Game>(), this))
        {
            throw new InvalidOperationException(GameWorldState.NotPartOfGameMessage);
        }

        var schedule = new PendingSchedule(gameObject, delay, action);
        _simulation.Post(() => schedule.Register(this));
        return schedule;
    }

    /// <inheritdoc />
    public void PlayAnimation(GameCaller? caller, string animation, GameVisualAnimationMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(animation);
        if (animation is "Idle" or "Walk")
        {
            throw new ArgumentException("Idle and Walk are played by the game.", nameof(animation));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (caller is null || GetActor(caller) is null)
        {
            throw new InvalidOperationException(NoAnimationActorMessage);
        }

        var clientId = caller.ClientId;
        _simulation.Post(() =>
        {
            if (_world.Players.TryGetValue(clientId, out var player) && player.IsConnected)
            {
                player.AnimationRateLimiter ??= new RateLimiter(_timeProvider, AnimationInterval, AnimationBurst);
                if (!player.AnimationRateLimiter.TryTake())
                {
                    return;
                }

                player.StartAnimation(animation, mode, _timeProvider.GetTimestamp());
                _world.OnAnimationChanged(player);
            }
        });
    }

    // Called from another thread, the task waits until the object set with the new objects is written, so their World
    // resolves when it completes; on the simulation thread it completes at once.
    private Task RunPlacementAsync(Action placement, CancellationToken cancellationToken)
    {
        if (_simulation.IsCurrent)
        {
            return _simulation.InvokeAsync(() =>
            {
                placement();
                return true;
            }, cancellationToken);
        }

        return _placementsAwaitingWrite.InvokeAsync(_simulation, _publisher, placement, cancellationToken);
    }

    /// <summary>
    /// Completes the places and replaces whose objects a write of the object set published; call on the simulation thread.
    /// </summary>
    private void CompleteWrittenPlacements() => _placementsAwaitingWrite.Complete(_publisher.ObjectSetVersion);

    /// <summary>
    /// Fails the places and replaces still waiting for a write of the object set; call once the simulation stopped.
    /// </summary>
    private void AbandonPlacements() => _placementsAwaitingWrite.Abandon();

    /// <summary>
    /// Cancels the schedules of the objects that left the world; call on the simulation thread. The objects that entered
    /// it get <see cref="IGameObjectLifecycle.OnAddedToWorld"/> after the publish that attaches them.
    /// </summary>
    private void OnObjectsTakenOver(List<GameObject> removed)
    {
        foreach (var subject in removed)
        {
            _simulation.CancelSchedules(subject);
        }
    }

    // Collected first, because a hook may remove an object, which changes the world's objects.
    private void RunAddedToWorldOfKeptObjects()
    {
        var subjects = new List<GameObject>(_world.Objects.Count);
        foreach (var entity in _world.Objects.Values)
        {
            if (!_world.IsAddedToWorldPending(entity.Subject))
            {
                subjects.Add(entity.Subject);
            }
        }

        foreach (var subject in subjects)
        {
            RunAddedToWorld(subject);
        }
    }

    // Moved out first, because a hook may remove an object, which takes it out of the world's list.
    private void RunAddedToWorldOfPublishedObjects()
    {
        _world.MovePublishedNewObjectsTo(_addedObjects);
        if (_addedObjects.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var subject in _addedObjects)
            {
                RunAddedToWorld(subject);
            }
        }
        finally
        {
            _addedObjects.Clear();
        }
    }

    // An earlier hook of the same batch may have removed the object.
    private void RunAddedToWorld(GameObject subject)
    {
        if (_world.NamesBySubject.ContainsKey(subject))
        {
            _simulation.RunLogged(CallOnAddedToWorld, subject);
        }
    }

    private static void CallOnAddedToWorld(GameObject subject) => ((IGameObjectLifecycle)subject).OnAddedToWorld();

    /// <summary>
    /// Gets the connected player of this game the caller acts as, or null when it acts as none, for example from the
    /// subject page, MCP or an agent. Outside the simulation thread it answers from the state of the last tick.
    /// </summary>
    internal GamePlayer? GetActor(GameCaller? caller)
    {
        if (caller is null)
        {
            return null;
        }

        if (_simulation.IsCurrent)
        {
            return _world.Players.TryGetValue(caller.ClientId, out var player) && player.IsConnected ? player.Subject : null;
        }

        return _world.PlayerPositions.TryGetValue(caller.ClientId, out var position) && position.IsConnected ? position.Subject : null;
    }

    /// <summary>
    /// Turns the player of the client toward <paramref name="direction"/> and, unless <paramref name="turnOnly"/>, steps
    /// one tile that way when the step is allowed; the caller must pass only its own client. Completes in the next tick.
    /// </summary>
    /// <returns>Null when the client has no player in this game.</returns>
    internal Task<MoveResult?> StepAsync(string clientId, GameDirection direction, bool turnOnly, CancellationToken cancellationToken = default) =>
        _simulation.InvokeAsync(() => _world.Players.TryGetValue(clientId, out var player)
            ? _world.TryMove(player, direction, turnOnly)
            : (MoveResult?)null, cancellationToken);

    private GameViewSessions CreateViewSessions() => new(_simulation, _world, _logger);

    // Runs after everything that changes the index in the tick, so the sessions see all of this tick's changes.
    private void UpdateViewSessions() => _viewSessions.Update(_publisher.Version);

    /// <exception cref="InvalidOperationException">The sender is not a connected player of this game or sent a message
    /// less than <see cref="MessageInterval"/> ago.</exception>
    internal async Task SendMessageAsync(GamePlayer recipient, GameCaller? sender, string message, CancellationToken cancellationToken)
    {
        var senderTitle = sender is null ? null : await _simulation.InvokeAsync(() => TakeMessageSlot(sender.ClientId), cancellationToken);
        var notification = new Notification
        {
            Title = senderTitle is null ? "Message" : $"Message from {senderTitle}",
            Message = message
        };
        await _clients.SendNotificationAsync(recipient.ClientId, notification, cancellationToken);
    }

    private string TakeMessageSlot(string clientId)
    {
        if (!_world.Players.TryGetValue(clientId, out var sender) || !sender.IsConnected)
        {
            throw new InvalidOperationException(NoSenderMessage);
        }

        var now = _timeProvider.GetTimestamp();
        if (sender.LastMessageTimestamp is { } last && _timeProvider.GetElapsedTime(last, now) < MessageInterval)
        {
            throw new InvalidOperationException("Wait a moment before sending another message.");
        }

        sender.LastMessageTimestamp = now;
        return sender.Title;
    }

    private bool TryGetActorPosition(GameCaller? caller, out GameArea area, out GameDirection facing)
    {
        if (caller is not null && _simulation.IsCurrent)
        {
            if (_world.Players.TryGetValue(caller.ClientId, out var player) && player.IsConnected)
            {
                (area, facing) = (player.Area, player.Facing);
                return true;
            }
        }
        else if (caller is not null && _world.PlayerPositions.TryGetValue(caller.ClientId, out var position) && position.IsConnected)
        {
            (area, facing) = (position.Area, position.Facing);
            return true;
        }

        (area, facing) = (default, default);
        return false;
    }

    /// <summary>
    /// A schedule made outside the simulation thread, which the simulation registers in its next tick; disposing it
    /// cancels it whether it is registered yet or not.
    /// </summary>
    private sealed class PendingSchedule(GameObject gameObject, TimeSpan delay, Action action) : IDisposable
    {
        private GameObject? _gameObject = gameObject;
        private Action? _action = action;
        private IDisposable? _registration;
        private int _isDisposed;

        // On the simulation thread. An object removed meanwhile had its schedules canceled, so this one is dropped too.
        // The references go either way, so a held handle does not keep the object or the action alive.
        public void Register(Game game)
        {
            var owner = _gameObject;
            var scheduledAction = Volatile.Read(ref _action);
            _gameObject = null;
            Volatile.Write(ref _action, null);
            if (owner is null || scheduledAction is null || !game._world.NamesBySubject.ContainsKey(owner))
            {
                return;
            }

            var registration = game._simulation.Schedule(owner, delay, scheduledAction);

            // Full fences on both sides: either Dispose sees the registration or this sees the disposal.
            Interlocked.Exchange(ref _registration, registration);
            if (Volatile.Read(ref _isDisposed) == 1)
            {
                registration.Dispose();
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _isDisposed, 1);
            Volatile.Write(ref _action, null);
            Volatile.Read(ref _registration)?.Dispose();
        }
    }
}
