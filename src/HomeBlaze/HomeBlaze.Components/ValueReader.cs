using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
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
    /// Reads the value at the path and applies the map. A path that ends at no property but leads to a subject, such as
    /// the name of a block, binds that subject, so that the cases of the map can read its states.
    /// </summary>
    /// <param name="resolver">The resolver of the path and the map.</param>
    /// <param name="widget">The subject the path and the map are relative to, usually the widget itself.</param>
    /// <param name="path">The path of the property or the subject. Null, empty or leading nowhere gives a missing reading.</param>
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
        return ReadCore(ValueBinding.FromPath(resolver, widget, path), valueMap, mapError, resolver, timeZone);
    }

    /// <summary>
    /// Reads a property that <see cref="ResolveProperty"/> already resolved and applies a map that
    /// <see cref="ResolveMap"/> already resolved, with the same text, color and missing value as <see cref="Read"/>.
    /// </summary>
    /// <param name="resolver">The resolver of the paths of the cases.</param>
    /// <param name="property">The property, or null for a missing reading.</param>
    /// <param name="valueMap">The resolved map, or null for none.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ValueReading ReadResolved(
        SubjectPathResolver resolver,
        RegisteredSubjectProperty? property,
        ValueMap? valueMap,
        ITimeZoneDisplay? timeZone)
    {
        return ReadCore(property is null ? default : ValueBinding.FromProperty(property), valueMap, mapError: null, resolver, timeZone);
    }

    /// <summary>
    /// Reads the property with the name on a subject and applies a map that <see cref="ResolveMap"/> already
    /// resolved, with the same text, color and missing value as <see cref="Read"/> gives for a path. The subject is the
    /// <c>subject</c> of the cases.
    /// </summary>
    /// <param name="resolver">The resolver of the paths of the cases.</param>
    /// <param name="subject">The subject that has the property, for example a row of a table.</param>
    /// <param name="propertyName">The name of the property. Null, empty, unknown or a property that is not
    /// <see cref="HomeBlaze.Services.SubjectRegistryExtensions.IsDisplayable"/>, as for a path, gives a missing reading.</param>
    /// <param name="valueMap">The resolved map, or null for none.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ValueReading ReadProperty(
        SubjectPathResolver resolver,
        IInterceptorSubject subject,
        string? propertyName,
        ValueMap? valueMap,
        ITimeZoneDisplay? timeZone)
    {
        var property = string.IsNullOrEmpty(propertyName) ? null : subject.TryGetRegisteredProperty(propertyName);
        var binding = property is not null && property.IsDisplayable() ? ValueBinding.FromProperty(property) : default;
        return ReadCore(binding, valueMap, mapError: null, resolver, timeZone);
    }

    private static ValueReading ReadCore(
        in ValueBinding binding,
        ValueMap? valueMap,
        string? mapError,
        SubjectPathResolver resolver,
        ITimeZoneDisplay? timeZone)
    {
        // A path that leads nowhere tries no case, so that a default case does not hide a mistake in the path.
        var matched = binding.IsEmpty ? null : valueMap?.FindCase(binding, resolver);
        var value = binding.Value;
        if (matched is null && value is null)
        {
            return new ValueReading(binding.Property, null, MissingText, ValueColor.Default, null, mapError);
        }

        return new ValueReading(
            binding.Property,
            value,
            GetText(binding, matched, resolver, timeZone),
            matched?.GetColor() ?? ValueColor.Default,
            string.IsNullOrEmpty(matched?.Icon) ? null : matched.Icon,
            mapError)
        {
            TextColor = matched?.GetTextColor() ?? ValueColor.Default,
            HasCase = matched is not null
        };
    }

    /// <summary>
    /// Gets the text of a value: that of the matched case, otherwise the formatted value, and
    /// <see cref="MissingText"/> for a missing one.
    /// </summary>
    internal static string GetText(in ValueBinding binding, ValueMapCase? matched, SubjectPathResolver resolver, ITimeZoneDisplay? timeZone)
    {
        if (matched?.GetText(binding, resolver, timeZone) is { } text)
        {
            return text;
        }

        if (binding.Value is not { } value)
        {
            return MissingText;
        }

        return binding.Property is { } property
            ? property.GetPropertyDisplayValue(value, timeZone)
            : StateUnitExtensions.FormatDisplayValue(value, timeZone);
    }

    /// <summary>
    /// Reads the value at the path alone, for a widget that shows no text of it.
    /// Null when the path is null, empty or leads nowhere.
    /// </summary>
    public static object? ReadValue(SubjectPathResolver resolver, IInterceptorSubject widget, string? path) =>
        ResolveProperty(resolver, widget, path)?.GetValue();

    /// <summary>
    /// Gets the label of a value or a control: the given one, otherwise the title the property declares, otherwise its
    /// name, and the path when the path leads nowhere.
    /// </summary>
    public static string GetLabel(RegisteredSubjectProperty? property, string? label, string? path)
    {
        if (!string.IsNullOrEmpty(label))
        {
            return label;
        }

        return property?.GetDisplayName() ?? path ?? string.Empty;
    }

    /// <summary>
    /// Gets the color of an indicator: that of the matched case, and without a map the primary color for true.
    /// </summary>
    public static ValueColor GetIndicatorColor(ValueReading reading, string? map)
    {
        if (string.IsNullOrEmpty(map) && reading.Value is true)
        {
            return MudBlazor.Color.Primary;
        }

        return reading.Color;
    }

    /// <summary>
    /// Gets where a number stands between minimum and maximum, from 0 to 100, or null when the value is
    /// no number (NaN included) or the range is empty or has a NaN bound.
    /// </summary>
    public static double? GetProgressPercent(object? value, double minimum, double maximum)
    {
        if (double.IsNaN(minimum) || double.IsNaN(maximum) || maximum <= minimum || !NumberValues.TryGetNumber(value, out var number))
        {
            return null;
        }

        return Math.Clamp((number - minimum) / (maximum - minimum) * FullPercent, 0, FullPercent);
    }

    /// <summary>
    /// Resolves the name or path of a <see cref="ValueMap"/>. Null or empty gives no map and no error.
    /// </summary>
    /// <param name="resolver">The resolver of the map.</param>
    /// <param name="widget">The subject the map is relative to, usually the widget itself.</param>
    /// <param name="map">The name or path of the map as written.</param>
    /// <param name="error">The widget argument error when the map leads nowhere or to no <see cref="ValueMap"/>, or
    /// when the <see cref="ValueMapCase.Color"/> or the <see cref="ValueMapCase.TextColor"/> of one of its cases is no
    /// color, or when a case has a condition or a text with a mistake, or a case without Value and When is not the last one,
    /// otherwise null.</param>
    public static ValueMap? ResolveMap(SubjectPathResolver resolver, IInterceptorSubject widget, string? map, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(map))
        {
            return null;
        }

        if (ResolveSubject(resolver, widget, map) is ValueMap valueMap)
        {
            var cases = valueMap.Cases;
            for (var index = 0; index < cases.Length; index++)
            {
                var valueCase = cases[index];
                if (!valueCase.IsColorValid())
                {
                    error = ValueColor.GetError(valueCase.Color);
                    return null;
                }

                if (!valueCase.IsTextColorValid())
                {
                    error = ValueColor.GetError(valueCase.TextColor);
                    return null;
                }

                if (valueCase.GetExpressionError() is { } expressionError)
                {
                    error = $"Case {index + 1}, {expressionError}";
                    return null;
                }

                if (valueCase.IsDefault && index < cases.Length - 1)
                {
                    error = $"Case {index + 1} has no Value and no When, so it matches every value and the cases after it never match. Make it the last case.";
                    return null;
                }
            }

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
            return resolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, widget);
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
