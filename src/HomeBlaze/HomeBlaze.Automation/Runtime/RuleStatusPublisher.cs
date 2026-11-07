namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Writes the status text of the rule subjects; a rule whose configuration was not applied shows <c>notApplied</c>. Only
/// the status of a rule whose <see cref="CompiledRule.StatusVersion"/> changed is formatted again, unless every status
/// is written again.
/// </summary>
internal sealed class RuleStatusPublisher
{
    public const string NotAppliedInvalid = "Not applied: the configuration is invalid";

    public const string NotAppliedYet = "Not applied yet";

    public const string Stopped = "Inactive: the automation is stopped";

    // What every status depends on besides its rule, which changes rarely.
    private (CompiledAutomation, Dictionary<string, AutomationRule>, bool, string?, string)? _shown;

    /// <summary>
    /// Writes the statuses of the rules of the automation, all of them when the snapshot, the rule subjects, the enabled
    /// flag, the current state or <paramref name="notApplied"/> changed since the last call.
    /// </summary>
    public void Publish(Automation automation, CompiledAutomation snapshot, Func<CompiledRule, string?> getInactiveReason, string notApplied)
    {
        var rules = automation.Rules;
        var shown = (snapshot, rules, automation.Enabled, automation.CurrentState, notApplied);
        var writesEvery = !shown.Equals(_shown);
        _shown = shown;
        var timeZone = automation.TimeProvider.LocalTimeZone;
        foreach (var (key, subject) in rules)
        {
            if (snapshot.FindRule(key) is not { } rule || !ReferenceEquals(rule.Rule, subject))
            {
                if (writesEvery)
                {
                    subject.Status = notApplied;
                }

                continue;
            }

            var version = rule.StatusVersion;
            if (writesEvery || version != rule.PublishedStatusVersion)
            {
                rule.PublishedStatusVersion = version;
                subject.Status = RuleStatusText.Format(rule.Mode, getInactiveReason(rule), rule.GetTriggerStatuses(), timeZone, rule.SkipReason);
            }
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
