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

    [Fact]
    public void WhenSeveralRulesAndTheStatesAreInvalid_ThenAllErrorsAreCombinedInTheCompilerOrder()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule>
        {
            ["a"] = Rule("A", triggers: []),
            ["b"] = Rule("B", actions: [])
        };

        // Act
        var isCompiled = AutomationCompiler.TryCompile(["Day", "Day"], "Day", rules, ["b"], out var compiled, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Null(compiled);
        Assert.Equal("The state 'Day' is defined twice; Rule 'B': it has no action; Rule 'A': it has no trigger", error);
    }

    [Fact]
    public void WhenOneRuleHasSeveralErrors_ThenGuardTriggerAndActionErrorsAreReportedInOrder()
    {
        // Arrange
        var rule = Rule(
            guard: "event/Gesture == Release",
            triggers: [new SubjectEventTrigger { SubjectPath = "/Button" }],
            actions: [new InvokeOperationAction { OperationPath = "" }]);

        // Act
        var isCompiled = AutomationCompiler.TryCompileRule("storm", rule, [], out var compiled, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Null(compiled);
        Assert.Equal(
            "Rule 'Storm': guard: " + EventOnly + "; trigger 1: the event type is missing; action 1: the operation path is missing",
            error);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 3)]
    public void WhenAPropertyTriggerExpressionIsBlank_ThenItIsRejected(string expression, int position)
    {
        // Arrange
        var rule = Rule(triggers: [new PropertyTrigger { Expression = expression }]);

        // Act
        var isCompiled = AutomationCompiler.TryCompileRule("storm", rule, [], out var compiled, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Null(compiled);
        Assert.Equal($"Rule 'Storm': trigger 1: expected a value at position {position}", error);
    }

    [Fact]
    public void WhenTheHoldIsZero_ThenTheTriggerIsAccepted()
    {
        // Arrange
        var rule = Rule(triggers: [new PropertyTrigger { Expression = "/Weather/Gust > 8", For = TimeSpan.Zero }]);

        // Act
        var isCompiled = AutomationCompiler.TryCompileRule("storm", rule, [], out var compiled, out var error);

        // Assert
        Assert.True(isCompiled, error);
        Assert.NotNull(compiled);
        Assert.Single(compiled.PropertyTriggers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenAStateNameIsBlank_ThenTheErrorSaysAStateNeedsAName(string blank)
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule>();

        // Act
        var isCompiled = AutomationCompiler.TryCompile(["Day", blank], "Day", rules, [], out _, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Equal("A state needs a name", error);
    }

    [Theory]
    [InlineData("", "1", "the property path is missing")]
    [InlineData("/Blinds/Level", "{{ /Weather/Gust", "value: '{{' is not closed at position 0")]
    public void WhenASetPropertyActionIsInvalid_ThenItIsRejected(string path, string value, string expected)
    {
        // Arrange
        var rule = Rule(actions: [new SetPropertyAction { Id = "set", PropertyPath = path, Value = value }]);

        // Act
        var isCompiled = AutomationCompiler.TryCompileRule("storm", rule, [], out var compiled, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Null(compiled);
        Assert.Equal($"Rule 'Storm': action 'set': {expected}", error);
    }

    [Fact]
    public void WhenTheRuleHasAnEventTrigger_ThenAnActionArgumentMayReadTheEvent()
    {
        // Arrange
        var rule = Rule(
            triggers: [new SubjectEventTrigger { SubjectPath = "/Button", EventType = "TestPressEvent" }],
            actions: [EventArgumentAction()]);

        // Act
        var isCompiled = AutomationCompiler.TryCompileRule("storm", rule, [], out var compiled, out var error);

        // Assert
        Assert.True(isCompiled, error);
        Assert.NotNull(compiled);
    }

    [Fact]
    public void WhenTheRuleHasNoEventTrigger_ThenAnActionArgumentCannotReadTheEvent()
    {
        // Arrange
        var rule = Rule(actions: [EventArgumentAction()]);

        // Act
        var isCompiled = AutomationCompiler.TryCompileRule("storm", rule, [], out var compiled, out var error);

        // Assert
        Assert.False(isCompiled);
        Assert.Null(compiled);
        Assert.Equal("Rule 'Storm': action 'notify': argument 'Message': " + EventOnly, error);
    }

    private static InvokeOperationAction EventArgumentAction() => new()
    {
        Id = "notify",
        OperationPath = "/Notifier/Send",
        Arguments = new Dictionary<string, string> { ["Message"] = "{{ event/Gesture }}" }
    };
}
