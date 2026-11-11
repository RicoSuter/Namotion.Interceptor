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

    /// <summary>Every trigger waits for its condition to become true.</summary>
    public const string Armed = "Armed";

    /// <summary>A trigger was rearmed while its condition is true and fires once it became false and true again.</summary>
    public const string ArmedWaitingUntilFalse = "Armed, waiting until false";

    /// <summary>Every condition was true when the rule became active, and each trigger waits for it to become false.</summary>
    public const string AlreadyTrueWaitingUntilFalse = "Already true, waiting until false";

    /// <summary>Every condition was true when the rule became active, and the triggers wait to rearm.</summary>
    public const string AlreadyTrueWaitingToRearm = "Already true, waiting to rearm";

    /// <summary>The triggers fired and wait to rearm.</summary>
    public const string TriggeredWaitingToRearm = "Triggered, waiting to rearm";
}
