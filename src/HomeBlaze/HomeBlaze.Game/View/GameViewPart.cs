using System.Text.Json.Serialization;
using HomeBlaze.Game.Visuals;

namespace HomeBlaze.Game.View;

/// <summary>
/// A part of an object's visual as the browser draws it. The names are short because every page receives the parts of
/// every object in view.
/// </summary>
internal sealed class GameViewPart
{
    [JsonPropertyName("k")]
    public required string Shape { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }

    [JsonPropertyName("w")]
    public double Width { get; init; }

    [JsonPropertyName("h")]
    public double Height { get; init; }

    [JsonPropertyName("d")]
    public double Depth { get; init; }

    [JsonPropertyName("px")]
    public double PivotX { get; init; }

    [JsonPropertyName("pz")]
    public double PivotZ { get; init; }

    [JsonPropertyName("r")]
    public double Rotation { get; init; }

    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Texture { get; init; }

    [JsonPropertyName("ts")]
    public double TextureScale { get; init; }

    [JsonPropertyName("c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; init; }

    [JsonPropertyName("m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("e")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effect { get; init; }

    [JsonPropertyName("a")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Animation { get; init; }

    public static GameViewPart[] FromVisual(GameVisual visual)
    {
        var parts = new GameViewPart[visual.Parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = From(visual.Parts[index]);
        }

        return parts;
    }

    private static GameViewPart From(GameVisualPart part) => new()
    {
        Shape = part.Shape == GameVisualShape.Model ? "model" : "box",
        X = part.Offset.X,
        Y = part.Offset.Y,
        Z = part.Offset.Z,
        Width = part.Size.X,
        Height = part.Size.Y,
        Depth = part.Size.Z,
        PivotX = part.Pivot.X,
        PivotZ = part.Pivot.Z,
        Rotation = part.Rotation,
        Texture = part.Texture,
        TextureScale = part.TextureScale,
        Color = part.Color,
        Model = part.Model,
        Effect = part.Effect == GameVisualEffect.Wind ? "wind" : null,
        Animation = part.Animation
    };
}
