namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>Whether a rule would run with the current values, and why.</summary>
internal sealed record RuleTestVerdict(bool WouldRun, string Text);
