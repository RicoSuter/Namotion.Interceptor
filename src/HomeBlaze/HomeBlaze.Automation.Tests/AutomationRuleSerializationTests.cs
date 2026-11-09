using System.Text.Json;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Automation.Tests;

public sealed class AutomationRuleSerializationTests : IDisposable
{
    private readonly ServiceProvider _services = CreateServices();

    public void Dispose() => _services.Dispose();

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SubjectPathResolver(() => null));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHomeBlazeAutomation();
        return services.BuildServiceProvider();
    }

    private ConfigurableSubjectSerializer CreateSerializer() =>
        new(new TypeProvider().AddAssembly(typeof(AutomationRule).Assembly), _services);

    [Fact]
    public void WhenRuleIsSerialized_ThenTriggersAndActionsKeepTheirTypesAndValues()
    {
        // Arrange
        var serializer = CreateSerializer();
        var rule = new AutomationRule
        {
            Name = "Retract blinds",
            Mode = AutomationRuleMode.On,
            InState = "Day",
            OnlyIf = "not /Blinds/IsMoving",
            Triggers =
            [
                new PropertyTrigger { Expression = "/Weather/Gust > 8", For = TimeSpan.FromSeconds(20), Rearm = "/Weather/Gust < 6", OnActivation = TriggerActivation.WaitForFalseToTrue },
                new SubjectEventTrigger { SubjectPath = "/Button", EventType = "ButtonEvent", Filter = "event/ButtonState == Release" }
            ],
            Actions =
            [
                new InvokeOperationAction { Id = "close", OperationPath = "/Blinds/Close" },
                new InvokeOperationAction
                {
                    Id = "notify",
                    OperationPath = "/Notifier/SendNotification",
                    OnError = ActionErrorPolicy.StopRule,
                    Arguments = new Dictionary<string, string> { ["notification.Message"] = "Gust {{ /Weather/Gust }} m/s" }
                },
                new SetPropertyAction { Id = "position", PropertyPath = "/Blinds/Position", Value = "0" },
                new ChangeStateAction { Id = "night", TargetState = "Night" }
            ]
        };

        // Act
        var json = serializer.Serialize(rule);
        var copy = (AutomationRule)serializer.Deserialize(json)!;

        // Assert
        using var document = JsonDocument.Parse(json);
        Assert.Equal("PropertyTrigger", document.RootElement.GetProperty("triggers")[0].GetProperty("$type").GetString());
        Assert.Equal("20s", document.RootElement.GetProperty("triggers")[0].GetProperty("for").GetString());
        Assert.Equal("not /Blinds/IsMoving", document.RootElement.GetProperty("onlyIf").GetString());
        Assert.Equal("InvokeOperation", document.RootElement.GetProperty("actions")[1].GetProperty("$type").GetString());
        Assert.Equal(rule.Triggers, copy.Triggers);
        Assert.Equal(rule.Actions[2], copy.Actions[2]);
        Assert.Equal(rule.Actions[3], copy.Actions[3]);
        var notify = Assert.IsType<InvokeOperationAction>(copy.Actions[1]);
        Assert.Equal(ActionErrorPolicy.StopRule, notify.OnError);
        Assert.Equal("Gust {{ /Weather/Gust }} m/s", notify.Arguments["notification.Message"]);
        Assert.Equal(AutomationRuleMode.On, copy.Mode);
        Assert.Equal("Day", copy.InState);
        Assert.Equal("not /Blinds/IsMoving", copy.OnlyIf);
    }

    [Theory]
    [InlineData("\"10m\"", 0, 10, 0)]
    [InlineData("\"1h30m\"", 1, 30, 0)]
    [InlineData("\"20s\"", 0, 0, 20)]
    [InlineData("\"00:10:00\"", 0, 10, 0)]
    [InlineData("\"1.02:00:00\"", 26, 0, 0)]
    public void WhenTheHoldIsADurationOrATimeSpan_ThenItIsRead(string value, int hours, int minutes, int seconds)
    {
        // Arrange
        var serializer = CreateSerializer();
        var json = AutomationWithRule(RuleWithHold(value));

        // Act
        using var automation = (Automation)serializer.Deserialize(json)!;

        // Assert
        Assert.Equal(new TimeSpan(hours, minutes, seconds), Assert.IsType<PropertyTrigger>(Assert.Single(automation.Rules["retract"].Triggers)).For);
    }

    [Fact]
    public void WhenTheHoldIsNull_ThenTheTriggerHasNoHold()
    {
        // Arrange
        var serializer = CreateSerializer();

        // Act
        using var automation = (Automation)serializer.Deserialize(AutomationWithRule(RuleWithHold("null")))!;

        // Assert
        Assert.Null(Assert.IsType<PropertyTrigger>(Assert.Single(automation.Rules["retract"].Triggers)).For);
    }

    [Theory]
    [InlineData("\"10\"", "10")]
    [InlineData("\"ten minutes\"", "ten minutes")]
    [InlineData("600", "600")]
    public void WhenTheHoldIsNoDuration_ThenTheTriggerIsAnErrorThatNamesTheValue(string value, string expected)
    {
        // Arrange
        var serializer = CreateSerializer();

        // Act
        using var automation = (Automation)serializer.Deserialize(AutomationWithRule(RuleWithHold(value)))!;

        // Assert
        Assert.Equal(
            $"Rule 'Hold': trigger 1: '{expected}' is not a duration: use a text such as 10m, 1h30m or 00:10:00.",
            GetConfigurationError(automation));
    }

    private static string RuleWithHold(string value) => $$"""
        {
          "name": "Hold",
          "triggers": [ { "$type": "PropertyTrigger", "expression": "/Weather/Gust > 8", "for": {{value}} } ],
          "actions": [ { "$type": "InvokeOperation", "operationPath": "/Blinds/Close" } ]
        }
        """;

    private static string AutomationWithRule(string rule) => $$"""
        {
          "$type": "HomeBlaze.Automation.Automation",
          "rules": { "retract": {{rule}} }
        }
        """;

    private static string? GetConfigurationError(Automation automation)
    {
        AutomationCompiler.TryCompile(automation.States, automation.InitialState, automation.Rules, automation.RuleOrder, out _, out var error);
        return error;
    }

    [Theory]
    [InlineData("\"OnlyIf\": \"not /Blinds/IsMoving\"", "Rule 'Retract': 'OnlyIf' is no setting of a rule")]
    [InlineData("\"guard\": \"not /Blinds/IsMoving\"", "Rule 'Retract': 'guard' is no setting of a rule")]
    public void WhenTheRuleHasAnUnknownKey_ThenTheRuleIsAnErrorThatNamesTheKey(string setting, string expected)
    {
        // Arrange
        var serializer = CreateSerializer();
        var rule = $$"""
            {
              "name": "Retract",
              {{setting}},
              "triggers": [ { "$type": "PropertyTrigger", "expression": "/Weather/Gust > 8" } ],
              "actions": [ { "$type": "InvokeOperation", "operationPath": "/Blinds/Close" } ]
            }
            """;

        // Act
        using var automation = (Automation)serializer.Deserialize(AutomationWithRule(rule))!;

        // Assert
        Assert.Equal(expected, GetConfigurationError(automation));
    }

    [Theory]
    [InlineData("{ \"$type\": \"PropertyTrigger\", \"expression\": \"/A/B > 1\", \"hold\": \"10m\" }", "'hold' is no setting of PropertyTrigger")]
    [InlineData("{ \"$type\": \"PropertyTrigger\", \"expression\": \"/A/B > 1\", \"Rearm\": \"/A/B < 1\" }", "'Rearm' is no setting of PropertyTrigger")]
    [InlineData("{ \"$type\": \"TimerTrigger\", \"interval\": \"00:01:00\" }", "the type 'TimerTrigger' is unknown")]
    [InlineData("{ \"expression\": \"/A/B > 1\" }", "it has no $type")]
    public void WhenATriggerCannotBeRead_ThenTheRuleIsAnErrorThatNamesTheTrigger(string trigger, string expected)
    {
        // Arrange
        var serializer = CreateSerializer();
        var rule = $$"""
            {
              "name": "Retract",
              "triggers": [ { "$type": "PropertyTrigger", "expression": "/Weather/Gust > 8" }, {{trigger}} ],
              "actions": [ { "$type": "InvokeOperation", "operationPath": "/Blinds/Close" } ]
            }
            """;

        // Act
        using var automation = (Automation)serializer.Deserialize(AutomationWithRule(rule))!;

        // Assert
        Assert.Equal($"Rule 'Retract': trigger 2: {expected}", GetConfigurationError(automation));
        Assert.Equal(2, automation.Rules["retract"].Triggers.Length);
    }

    [Theory]
    [InlineData("{ \"$type\": \"SetProperty\", \"id\": \"b\", \"propertyPath\": \"/X/Z\", \"value\": 5 }", "action 'b': 'value': The JSON value could not be converted to System.String.")]
    [InlineData("{ \"$type\": \"InvokeOperation\", \"id\": \"b\", \"operationPath\": \"/X/Z\", \"onlyIf\": \"/X/Moving == false\" }", "action 'b': 'onlyIf' is no setting of InvokeOperation")]
    public void WhenAnActionCannotBeRead_ThenTheOtherActionsLoadAndTheRuleIsAnErrorThatNamesTheAction(string action, string expected)
    {
        // Arrange
        var serializer = CreateSerializer();
        var rule = $$"""
            {
              "name": "Retract",
              "triggers": [ { "$type": "PropertyTrigger", "expression": "/Weather/Gust > 8" } ],
              "actions": [ { "$type": "InvokeOperation", "id": "a", "operationPath": "/X/Y" }, {{action}} ]
            }
            """;

        // Act
        using var automation = (Automation)serializer.Deserialize(AutomationWithRule(rule))!;

        // Assert
        Assert.Equal($"Rule 'Retract': {expected}", GetConfigurationError(automation));
        Assert.Equal("/X/Y", Assert.IsType<InvokeOperationAction>(automation.Rules["retract"].Actions[0]).OperationPath);
    }

    [Fact]
    public void WhenAnAutomationWithElementsAndKeysThatCannotBeReadIsSaved_ThenTheyAreWrittenBackUnchanged()
    {
        // Arrange
        var serializer = CreateSerializer();
        const string rule = """
            {
              "name": "Retract",
              "OnlyIf": "not /Blinds/IsMoving",
              "triggers": [ { "$type": "PropertyTrigger", "expression": "/Weather/Gust > 8", "hold": "10m" } ],
              "actions": [
                { "$type": "InvokeOperation", "id": "a", "operationPath": "/X/Y" },
                { "$type": "SetProperty", "id": "b", "propertyPath": "/X/Z", "value": 5 }
              ]
            }
            """;
        using var automation = (Automation)serializer.Deserialize(AutomationWithRule(rule))!;

        // Act
        var saved = serializer.Serialize(automation);
        using var reloaded = (Automation)serializer.Deserialize(saved)!;

        // Assert
        using var document = JsonDocument.Parse(saved);
        var savedRule = document.RootElement.GetProperty("rules").GetProperty("retract");
        Assert.Equal("not /Blinds/IsMoving", savedRule.GetProperty("OnlyIf").GetString());
        Assert.Equal("10m", savedRule.GetProperty("triggers")[0].GetProperty("hold").GetString());
        Assert.Equal("/X/Y", savedRule.GetProperty("actions")[0].GetProperty("operationPath").GetString());
        Assert.Equal(5, savedRule.GetProperty("actions")[1].GetProperty("value").GetInt32());
        Assert.Equal(GetConfigurationError(automation), GetConfigurationError(reloaded));
    }

    [Fact]
    public void WhenRuleIsCreated_ThenItIsADryRunWithoutTriggersActionsOrRuns()
    {
        // Act
        var rule = new AutomationRule();

        // Assert
        Assert.Equal(AutomationRuleMode.DryRun, rule.Mode);
        Assert.Empty(rule.Triggers);
        Assert.Empty(rule.Actions);
        Assert.Null(rule.RunCount);
    }
}
