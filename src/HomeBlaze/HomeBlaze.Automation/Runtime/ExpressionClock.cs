using System.Collections.Immutable;
using System.Text.RegularExpressions;
using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// When an expression that reads the clock can change while the properties it reads stay the same. An expression whose
/// every read of the clock is a comparison <c>now - path op duration</c> or <c>time op duration</c>, joined by <c>and</c>
/// or <c>or</c>, as a cooldown such as <c>now - ./LastTriggeredAt &gt; 10m</c> or a period such as
/// <c>time &gt;= 8h30m and time &lt; 21h30m</c> is, changes only when the instant at the path plus the duration passes, or
/// when the local time of day passes the duration or midnight. It is evaluated again at those instants only. Any other
/// read of <c>now</c> or <c>time</c> is evaluated every second.
/// </summary>
internal sealed partial class ExpressionClock
{
    private static readonly ExpressionClock EverySecondClock = new([], []);

    private ExpressionClock(ImmutableArray<ElapsedComparison> elapsed, ImmutableArray<TimeOfDayComparison> timesOfDay)
    {
        Elapsed = elapsed;
        TimesOfDay = timesOfDay;
    }

    /// <summary>The comparisons of elapsed time.</summary>
    public ImmutableArray<ElapsedComparison> Elapsed { get; }

    /// <summary>The comparisons of the local time of day.</summary>
    public ImmutableArray<TimeOfDayComparison> TimesOfDay { get; }

    /// <summary>Whether the expression is evaluated every second, since when it changes cannot be derived.</summary>
    public bool IsEverySecond => Elapsed.IsEmpty && TimesOfDay.IsEmpty;

    /// <summary>Gets the clock of the expression, or null for an expression that does not read the clock.</summary>
    public static ExpressionClock? Create(ParsedExpression expression)
    {
        if (!AutomationExpressionCheck.UsesTime(expression))
        {
            return null;
        }

        var propertyPaths = AutomationExpressionCheck.GetPropertyPaths(expression).ToHashSet(StringComparer.Ordinal);
        var elapsed = ImmutableArray.CreateBuilder<ElapsedComparison>();
        foreach (Match match in ElapsedComparisonPattern().Matches(expression.Text))
        {
            var path = match.Groups["path"].Value;
            if (!propertyPaths.Contains(path) || ReadDuration(match.Groups["duration"].Value) is not { } duration)
            {
                return EverySecondClock;
            }

            elapsed.Add(new ElapsedComparison(path, duration + GetOffset(match)));
        }

        var timesOfDay = ImmutableArray.CreateBuilder<TimeOfDayComparison>();
        foreach (Match match in TimeOfDayComparisonPattern().Matches(expression.Text))
        {
            if (ReadDuration(match.Groups["duration"].Value) is not { } timeOfDay)
            {
                return EverySecondClock;
            }

            timesOfDay.Add(new TimeOfDayComparison(timeOfDay + GetOffset(match)));
        }

        // A read of the clock outside such a comparison, also one the pattern matched in a text literal, is evaluated
        // every second.
        var isDerivable = elapsed.Count + timesOfDay.Count > 0 &&
            elapsed.Count == NowPattern().Count(expression.Text) &&
            timesOfDay.Count == TimePattern().Count(expression.Text);
        return isDerivable ? new ExpressionClock(elapsed.ToImmutable(), timesOfDay.ToImmutable()) : EverySecondClock;
    }

    /// <summary>The clock of two expressions that are evaluated together, or null when neither reads the clock.</summary>
    public static ExpressionClock? Combine(ExpressionClock? first, ExpressionClock? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }

