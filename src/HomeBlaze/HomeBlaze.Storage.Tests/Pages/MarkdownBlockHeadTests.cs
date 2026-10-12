using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownBlockHeadTests
{
    [Theory]
    [InlineData(":::::")]
    [InlineData("::::: ")]
    [InlineData(":::::   ")]
    public void WhenHeadHasTypeIdAndArguments_ThenAllPartsAreParsed(string fence)
    {
        // Arrange
        var line = fence + "HomeBlaze.Components.Card#main Title=\"Heizung {x}\" Icon=Thermostat Speed=3000";

        // Act
        var success = MarkdownBlockHead.TryParse(line, out var head, out var error);

        // Assert
        Assert.True(success, error);
        Assert.Equal(5, head!.FenceLength);
        Assert.Equal("HomeBlaze.Components.Card", head.Name);
        Assert.Equal("main", head.Id);
        Assert.True(head.IsWidget);
        Assert.Equal(
            [new MarkdownBlockArgument("Title", "Heizung {x}", true), new MarkdownBlockArgument("Icon", "Thermostat", false), new MarkdownBlockArgument("Speed", "3000", false)],
            head.Arguments);
    }

    [Fact]
    public void WhenNameHasNoDot_ThenHeadIsASlot()
    {
        // Act
        var success = MarkdownBlockHead.TryParse("::: Tab Title=Strom", out var head, out _);

        // Assert
        Assert.True(success);
        Assert.False(head!.IsWidget);
        Assert.Null(head.Id);
    }

    [Fact]
    public void WhenQuotedValueHasEscapes_ThenTheyAreUnescaped()
    {
        // Act
        MarkdownBlockHead.TryParse(":: A.B Text=\"a \\\"b\\\" \\\\ c\"", out var head, out _);

        // Assert
        Assert.Equal("a \"b\" \\ c", head!.Arguments[0].Value);
    }

    [Fact]
    public void WhenBareValueContainsColonsAndDots_ThenItIsKeptWhole()
    {
        // Act
        MarkdownBlockHead.TryParse(":: A.B Interval=00:00:01.500", out var head, out _);

        // Assert
        Assert.Equal("00:00:01.500", head!.Arguments[0].Value);
    }

    [Theory]
    [InlineData(":: A.B {.extra}", "key=value")]
    [InlineData(":: A.B Text=\"open", "closing quote")]
    [InlineData(":: A.B A=1 A=2", "twice")]
    [InlineData(":: A..B", "not a valid block name")]
    [InlineData("::A..B", "not a valid block name")]
    [InlineData(":: A.B#1x", "not a valid name after '#'")]
    [InlineData(":: A.B #m", "key=value")]
    [InlineData("::", "'' is not a valid block name")]
    [InlineData(":A.B", "A block starts with at least 2 ':' characters.")]
    [InlineData("A.B", "A block starts with at least 2 ':' characters.")]
    public void WhenHeadIsMalformed_ThenErrorNamesTheProblem(string line, string expectedPart)
    {
        // Act
        var success = MarkdownBlockHead.TryParse(line, out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Contains(expectedPart, error);
    }

    [Fact]
    public void WhenHeadIsFormatted_ThenItParsesBackToTheSameHead()
    {
        // Arrange
        const string line = "::: A.B#m Title=\"x y\" Path=\"/a/{b}\" Count=3 Quote=\"say \\\"hi\\\"\"";
        MarkdownBlockHead.TryParse(line, out var head, out _);

        // Act
        var formatted = head!.Format();

        // Assert
        Assert.Equal(line, formatted);
    }

    [Fact]
    public void WhenUnquotedValueNeedsQuotes_ThenFormatAddsThem()
    {
        // Arrange
        var head = new MarkdownBlockHead(3, "A.B", null, [new MarkdownBlockArgument("Name", "My Motor", false), new MarkdownBlockArgument("Empty", "", false)]);

        // Act
        var formatted = head.Format();

        // Assert
        Assert.Equal("::: A.B Name=\"My Motor\" Empty=\"\"", formatted);
    }

    [Fact]
    public void WhenHeadWithoutSpaceAfterTheFenceIsFormatted_ThenItHasOneSpaceAndTheSameFenceLength()
    {
        // Arrange
        MarkdownBlockHead.TryParse("::::A.B#m Count=3", out var head, out _);

        // Act
        var formatted = head!.Format();

        // Assert
        Assert.Equal(":::: A.B#m Count=3", formatted);
    }
}
