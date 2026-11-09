using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation;

/// <summary>
/// Writes and reads durations as automation expressions do, such as <c>20s</c>, <c>10m</c> or <c>1h30m</c>: the units
/// <c>d</c>, <c>h</c>, <c>m</c>, <c>s</c> and <c>ms</c> from the largest to the smallest.
/// </summary>
public static class AutomationDuration
{
    /// <summary>
    /// The longest duration an automation takes, 36500 days (about 100 years), so that a hold or a cooldown added to the
    /// current time stays within the range of <see cref="DateTimeOffset"/>.
    /// </summary>
    public static TimeSpan Maximum { get; } = TimeSpan.FromDays(36500);

    private static readonly (ulong Ticks, string Suffix)[] Units =
    [
        (TimeSpan.TicksPerDay, "d"),
        (TimeSpan.TicksPerHour, "h"),
        (TimeSpan.TicksPerMinute, "m"),
        (TimeSpan.TicksPerSecond, "s"),
        (TimeSpan.TicksPerMillisecond, "ms")
    ];

    /// <summary>
    /// Formats the duration with its units, such as <c>1h30m</c>; a part below a millisecond is a fraction of the
    /// milliseconds, such as <c>1.5ms</c>, and zero is <c>0s</c>.
    /// </summary>
    public static string Format(TimeSpan duration)
    {
        var ticks = duration.Ticks;
        if (ticks < 0)
        {
            // Unsigned, since the magnitude of long.MinValue is beyond long.MaxValue.
            return "-" + FormatTicks((ulong)-(ticks + 1) + 1);
        }

        return FormatTicks((ulong)ticks);
    }

    /// <summary>
    /// Reads a duration literal such as <c>20s</c> or <c>1h30m</c>, also a sum of such literals; white space around it is
    /// ignored. A text without a unit, a path or a variable is no duration, and neither is a duration longer than
    /// <see cref="Maximum"/> in either direction; the duration is then zero.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        if (TryParseUnlimited(text, out duration) && IsWithinMaximum(duration))
        {
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    /// <summary>Whether the duration is at most <see cref="Maximum"/> in either direction.</summary>
    internal static bool IsWithinMaximum(TimeSpan duration) => duration <= Maximum && duration >= -Maximum;

    /// <summary>Reads a duration literal as <see cref="TryParse"/> does, without the limit of <see cref="Maximum"/>.</summary>
    internal static bool TryParseUnlimited(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text) ||
            !ParsedExpression.TryParse(text.Trim(), '/', out var expression, out _) ||
            expression.Paths.Length > 0 ||
            expression.Pipes.Length > 0)
        {
            return false;
        }

        var context = new LiteralContext();
        if (expression.Evaluate(ref context).Value is not TimeSpan value)
        {
            return false;
        }

        duration = value;
        return true;
    }

    private static string FormatTicks(ulong ticks)
    {
        var builder = new StringBuilder();
        var rest = ticks;
        foreach (var (unitTicks, suffix) in Units)
        {
            var count = rest / unitTicks;
            rest -= count * unitTicks;
            if (unitTicks == TimeSpan.TicksPerMillisecond && rest > 0)
            {
                builder.Append(count.ToString(CultureInfo.InvariantCulture)).Append('.')
                    .Append(rest.ToString("D4", CultureInfo.InvariantCulture).TrimEnd('0')).Append(suffix);
            }
            else if (count > 0)
            {
                builder.Append(count.ToString(CultureInfo.InvariantCulture)).Append(suffix);
            }
        }

        return builder.Length > 0 ? builder.ToString() : "0s";
    }

    // Literals need no context; a path never reaches it, because TryParse refuses expressions with paths.
    private readonly struct LiteralContext : IExpressionContext
    {
        public ExpressionValue ResolvePath(string path) => ExpressionValue.Missing;

        public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
