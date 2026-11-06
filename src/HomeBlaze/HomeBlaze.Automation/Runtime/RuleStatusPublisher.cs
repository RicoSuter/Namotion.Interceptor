namespace HomeBlaze.Automation.Runtime;

/// <summary>Writes the status text of every rule subject; a rule whose configuration was not applied shows <c>notApplied</c>.</summary>
internal static class RuleStatusPublisher
{
    public const string NotAppliedInvalid = "Not applied: the configuration is invalid";

    public const string NotAppliedYet = "Not applied yet";

    public const string Stopped = "Inactive: the automation is stopped";

    public static void Publish(
        IReadOnlyDictionary<string, AutomationRule> rules,
        CompiledAutomation snapshot,
        Func<CompiledRule, string?> getInactiveReason,
        TimeZoneInfo timeZone,
        string notApplied)
    {
        foreach (var (key, subject) in rules)
        {
            subject.Status = snapshot.FindRule(key) is { } rule && ReferenceEquals(rule.Rule, subject)
                ? RuleStatusText.Format(rule.Mode, getInactiveReason(rule), rule.GetTriggerStatuses(), timeZone)
                : notApplied;
        }
    }

    /// <summary>Writes the inactive status of a stopped automation on every rule subject.</summary>
    public static void PublishStopped(IReadOnlyDictionary<string, AutomationRule> rules, TimeZoneInfo timeZone)
    {
        foreach (var subject in rules.Values)
        {
            subject.Status = RuleStatusText.Format(subject.Mode, Stopped, [], timeZone);
        }
    }
}
