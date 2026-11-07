using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Automation.Runtime;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Automation;

public partial class AutomationRule
{
    /// <summary>
    /// Evaluates the triggers and Only if against the current values and renders the actions with their arguments, in
    /// every mode. Executes nothing and records nothing. A value the caller may not read, such as for an agent that calls
    /// this query through MCP, is missing.
    /// </summary>
    [Query(
        Title = "Test Now",
        Description = "Evaluates the triggers and Only if against the current values and shows the actions with their arguments. Executes and records nothing.",
        Icon = "Science",
        Position = 1)]
    public AutomationRuleTestResult TestNow()
    {
        var automation = this.TryGetFirstParentOrSelf<Automation>();
        return automation is null
            ? new AutomationRuleTestResult { Error = "The rule is not part of an automation." }
            : RuleTester.Test(automation, this);
    }
}
