namespace HomeBlaze.Game;

/// <summary>
/// The intervals of the game simulation; fixed in code for now, like <see cref="Voice.GameVoiceSettings"/>.
/// </summary>
internal static class GameSimulationSettings
{
    /// <summary>
    /// The time between two ticks of the simulation.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The shortest time between two publishes of the simulation's state to the subjects.
    /// </summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The shortest time between two saves of a changed world.
    /// </summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
}
