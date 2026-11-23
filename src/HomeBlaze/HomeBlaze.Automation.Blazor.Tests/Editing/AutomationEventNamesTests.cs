using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class AutomationEventNamesTests
{
    [Theory]
    [InlineData("AutomationStateChangedEvent", "Automation state changed")]
    [InlineData("ButtonPressed", "Button pressed")]
    [InlineData("Event", "Event")]
    [InlineData("", "")]
    public void WhenAnEventTypeNameIsHumanized_ThenTheSuffixIsDroppedAndTheWordsAreSplit(string name, string expected)
    {
        // Act
        var display = AutomationEventNames.Humanize(name);

        // Assert
        Assert.Equal(expected, display);
    }

    [Theory]
    [InlineData("B", "B", new[] { "A", "B" }, "B")]
    [InlineData("C", "C", new[] { "A", "B" }, "A")]
    [InlineData("C", "C", new string[0], "C")]
    [InlineData("", "", new string[0], "")]
    [InlineData("Pressed", "StateChanged", new[] { "Released", "Pressed" }, "Pressed")]
    [InlineData("Pressed", "Released", new[] { "Released", "Tilted" }, "Released")]
    public void WhenTheSourceChanges_ThenTheChosenEventTypeIsKeptWhileTheSourceIsUnknownOrStillPublishesIt(
        string chosen, string current, string[] available, string expected)
    {
        // Act
        var eventType = AutomationEventNames.Choose(chosen, current, available);

        // Assert
        Assert.Equal(expected, eventType);
    }
}
