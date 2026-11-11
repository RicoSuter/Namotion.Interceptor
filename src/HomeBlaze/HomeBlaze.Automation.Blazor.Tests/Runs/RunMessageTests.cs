using HomeBlaze.Automation.Blazor.Runs;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RunMessageTests
{
    [Fact]
    public void WhenARunCalledAnOperationAndChangedTheState_ThenItHasOneActionPerPart()
    {
        // Act
        var actions = RunMessage.Parse("Ran /House/Hue/Rooms[a2e4]/SetBrightness(brightness: 0.3); ChangeState(Nacht)");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("SetBrightness", "/House/Hue/Rooms[a2e4]", "brightness: 0.3"),
            new RunMessageAction("ChangeState", null, "Nacht")
        ], actions);
    }

    [Fact]
    public void WhenARunWroteAProperty_ThenTheActionNamesThePropertyAndTheValue()
    {
        // Act
        var actions = RunMessage.Parse("Ran /House/Lamp/On = true");

        // Assert
        Assert.Equal([new RunMessageAction("On", "/House/Lamp", "= true")], actions);
    }

    [Fact]
    public void WhenTheMessageIsAFailure_ThenItIsOneTextAction()
    {
        // Act
        var actions = RunMessage.Parse("/House/Lamp/Toggle() failed: offline");

        // Assert
        Assert.Equal([new RunMessageAction(null, null, "/House/Lamp/Toggle() failed: offline")], actions);
    }

    [Fact]
    public void WhenAPartDoesNotMatchADescription_ThenItIsKeptAsText()
    {
        // Act
        var actions = RunMessage.Parse("Ran /House/Lamp/Toggle(); /House/Very/Long/Pa...");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Toggle", "/House/Lamp", ""),
            new RunMessageAction(null, null, "/House/Very/Long/Pa...")
        ], actions);
    }

    [Fact]
    public void WhenTheMessageIsNull_ThenThereAreNoActions()
    {
        // Act & Assert
        Assert.Empty(RunMessage.Parse(null));
    }

    [Fact]
    public void WhenARunHadNoActions_ThenThereAreNoActions()
    {
        // Act & Assert
        Assert.Empty(RunMessage.Parse("Ran "));
    }

    [Fact]
    public void WhenAnOperationHasSeveralArguments_ThenTheDetailHasAllOfThem()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Blinds/MoveTo(position: 40, speed: \"slow\")");

        // Assert
        Assert.Equal([new RunMessageAction("MoveTo", "/Blinds", "position: 40, speed: \"slow\"")], actions);
    }

    [Fact]
    public void WhenTheTargetIsOnTheRoot_ThenTheSubjectIsTheRoot()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Restart(); /Mode = 2");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Restart", "/", ""),
            new RunMessageAction("Mode", "/", "= 2")
        ], actions);
    }

    [Fact]
    public void WhenAKeyHoldsParenthesesAnEqualsSignOrSpaces_ThenItStaysInThePath()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Rooms[Living (main)]/Lamp/Toggle(); /Files/My Folder (old)/Lamp/Off(); /Rooms[a = b]/Lamp/On = false");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Toggle", "/Rooms[Living (main)]/Lamp", ""),
            new RunMessageAction("Off", "/Files/My Folder (old)/Lamp", ""),
            new RunMessageAction("On", "/Rooms[a = b]/Lamp", "= false")
        ], actions);
    }

    [Fact]
    public void WhenAnArgumentOrValueHoldsAPathLikeText_ThenItStaysInTheDetail()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Speaker/Say(text: \"see /Docs/Open(now)\"); /Display/Text = a/B = c");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Say", "/Speaker", "text: \"see /Docs/Open(now)\""),
            new RunMessageAction("Text", "/Display", "= a/B = c")
        ], actions);
    }

    [Theory]
    [InlineData("./Toggle()")]
    [InlineData("../Lamp/Toggle()")]
    [InlineData("Lamp/On = true")]
    [InlineData("LastNote = \"hello\"")]
    public void WhenThePathIsRelativeToTheRule_ThenTheActionIsTextWithoutASubject(string description)
    {
        // Act
        var actions = RunMessage.Parse("Ran " + description);

        // Assert
        Assert.Equal([new RunMessageAction(null, null, description)], actions);
    }

    [Fact]
    public void WhenTheMessageWasShortenedInAValue_ThenTheValueKeepsTheEllipsis()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Display/Text = a long te...");

        // Assert
        Assert.Equal([new RunMessageAction("Text", "/Display", "= a long te...")], actions);
    }
}
