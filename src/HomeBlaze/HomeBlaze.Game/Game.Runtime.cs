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

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = ((IInterceptorSubject)this).Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);

        RequestIndexRebuild();
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

            try
            {
                await Task.WhenAny(_signal.WaitAsync(stoppingToken), Task.Delay(HousekeepingInterval, _timeProvider, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // Runs synchronously inside the write, possibly on a thread that holds another subject's lock, so it only signals
    // or marks the index stale and never waits for the game.
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
