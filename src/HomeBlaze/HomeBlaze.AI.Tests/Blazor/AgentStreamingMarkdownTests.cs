using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentStreamingMarkdownTests
{
    [Theory]
    [InlineData("")]
    [InlineData("A paragraph being written")]
    [InlineData("First line\nsecond line\n")]
    [InlineData("First paragraph.\n  ")]
    public void WhenTextHasNoCompleteBlankLine_ThenNothingIsStable(string text)
    {
        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal(0, stableLength);
    }

    [Theory]
    [InlineData("First paragraph.\n\nSecond paragraph.\n\nThird is still", "First paragraph.\n\nSecond paragraph.\n\n")]
    [InlineData("Intro.\n\n```json\n{\n\n  \"a\": 1\n\n", "Intro.\n\n")]
    [InlineData("- one\n- two\n- three\n\nNext para", "- one\n- two\n- three\n\n")]
    [InlineData("Values:\n\n| A | B |\n|---|---|\n| 1 | 2 |\n| 3 |", "Values:\n\n")]
    [InlineData("One.\r\n\r\nTwo", "One.\r\n\r\n")]
    [InlineData("```\ncode\n\nmore\n```\n\nAfter", "```\ncode\n\nmore\n```\n\n")]
    [InlineData("~~~~\ncode\n```\n\nstill code\n~~~~\n\nAfter", "~~~~\ncode\n```\n\nstill code\n~~~~\n\n")]
    [InlineData("````\ncode\n```\n\nstill code", "")]
    public void WhenBlocksAreComplete_ThenTheStablePartEndsAfterTheLastBlankLineOutsideACodeBlock(string text, string expected)
    {
        // Act
        var stableLength = AgentStreamingMarkdown.FindStableLength(text);

        // Assert
        Assert.Equal(expected, text[..stableLength]);
    }
}
