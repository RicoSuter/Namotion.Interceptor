using HomeBlaze.Game.Abstractions;
using HomeBlaze.Game.Simulation;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game;

public partial class Game
{
    /// <summary>
    /// The most characters a name that a player gives its client may have.
    /// </summary>
    internal const int MaximumNameLength = 50;

    /// <summary>
    /// The message with which renaming and editing are refused for a caller that is not a player of this game.
    /// </summary>
    internal const string NoPlayerActorMessage = "Only a player in the game can edit their name and look.";

    /// <summary>
    /// How often the game reconciles its players without a signal.
    /// </summary>
    internal static readonly TimeSpan HousekeepingInterval = TimeSpan.FromSeconds(5);

    // Never disposed: change observers may still signal a disposed game, and without AvailableWaitHandle the semaphore
    // holds nothing to release.
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// Gets the name of the client of the caller's player, or null while that client is anonymous or the caller is not a
    /// player of this game.
    /// </summary>
    internal string? GetClientName(GameCaller? caller)
    {
        if (GetActor(caller) is null || _clients.GetClients() is not { } clients)
        {
            return null;
        }

        foreach (var client in clients)
        {
            if (client.Id == caller!.ClientId)
            {
                return client.IsNamed ? client.Title : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Names the client of the caller's player, which keeps the player when its browser closes; the player takes the
    /// name with the reconcile that this queues.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or longer than <see cref="MaximumNameLength"/> after trimming.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, or its client is gone.</exception>
    internal async Task RenameAsync(GameCaller? caller, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > MaximumNameLength)
        {
            throw new ArgumentException($"A name has at most {MaximumNameLength} characters.", nameof(name));
        }

        if (GetActor(caller) is null)
        {
            throw new InvalidOperationException(NoPlayerActorMessage);
        }

        await _clients.RenameAsync(caller!.ClientId, trimmed, cancellationToken);

        // Not every directory reports the rename as a client change.
        PostReconcile();
    }

    /// <summary>
    /// Changes the caller's player: renames its client when <paramref name="name"/> is not empty and differs from the
    /// current name of the client, and sets the person and shirt color, where null shows the default of the client.
    /// An empty name keeps the name.
    /// </summary>
    /// <exception cref="ArgumentException">The name is longer than <see cref="MaximumNameLength"/> after trimming.</exception>
    /// <exception cref="InvalidOperationException">The caller is not a player of this game, or its client is gone.</exception>
    internal async Task EditAsync(GameCaller? caller, string? name, string? character, string? color, CancellationToken cancellationToken)
    {
        if (GetActor(caller) is not { } player)
        {
            throw new InvalidOperationException(NoPlayerActorMessage);
        }

        // An unchanged name is not written, because a rename saves the configuration of the installation.
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != GetClientName(caller))
        {
            await RenameAsync(caller, name, cancellationToken);
        }

        // Ordinary subject writes; the facade hands them to the simulation like any outside write.
        player.Character = character;
        player.Color = color;
    }

    /// <summary>
    /// Makes the housekeeping loop reconcile the players soon; never blocks, so change observers may call it.
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

    /// <summary>
    /// Reads the clients now and queues a reconcile of the players with them; does nothing while there is no installation.
    /// </summary>
    internal void PostReconcile()
    {
        if (_clients.GetClients() is { } clients)
        {
            _simulation.Post(() => LogJoined(_world.Reconcile(clients)));
        }
    }

    private async Task RunHousekeepingLoopAsync(CancellationToken cancellationToken)
    {
        // One wait outlives the passes that a delay ends; a new wait per pass would leave the old one queued to take the next signal.
        Task? signalTask = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PostReconcile();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Updating the players of the game {Game} failed", Title);
            }

            // WhenAny does not throw; a canceled wait ends the loop through the condition.
            signalTask ??= _signal.WaitAsync(cancellationToken);
            var completed = await Task.WhenAny(signalTask, Task.Delay(HousekeepingInterval, _timeProvider, cancellationToken));
            if (completed == signalTask)
            {
                signalTask = null;
            }
        }
    }

    private void LogJoined(List<PlayerEntity>? joined)
    {
        foreach (var player in joined ?? [])
        {
            _logger.LogDebug("{Client} joined the game {Game} at {X}, {Y}", player.ClientTitle, Title, player.Area.X, player.Area.Y);
        }
    }
}
