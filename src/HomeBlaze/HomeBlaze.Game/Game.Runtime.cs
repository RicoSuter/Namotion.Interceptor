using System.Reactive.Concurrency;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// How often the game reconciles and saves without a signal.
    /// </summary>
    internal static readonly TimeSpan HousekeepingInterval = TimeSpan.FromSeconds(5);

    // Never disposed: change observers may still signal a disposed game, and without AvailableWaitHandle the semaphore
    // holds nothing to release.
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// Makes the loop reconcile the players soon; never blocks, so change observers may call it.
    /// </summary>
    internal void SignalReconcile()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = ((IInterceptorSubject)this).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);

        RequestIndexRebuild();

        // One wait outlives the passes that a delay ends; a new wait per pass would leave the old one queued to take the next signal.
        Task? signalTask = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReconcilePlayers();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Updating the players of the game {Game} failed", Title);
            }

            try
            {
                await FlushPendingSaveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // WhenAny does not throw; a canceled wait ends the loop through the condition.
            signalTask ??= _signal.WaitAsync(stoppingToken);
            var completed = await Task.WhenAny(signalTask, Task.Delay(HousekeepingInterval, _timeProvider, stoppingToken));
            if (completed == signalTask)
            {
                signalTask = null;
            }
        }
    }

    // Runs synchronously inside the write while the change observable holds its own lock, which a write under _lock
    // waits for; so it only signals or marks the index stale and never takes _lock.
    private void OnPropertyChanged(SubjectPropertyChange change)
    {
        if (_clients.IsClientChange(change))
        {
            SignalReconcile();
            return;
        }

        // The game's own writes keep the index up to date themselves.
        if (_lock.IsHeldByCurrentThread)
        {
            return;
        }

        if (IsOutsideChange(change))
        {
            RequestIndexRebuild();

            // Reloaded players of connected clients join again right away.
            if (ReferenceEquals(change.Property.Subject, this) && change.Property.Name == nameof(Players))
            {
                SignalReconcile();
            }
        }
    }

    private bool IsOutsideChange(SubjectPropertyChange change)
    {
        var name = change.Property.Name;
        return change.Property.Subject switch
        {
            GameObject gameObject => IsFootprintProperty(name) && ReferenceEquals(gameObject.ParentGame, this),
            var subject => ReferenceEquals(subject, this) && IsCollectionProperty(name)
        };
    }

    private static bool IsFootprintProperty(string name) =>
        name is nameof(GameObject.X) or nameof(GameObject.Y) or nameof(GameObject.Width) or nameof(GameObject.Depth)
            || name == nameof(GamePlayer.IsConnected);

    private static bool IsCollectionProperty(string name) =>
        name is nameof(Players) or nameof(Objects);
}
