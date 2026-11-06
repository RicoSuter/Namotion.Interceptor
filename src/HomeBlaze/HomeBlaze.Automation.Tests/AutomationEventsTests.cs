using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationEventsTests
{
    [Fact]
    public void WhenTheEventTypesOfASubjectAreRequested_ThenTheyAreItsObservableEvents()
    {
        // Act
        var buttonEvents = AutomationEvents.GetEventTypes(typeof(TestButton));
        var automationEvents = AutomationEvents.GetEventTypes(typeof(Automation));

        // Assert
        Assert.Equal([typeof(TestPressEvent), typeof(TestTiltEvent)], buttonEvents.OrderBy(type => type.Name).ToArray());
        Assert.Equal([typeof(AutomationStateChangedEvent)], automationEvents.ToArray());
    }

    [Fact]
    public void WhenThePayloadFieldsAreRequested_ThenTheyAreThePublicPropertiesByName()
    {
        // Act
        var fields = AutomationEvents.GetPayloadFields(typeof(TestPressEvent));

        // Assert
        Assert.Equal(["DeviceId", "Gesture", "Timestamp"], fields.Select(field => field.Name).ToArray());
        Assert.Equal(typeof(string), fields.Single(field => field.Name == "Gesture").Type);
    }

    [Fact]
    public async Task WhenTheSourceOfATriggerIsResolved_ThenRelativePathsStartAtTheRule()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
            automation.Rules = Keyed(("retract", On("Retract", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));

        // Act
        var button = AutomationEvents.ResolveSource(host.Automation, "retract", "/Button");
        var ownFromSavedRule = AutomationEvents.ResolveSource(host.Automation, "retract", AutomationEvents.OwnAutomationPath);
        var ownFromNewRule = AutomationEvents.ResolveSource(host.Automation, "new", AutomationEvents.OwnAutomationPath);
        var missing = AutomationEvents.ResolveSource(host.Automation, "retract", "/Nothing");

        // Assert
        Assert.Same(host.Button, button);
        Assert.Same(host.Automation, ownFromSavedRule);
        Assert.Same(host.Automation, ownFromNewRule);
        Assert.Null(missing);
    }
}
