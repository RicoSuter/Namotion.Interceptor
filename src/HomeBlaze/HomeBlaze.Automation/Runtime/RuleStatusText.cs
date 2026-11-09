using System.Globalization;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The status text of a rule: its mode, then why it is inactive or the state of the trigger that matters most, followed
/// by why its last request did not run. Times are local to the host. The text changes only on a transition, so history
/// records it once per transition.
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
                    ? $"{mode}: Waiting since {FormatInstant(since, timeZone)}"
                    : $"{mode}: Waiting since {FormatInstant(since, timeZone)}: {skipReason}";
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
                return $"Holding since {FormatTime(start, timeZone)} for {AutomationDuration.Format(hold)}";
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

        return DescribeSettled(triggers);
    }

    private static string DescribeSettled(IReadOnlyList<TriggerStatus> triggers)
    {
        if (triggers.Count == 0 || !triggers.All(trigger => trigger.Phase is TriggerPhase.WaitingToRearm or TriggerPhase.WaitingForFalse or TriggerPhase.RearmedWhileTrue))
        {
            return "Armed";
        }

        if (triggers.Any(trigger => trigger.Phase == TriggerPhase.RearmedWhileTrue))
        {
            return "Armed, waiting until false";
        }

        if (triggers.All(trigger => trigger.Phase == TriggerPhase.WaitingForFalse))
        {
            return "Already true, waiting until false";
        }

        return triggers.All(trigger => trigger.Phase == TriggerPhase.WaitingForFalse || trigger.WasTrueAtActivation)
            ? "Already true, waiting to rearm"
            : "Triggered, waiting to rearm";
    }

    private static string DescribeUnknown(DateTimeOffset since, IReadOnlyList<string> missingPaths, TimeZoneInfo timeZone) =>
        missingPaths.Count > 0
            ? $"Unknown since {FormatInstant(since, timeZone)}, missing {string.Join(", ", missingPaths)}"
            : $"Unknown since {FormatInstant(since, timeZone)}";

    private static string FormatTime(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(instant, timeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // A value can stay missing, and a trigger can wait, for days, so the text names the day as well.
    private static string FormatInstant(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(instant, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
