namespace HomeBlaze.Game.Visuals;

/// <summary>
/// A motion the browser adds to a <see cref="GameVisualPart"/>.
/// </summary>
public enum GameVisualEffect
{
    None,

    /// <summary>
    /// Bends the part sideways in proportion to the height above the object's base, as wind moves a tree.
    /// </summary>
    Wind
}
