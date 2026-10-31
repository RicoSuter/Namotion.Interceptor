namespace HomeBlaze.Game.Visuals;

/// <summary>
/// A point or a size in tiles: X grows to the east, Y upward and Z to the north. As a size, X is the width, Y the height
/// and Z the depth.
/// </summary>
public readonly record struct GameVisualVector(double X, double Y, double Z);
