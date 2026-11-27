using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using Namotion.Interceptor;
using Namotion.Interceptor.Interceptors;

namespace HomeBlaze.Game;

public partial class Game
{
    // Called for every start, also after a stop: work given to a stopped game was dropped, so the game takes over its
    // subjects again.
    private void Start()
    {
        var isRestarting = _simulation.HasStopped;
        if (isRestarting)
        {
            // Left over when a stop timed out while a save paused publishing; the restart's take-over drops their objects.
            _simulation.RunExclusive(AbandonPlacements);

            // The stop dropped the completion of a save that ran then; StopAsync awaited it.
            _simulation.Restart();
            _isSaving = false;
            _publisher.IsPaused = false;
            ResetVolumeTableBuild();
        }

        if (Interlocked.Exchange(ref _isStarted, 1) == 0)
        {
            ((IInterceptorSubject)this).Context.TryAddService<IWriteInterceptor>(() => new GameWriteInterceptor(), existing => existing is GameWriteInterceptor);
        }

        // Reads the sets in the tick, after the interceptor is in place: a write that lands before that tick is read here,
        // one after it queues its own take-over, which runs later.
        _simulation.Post(() =>
        {
            _world.TakeOverPlayers(Players, isRestart: isRestarting);
            OnObjectsTakenOver(_world.TakeOverRegions(Regions));

            // The stop dropped the schedules, so every object of a restarted game sets them up again; one whose
            // OnAddedToWorld is pending gets it with the next publish anyway.
            if (isRestarting)
            {
                RunAddedToWorldOfKeptObjects();
                _viewSessions.RequestFullRefresh();
            }
        });
    }

    /// <summary>
    /// Takes in a write that landed on this game, one of its regions or one of its objects; called by <see cref="GameWriteInterceptor"/>
    /// inside the write, so it never waits for the simulation.
    /// </summary>
    internal void OnWritten<TProperty>(IInterceptorSubject subject, string name, TProperty value, bool isOnSimulation)
    {
        if (isOnSimulation && IsPublishersOwnWrite(subject, name, value))
        {
            return;
        }

        switch (subject)
        {
            case Game:
                OnGameWritten(name, value);
                break;
            case GamePlayer player:
                OnPlayerWritten(player, name, value);
                break;
            case GameRegion region:
                OnRegionWritten(region, name, value);
                break;
            // Read again at the end of the tick, so a schedule or handler writing several properties is read once.
            case GameObject gameObject when isOnSimulation:
                _world.ChangedSubjects.Add(gameObject);
                break;

            // Read again in queue order, so a command queued after the write sees it.
            case GameObject gameObject:
                _simulation.Post(() => _world.RefreshSubject(gameObject));
                break;
        }
    }

    // A set is compared by reference, so an observer that writes another set of the game during a publish is not ignored.
    private bool IsPublishersOwnWrite<TProperty>(IInterceptorSubject subject, string name, TProperty value) => subject switch
    {
        Game or GameRegion => _publisher.IsOwnWrite(subject, name, value),
        GamePlayer => _publisher.IsOwnWrite(subject, name, null),
        _ => false
    };

    private void OnGameWritten<TProperty>(string name, TProperty value)
    {
        if (name == nameof(Players) && value is Dictionary<string, GamePlayer> players)
        {
            _simulation.Post(() =>
            {
                _world.TakeOverPlayers(players);
                _world.IsSaveRequested = true;
            });
            SignalReconcile();
        }
        else if (name == nameof(Regions) && value is Dictionary<string, GameRegion> regions)
        {
            _simulation.Post(() =>
            {
                OnObjectsTakenOver(_world.TakeOverRegions(regions));
                _world.IsSaveRequested = true;
            });
        }
    }

    private void OnRegionWritten<TProperty>(GameRegion region, string name, TProperty value)
    {
        if (name == nameof(GameRegion.Objects) && value is Dictionary<string, GameObject> objects)
        {
            _simulation.Post(() =>
            {
                OnObjectsTakenOver(_world.TakeOverRegion(region, objects));
                _world.IsSaveRequested = true;
            });
        }
    }

    private void OnPlayerWritten<TProperty>(GamePlayer player, string name, TProperty value)
    {
        switch (name)
        {
            case nameof(GamePlayer.X) when value is int x:
                _simulation.Post(() => _world.Teleport(player, x, null));
                break;
            case nameof(GamePlayer.Y) when value is int y:
                _simulation.Post(() => _world.Teleport(player, null, y));
                break;
            case nameof(GamePlayer.Facing) when value is GameDirection facing:
                _simulation.Post(() => _world.Turn(player, facing));
                break;
        }
    }
}
