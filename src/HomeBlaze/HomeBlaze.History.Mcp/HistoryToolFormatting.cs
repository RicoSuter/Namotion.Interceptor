using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Mcp;

/// <summary>
/// Pure output formatting for the <c>get_property_history</c> MCP tool: compact timestamps and durations and the
/// coverage summary. Kept separate from the tool so it is unit-testable without a registry.
/// </summary>
public static class HistoryToolFormatting
{
    /// <summary>
    /// Formats <paramref name="value"/> as <c>yyyy-MM-ddTHH:mm:ss</c> with fractional seconds only when they are
    /// not zero, as local time in <paramref name="timeZone"/> (UTC when null) with its offset, for example
    /// <c>+01:00</c>, or <c>Z</c> when the offset is zero.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value, TimeZoneInfo? timeZone = null)
    {
        var local = timeZone is null ? value.ToUniversalTime() : TimeZoneInfo.ConvertTime(value, timeZone);
        return local.Offset == TimeSpan.Zero
            ? local.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> to 12 significant digits, so the noise of a computed aggregate
    /// (<c>500.18000000000757</c>) serializes as the value it stands for (<c>500.18</c>). Zero and non-finite values
    /// are returned unchanged.
    /// </summary>
    internal static double RoundToSignificantDigits(double value)
    {
        const int significantDigits = 12;
        if (value == 0 || !double.IsFinite(value))
        {
            return value;
        }

        var decimals = significantDigits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (decimals is >= 0 and <= 15)
        {
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        // Math.Round takes at most 15 decimals; a power of ten up to 10^22 is exact, so scaling keeps the rounding exact.
        if (decimals < 0)
        {
            var factor = Math.Pow(10, -decimals);
            return Math.Round(value / factor, MidpointRounding.AwayFromZero) * factor;
        }

        var scale = Math.Pow(10, decimals);
        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    /// <summary>
    /// Formats <paramref name="duration"/> as its non-zero day, hour, minute and second parts, for example
    /// <c>2h14m</c>. Returns <c>0s</c> for zero and <c>&lt;1s</c> for a positive duration under a second.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (duration < TimeSpan.FromSeconds(1))
        {
            return "<1s";
        }

        var builder = new StringBuilder();
        AppendPart(builder, (long)duration.TotalDays, 'd');
        AppendPart(builder, duration.Hours, 'h');
        AppendPart(builder, duration.Minutes, 'm');
        AppendPart(builder, duration.Seconds, 's');
        return builder.ToString();
    }

    /// <summary>
    /// Summarizes how much of [<paramref name="from"/>, <paramref name="to"/>) the normalized
    /// <paramref name="ranges"/> cover: the covered fraction rounded toward zero to three decimals, the number of
    /// uncovered gaps and the longest gap.
    /// </summary>
    public static HistoryCoverageSummary Summarize(
        ImmutableArray<HistoryCoverage> ranges, DateTimeOffset from, DateTimeOffset to)
    {
        var window = new HistoryCoverage(from, to);
        var clipped = HistoryCoverage.Clip(ranges, window);
        var total = to - from;
        var covered = HistoryCoverage.CoveredDuration(clipped, window);

        var gaps = 0;
        var largestGap = TimeSpan.Zero;
        var cursor = from;
        foreach (var range in clipped)
        {
            AddGap(range.From - cursor, ref gaps, ref largestGap);
            cursor = range.To;
        }

        AddGap(to - cursor, ref gaps, ref largestGap);

        var ratio = total > TimeSpan.Zero
            ? Math.Round(covered.Ticks / (double)total.Ticks, 3, MidpointRounding.ToZero)
            : 0;
        return new HistoryCoverageSummary(ratio, gaps, largestGap);
    }

    private static void AddGap(TimeSpan gap, ref int gaps, ref TimeSpan largestGap)
    {
        if (gap <= TimeSpan.Zero)
        {
            return;
        }

        gaps++;
        if (gap > largestGap)
        {
            largestGap = gap;
        }
    }

    private static void AppendPart(StringBuilder builder, long value, char unit)
    {
        if (value > 0)
        {
            builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append(unit);
        }
    }
}

/// <summary>
/// How much of a requested range the history covers: the covered fraction, the number of uncovered gaps and the
/// longest gap.
/// </summary>
public readonly record struct HistoryCoverageSummary(double Ratio, int Gaps, TimeSpan LargestGap);
