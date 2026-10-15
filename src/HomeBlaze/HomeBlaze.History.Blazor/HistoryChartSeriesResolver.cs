using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>How a series of a <see cref="HistoryChart"/> is drawn.</summary>
public enum HistoryChartSeriesKind
{
    /// <summary>The path leads nowhere.</summary>
    Missing,

    /// <summary>A number, drawn as a line.</summary>
    Line,

    /// <summary>A state, drawn as a band.</summary>
    Band
}

/// <summary>
/// A series of a <see cref="HistoryChart"/> with the property its path leads to.
/// </summary>
/// <param name="Label">The label in the legend.</param>
/// <param name="Color">The colour the series asks for, or null.</param>
/// <param name="Property">The property, or null when the path leads nowhere.</param>
/// <param name="Kind">How the series is drawn.</param>
/// <param name="Unit">The unit of the property.</param>
/// <param name="HasHistory">Whether the property is recorded.</param>
/// <param name="IsCumulative">Whether the property is a counter that only grows.</param>
public sealed record HistoryChartSeriesInfo(
    string Label,
    string? Color,
    RegisteredSubjectProperty? Property,
    HistoryChartSeriesKind Kind,
    StateUnit Unit,
    bool HasHistory,
    bool IsCumulative)
{
    /// <summary>Gets what the legend says about a series that has no history to show, or null.</summary>
    public string? Note =>
        Kind == HistoryChartSeriesKind.Missing ? "not found" :
        HasHistory ? null : "no history recorded";
}

/// <summary>
/// Resolves and checks the series of a <see cref="HistoryChart"/>.
/// </summary>
public static class HistoryChartSeriesResolver
{
    /// <summary>
    /// Resolves the path of every series relative to the series. Reads the properties of the series, so a
    /// caller in a tracking scope renders again when one changes.
    /// </summary>
    public static IReadOnlyList<HistoryChartSeriesInfo> Resolve(SubjectPathResolver resolver, HistoryChart chart)
    {
        var series = chart.Series;
        var result = new HistoryChartSeriesInfo[series.Length];
        for (var index = 0; index < series.Length; index++)
        {
            var entry = series[index];
            var path = entry.Path;
            var label = entry.Label;
            var property = ResolveProperty(resolver, entry, path);
            var metadata = property?.GetStateMetadata();
            var kind = property is null
                ? HistoryChartSeriesKind.Missing
                : PropertyHistoryChartModel.IsDiscrete(property.Type, metadata?.IsDiscrete ?? false)
                    ? HistoryChartSeriesKind.Band
                    : HistoryChartSeriesKind.Line;

            result[index] = new HistoryChartSeriesInfo(
                GetLabel(label, metadata, property, path),
                entry.Color,
                property,
                kind,
                metadata?.Unit ?? StateUnit.Default,
                property?.HasHistory() ?? false,
                metadata?.IsCumulative ?? false);
        }

        return result;
    }

    /// <summary>
    /// Gets the widget argument error of a chart, or null: an unknown range or bucket, or a recorded line
    /// whose unit differs from that of the first recorded line.
    /// </summary>
    public static string? FindArgumentError(string? range, string? bucket, IReadOnlyList<HistoryChartSeriesInfo> series)
    {
        if (!PropertyHistoryChartModel.TryGetRange(range, out _))
        {
            var choices = string.Join(", ", PropertyHistoryChartModel.RangePresets.Select(preset => preset.ShortName));
            return $"Unknown range '{range}': use one of {choices}";
        }

        if (!PropertyHistoryChartModel.TryGetPeriod(bucket, out _))
        {
            var choices = string.Join(", ", PropertyHistoryChartModel.Periods.Select(period => period.ShortName));
            return $"Unknown bucket '{bucket}': use one of {choices}";
        }

        HistoryChartSeriesInfo? first = null;
        foreach (var entry in series)
        {
            if (entry.Kind != HistoryChartSeriesKind.Line || !entry.HasHistory)
            {
                continue;
            }

            if (first is null)
            {
                first = entry;
            }
            else if (entry.Unit != first.Unit)
            {
                return $"Series '{entry.Label}' ({DescribeUnit(entry.Unit)}) has another unit than " +
                    $"'{first.Label}' ({DescribeUnit(first.Unit)}): show it in a second chart";
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the colour of every recorded line, in series order: its own, otherwise the next of
    /// <see cref="HistoryChartPalette.LineColors"/> by its position among the recorded lines. Null for
    /// everything else.
    /// </summary>
    public static string?[] AssignLineColors(IReadOnlyList<HistoryChartSeriesInfo> series)
    {
        var colors = new string?[series.Count];
        var lineIndex = 0;
        for (var index = 0; index < series.Count; index++)
        {
            var entry = series[index];
            if (entry.Kind != HistoryChartSeriesKind.Line || !entry.HasHistory)
            {
                continue;
            }

            colors[index] = !string.IsNullOrEmpty(entry.Color) ? entry.Color : HistoryChartPalette.GetLineColor(lineIndex);
            lineIndex++;
        }

        return colors;
    }

    /// <summary>
    /// Chooses the aggregation of a series: the first that the history dialog offers for a line, which
    /// is the time-weighted average when a store supports it; the last value for a band, a counter or a
    /// raw query, so those share one batch.
    /// </summary>
    public static string ChooseAggregation(
        Type propertyType, bool isBand, bool isCumulative, bool isRaw, IReadOnlySet<string> storeAggregations)
    {
        if (isRaw || isBand)
        {
            return HistoryAggregations.Last;
        }

        var offered = PropertyHistoryChartModel.GateAggregations(
            HistoryColumns.GetValueColumnFor(propertyType), isCumulative, storeAggregations);
        return offered.Count > 0 ? offered[0] : HistoryAggregations.Last;
    }

    /// <summary>
    /// Gets the path the history of a property is recorded under, or null when its subject is not in the graph.
    /// </summary>
    public static string? GetHistoryPath(SubjectPathResolver resolver, RegisteredSubjectProperty property)
    {
        var subjectPath = resolver.GetPath(property.Subject, PathStyle.Canonical);
        if (subjectPath is null)
        {
            return null;
        }

        return subjectPath == "/" ? $"/{property.Name}" : $"{subjectPath}/{property.Name}";
    }

    // Same order as GetDisplayName, reusing the metadata that Resolve already looked up.
    private static string GetLabel(string? label, StateMetadata? metadata, RegisteredSubjectProperty? property, string? path)
    {
        if (!string.IsNullOrEmpty(label))
        {
            return label;
        }

        if (property is null)
        {
            return path ?? string.Empty;
        }

        return !string.IsNullOrEmpty(metadata?.Title) ? metadata.Title : property.Name;
    }

    private static string DescribeUnit(StateUnit unit) => StateUnitExtensions.GetUnitSuffix(unit) ?? "no unit";

    // The path is written by the author of a page, and a malformed one must not take the page down.
    private static RegisteredSubjectProperty? ResolveProperty(SubjectPathResolver resolver, HistoryChartSeries series, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return resolver.ResolveScopedProperty(path, PathStyle.Canonical, series);
        }
        catch
        {
            return null;
        }
    }
}
