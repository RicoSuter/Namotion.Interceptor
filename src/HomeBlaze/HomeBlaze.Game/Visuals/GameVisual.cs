using System.Collections.Immutable;

namespace HomeBlaze.Game.Visuals;

/// <summary>
/// How an object looks: parts placed relative to the south-west corner of its footprint at ground level. Compares by
/// value, including its parts, so an unchanged description is not sent to the browser again.
/// </summary>
public sealed record GameVisual
{
    /// <summary>
    /// The color of a box without texture and color, which is also the default look of an object.
    /// </summary>
    public const string DefaultColor = "#8d8577";

    /// <summary>
    /// The most parts a visual may have; an object whose visual has more shows as its default box.
    /// </summary>
    public const int MaximumParts = 16;

    /// <summary>
    /// The largest absolute coordinate of an offset, size or pivot in tiles; an object whose visual exceeds it shows as
    /// its default box.
    /// </summary>
    public const double MaximumExtent = 1024;

    public GameVisual(ImmutableArray<GameVisualPart> parts)
    {
        Parts = parts.IsDefault ? [] : parts;
    }

    public ImmutableArray<GameVisualPart> Parts { get; }

    /// <summary>
    /// Creates a visual of one box of the size at the corner of the footprint.
    /// </summary>
    public static GameVisual Box(double width, double height, double depth, string color = DefaultColor) =>
        new([new GameVisualPart { Shape = GameVisualShape.Box, Size = new GameVisualVector(width, height, depth), Color = color }]);

    public bool Equals(GameVisual? other) =>
        ReferenceEquals(this, other) || (other is not null && Parts.AsSpan().SequenceEqual(other.Parts.AsSpan()));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in Parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }
}
