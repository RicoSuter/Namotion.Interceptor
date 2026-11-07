using System.Collections.Immutable;
using System.Text.RegularExpressions;
using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// When a guard that reads the clock can change while the properties it reads stay the same. A guard whose every read of
/// the clock is a comparison <c>now - path op duration</c> joined by <c>and</c> or <c>or</c>, as a cooldown such as
/// <c>now - ./LastTriggeredAt &gt; 10m</c> is, changes only when the instant at the path plus the duration passes, so it
/// is evaluated again at that instant only. Any other read of <c>now</c> or <c>time</c> is evaluated every second.
/// </summary>
internal sealed partial class GuardClock
{
    private static readonly GuardClock EverySecondClock = new([]);

    private GuardClock(ImmutableArray<ElapsedComparison> comparisons) => Comparisons = comparisons;

    /// <summary>The comparisons of elapsed time, empty for a clock that is evaluated every second.</summary>
    public ImmutableArray<ElapsedComparison> Comparisons { get; }

    /// <summary>Whether the guard is evaluated every second, since when it changes cannot be derived.</summary>
    public bool IsEverySecond => Comparisons.IsEmpty;

    /// <summary>Gets the clock of the guard, or null for a guard that does not read the clock.</summary>
    public static GuardClock? Create(ParsedExpression guard)
    {
        if (!AutomationExpressionCheck.UsesTime(guard))
        {
            return null;
        }

        var readsTimeOfDay = guard.Paths.Any(path => guard.TryGetVariable(path, out var variable, out _) && variable == AutomationVariables.Time);
        if (readsTimeOfDay)
        {
            return EverySecondClock;
        }

        var propertyPaths = AutomationExpressionCheck.GetPropertyPaths(guard).ToHashSet(StringComparer.Ordinal);
        var comparisons = ImmutableArray.CreateBuilder<ElapsedComparison>();
        foreach (Match match in ElapsedComparisonPattern().Matches(guard.Text))
        {
            var path = match.Groups["path"].Value;
            if (!propertyPaths.Contains(path) || ReadDuration(match.Groups["duration"].Value) is not { } duration)
            {
                return EverySecondClock;
            }

            comparisons.Add(new ElapsedComparison(path, duration));
        }

        // A read of now outside such a comparison, also one the pattern matched in a text literal, is evaluated every second.
        return comparisons.Count > 0 && comparisons.Count == NowPattern().Count(guard.Text)
            ? new GuardClock(comparisons.ToImmutable())
            : EverySecondClock;
    }

    /// <summary>
    /// Gets the next instant after <paramref name="now"/> at which a comparison can change, with the instants the paths
    /// lead to now; null when none can change before one of them does. False when a path leads to a value that is no
    /// instant, so that the guard is evaluated every second.
    /// </summary>
    public bool TryGetNextChange(Func<string, object?> readInstant, DateTimeOffset now, out DateTimeOffset? nextChange)
    {
        nextChange = null;
        foreach (var comparison in Comparisons)
        {
            switch (readInstant(comparison.Path))
            {
                case null:
                    continue;
                case DateTimeOffset instant:
                    // The tick after the instant, where a strict comparison has changed as well.
                    var change = instant + comparison.Duration + TimeSpan.FromTicks(1);
                    if (change > now && (nextChange is null || change < nextChange))
                    {
                        nextChange = change;
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

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

    [GeneratedRegex(@"(?<=(^|\(|\band|\bor|&&|\|\|)\s*)(?<![\w./])now\s+-\s+(?<path>[^\s()]+)\s*(>=|<=|>|<)\s*(?<duration>[0-9][0-9A-Za-z.]*)(?=\s*($|\)|and\b|or\b|&&|\|\|))")]
    private static partial Regex ElapsedComparisonPattern();

    [GeneratedRegex(@"(?<![\w./])now(?![\w/])")]
    private static partial Regex NowPattern();

    /// <summary>A comparison <c>now - Path op Duration</c>.</summary>
    internal readonly record struct ElapsedComparison(string Path, TimeSpan Duration);

    private readonly struct ConstantContext : IExpressionContext
    {
        public ExpressionValue ResolvePath(string path) => ExpressionValue.Missing;

        public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => ExpressionValue.Missing;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
