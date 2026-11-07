using HomeBlaze.Automation;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// A dry-run automation of a washing machine with the states Idle and Running over the demo motor, with the role
/// Operator, so that an action a test adds may call the motor. Its conditions never become true there, so no rule runs
/// during a test.
/// </summary>
internal static class WasherAutomation
{
    public static void Configure(HomeBlaze.Automation.Automation automation)
    {
        automation.Name = "Washer";
        automation.Roles = ["Operator"];
        automation.States = ["Idle", "Running"];
        automation.InitialState = "Idle";
        automation.RuleOrder = ["started", "finished"];
        automation.Rules = new Dictionary<string, AutomationRule>
        {
            ["started"] = new()
            {
                Name = "Started",
                Mode = AutomationRuleMode.DryRun,
                InState = "Idle",
                Triggers = [new PropertyTrigger { Expression = "/Demo/Motor/Temperature > 1000" }],
                Actions = [new ChangeStateAction { Id = "state-1", TargetState = "Running" }]
            },
            ["finished"] = new()
            {
                Name = "Finished",
                Mode = AutomationRuleMode.DryRun,
                InState = "Running",
                Triggers = [new PropertyTrigger { Expression = "/Demo/Motor/Temperature < 5", For = TimeSpan.FromMinutes(10) }],
                Actions = [new ChangeStateAction { Id = "state-1", TargetState = "Idle" }]
            }
        };
    }

    /// <summary>The washer with one action in the rule with the key: a call of the operation with the arguments, with the id call-1.</summary>
    public static Action<HomeBlaze.Automation.Automation> WithCall(string ruleKey, string operationPath, params (string Key, string Value)[] arguments) =>
        automation =>
        {
            Configure(automation);
            automation.Rules[ruleKey].Actions =
            [
                new InvokeOperationAction
                {
                    Id = "call-1",
                    OperationPath = operationPath,
                    Arguments = arguments.ToDictionary(argument => argument.Key, argument => argument.Value)
                }
            ];
        };
}
