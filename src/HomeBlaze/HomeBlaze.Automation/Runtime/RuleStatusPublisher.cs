namespace HomeBlaze.Automation.Runtime;

/// <summary>Writes the status text of every rule subject; a rule whose configuration was not applied shows <c>notApplied</c>.</summary>
internal static class RuleStatusPublisher
{
    public const string NotAppliedInvalid = "Not applied: the configuration is invalid";

    public const string NotAppliedYet = "Not applied yet";

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
}
