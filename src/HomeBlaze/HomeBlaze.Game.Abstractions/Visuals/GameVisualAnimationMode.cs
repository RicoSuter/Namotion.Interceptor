namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// How the animation of a model part plays.
/// </summary>
public enum GameVisualAnimationMode
{
    /// <summary>
    /// Plays the animation in a loop.
    /// </summary>
    Loop,

    /// <summary>
    /// Plays the animation once whenever the part's animation changes and holds its last frame.
    /// </summary>
    Once
}
