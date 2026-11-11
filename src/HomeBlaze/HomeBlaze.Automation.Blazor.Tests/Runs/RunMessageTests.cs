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
    public void WhenAValueEndsWithAnEllipsisInAMessageThatWasNotShortened_ThenTheValueKeepsIt()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Display/Text = Loading...");

        // Assert
        Assert.Equal([new RunMessageAction("Text", "/Display", "= Loading...")], actions);
    }

    [Theory]
    [InlineData("/Speaker/Say(text: \"see a/b = c and m")]
    [InlineData("/Display/Text = a long te")]
    public void WhenTheMessageWasShortened_ThenTheLastPartIsText(string shortenedPart)
    {
        // Arrange
        const int maxMessageLength = 500;
        var prefix = "Ran /Lamp/Toggle(); /Display/Text = " + new string('x', 400) + "; ";
        var lastPart = shortenedPart + new string('m', maxMessageLength - 3 - prefix.Length - shortenedPart.Length) + "...";
        var message = prefix + lastPart;

        // Act
        var actions = RunMessage.Parse(message);

        // Assert
        Assert.Equal(maxMessageLength, message.Length);
        Assert.Equal(
        [
            new RunMessageAction("Toggle", "/Lamp", ""),
            new RunMessageAction("Text", "/Display", "= " + new string('x', 400)),
            new RunMessageAction(null, null, lastPart)
        ], actions);
    }

    [Fact]
    public void WhenTheTargetIsOnTheRootAndTheArgumentOrValueHoldsAPathLikeText_ThenTheSubjectIsTheRoot()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Mode = a/B = c; /Restart(reason: \"x/Y(z: 1)\")");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Mode", "/", "= a/B = c"),
            new RunMessageAction("Restart", "/", "reason: \"x/Y(z: 1)\"")
        ], actions);
    }

    [Fact]
    public void WhenAnArgumentOrValueHoldsANewLine_ThenTheActionKeepsItsSubject()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Speaker/Say(text: \"a\nb/C(d: e)\"); /Display/Text = a\nb/C = d; /Display/Note = x\n");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Say", "/Speaker", "text: \"a\nb/C(d: e)\""),
            new RunMessageAction("Text", "/Display", "= a\nb/C = d"),
            new RunMessageAction("Note", "/Display", "= x\n")
        ], actions);
    }

    [Fact]
    public void WhenAnArgumentIsAMemberOfARecord_ThenTheActionIsTheOperation()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Lamp/SetColor(color.Red: 255); /Lamp/Set(color.Name: a/b = c)");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("SetColor", "/Lamp", "color.Red: 255"),
            new RunMessageAction("Set", "/Lamp", "color.Name: a/b = c")
        ], actions);
    }

    [Fact]
    public void WhenADryRunDescribesItsActions_ThenItHasOneActionPerPart()
    {
        // Act
        var actions = RunMessage.Parse("Would run /Blinds/MoveTo(position: 18); /Lamp/On = true; ChangeState(Night)");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("MoveTo", "/Blinds", "position: 18"),
            new RunMessageAction("On", "/Lamp", "= true"),
            new RunMessageAction("ChangeState", null, "Night")
        ], actions);
    }

    [Fact]
    public void WhenADryRunHadNoActions_ThenThereAreNoActions()
    {
        // Act & Assert
        Assert.Empty(RunMessage.Parse("Would run "));
    }

    [Theory]
    [InlineData("/Blinds/Close: the authorization failed: The policy store is offline")]
    [InlineData("/Lamp/Toggle is disabled")]
    [InlineData("/Lamp/On is configuration, which an automation cannot change")]
    [InlineData("/Lamp/On: speed: cannot read /Sensors/Speed = unknown")]
    [InlineData("/Lamp/Toggle: brightness: no value at /Sensors/Read(x: 1)")]
    [InlineData("/Lamp/Toggle: No operation at /Lamp/Toggle")]
    public void WhenADryRunCannotPrepareAnAction_ThenItIsTextWithoutASubject(string description)
    {
        // Act
        var actions = RunMessage.Parse("Would run ChangeState(Night); " + description);

        // Assert
        Assert.Equal([new RunMessageAction("ChangeState", null, "Night"), new RunMessageAction(null, null, description)], actions);
    }

    [Fact]
    public void WhenAKeyHoldsAColonAndASpace_ThenItStaysInThePath()
    {
        // Act
        var actions = RunMessage.Parse("Ran /Rooms[a: b]/Lamp/Toggle(); /Rooms[a: b]/Lamp/On = true");

        // Assert
        Assert.Equal(
        [
            new RunMessageAction("Toggle", "/Rooms[a: b]/Lamp", ""),
            new RunMessageAction("On", "/Rooms[a: b]/Lamp", "= true")
        ], actions);
    }
}
