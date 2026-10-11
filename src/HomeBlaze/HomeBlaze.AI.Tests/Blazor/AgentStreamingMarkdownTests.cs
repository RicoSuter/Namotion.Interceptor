using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentStreamingMarkdownTests
{
    [Theory]
    [InlineData("")]
    [InlineData("A paragraph being written")]
    [InlineData("First line\nsecond line\n")]
    public void WhenTextHasNoBlankLine_ThenNothingIsStable(string text)
    {
        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal(0, stableLength);
    }

    [Fact]
    public void WhenParagraphsAreSeparatedByBlankLines_ThenTheStablePartEndsAfterTheLastBlankLine()
    {
        // Arrange
        const string text = "First paragraph.\n\nSecond paragraph.\n\nThird is still";

        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal("First paragraph.\n\nSecond paragraph.\n\n", text[..stableLength]);
    }

    [Fact]
    public void WhenTheBlankLineIsStillBeingWritten_ThenItDoesNotCount()
    {
        // Arrange
        const string text = "First paragraph.\n  ";

        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal(0, stableLength);
    }

    [Fact]
    public void WhenACodeFenceIsOpen_ThenBlankLinesInsideItAreNotBoundaries()
    {
        // Arrange
        const string text = "Intro.\n\n```json\n{\n\n  \"a\": 1\n\n";

        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal("Intro.\n\n", text[..stableLength]);
    }

    [Theory]
    [InlineData("```\ncode\n\nmore\n```\n\nAfter", "```\ncode\n\nmore\n```\n\n")]
    [InlineData("~~~~\ncode\n```\n\nstill code\n~~~~\n\nAfter", "~~~~\ncode\n```\n\nstill code\n~~~~\n\n")]
    [InlineData("````\ncode\n```\n\nstill code", "")]
    public void WhenACodeFenceIsClosedByAMatchingFence_ThenBlankLinesAfterItAreBoundaries(string text, string expected)
    {
        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal(expected, text[..stableLength]);
    }

    [Fact]
    public void WhenAListIsFollowedByABlankLine_ThenTheWholeListIsStable()
    {
        // Arrange
        const string text = "- one\n- two\n- three\n\nNext para";

        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal("- one\n- two\n- three\n\n", text[..stableLength]);
    }

    [Fact]
    public void WhenATableIsStillGrowing_ThenItStaysInTheTail()
    {
        // Arrange
        const string text = "Values:\n\n| A | B |\n|---|---|\n| 1 | 2 |\n| 3 |";

        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal("Values:\n\n", text[..stableLength]);
    }

    [Fact]
    public void WhenLinesEndWithCarriageReturns_ThenBlankLinesAreStillFound()
    {
        // Arrange
        const string text = "One.\r\n\r\nTwo";

        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal("One.\r\n\r\n", text[..stableLength]);
    }
}
