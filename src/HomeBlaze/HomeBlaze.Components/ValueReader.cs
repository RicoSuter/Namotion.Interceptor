using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// Reads the value of a value widget and decides what the widget shows for it.
/// </summary>
public static class ValueReader
{
    /// <summary>The text shown in place of a value that is missing.</summary>
    public const string MissingText = "–";

    private const double FullPercent = 100;

    /// <summary>
    /// Reads the value at the path and applies the map.
    /// </summary>
    /// <param name="resolver">The resolver of the path and the map.</param>
    /// <param name="widget">The subject the path and the map are relative to, usually the widget itself.</param>
    /// <param name="path">The path of the property. Null, empty or leading nowhere gives a missing reading.</param>
    /// <param name="map">The name or path of a <see cref="ValueMap"/>, or null or empty for none.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ValueReading Read(
        SubjectPathResolver resolver,
        IInterceptorSubject widget,
        string? path,
        string? map,
        ITimeZoneDisplay? timeZone)
    {
        var valueMap = ResolveMap(resolver, widget, map, out var mapError);

        var property = string.IsNullOrEmpty(path) ? null : TryResolve(() => resolver.ResolveScopedProperty(path, PathStyle.Canonical, widget));
        var value = property?.GetValue();
        if (property is null || value is null)
        {
            return new ValueReading(property, null, MissingText, Color.Default, null, mapError);
        }

        var matched = valueMap?.FindCase(RawValueFormatter.Format(value));
        var text = string.IsNullOrEmpty(matched?.Text)
            ? property.GetPropertyDisplayValue(value, timeZone)
            : matched.Text;

        return new ValueReading(
            property,
            value,
            text,
            matched?.Color ?? Color.Default,
            string.IsNullOrEmpty(matched?.Icon) ? null : matched.Icon,
            mapError);
    }

    /// <summary>
    /// Gets the label of a value: the given one, otherwise the title the property declares, otherwise its name.
    /// </summary>
    public static string GetLabel(ValueReading? reading, string? label)
    {
        if (!string.IsNullOrEmpty(label))
        {
            return label;
        }

        var property = reading?.Property;
        return property?.GetStateMetadata()?.Title ?? property?.Name ?? string.Empty;
    }

    /// <summary>
    /// Gets the colour of an indicator: that of the matched case, and without a map the primary colour for true.
    /// </summary>
    public static Color GetIndicatorColor(ValueReading reading, string? map)
    {
        if (string.IsNullOrEmpty(map) && reading.Value is true)
        {
            return Color.Primary;
        }

        return reading.Color;
    }

    /// <summary>
    /// Gets where a number stands between minimum and maximum, from 0 to 100, or null when the value is
    /// no number or the range is empty.
    /// </summary>
    public static double? GetProgressPercent(object? value, double minimum, double maximum)
    {
        if (maximum <= minimum || !IsNumber(value))
        {
            return null;
        }

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return Math.Clamp((number - minimum) / (maximum - minimum) * FullPercent, 0, FullPercent);
    }

    private static bool IsNumber(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static ValueMap? ResolveMap(SubjectPathResolver resolver, IInterceptorSubject widget, string? map, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(map))
        {
            return null;
        }

        if (TryResolve(() => resolver.ResolveScopedSubject(map, PathStyle.Canonical, widget)) is ValueMap valueMap)
        {
            return valueMap;
        }

        error = $"Cannot resolve map: {map}";
        return null;
    }

    // A path is written by the author of a page, and a malformed one must not take the page down.
    private static T? TryResolve<T>(Func<T?> resolve)
        where T : class
    {
        try
        {
            return resolve();
        }
        catch
        {
            return null;
        }
    }
}
