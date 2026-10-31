using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Game.Visuals;

/// <summary>
/// Replaces a visual that the browser must not get by the default box of the object.
/// </summary>
internal static class GameVisualValidator
{
    private const int MaximumAnimationLength = 64;
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly ConcurrentDictionary<Type, bool> WarnedTypes = new();

    /// <summary>
    /// Gets the visual when it is valid, else the default box of the object, warning once per object type and process.
    /// </summary>
    public static GameVisual GetValidVisual(GameObject gameObject, GameVisual? visual, ILogger logger)
    {
        if (visual is not null && IsValid(visual))
        {
            return visual;
        }

        if (WarnedTypes.TryAdd(gameObject.GetType(), true))
        {
            logger.LogWarning("The visual of {Type} is invalid, so its objects show as gray boxes", gameObject.GetType().FullName);
        }

        return GameVisual.Box(gameObject.Width, gameObject.Height, gameObject.Depth);
    }

    private static bool IsValid(GameVisual visual)
    {
        if (visual.Parts.Length > GameVisual.MaximumParts)
        {
            return false;
        }

        foreach (var part in visual.Parts)
        {
            if (!IsValid(part))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValid(GameVisualPart? part) => part is not null && IsValidPlacement(part) && IsValidLook(part);

    private static bool IsValidPlacement(GameVisualPart part) =>
        IsInRange(part.Offset) && IsValidSize(part.Size) && IsValidTurn(part);

    private static bool IsValidTurn(GameVisualPart part) =>
        IsInRange(part.Pivot.X) && IsInRange(part.Pivot.Z) && double.IsFinite(part.Rotation);

    private static bool IsValidLook(GameVisualPart part) =>
        Enum.IsDefined(part.Shape) && Enum.IsDefined(part.Effect) && IsValidSurface(part) && IsValidSource(part);

    private static bool IsValidSurface(GameVisualPart part) =>
        IsValidTextureScale(part.TextureScale) && IsValidColor(part.Color) && IsValidAnimation(part.Animation);

    private static bool IsValidSource(GameVisualPart part) =>
        IsAssetUrl(part.Texture) && IsAssetUrl(part.Model) && (part.Shape != GameVisualShape.Model || part.Model is not null);

    private static bool IsValidTextureScale(double scale) => scale is > 0 and <= GameVisual.MaximumExtent;

    private static bool IsValidColor(string? color) =>
        color is null || (color.Length == 7 && color[0] == '#' && !color.AsSpan(1).ContainsAnyExcept(HexDigits));

    private static bool IsValidAnimation(string? animation) => animation is null || animation.Length <= MaximumAnimationLength;

    private static bool IsAssetUrl(string? url) =>
        url is null || (url.StartsWith(GameAssets.ContentPrefix, StringComparison.Ordinal) && !url.Contains("..", StringComparison.Ordinal));

    private static bool IsValidSize(GameVisualVector size) => IsInRange(size) && size.X >= 0 && size.Y >= 0 && size.Z >= 0;

    private static bool IsInRange(GameVisualVector vector) => IsInRange(vector.X) && IsInRange(vector.Y) && IsInRange(vector.Z);

    // NaN fails both comparisons, so it is out of range too.
    private static bool IsInRange(double value) => value is >= -GameVisual.MaximumExtent and <= GameVisual.MaximumExtent;
}
