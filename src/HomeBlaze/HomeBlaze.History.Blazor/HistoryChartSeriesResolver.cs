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
/// <param name="Aggregation">The aggregation the series asks for as written, its own or else the one of the chart, or null or empty for the automatic choice.</param>
public sealed record HistoryChartSeriesInfo(
    string Label,
    string? Color,
    RegisteredSubjectProperty? Property,
    HistoryChartSeriesKind Kind,
    StateUnit Unit,
    bool HasHistory,
    bool IsCumulative,
    string? Aggregation = null)
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
    private static readonly string AggregationChoices = string.Join(", ", HistoryAggregations.All.SelectMany(aggregation =>
        aggregation == HistoryAggregations.TimeWeightedAverage
            ? new[] { aggregation, PropertyHistoryChartModel.AverageAlias }
            : new[] { aggregation }));

    /// <summary>
    /// Resolves the path of every series relative to the series. Reads the properties of the series, so a
    /// caller in a tracking scope renders again when one changes.
    /// </summary>
    public static IReadOnlyList<HistoryChartSeriesInfo> Resolve(SubjectPathResolver resolver, HistoryChart chart)
    {
        var series = chart.Series;
        var chartAggregation = chart.Aggregation;
        var result = new HistoryChartSeriesInfo[series.Length];
        for (var index = 0; index < series.Length; index++)
        {
            var entry = series[index];
            var path = entry.Path;
            var label = entry.Label;
            var aggregation = entry.Aggregation;
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
                metadata?.IsCumulative ?? false,
                string.IsNullOrEmpty(aggregation) ? chartAggregation : aggregation);
        }

        return result;
    }

    /// <summary>
    /// Gets the widget argument error of a chart, or null: an unknown range, bucket or aggregation, or a recorded
    /// line whose unit differs from that of the first recorded line. A line that counts samples has no unit. An
    /// aggregation that the property or the stores do not offer is no error, see <see cref="GetAggregationNote"/>.
    /// </summary>
    public static string? FindArgumentError(
        string? range, string? bucket, string? aggregation, IReadOnlyList<HistoryChartSeriesInfo> series)
    {
        if (!PropertyHistoryChartModel.TryGetRange(range, out _))
        {
            var choices = string.Join(", ", PropertyHistoryChartModel.RangePresets.Select(preset => preset.ShortName));
            return $"Unknown range '{range}': use one of {choices}";
        }

        if (!PropertyHistoryChartModel.TryGetPeriod(bucket, out var period))
        {
            var choices = string.Join(", ", PropertyHistoryChartModel.Periods.Select(period => period.ShortName));
            return $"Unknown bucket '{bucket}': use one of {choices}";
        }

        if (!string.IsNullOrEmpty(aggregation) && !PropertyHistoryChartModel.TryGetAggregation(aggregation, out _))
        {
            return $"Unknown aggregation '{aggregation}': use one of {AggregationChoices}";
        }

        // The chart's own is checked above, so an unknown aggregation of a series is the series' own.
        foreach (var entry in series)
        {
            if (!string.IsNullOrEmpty(entry.Aggregation) && !PropertyHistoryChartModel.TryGetAggregation(entry.Aggregation, out _))
            {
                return $"Unknown aggregation '{entry.Aggregation}' of series '{entry.Label}': use one of {AggregationChoices}";
            }
        }

        HistoryChartSeriesInfo? first = null;
        var firstIsCount = false;
        foreach (var entry in series)
        {
            if (entry.Kind != HistoryChartSeriesKind.Line || !entry.HasHistory)
            {
                continue;
            }

            // Every line offers Count, so a line that asks for it draws a count of samples, except in a raw query.
            var isCount = !period.IsRaw &&
                PropertyHistoryChartModel.TryGetAggregation(entry.Aggregation, out var requested) &&
                requested == HistoryAggregations.Count;
            if (first is null)
            {
                first = entry;
                firstIsCount = isCount;
            }
            else if (GetCheckedUnit(entry.Unit, isCount) != GetCheckedUnit(first.Unit, firstIsCount))
            {
                return $"Series '{entry.Label}' ({DescribeUnit(entry.Unit, isCount)}) has another unit than " +
                    $"'{first.Label}' ({DescribeUnit(first.Unit, firstIsCount)}): show it in a second chart";
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
    /// Gets the index of the first recorded line, the series a click on the lines shows the history of, or -1.
    /// </summary>
    public static int FindFirstRecordedLine(IReadOnlyList<HistoryChartSeriesInfo> series)
    {
        for (var index = 0; index < series.Count; index++)
        {
            if (series[index] is { Kind: HistoryChartSeriesKind.Line, HasHistory: true })
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Chooses the aggregation of a series: for a line the <paramref name="requested"/> one when the history dialog
    /// offers it for the property, otherwise the first that the dialog offers, which is the time-weighted average when
    /// a store supports it and the last value for a counter; the last value for a raw query, so those share one batch,
    /// and for a band, which is always queried raw.
    /// </summary>
    public static string ChooseAggregation(
        Type propertyType,
        bool isBand,
        bool isCumulative,
        bool isRaw,
        IReadOnlySet<string> storeAggregations,
        string? requested = null)
    {
        if (isRaw || isBand)
        {
            return HistoryAggregations.Last;
        }

        var offered = PropertyHistoryChartModel.GateAggregations(
            HistoryColumns.GetValueColumnFor(propertyType), isCumulative, storeAggregations);
        if (PropertyHistoryChartModel.TryGetAggregation(requested, out var aggregation) && offered.Contains(aggregation))
        {
            return aggregation;
        }

        return offered.Count > 0 ? offered[0] : HistoryAggregations.Last;
    }

    /// <summary>
    /// Gets what the legend says about a line that shows another aggregation than the one it asks for, because the
    /// property or the stores do not offer it, or null. A band and a raw query use no aggregation.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="isRaw">Whether the lines are queried as raw samples.</param>
    /// <param name="chosen">The aggregation <see cref="ChooseAggregation"/> chose for the series.</param>
    public static string? GetAggregationNote(HistoryChartSeriesInfo series, bool isRaw, string chosen)
    {
        if (series.Kind != HistoryChartSeriesKind.Line || isRaw ||
            !PropertyHistoryChartModel.TryGetAggregation(series.Aggregation, out var requested) || requested == chosen)
        {
            return null;
        }

        return $"{PropertyHistoryChartModel.GetAggregationLabel(requested)} not available, " +
            $"shows {PropertyHistoryChartModel.GetAggregationLabel(chosen)}";
    }

    /// <summary>
    /// Gets the selections the history dialog of a series opens with: the range of the chart, and for a line also its
    /// bucket and the aggregation it asks for, which the dialog gates like <see cref="ChooseAggregation"/>. A band keeps
    /// the state timeline of the dialog.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="range">The range of the chart as written.</param>
    /// <param name="bucket">The bucket of the chart as written.</param>
    public static PropertyHistoryDialogSettings GetDialogSettings(HistoryChartSeriesInfo series, string? range, string? bucket)
    {
        TimeSpan? span = PropertyHistoryChartModel.TryGetRange(range, out var rangeSpan) ? rangeSpan : null;
        if (series.Kind != HistoryChartSeriesKind.Line)
        {
            return new PropertyHistoryDialogSettings(span);
        }

        PropertyHistoryChartModel.ChartPeriod? period = PropertyHistoryChartModel.TryGetPeriod(bucket, out var chartPeriod) ? chartPeriod : null;
        return new PropertyHistoryDialogSettings(span, period, string.IsNullOrEmpty(series.Aggregation) ? null : series.Aggregation);
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

    private static StateUnit GetCheckedUnit(StateUnit unit, bool isCount) => isCount ? StateUnit.Default : unit;

    private static string DescribeUnit(StateUnit unit, bool isCount) =>
        isCount ? "count" : StateUnitExtensions.GetUnitSuffix(unit) ?? "no unit";

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
