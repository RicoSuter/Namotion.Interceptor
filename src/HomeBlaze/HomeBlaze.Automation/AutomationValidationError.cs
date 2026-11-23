namespace HomeBlaze.Automation;

/// <summary>An error in the configuration of an automation, or of one of its rules when <see cref="RuleKey"/> is set.</summary>
public sealed record AutomationValidationError(string? RuleKey, string Message);
