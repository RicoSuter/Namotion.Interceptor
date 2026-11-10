using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor.Testing;
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
    public async Task WhenTheRulesAreReordered_ThenRulesOnTheSameEventRunInTheNewOrder()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.RuleOrder = ["a", "b"];
            automation.Rules = Keyed(
                ("a", On("A", OnEvent("/Button", "TestPressEvent"), Set("/Blinds/Position", "1"))),
                ("b", On("B", OnEvent("/Button", "TestPressEvent"), Set("/Blinds/Position", "2"))));
        });
        host.Button.Press("Release");
        await host.FlushAsync();
        var positionBeforeTheReorder = host.Blinds.Position;

        // Act
        host.Automation.RuleOrder = ["b", "a"];
        await host.SaveAsync();
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal(2, positionBeforeTheReorder);
        Assert.Equal(1, host.Blinds.Position);
    }

    [Fact]
    public async Task WhenAnEarlierRuleChangesWhatTheFilterOfALaterRuleReads_ThenTheFilterSeesTheChange()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation =>
        {
            automation.RuleOrder = ["position", "close"];
            automation.Rules = Keyed(
                ("position", On("Position", OnEvent("/Button", "TestPressEvent"), Set("/Blinds/Position", "1"))),
                ("close", On("Close", OnEvent("/Button", "TestPressEvent", "/Blinds/Position == 1"), Invoke("/Blinds/Close"))));
        });

        // Act
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.Position);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("close").RunCount);
    }

    [Fact]
    public async Task WhenARuleIsSwitchedOnWhileAnEventIsQueued_ThenTheRuleHandlesOnlyLaterEvents()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", OnEvent("/Button", "TestPressEvent"), Invoke("/Blinds/Wait"))),
            ("close", Create("Close", AutomationRuleMode.Off, [OnEvent("/Button", "TestPressEvent")], [Invoke("/Blinds/Close")]))));
        host.Button.Press("Release");
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);
        host.Button.Press("Release");

        // Act
        host.Rule("close").Mode = AutomationRuleMode.On;
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        host.Blinds.Release();
        await host.FlushAsync();
        var closesForTheQueuedPress = host.Blinds.CloseCount;
        host.Button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, closesForTheQueuedPress);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenARuleIsSwitchedOnWhileTheWorkerIsBusy_ThenAPressAfterTheSaveIsHandled()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("wait", On("Wait", OnEvent("/Button", "TestPressEvent"), Invoke("/Blinds/Wait"))),
            ("close", Create("Close", AutomationRuleMode.Off, [OnEvent("/Button", "TestPressEvent")], [Invoke("/Blinds/Close")]))));
        host.Button.Press("Release");
        await AsyncTestHelpers.WaitUntilAsync(() => host.Blinds.IsWaiting);

        // Act
        host.Rule("close").Mode = AutomationRuleMode.On;
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        host.Button.Press("Release");
        host.Blinds.Release();
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1L, host.Rule("close").RunCount);
    }

    [Fact]
    public async Task WhenAnEventSourceRefusesTheSubscriptionAtTheStart_ThenTheOtherRulesRunAndTheRuleSaysWhy()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close"))),
            ("press", On("Press", OnEvent("/Refusing", "TestPressEvent"), Invoke("/Blinds/Open")))));
        host.Graph.Add("Refusing", new TestRefusingEventSource());
        await host.RestartAsync();

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.StartsWith("On: /Refusing refused the subscription to TestPressEvent", host.Rule("press").Status);
    }

    [Fact]
    public async Task WhenEndingASubscriptionFails_ThenTheRuleSubscribesAgainWhenItIsSwitchedOnAgain()
    {
        // Arrange
        var source = new TestFailingUnsubscribeEventSource();
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("press", On("Press", OnEvent("/Source", "TestPressEvent"), Invoke("/Blinds/Close")))));
        host.Graph.Add("Source", source);
        await host.FlushAsync();

        // Act
        host.Rule("press").Mode = AutomationRuleMode.Off;
        await host.SaveAsync();
        host.Rule("press").Mode = AutomationRuleMode.On;
        await host.SaveAsync();
        source.Press();
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(ServiceStatus.Running, host.Automation.Status);
        Assert.Contains(host.Logger.Messages, message => message.Contains("could not end its subscription", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenAnEventSourceRefusesTheSubscriptionOnASave_ThenTheNewPropertyRulesOfTheSaveRun()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("close", On("Close", When("/Weather/Gust > 8"), Invoke("/Blinds/Close")))));
        host.Graph.Add("Refusing", new TestRefusingEventSource());
        await host.FlushAsync();

        // Act
        await host.SaveAsync(Keyed(
            ("close", On("Close", When("/Weather/Gust > 9"), Invoke("/Blinds/Close"))),
            ("press", On("Press", OnEvent("/Refusing", "TestPressEvent"), Invoke("/Blinds/Open")))));
        host.Weather.Gust = 10;
        await host.FlushAsync();

        // Assert
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
