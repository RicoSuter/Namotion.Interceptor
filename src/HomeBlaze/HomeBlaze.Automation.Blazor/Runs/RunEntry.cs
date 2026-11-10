namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>A run of a rule as the automation page lists it; <see cref="At"/> is null for the last run of a rule shown without history.</summary>
internal sealed record RunEntry(DateTimeOffset? At, string RuleName, AutomationRunOutcome? Outcome, string? Message);
