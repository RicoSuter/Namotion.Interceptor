using HomeBlaze.Automation.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Tests;

public class AutomationValidatorTests
{
    private const string EventOnly = "'event' is only available in event filters and in the arguments of a rule with an event trigger";

    private static AutomationRule Rule(
        string? name = "Storm",
        string expression = "/Weather/Gust > 8",
        string? guard = null,
        AutomationAction[]? actions = null) => new()
    {
        Name = name,
        Mode = AutomationRuleMode.On,
        Guard = guard,
        Triggers = [new PropertyTrigger { Expression = expression }],
        Actions = actions ?? [new InvokeOperationAction { Id = "close", OperationPath = "/Blinds/Close" }]
    };

    [Fact]
    public void WhenTheConfigurationIsValid_ThenThereAreNoErrors()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule> { ["a"] = Rule(), ["b"] = Rule("Rain") };

        // Act
        var errors = AutomationValidator.Validate(["Day", "Night"], "Day", rules, ["b"]);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void WhenARuleIsInvalid_ThenItsErrorsCarryItsKeyWithoutItsName()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule> { ["a"] = Rule(guard: "/Weather/Gust >", actions: []) };

        // Act
        var errors = AutomationValidator.Validate([], null, rules, []);

        // Assert
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal("a", error.RuleKey));
        Assert.StartsWith("guard: ", errors[0].Message);
        Assert.Equal("it has no action", errors[1].Message);
    }

    [Fact]
    public void WhenARuleIsNull_ThenItsKeyReportsAnEmptyRule()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule> { ["a"] = null! };

        // Act
        var errors = AutomationValidator.Validate(null!, null, rules, null!);

        // Assert
        Assert.Equal(["it has no trigger", "it has no action"], errors.Select(error => error.Message).ToArray());
        Assert.All(errors, error => Assert.Equal("a", error.RuleKey));
    }

    [Fact]
    public void WhenRulesHaveErrors_ThenTheErrorsFollowTheRuleOrderAndNotTheDictionaryOrder()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule>
        {
            ["a"] = Rule(actions: []),
            ["b"] = Rule("Rain", actions: [])
        };

        // Act
        var errors = AutomationValidator.Validate([], null, rules, ["b", "a"]);

        // Assert
        Assert.Equal(["b", "a"], errors.Select(error => error.RuleKey!).ToArray());
    }

    [Fact]
    public void WhenAStateIsDefinedTwice_ThenTheErrorBelongsToTheAutomation()
    {
        // Act
        var errors = AutomationValidator.Validate(["Day", "Day"], "Day", new Dictionary<string, AutomationRule>(), []);

        // Assert
        var error = Assert.Single(errors);
        Assert.Null(error.RuleKey);
        Assert.Equal("The state 'Day' is defined twice", error.Message);
    }

    [Fact]
    public void WhenAStateNameStartsOrEndsWithWhiteSpace_ThenItIsAnError()
    {
        // Act
        var errors = AutomationValidator.Validate([" Day", "Night"], " Day", new Dictionary<string, AutomationRule>(), []);

        // Assert
        var error = Assert.Single(errors);
        Assert.Null(error.RuleKey);
        Assert.Equal("The state ' Day' starts or ends with white space", error.Message);
    }

    [Fact]
    public void WhenTwoRulesHaveTheSameName_ThenTheLaterRuleHasAnError()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule> { ["a"] = Rule("Storm"), ["b"] = Rule("storm ") };

        // Act
        var errors = AutomationValidator.Validate([], null, rules, ["a", "b"]);

        // Assert
        var error = Assert.Single(errors);
        Assert.Equal("b", error.RuleKey);
        Assert.Equal("Another rule is named 'storm'", error.Message);
    }

    [Fact]
    public void WhenTwoActionsOfARuleHaveTheSameId_ThenTheRuleHasAnError()
    {
        // Arrange
        var rules = new Dictionary<string, AutomationRule>
        {
            ["a"] = Rule(actions: [
                new InvokeOperationAction { Id = "close", OperationPath = "/Blinds/Close" },
                new InvokeOperationAction { Id = "close", OperationPath = "/Blinds/Open" }])
        };

        // Act
        var errors = AutomationValidator.Validate([], null, rules, []);

        // Assert
        var error = Assert.Single(errors);
        Assert.Equal("a", error.RuleKey);
        Assert.Equal("The action id 'close' is used 2 times", error.Message);
    }

    [Theory]
    [InlineData("/Weather/Gust > 8", false, null)]
    [InlineData("event/Gesture == Release", true, null)]
    [InlineData("event/Gesture == Release", false, EventOnly)]
    [InlineData("value > 1", false, "'value' is not available in automations")]
    public void WhenAnExpressionIsValidated_ThenTheVariableRulesOfAutomationsApply(string expression, bool allowsEvent, string? expected)
    {
        // Act
        var error = AutomationValidator.ValidateExpression(expression, allowsEvent);

        // Assert
        Assert.Equal(expected, error);
    }

    [Fact]
    public void WhenATemplateIsValidated_ThenItsExpressionsAreChecked()
    {
        // Act
        var valid = AutomationValidator.ValidateTemplate("Gust {{ /Weather/Gust }} m/s", allowsEvent: false);
        var invalid = AutomationValidator.ValidateTemplate("Gust {{ event/Gust }}", allowsEvent: false);

        // Assert
        Assert.Null(valid);
        Assert.Equal(EventOnly, invalid);
    }

    [Fact]
    public void WhenTheRuleKeysAreRequested_ThenTheyFollowTheRuleOrderAndThenTheKeys()
    {
        // Arrange
        var graph = new TestGraph();
        var services = new AutomationServices(graph.Resolver, TimeProvider.System, new AllowAllAutomationAuthorizer(), new ServiceCollection().BuildServiceProvider());
        using var automation = new Automation(services, NullLogger<Automation>.Instance)
        {
            Rules = new Dictionary<string, AutomationRule> { ["b"] = Rule("B"), ["a"] = Rule("A"), ["c"] = Rule("C") },
            RuleOrder = ["c", "missing"]
        };

        // Act
        var rules = automation.GetRulesInOrder();

        // Assert
        Assert.Equal(["c", "a", "b"], rules.Select(pair => pair.Key).ToArray());
        Assert.All(rules, pair => Assert.Same(automation.Rules[pair.Key], pair.Value));
    }
}
