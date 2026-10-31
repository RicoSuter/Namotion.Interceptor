namespace HomeBlaze.Game.Visuals;

/// <summary>
/// The geometry of a <see cref="GameVisualPart"/>.
/// </summary>
public enum GameVisualShape
{
    /// <summary>
    /// A box filling the part's offset and size, textured or colored.
    /// </summary>
    Box,

    /// <summary>
    /// A glTF model from <see cref="GameVisualPart.Model"/>, scaled to fit the part's size.
    /// </summary>
    Model
}
