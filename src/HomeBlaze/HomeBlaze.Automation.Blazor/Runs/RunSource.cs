namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>A rule whose runs are read from history: its name and its canonical path, such as <c>/Washer/Rules[k3f9]</c>.</summary>
public sealed record RunSource(string RuleName, string RulePath);
