using HomeBlaze.Automation.Expressions;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Something the worker of an automation processes. <see cref="ChainDepth"/> counts the automation runs that led to it:
/// 0 for an input from outside, one more for each run that caused the next input.
/// </summary>
internal abstract record AutomationInput(int ChainDepth);

/// <summary>A watched property changed to <see cref="Value"/> in the commit with <see cref="Revision"/>.</summary>
internal sealed record PropertyChangedInput(PropertyReference Property, object? Value, long Revision, int ChainDepth) : AutomationInput(ChainDepth);

/// <summary>A property that holds subjects changed, so that paths may now lead elsewhere.</summary>
internal sealed record GraphChangedInput(int ChainDepth) : AutomationInput(ChainDepth);

/// <summary>An event received by the subscription of an event trigger.</summary>
internal sealed record EventInput(EventTriggerSubscription Subscription, CapturedEvent Event, int ChainDepth) : AutomationInput(ChainDepth);

/// <summary>Rules that became active, whose property triggers evaluate their activation.</summary>
internal sealed record ActivationInput(IReadOnlyList<CompiledRule> Rules, int ChainDepth) : AutomationInput(ChainDepth);

/// <summary>The pending property triggers request their runs again, such as after a state change skipped them.</summary>
internal sealed record PendingRecheckInput(int ChainDepth) : AutomationInput(ChainDepth);

/// <summary>A second passed on the clock of the automation.</summary>
internal sealed record TickInput() : AutomationInput(0)
{
    public static TickInput Instance { get; } = new();
}

/// <summary>The configuration of the automation changed.</summary>
internal sealed record ConfigurationChangedInput() : AutomationInput(0)
{
    public static ConfigurationChangedInput Instance { get; } = new();
}

/// <summary>
/// The state of the automation is reset to its initial state. A reset requested by a run continues the chain of that run,
/// so that rules that reset the automation and enter a state again are stopped as a feedback chain.
/// </summary>
internal sealed record ResetStateInput(int ChainDepth) : AutomationInput(ChainDepth);

/// <summary>Completes once the queue is empty when it is processed.</summary>
internal sealed record FlushInput(TaskCompletionSource Completion) : AutomationInput(0)
{
    /// <summary>Creates a flush whose waiter continues on its own thread, not on the worker that completes it.</summary>
    public static FlushInput Create() => new(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// Inputs were dropped: the worker reports them and makes up for <see cref="Dropped"/> after the inputs queued before the
/// drop. Never dropped itself.
/// </summary>
internal sealed record ResynchronizeInput(DroppedInputs Dropped) : AutomationInput(0);
