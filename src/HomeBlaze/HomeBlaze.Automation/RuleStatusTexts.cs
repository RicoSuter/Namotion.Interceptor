namespace HomeBlaze.Automation;

/// <summary>
/// The texts of <see cref="AutomationRule.Status"/> for a rule that is off or whose triggers are settled. Except for
/// <see cref="Off"/>, the status puts the mode and <c>": "</c> in front of them, and can follow them with
/// <c>"; not run: "</c> and the reason why its last request did not run.
/// </summary>
public static class RuleStatusTexts
{
    /// <summary>The whole status of a rule in the mode <see cref="AutomationRuleMode.Off"/>.</summary>
    public const string Off = "Off";

    /// <summary>
    /// The rule has no trigger, or at least one of its triggers can fire: an event trigger, or a property trigger that is
    /// armed or waits for a first value of its condition.
    /// </summary>
    public const string Armed = "Armed";

    /// <summary>
    /// No trigger can fire now, and at least one was rearmed while its condition is still true; that one fires once its
    /// condition has become false and then true again.
    /// </summary>
    public const string ArmedWaitingUntilFalse = "Armed, waiting until false";

    /// <summary>Every trigger found its condition true when the rule became active and waits for it to become false.</summary>
    public const string AlreadyTrueWaitingUntilFalse = "Already true, waiting until false";

    /// <summary>
    /// Every trigger waits to rearm or for its condition to become false, and each found its condition true when the
    /// rule became active, so none has fired since.
    /// </summary>
    public const string AlreadyTrueWaitingToRearm = "Already true, waiting to rearm";

    /// <summary>
    /// Every trigger waits to rearm or for its condition to become false, and at least one of them fired since the rule
    /// became active.
    /// </summary>
    public const string TriggeredWaitingToRearm = "Triggered, waiting to rearm";
}
