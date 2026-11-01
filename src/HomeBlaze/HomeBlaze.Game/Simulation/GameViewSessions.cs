using HomeBlaze.Game.View;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// The view sessions of the pages of one game. Touched only on the simulation thread, except <see cref="OpenAsync"/>.
/// </summary>
internal sealed class GameViewSessions
{
    private readonly List<GameViewSession> _sessions = [];
    private readonly GameSimulation _simulation;
    private readonly GameWorldState _world;
    private readonly ILogger _logger;
    private readonly Action<(GameViewSession Session, long PublishVersion)> _update;

    public GameViewSessions(GameSimulation simulation, GameWorldState world, ILogger logger)
    {
        _simulation = simulation;
        _world = world;
        _logger = logger;
        _update = Update;
    }

    /// <summary>
    /// Opens a session that delivers to the sink, in every tick, what changed within
    /// <see cref="GameViewTracker.ViewRadius"/> of the client's player, or of the origin while the client has none,
    /// together with the panel state; its first delivery holds everything in view and comes in the tick that opens it.
    /// Disposing the session ends it; setting <see cref="GameViewSession.ClientId"/> makes it follow another client.
    /// </summary>
    /// <returns>A faulted task with a <see cref="GameStoppedException"/> once the game has stopped.</returns>
    public Task<GameViewSession> OpenAsync(string? clientId, IGameViewSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return _simulation.InvokeAsync(() =>
        {
            var session = new GameViewSession(clientId, sink);
            _sessions.Add(session);
            return session;
        }, cancellationToken);
    }

    /// <summary>
    /// Drops the disposed sessions and updates the others; a session that throws is logged and leaves the others alone.
    /// </summary>
    public void Update(long publishVersion)
    {
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            if (session.IsDisposed)
            {
                _sessions.RemoveAt(i);
                continue;
            }

            _simulation.RunLogged(_update, (session, publishVersion));
        }
    }

    public void RequestFullRefresh()
    {
        foreach (var session in _sessions)
        {
            session.RequestFullRefresh();
        }
    }

    private void Update((GameViewSession Session, long PublishVersion) state) =>
        state.Session.Update(_world, state.PublishVersion, _logger);
}
