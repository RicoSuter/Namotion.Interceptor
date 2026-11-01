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
    /// The message with which sending is refused for a caller that is not a connected player of this game.
    /// </summary>
    internal const string NoSenderMessage = "Only a player in the game can send messages.";

    // Touched only on the simulation thread: scratch space of the view and target queries, and of the objects whose
    // OnAddedToWorld runs.
    private readonly List<GameEntity> _queryResults = [];
    private readonly List<GameObject> _addedObjects = [];

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
            throw new InvalidOperationException(!Enum.IsDefined(facing) || facing.IsDiagonal() ? DiagonalMessage : GameMessages.Blocked);
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
    /// Cancels the schedules of the objects that left the world and runs <see cref="IGameObjectLifecycle.OnAddedToWorld"/>
    /// of the ones that entered it; call on the simulation thread.
    /// </summary>
    private void OnObjectsTakenOver(List<GameObject> added, List<GameObject> removed)
    {
        foreach (var subject in removed)
        {
            _simulation.CancelSchedules(subject);
        }

        foreach (var subject in added)
        {
            RunAddedToWorld(subject);
        }
    }

    private List<GameObject> GetObjectSubjects()
    {
        var subjects = new List<GameObject>(_world.Objects.Count);
        foreach (var entity in _world.Objects.Values)
        {
            subjects.Add(entity.Subject);
        }

        return subjects;
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

    /// <summary>
    /// Gets the visible objects other than the client's player in the tiles in front of it, from south-west to north-east.
    /// </summary>
    internal Task<IReadOnlyList<GameObject>> GetTargetsAsync(string clientId, CancellationToken cancellationToken = default) =>
        _simulation.InvokeAsync<IReadOnlyList<GameObject>>(() =>
        {
            if (!_world.Players.TryGetValue(clientId, out var player))
            {
                return [];
            }

            _queryResults.Clear();
            _world.GetTargets(player, _queryResults);
            var targets = new GameObject[_queryResults.Count];
            for (var i = 0; i < targets.Length; i++)
            {
                targets[i] = _queryResults[i].Subject;
            }

            _queryResults.Clear();
            return targets;
        }, cancellationToken);

    /// <summary>
    /// Brings the tracker in line with the visible entities within <see cref="GameViewTracker.ViewRadius"/> of the
    /// client's player, or of the origin while it has none, and returns what changed. The caller must not touch the
    /// tracker until the task completed.
    /// </summary>
    internal Task<GameViewUpdate> UpdateViewAsync(GameViewTracker tracker, string? clientId, CancellationToken cancellationToken = default) =>
        _simulation.InvokeAsync(() =>
        {
            var self = clientId is not null && _world.Players.TryGetValue(clientId, out var player) ? player : null;
            _queryResults.Clear();
            _world.Index.Query(GetViewArea(self?.Area.X ?? 0, self?.Area.Y ?? 0), _queryResults);
            var update = tracker.Update(_queryResults, self, _logger);
            _queryResults.Clear();
            return update;
        }, cancellationToken);

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

    private static GameArea GetViewArea(int centerX, int centerY)
    {
        var minimumX = Math.Max(int.MinValue, (long)centerX - GameViewTracker.ViewRadius);
        var maximumX = Math.Min(int.MaxValue, (long)centerX + GameViewTracker.ViewRadius);
        var minimumY = Math.Max(int.MinValue, (long)centerY - GameViewTracker.ViewRadius);
        var maximumY = Math.Min(int.MaxValue, (long)centerY + GameViewTracker.ViewRadius);
        return new GameArea((int)minimumX, (int)minimumY, (int)(maximumX - minimumX + 1), (int)(maximumY - minimumY + 1));
    }
}
