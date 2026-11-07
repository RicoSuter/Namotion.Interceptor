using HomeBlaze.Automation.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// A request of a trigger to run a rule, with the event it captured, if any, the trigger as <see cref="Source"/> for
/// logs, and the property trigger that is pending until the request runs, if any.
/// </summary>
internal sealed record RunRequest(CompiledRule Rule, CapturedEvent? Event, string Source, CompiledPropertyTrigger? Trigger = null);
