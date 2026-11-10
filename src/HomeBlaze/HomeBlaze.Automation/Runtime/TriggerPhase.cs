namespace HomeBlaze.Automation.Runtime;

/// <summary>Where a property trigger stands between two observations.</summary>
internal enum TriggerPhase
{
    /// <summary>Not active: observations are ignored.</summary>
    Inactive,

    /// <summary>Waits for a first known value as the baseline of <see cref="TriggerActivation.WaitForFalseToTrue"/>.</summary>
    AwaitingBaseline,

    /// <summary>A true expression starts the hold, or fires without one.</summary>
    Armed,

    /// <summary>The expression is true and the hold has not elapsed.</summary>
    Holding,

    /// <summary>
    /// Fired, and its run did not run yet, such as because Only if rejected it; runs when a request runs while the
    /// expression stays true, and is armed again without a run when the expression is false or unknown.
    /// </summary>
    Pending,

    /// <summary>Fired, or started true with a rearm expression; waits until the expression is false, or the rearm expression is true.</summary>
    WaitingToRearm,

    /// <summary>The activation found the expression true; waits until it is false.</summary>
    WaitingForFalse,

    /// <summary>
    /// The rearm expression became true while the expression was still true; arms once the expression is false or
    /// unknown, so that only a new change to true fires.
    /// </summary>
    RearmedWhileTrue
}
