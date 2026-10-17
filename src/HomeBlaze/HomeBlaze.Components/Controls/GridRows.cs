using System.Globalization;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Gets the rows of a <see cref="GridLayout"/>: shares of its height, or as high as a column is wide times an aspect ratio.
/// </summary>
internal static class GridRows
{
    /// <summary>The gap between the rows and between the columns, in pixels.</summary>
    public const int Gap = 16;

    /// <summary>
    /// Gets the value of grid-template-rows, or null with an error when the aspect ratio is invalid. With an aspect
    /// ratio W:H a row is as high as a column is wide times H/W; 100cqw is the width of the grid, which sits in an
    /// element with container-type inline-size.
    /// </summary>
    public static string? GetTemplate(int rows, int columns, string? cellAspectRatio, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(cellAspectRatio))
        {
            return string.Create(CultureInfo.InvariantCulture, $"repeat({rows}, 1fr)");
        }

        if (!CardSize.TryParseAspectRatio(cellAspectRatio, out var width, out var height))
        {
            error = $"CellAspectRatio must look like 1:1: {cellAspectRatio}";
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"repeat({rows}, calc((100cqw - {(columns - 1) * Gap}px) / {columns} * {height} / {width}))");
    }
}
