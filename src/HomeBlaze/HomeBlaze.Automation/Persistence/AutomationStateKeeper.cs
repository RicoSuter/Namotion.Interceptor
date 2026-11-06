using System.Text.Json;

namespace HomeBlaze.Automation.Persistence;

/// <summary>
/// The checkpoint an automation has written, kept in memory, and the only way to change it: a new checkpoint is written
/// first and becomes <see cref="Persisted"/> only when the write succeeded, so the caller publishes nothing that is not
/// saved. Used by the worker only.
/// </summary>
internal sealed class AutomationStateKeeper(AutomationCheckpointStore store)
{
    public AutomationCheckpointStore Store { get; private set; } = store;

    public AutomationCheckpoint Persisted { get; private set; } = new();

    /// <summary>Keeps the checkpoint in the store from now on, starting from an empty one, as for a new automation.</summary>
    public void UseStore(AutomationCheckpointStore newStore)
    {
        Store = newStore;
        Persisted = new AutomationCheckpoint();
    }

    /// <summary>Reads the checkpoint into <see cref="Persisted"/>; null without one, and with the error when it cannot be read.</summary>
    public AutomationCheckpoint? Load(out string? error)
    {
        try
        {
            var checkpoint = Store.Read();
            if (checkpoint is not null)
            {
                Persisted = checkpoint;
            }

            error = null;
            return checkpoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>Writes the checkpoint and keeps it; returns the error and keeps the previous checkpoint when the write fails.</summary>
    public string? TryPersist(AutomationCheckpoint next)
    {
        try
        {
            Store.Write(next);
            Persisted = next;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Gives each rule without a run count, which is a new instance, its persisted run properties, or zero runs. The run
    /// count is set last, so that its history sample comes after the outcome and the message of the same run.
    /// </summary>
    public void RestoreRules(IReadOnlyDictionary<string, AutomationRule> rules)
    {
        foreach (var (key, rule) in rules)
        {
            if (rule.RunCount is null)
            {
                Restore(key, rule);
            }
        }
    }

    /// <summary>
    /// Gives every rule its persisted run properties, or zero runs, also a rule that has some in memory: at a start the
    /// checkpoint is what the automation has, and one that moved has an empty one.
    /// </summary>
    public void RestoreAllRules(IReadOnlyDictionary<string, AutomationRule> rules)
    {
        foreach (var (key, rule) in rules)
        {
            Restore(key, rule);
        }
    }

    private void Restore(string key, AutomationRule rule)
    {
        var run = Persisted.Rules.GetValueOrDefault(key);
        rule.LastTriggeredAt = run?.LastTriggeredAt;
        rule.LastRunOutcome = run?.LastRunOutcome;
        rule.LastRunMessage = run?.LastRunMessage;
        rule.RunCount = run?.RunCount ?? 0;
    }

    public static AutomationCheckpoint WithState(AutomationCheckpoint checkpoint, string? state, DateTimeOffset enteredAt) =>
        checkpoint with { CurrentState = state, StateEnteredAt = enteredAt };

    public static AutomationCheckpoint WithRule(AutomationCheckpoint checkpoint, string key, RuleCheckpoint rule) =>
        checkpoint with { Rules = new Dictionary<string, RuleCheckpoint>(checkpoint.Rules) { [key] = rule } };

    public static AutomationCheckpoint WithoutTriggerTimes(AutomationCheckpoint checkpoint) =>
        checkpoint with { Rules = checkpoint.Rules.ToDictionary(pair => pair.Key, pair => pair.Value with { LastTriggeredAt = null }) };

    /// <summary>Keeps the runs of the rules with these keys only, so that the file forgets deleted rules.</summary>
    public static AutomationCheckpoint WithRulesOf(AutomationCheckpoint checkpoint, IEnumerable<string> keys)
    {
        var kept = keys.ToHashSet(StringComparer.Ordinal);
        return checkpoint with
        {
            Rules = checkpoint.Rules.Where(pair => kept.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }
}
