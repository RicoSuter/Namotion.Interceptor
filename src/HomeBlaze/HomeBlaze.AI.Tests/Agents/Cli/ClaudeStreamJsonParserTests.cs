using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

public class ClaudeStreamJsonParserTests
{
    private static IReadOnlyList<ClaudeStreamEvent> ParseFixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Agents", "Cli", "Fixtures", name))
            .Select(ClaudeStreamJsonParser.Parse)
            .OfType<ClaudeStreamEvent>()
            .ToList();

    [Fact]
    public void WhenParsingASuccessfulRun_ThenSessionTextRateLimitAndResultAreExtracted()
    {
        // Act
        var events = ParseFixture("success.jsonl");

        // Assert
        Assert.Equal("d57c5029-b6b6-4e1a-8767-6df4d71bb4ba", Assert.IsType<ClaudeInitEvent>(events[0]).SessionId);
        Assert.Equal(["Hello", " world"], events.OfType<ClaudeTextDeltaEvent>().Select(delta => delta.Text));
        var rateLimit = Assert.Single(events.OfType<ClaudeRateLimitEvent>()).RateLimit;
        Assert.Equal("allowed", rateLimit.Status);
        Assert.Equal(0.18, rateLimit.FiveHourUtilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791748800), rateLimit.SevenDayResetsAt);
        var result = Assert.IsType<ClaudeResultEvent>(events[^1]);
        Assert.False(result.IsError);
        Assert.Equal("Hello world", result.Text);
        Assert.Equal(new AgentUsage(382, 85, 20, 10), result.Usage);
    }

    [Fact]
    public void WhenParsingANotLoggedInRun_ThenTheAuthenticationErrorIsReported()
    {
        // Act
        var events = ParseFixture("not-logged-in.jsonl");

        // Assert
        Assert.Equal("authentication_failed", Assert.Single(events.OfType<ClaudeAssistantErrorEvent>()).Code);
        var result = Assert.IsType<ClaudeResultEvent>(events[^1]);
        Assert.True(result.IsError);
        Assert.Equal("Not logged in · Please run /login", result.Text);
    }

    [Fact]
    public void WhenParsingAMissingResumeSession_ThenAnErrorResultWithTheSubtypeIsReported()
    {
        // Act
        var events = ParseFixture("resume-missing.jsonl");

        // Assert
        var result = Assert.IsType<ClaudeResultEvent>(Assert.Single(events));
        Assert.True(result.IsError);
        Assert.Equal("error_during_execution", result.Subtype);
        Assert.Equal("00000000-0000-4000-8000-000000000000", result.SessionId);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"status\"}")]
    [InlineData("[1,2]")]
    public void WhenALineIsIrrelevantOrMalformed_ThenNoEventIsReturned(string line)
    {
        // Act
        var parsed = ClaudeStreamJsonParser.Parse(line);

        // Assert
        Assert.Null(parsed);
    }
}
