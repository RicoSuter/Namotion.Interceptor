using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation;

/// <summary>The order in which the rules of an automation process an input.</summary>
public static class AutomationRuleOrder
{
    /// <summary>
    /// The rules with their keys in the order they process an input: those in <see cref="Automation.RuleOrder"/> first, then the
    /// others in key order. Reads <see cref="Automation.Rules"/> once, so the pairs are consistent even when the configuration is
    /// replaced meanwhile.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, AutomationRule>> GetRulesInOrder(this Automation automation)
    {
        var rules = automation.Rules;
        return [.. AutomationCompiler.OrderKeys(rules.Keys, automation.RuleOrder).Select(key => new KeyValuePair<string, AutomationRule>(key, rules[key]))];
    }
}
