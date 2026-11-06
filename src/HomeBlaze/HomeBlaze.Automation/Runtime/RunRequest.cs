using HomeBlaze.Automation.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>A request of a trigger to run a rule, with the event it captured, if any, and the trigger as <see cref="Source"/> for logs.</summary>
internal sealed record RunRequest(CompiledRule Rule, CapturedEvent? Event, string Source);
