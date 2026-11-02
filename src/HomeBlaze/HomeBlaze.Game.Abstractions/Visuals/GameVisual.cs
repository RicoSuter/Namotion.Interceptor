using System.Collections.Immutable;

namespace HomeBlaze.Game.Abstractions;

/// <summary>
/// How an object looks: parts placed relative to the south-west corner of its area at ground level. Compares by
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

    /// <summary>
    /// Creates a visual of the parts; a default array becomes no parts.
    /// </summary>
    public GameVisual(ImmutableArray<GameVisualPart> parts)
    {
        Parts = parts.IsDefault ? [] : parts;
    }

    /// <summary>
    /// The parts in drawing order; never a default array.
    /// </summary>
    public ImmutableArray<GameVisualPart> Parts { get; }

    /// <summary>
    /// Creates a visual of one box of the size at the corner of the area.
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
