using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

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
        return ReadCore(ResolveProperty(resolver, widget, path), valueMap, mapError, timeZone);
    }

    /// <summary>
    /// Reads a property that <see cref="ResolveProperty"/> already resolved and applies the map, with the same
    /// text, colour and missing value as <see cref="Read"/> gives for its path.
    /// </summary>
    /// <param name="resolver">The resolver of the map.</param>
    /// <param name="widget">The subject the map is relative to, usually the widget itself.</param>
    /// <param name="property">The property, or null for a missing reading.</param>
    /// <param name="map">The name or path of a <see cref="ValueMap"/>, or null or empty for none.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ValueReading ReadResolved(
        SubjectPathResolver resolver,
        IInterceptorSubject widget,
        RegisteredSubjectProperty? property,
        string? map,
        ITimeZoneDisplay? timeZone)
    {
        var valueMap = ResolveMap(resolver, widget, map, out var mapError);
        return ReadCore(property, valueMap, mapError, timeZone);
    }

    /// <summary>
    /// Reads the property with the name on a subject and applies the map, with the same text, colour and
    /// missing value as <see cref="Read"/> gives for a path.
    /// </summary>
    /// <param name="resolver">The resolver of the map.</param>
    /// <param name="widget">The subject the map is relative to, usually the widget or its slot block.</param>
    /// <param name="subject">The subject that has the property, for example a row of a table.</param>
    /// <param name="propertyName">The name of the property. Null, empty or unknown gives a missing reading.</param>
    /// <param name="map">The name or path of a <see cref="ValueMap"/>, or null or empty for none.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ValueReading ReadProperty(
        SubjectPathResolver resolver,
        IInterceptorSubject widget,
        IInterceptorSubject subject,
        string? propertyName,
        string? map,
        ITimeZoneDisplay? timeZone)
    {
        var valueMap = ResolveMap(resolver, widget, map, out var mapError);
        return ReadProperty(subject, propertyName, valueMap, mapError, timeZone);
    }

    /// <summary>
    /// Reads the property with the name on a subject and applies a map that <see cref="ResolveMap"/> already
    /// resolved, with the same reading as the overload that takes the name or path of the map.
    /// </summary>
    /// <param name="subject">The subject that has the property, for example a row of a table.</param>
    /// <param name="propertyName">The name of the property. Null, empty or unknown gives a missing reading.</param>
    /// <param name="valueMap">The resolved map, or null for none.</param>
    /// <param name="mapError">The error that <see cref="ResolveMap"/> gave for the map, or null.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ValueReading ReadProperty(
        IInterceptorSubject subject,
        string? propertyName,
        ValueMap? valueMap,
        string? mapError,
        ITimeZoneDisplay? timeZone)
    {
        var property = string.IsNullOrEmpty(propertyName) ? null : subject.TryGetRegisteredProperty(propertyName);
        return ReadCore(property, valueMap, mapError, timeZone);
    }

    private static ValueReading ReadCore(
        RegisteredSubjectProperty? property,
        ValueMap? valueMap,
        string? mapError,
        ITimeZoneDisplay? timeZone)
    {
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
    /// Reads the value at the path alone, for a widget that shows no text of it.
    /// Null when the path is null, empty or leads nowhere.
    /// </summary>
    public static object? ReadValue(SubjectPathResolver resolver, IInterceptorSubject widget, string? path) =>
        ResolveProperty(resolver, widget, path)?.GetValue();

    /// <summary>
    /// Gets the label of a value: the given one, otherwise the title the property declares, otherwise its name.
    /// </summary>
    public static string GetLabel(ValueReading? reading, string? label) =>
        GetControlLabel(reading?.Property, label, path: null);

    /// <summary>
    /// Gets the label of a control: the given one, otherwise the title the property declares, otherwise its name,
    /// and the path when the path leads nowhere.
    /// </summary>
    public static string GetControlLabel(RegisteredSubjectProperty? property, string? label, string? path)
    {
        if (!string.IsNullOrEmpty(label))
        {
            return label;
        }

        return property?.GetDisplayName() ?? path ?? string.Empty;
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
    /// no number (NaN included) or the range is empty or has a NaN bound.
    /// </summary>
    public static double? GetProgressPercent(object? value, double minimum, double maximum)
    {
        if (double.IsNaN(minimum) || double.IsNaN(maximum) || maximum <= minimum || !IsNumber(value))
        {
            return null;
        }

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (double.IsNaN(number))
        {
            return null;
        }

        return Math.Clamp((number - minimum) / (maximum - minimum) * FullPercent, 0, FullPercent);
    }

    private static bool IsNumber(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>
    /// Resolves the name or path of a <see cref="ValueMap"/>. Null or empty gives no map and no error.
    /// </summary>
    /// <param name="resolver">The resolver of the map.</param>
    /// <param name="widget">The subject the map is relative to, usually the widget itself.</param>
    /// <param name="map">The name or path of the map as written.</param>
    /// <param name="error">The widget argument error when the map leads nowhere or to no <see cref="ValueMap"/>, otherwise null.</param>
    public static ValueMap? ResolveMap(SubjectPathResolver resolver, IInterceptorSubject widget, string? map, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(map))
        {
            return null;
        }

        if (ResolveSubject(resolver, widget, map) is ValueMap valueMap)
        {
            return valueMap;
        }

        error = $"Cannot resolve map: {map}";
        return null;
    }

    /// <summary>
    /// Resolves the path of a widget to a property, or to null when the path is null, empty, malformed or leads nowhere.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, IInterceptorSubject widget, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return resolver.ResolveScopedProperty(path, PathStyle.Canonical, widget);
        }
        catch
        {
            return null;
        }
    }

    // The path of a property and that of a map are written by the author of a page, and a malformed one
    // must not take the page down. This holds for ResolveProperty above as well.
    internal static IInterceptorSubject? ResolveSubject(SubjectPathResolver resolver, IInterceptorSubject widget, string path)
    {
        try
        {
            return resolver.ResolveScopedSubject(path, PathStyle.Canonical, widget);
        }
        catch
        {
            return null;
        }
    }
}
