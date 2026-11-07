using System.Globalization;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The status text of a rule: its mode, then why it is inactive, or the first pending trigger with why its run did not
/// run, the first holding trigger, the first problem, the first unknown trigger, "Triggered, waiting to rearm" when
/// every trigger fired, and otherwise "Armed", followed by why its last request did not run. Times are local to the
/// host. The text changes only on a transition, so history records it once per transition.
/// </summary>
internal static class RuleStatusText
{
    public static string Format(
        AutomationRuleMode mode, string? inactiveReason, IReadOnlyList<TriggerStatus> triggers, TimeZoneInfo timeZone, string? skipReason = null)
    {
        if (mode == AutomationRuleMode.Off)
        {
            return "Off";
        }

        if (inactiveReason is not null)
        {
            return $"{mode}: {inactiveReason}";
        }

        foreach (var trigger in triggers)
        {
            if (trigger is { Phase: TriggerPhase.Pending, PendingSince: { } since })
            {
                return skipReason is null
                    ? $"{mode}: Waiting since {FormatTime(since, timeZone)}"
                    : $"{mode}: Waiting since {FormatTime(since, timeZone)}: {skipReason}";
            }
        }

        var description = Describe(triggers, timeZone);
        return skipReason is null ? $"{mode}: {description}" : $"{mode}: {description}; not run: {skipReason}";
    }

    private static string Describe(IReadOnlyList<TriggerStatus> triggers, TimeZoneInfo timeZone)
    {
        foreach (var trigger in triggers)
        {
            if (trigger is { Phase: TriggerPhase.Holding, HoldStartedAt: { } start, Hold: { } hold })
            {
                return $"Holding since {FormatTime(start, timeZone)} for {hold:c}";
            }
        }

        foreach (var trigger in triggers)
        {
            if (trigger.Problem is { } problem)
            {
                return problem;
            }
        }

        foreach (var trigger in triggers)
        {
            if (trigger.UnknownSince is { } since)
            {
                return DescribeUnknown(since, trigger.MissingPaths, timeZone);
            }
        }

        return triggers.Count > 0 && triggers.All(trigger => trigger.Phase is TriggerPhase.WaitingToRearm or TriggerPhase.WaitingForFalse)
            ? "Triggered, waiting to rearm"
            : "Armed";
    }

    private static string DescribeUnknown(DateTimeOffset since, IReadOnlyList<string> missingPaths, TimeZoneInfo timeZone) =>
        missingPaths.Count > 0
            ? $"Unknown since {FormatTime(since, timeZone)}, missing {string.Join(", ", missingPaths)}"
            : $"Unknown since {FormatTime(since, timeZone)}";

    private static string FormatTime(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(instant, timeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
