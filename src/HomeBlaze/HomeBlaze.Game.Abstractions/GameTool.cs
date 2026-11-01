using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// A tool of a game, whose operations the game page lists for every player, for example to build or plant objects. An
/// operation takes a nullable <c>[FromServices] GameCaller</c> parameter for the acting player.
/// </summary>
[InterceptorSubject]
public abstract partial class GameTool : IConfigurable
{
    /// <summary>
    /// The game this tool is part of.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tool is not part of a game.</exception>
    protected IGameWorld World =>
        this.TryGetFirstParent<IGameWorld>() ?? throw new InvalidOperationException("The tool is not part of a game.");

    public virtual Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
