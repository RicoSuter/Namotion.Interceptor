namespace HomeBlaze.Game.Visuals;

/// <summary>
/// A point on the ground in tiles: X grows to the east and Z to the north.
/// </summary>
public readonly record struct GameVisualPoint(double X, double Z);
