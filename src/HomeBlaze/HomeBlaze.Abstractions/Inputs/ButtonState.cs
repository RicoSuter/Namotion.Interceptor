namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Represents the state of a button press.
/// </summary>
public enum ButtonState
{
    None,
    Down,
    Repeat,
    Release,
    LongRelease
}
