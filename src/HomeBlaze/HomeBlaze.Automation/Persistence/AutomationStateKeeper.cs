using System.Text.Json;

namespace HomeBlaze.Automation.Persistence;

/// <summary>
/// The checkpoint an automation has written, kept in memory, and the only way to change it: a new checkpoint is written
/// first and becomes <see cref="Persisted"/> only when the write succeeded, so the caller publishes nothing that is not
/// saved. Used by the worker only.
/// </summary>
internal sealed class AutomationStateKeeper(AutomationCheckpointStore store)
{
    private string? _overwriteRefusal;

    public AutomationCheckpointStore Store { get; private set; } = store;

    public AutomationCheckpoint Persisted { get; private set; } = new();

    /// <summary>Keeps the checkpoint in the store from now on, starting from an empty one, as for a new automation.</summary>
    public void UseStore(AutomationCheckpointStore newStore)
    {
        Store = newStore;
        Persisted = new AutomationCheckpoint();
    }

    /// <summary>
    /// Reads the checkpoint into <see cref="Persisted"/>; null without one, and with the error when it cannot be read. A
    /// file that cannot be read is moved aside, so that the next write does not replace it; when that fails too, nothing
    /// is written until <see cref="AllowOverwrite"/>.
    /// </summary>
    public AutomationCheckpoint? Load(DateTimeOffset now, out string? error)
    {
        _overwriteRefusal = null;
        try
        {
            var checkpoint = Store.Read() is { } read ? WithoutNullRules(read) : null;
            if (checkpoint is not null)
            {
                Persisted = checkpoint;
            }

            error = null;
            return checkpoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = MoveAside(exception.Message, now);
            return null;
        }
    }

    /// <summary>Writes again after an unreadable checkpoint could not be moved aside, which then is replaced.</summary>
    public void AllowOverwrite() => _overwriteRefusal = null;

    /// <summary>Writes the checkpoint and keeps it; returns the error and keeps the previous checkpoint when the write fails.</summary>
    public string? TryPersist(AutomationCheckpoint next)
    {
        if (_overwriteRefusal is { } refusal)
        {
            return refusal;
        }

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

    private string MoveAside(string readError, DateTimeOffset now)
    {
        try
        {
            var movedTo = Store.MoveAside(now);
            return $"The checkpoint could not be read and was moved to {movedTo}, so the automation starts from its initial state: {readError}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _overwriteRefusal = $"The unreadable checkpoint at {Store.FilePath} is not replaced until the automation is reset";
            return $"The checkpoint could not be read and not be moved aside, so the automation saves nothing until it is reset: {readError}; {exception.Message}";
        }
    }

    // A file may hold an explicit null for the rules or for a rule, which the keeper never expects.
    private static AutomationCheckpoint WithoutNullRules(AutomationCheckpoint checkpoint)
    {
        if (checkpoint.Rules is null)
        {
            return checkpoint with { Rules = new Dictionary<string, RuleCheckpoint>() };
        }

        return checkpoint.Rules.Values.Any(rule => rule is null)
            ? checkpoint with { Rules = checkpoint.Rules.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value) }
            : checkpoint;
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
