using System.Text.Json;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Automation.Tests;

public class AutomationRuleSerializationTests
{
    private static ConfigurableSubjectSerializer CreateSerializer()
    {
        var typeProvider = new TypeProvider().AddAssembly(typeof(AutomationRule).Assembly);
        return new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
    }

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
            Guard = "not /Blinds/IsMoving",
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
        Assert.Equal("InvokeOperation", document.RootElement.GetProperty("actions")[1].GetProperty("$type").GetString());
        Assert.Equal(rule.Triggers, copy.Triggers);
        Assert.Equal(rule.Actions[2], copy.Actions[2]);
        Assert.Equal(rule.Actions[3], copy.Actions[3]);
        var notify = Assert.IsType<InvokeOperationAction>(copy.Actions[1]);
        Assert.Equal(ActionErrorPolicy.StopRule, notify.OnError);
        Assert.Equal("Gust {{ /Weather/Gust }} m/s", notify.Arguments["notification.Message"]);
        Assert.Equal(AutomationRuleMode.On, copy.Mode);
        Assert.Equal("Day", copy.InState);
        Assert.Equal("not /Blinds/IsMoving", copy.Guard);
    }

    [Fact]
    public void WhenTriggerHasAnUnknownDiscriminator_ThenTheTriggersAreRejected()
    {
        // Arrange
        var serializer = CreateSerializer();
        const string json = """
            {
              "$type": "HomeBlaze.Automation.AutomationRule",
              "name": "Unknown",
              "triggers": [ { "$type": "TimerTrigger", "interval": "00:01:00" } ]
            }
            """;
        var skipped = new List<SkippedConfigurationProperty>();

        // Act
        var rule = (AutomationRule)serializer.Deserialize(json, created: null, skipped.Add)!;

        // Assert
        Assert.Empty(rule.Triggers);
        Assert.Equal(nameof(AutomationRule.Triggers), Assert.Single(skipped).PropertyName);
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
