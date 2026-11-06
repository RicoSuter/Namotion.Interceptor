namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// One evaluation of a property trigger: its expression, its rearm expression when the trigger waits to rearm, and the
/// paths that led to no value when the expression is unknown. <see cref="MissingPaths"/> holds the paths of the
/// expression and, when the rearm expression was evaluated and is unknown, the paths of the rearm expression too.
/// </summary>
internal readonly record struct TriggerObservation(bool? Value, bool? Rearm, IReadOnlyList<string> MissingPaths);
