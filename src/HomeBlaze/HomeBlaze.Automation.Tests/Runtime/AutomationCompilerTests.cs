using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationCompilerTests
{
    private const string EventOnly = "'event' is only available in event filters and in the arguments of a rule with an event trigger";

    private static AutomationRule Rule(
        string? name = "Storm",
        AutomationTrigger[]? triggers = null,
        AutomationAction[]? actions = null,
        string? guard = null,
        string? inState = null) => new()
    {
        Name = name,
        Mode = AutomationRuleMode.On,
        Guard = guard,
        InState = inState,
        Triggers = triggers ?? [new PropertyTrigger { Expression = "/Weather/Gust > 8" }],
        Actions = actions ?? [new InvokeOperationAction { Id = "close", OperationPath = "/Blinds/Close" }]
    };

    [Fact]
    public void WhenTheConfigurationIsValid_ThenTheRulesAreCompiledInTheConfiguredOrder()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule>
        {
            ["b"] = Rule("B", [
                new PropertyTrigger { Expression = "now - ./LastTriggeredAt > 30m", Rearm = "/Weather/Gust < 6" },
                new SubjectEventTrigger { SubjectPath = "/Button", EventType = "TestPressEvent", Filter = "event/Gesture == Release" }]),
            ["a"] = Rule("A"),
            ["c"] = Rule("C")
        };

        // Act
        var isCompiled = AutomationCompiler.TryCompile(["Day", "Night"], "Day", rules, ["c", "missing"], out var compiled, out var error);

        // Assert
        Assert.True(isCompiled, error);
        Assert.NotNull(compiled);
        Assert.Equal(["c", "a", "b"], compiled.Rules.Select(rule => rule.Key).ToArray());
        var ruleB = compiled.Rules[2];
        var propertyTrigger = Assert.Single(ruleB.PropertyTriggers);
        Assert.True(propertyTrigger.UsesTime);
        Assert.Equal(["./LastTriggeredAt", "/Weather/Gust"], propertyTrigger.PropertyPaths.ToArray());
        Assert.Equal(1, Assert.Single(ruleB.EventTriggers).Index);
        Assert.True(ruleB.AllowsEvent);
        Assert.True(compiled.HasState("Night"));
        Assert.False(compiled.HasState("Dusk"));
    }

    public static TheoryData<string[], string?, AutomationRule, string> InvalidConfigurations => new()
    {
        { ["Day", "Day"], "Day", Rule(), "The state 'Day' is defined twice" },
        { ["Day"], null, Rule(), "Choose the initial state" },
        { ["Day"], "Night", Rule(), "The initial state 'Night' is not a state" },
        { [], "Day", Rule(), "An initial state needs states" },
        { ["Day"], "Day", Rule(inState: "Night"), "Rule 'Storm': 'Night' is not a state" },
        { ["Day"], "Day", Rule(actions: [new ChangeStateAction { Id = "night", TargetState = "Night" }]), "Rule 'Storm': action 'night': 'Night' is not a state" },
        { [], null, Rule(triggers: [], actions: []), "Rule 'Storm': it has no trigger; it has no action" },
        { [], null, Rule(name: null, triggers: []), "Rule 'storm': it has no trigger" },
        { [], null, Rule(triggers: [new PropertyTrigger { Expression = "event/Gesture == Release" }]), "Rule 'Storm': trigger 1: " + EventOnly },
        { [], null, Rule(triggers: [new PropertyTrigger { Expression = "/Weather/Gust > 8", For = TimeSpan.FromSeconds(-1) }]), "Rule 'Storm': trigger 1: the hold cannot be negative" },
        { [], null, Rule(triggers: [new SubjectEventTrigger { SubjectPath = "/Button" }]), "Rule 'Storm': trigger 1: the event type is missing" },
        { [], null, Rule(guard: "event/Gesture == Release"), "Rule 'Storm': guard: " + EventOnly },
        { [], null, Rule(actions: [new InvokeOperationAction { OperationPath = "" }]), "Rule 'Storm': action 1: the operation path is missing" },
        {
            [], null,
            Rule(actions: [new InvokeOperationAction
            {
                Id = "notify",
                OperationPath = "/Notifier/SendNotification",
                Arguments = new Dictionary<string, string> { ["notification.Message"] = "{{ /Weather/Gust" }
            }]),
            "Rule 'Storm': action 'notify': argument 'notification.Message': '{{' is not closed at position 0"
        }
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void WhenTheConfigurationIsInvalid_ThenTheErrorSaysWhereAndWhy(string[] states, string? initialState, AutomationRule rule, string expected)
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule> { ["storm"] = rule };

        // Act
        var isCompiled = AutomationCompiler.TryCompile(states, initialState, rules, [], out var compiled, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Null(compiled);
        Assert.Equal(expected, error);
    }
}
