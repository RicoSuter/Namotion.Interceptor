namespace HomeBlaze.Automation;

/// <summary>What a property trigger does with the value it finds when it becomes active.</summary>
public enum TriggerActivation
{
    /// <summary>A true expression starts the hold at activation, and the trigger fires when the hold elapsed.</summary>
    EvaluateImmediately,

    /// <summary>
    /// The current value is the baseline: a true expression must turn false and true again before the trigger fires.
    /// With a rearm expression, a true baseline instead waits until the rearm expression is true, as after a run.
    /// </summary>
    WaitForFalseToTrue
}
