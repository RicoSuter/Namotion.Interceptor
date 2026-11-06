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
    [InlineData("B", new[] { "A", "B" }, "B")]
    [InlineData("C", new[] { "A", "B" }, "A")]
    [InlineData("C", new string[0], "C")]
    [InlineData("", new string[0], "")]
    public void WhenTheSourceChanges_ThenTheEventTypeIsKeptWhileTheSourceIsUnknownOrStillPublishesIt(string current, string[] available, string expected)
    {
        // Act
        var chosen = AutomationEventNames.Choose(current, available);

        // Assert
        Assert.Equal(expected, chosen);
    }
}
