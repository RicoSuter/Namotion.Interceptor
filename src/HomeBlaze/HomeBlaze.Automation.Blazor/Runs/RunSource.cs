namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// A rule whose runs are read from history: its name and its canonical path, such as <c>/Washer/Rules[k3f9]</c>, and optionally
/// its run count as it is now, which <see cref="RunHistory.IsBehindAsync"/> compares with the stored one.
/// </summary>
internal sealed record RunSource(string RuleName, string RulePath, long? RunCount = null);
