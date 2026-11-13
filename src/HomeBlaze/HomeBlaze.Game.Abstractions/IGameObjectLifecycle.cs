namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// What a game calls on its objects; object types override the protected members of <see cref="GameObject"/> instead.
/// </summary>
public interface IGameObjectLifecycle
{
    /// <summary>
    /// Called on the game's own thread when the object enters the world.
    /// </summary>
    void OnAddedToWorld();
}
