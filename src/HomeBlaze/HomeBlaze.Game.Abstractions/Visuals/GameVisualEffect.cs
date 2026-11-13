namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// A motion the browser adds to a <see cref="GameVisualPart"/>.
/// </summary>
public enum GameVisualEffect
{
    /// <summary>
    /// No motion.
    /// </summary>
    None,

    /// <summary>
    /// Bends the part sideways in proportion to the square of the height above the object's base, as wind moves a tree.
    /// </summary>
    Wind
}
