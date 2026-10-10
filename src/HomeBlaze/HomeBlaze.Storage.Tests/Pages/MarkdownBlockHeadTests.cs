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
        var line = fence + "HomeBlaze.Components.Card#main title=\"Heizung {x}\" icon=Thermostat speed=3000";

        // Act
        var success = MarkdownBlockHead.TryParse(line, out var head, out var error);

        // Assert
        Assert.True(success, error);
        Assert.Equal(5, head!.FenceLength);
        Assert.Equal("HomeBlaze.Components.Card", head.Name);
        Assert.Equal("main", head.Id);
        Assert.True(head.IsWidget);
        Assert.Equal(
            [new MarkdownBlockArgument("title", "Heizung {x}", true), new MarkdownBlockArgument("icon", "Thermostat", false), new MarkdownBlockArgument("speed", "3000", false)],
            head.Arguments);
    }

    [Fact]
    public void WhenNameHasNoDot_ThenHeadIsASlot()
    {
        // Act
        var success = MarkdownBlockHead.TryParse("::: Tab title=Strom", out var head, out _);

        // Assert
        Assert.True(success);
        Assert.False(head!.IsWidget);
        Assert.Null(head.Id);
    }

    [Fact]
    public void WhenQuotedValueHasEscapes_ThenTheyAreUnescaped()
    {
        // Act
        MarkdownBlockHead.TryParse(":: A.B text=\"a \\\"b\\\" \\\\ c\"", out var head, out _);

        // Assert
        Assert.Equal("a \"b\" \\ c", head!.Arguments[0].Value);
    }

    [Fact]
    public void WhenBareValueContainsColonsAndDots_ThenItIsKeptWhole()
    {
        // Act
        MarkdownBlockHead.TryParse(":: A.B interval=00:00:01.500", out var head, out _);

        // Assert
        Assert.Equal("00:00:01.500", head!.Arguments[0].Value);
    }

    [Theory]
    [InlineData(":: A.B {.extra}", "key=value")]
    [InlineData(":: A.B text=\"open", "closing quote")]
    [InlineData(":: A.B a=1 a=2", "twice")]
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
        const string line = "::: A.B#m title=\"x y\" path=\"/a/{b}\" count=3 quote=\"say \\\"hi\\\"\"";
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
        var head = new MarkdownBlockHead(3, "A.B", null, [new MarkdownBlockArgument("name", "My Motor", false), new MarkdownBlockArgument("empty", "", false)]);

        // Act
        var formatted = head.Format();

        // Assert
        Assert.Equal("::: A.B name=\"My Motor\" empty=\"\"", formatted);
    }

    [Fact]
    public void WhenHeadWithoutSpaceAfterTheFenceIsFormatted_ThenItHasOneSpaceAndTheSameFenceLength()
    {
        // Arrange
        MarkdownBlockHead.TryParse("::::A.B#m count=3", out var head, out _);

        // Act
        var formatted = head!.Format();

        // Assert
        Assert.Equal(":::: A.B#m count=3", formatted);
    }
}
