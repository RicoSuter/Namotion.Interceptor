namespace HomeBlaze.Game.Tools;

/// <summary>
/// Where an object fits on the grid: the south-west corner of its footprint, its size in tiles along X (width) and Y
/// (depth), and the direction the placing player faces.
/// </summary>
public sealed record GamePlacement(int X, int Y, int Width, int Depth, GameDirection Facing);
