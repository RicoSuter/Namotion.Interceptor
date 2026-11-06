using HomeBlaze.Automation.Tests.Testing;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationEventTriggerTests
{
    private static Action<Automation> ButtonRule(string subjectPath, params AutomationAction[] actions) => automation => automation.Rules = Keyed(
        ("press", Create("Press", AutomationRuleMode.On, [OnEvent(subjectPath, "TestPressEvent", "event/Gesture == Release")], actions)));

    [Fact]
    public async Task WhenMatchingEventsArrive_ThenEveryOccurrenceRunsTheRule()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ButtonRule("/Button", Invoke("/Blinds/Close")));

        // Act
        host.Button.Press("Release");
        host.Button.Press("Release");
        host.Button.Press("Hold");
        await host.FlushAsync();

        // Assert
        Assert.Equal(2, host.Blinds.CloseCount);
        Assert.Equal(2L, host.Rule("press").RunCount);
    }

    [Fact]
    public async Task WhenAnEventStartsTheRun_ThenTheArgumentsReadThePayloadAndThePublisher()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ButtonRule("/Button", Set("/Weather/Name", "{{ event/Gesture }} from {{ subject/Name }}")));
        host.Button.Name = "Hall";

        // Act
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal("Release from Hall", host.Weather.Name);
    }

    [Fact]
    public async Task WhenTheSourceIsReplaced_ThenTheTriggerFollowsTheNewSource()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(ButtonRule("/Button", Invoke("/Blinds/Close")));
        var replacement = new TestButton();
        host.Graph.Add("Button", replacement);
        await host.FlushAsync();

        // Act
        host.Button.Press("Release");
        await host.FlushAsync();
        var closesAfterThePressOfTheOldSource = host.Blinds.CloseCount;
        replacement.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, closesAfterThePressOfTheOldSource);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheSourceIsMissing_ThenTheRuleSaysWhichSubjectItWaitsFor()
    {
        // Act
        await using var host = await AutomationTestHost.StartAsync(ButtonRule("/Doorbell", Invoke("/Blinds/Close")));

        // Assert
        Assert.Equal("On: No subject at /Doorbell", host.Rule("press").Status);
    }
}