        return first.IsEverySecond || second.IsEverySecond
            ? EverySecondClock
            : new ExpressionClock(first.Elapsed.AddRange(second.Elapsed), first.TimesOfDay.AddRange(second.TimesOfDay));
    }

    /// <summary>The next whole second after <paramref name="now"/>, when an expression evaluated every second is evaluated again.</summary>
    public static DateTimeOffset GetNextSecond(DateTimeOffset now) =>
        new(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerSecond) + TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>
    /// Gets the next instant after <paramref name="now"/> at which a comparison can change, with the instants the paths
    /// lead to now and the local time of <paramref name="timeZone"/>; null when none can change before one of the
    /// instants does. False when a path leads to a value that is no instant, so that the expression is evaluated every
    /// second.
    /// </summary>
    public bool TryGetNextChange(Func<string, object?> readInstant, DateTimeOffset now, TimeZoneInfo timeZone, out DateTimeOffset? nextChange) =>
        TryGetNextChange(readInstant, now, timeZone, out nextChange, out _);

    /// <summary>
    /// Gets the next change as the other overload does, and whether a comparison changed already before
    /// <paramref name="now"/>, which a wall clock set back can undo.
    /// </summary>
    public bool TryGetNextChange(
        Func<string, object?> readInstant, DateTimeOffset now, TimeZoneInfo timeZone, out DateTimeOffset? nextChange, out bool hasChanged)
    {
        nextChange = null;
        hasChanged = false;
        foreach (var comparison in Elapsed)
        {
            switch (readInstant(comparison.Path))
            {
                case null:
                    continue;
                case DateTimeOffset instant:
                    hasChanged |= instant + comparison.Duration <= now;
                    nextChange = Earliest(nextChange, instant + comparison.Duration, now);
                    break;
                default:
                    return false;
            }
        }

        if (!TimesOfDay.IsEmpty)
        {
            nextChange = GetNextTimeOfDayChange(now, timeZone, nextChange);
        }

        return true;
    }

    // The time of day changes linearly with the instant while the offset of the time zone stays, so each comparison and
    // midnight are derived with the current offset; a change of the offset before them, such as for daylight saving
    // time, is a change of its own, after which they are derived again with the new offset. In the hour that repeats
    // when the clocks go back, time stays as LocalClock reads it, so an instant derived in that hour changes nothing.
    private DateTimeOffset? GetNextTimeOfDayChange(DateTimeOffset now, TimeZoneInfo timeZone, DateTimeOffset? nextChange)
    {
        var offset = timeZone.GetUtcOffset(now);
        var localDay = new DateTimeOffset(now.ToOffset(offset).Date, offset);
        nextChange = Earliest(nextChange, NextOccurrence(localDay, TimeSpan.Zero, now), now);
        foreach (var comparison in TimesOfDay)
        {
            nextChange = Earliest(nextChange, NextOccurrence(localDay, comparison.TimeOfDay, now), now);
        }

        return LocalClock.GetOffsetChange(timeZone, offset, now, nextChange!.Value) ?? nextChange;
    }

    private static DateTimeOffset NextOccurrence(DateTimeOffset localDay, TimeSpan timeOfDay, DateTimeOffset now)
    {
        var occurrence = localDay + timeOfDay;
        return occurrence > now ? occurrence : occurrence.AddDays(1);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? current, DateTimeOffset candidate, DateTimeOffset now) =>
        candidate > now && (current is null || candidate < current) ? candidate : current;

    // A strict comparison changes one tick after the boundary, for example now - x > d at x + d plus one tick.
    private static TimeSpan GetOffset(Match match) => match.Groups["operator"].Value is ">" or "<=" ? TimeSpan.FromTicks(1) : TimeSpan.Zero;

    private static TimeSpan? ReadDuration(string text)
    {
        if (!ParsedExpression.TryParse(text, AutomationPaths.Separator, AutomationVariables.All, out var expression, out _) ||
            !expression.Paths.IsEmpty)
        {
            return null;
        }

        var context = new ConstantContext();
        return expression.Evaluate(ref context).Value is TimeSpan duration ? duration : null;
    }

    [GeneratedRegex(@"(?<=(^|\(|\band|\bor|&&|\|\|)\s*)(?<![\w./])now\s+-\s+(?<path>[^\s()]+)\s*(?<operator>>=|<=|>|<)\s*(?<duration>[0-9][0-9A-Za-z.]*)(?=\s*($|\)|and\b|or\b|&&|\|\|))")]
    private static partial Regex ElapsedComparisonPattern();

    [GeneratedRegex(@"(?<=(^|\(|\band|\bor|&&|\|\|)\s*)(?<![\w./])time\s*(?<operator>>=|<=|>|<)\s*(?<duration>[0-9][0-9A-Za-z.]*)(?=\s*($|\)|and\b|or\b|&&|\|\|))")]
    private static partial Regex TimeOfDayComparisonPattern();

    [GeneratedRegex(@"(?<![\w./])now(?![\w/])")]
    private static partial Regex NowPattern();

    [GeneratedRegex(@"(?<![\w./])time(?![\w/])")]
    private static partial Regex TimePattern();

    /// <summary>A comparison <c>now - Path op duration</c>, which changes when the instant at the path plus <see cref="Duration"/> passes.</summary>
    internal readonly record struct ElapsedComparison(string Path, TimeSpan Duration);

    /// <summary>A comparison <c>time op duration</c>, which changes when the local time of day passes <see cref="TimeOfDay"/>.</summary>
    internal readonly record struct TimeOfDayComparison(TimeSpan TimeOfDay);

    private readonly struct ConstantContext : IExpressionContext
    {
        public ExpressionValue ResolvePath(string path) => ExpressionValue.Missing;

        public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => ExpressionValue.Missing;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
