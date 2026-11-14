using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.ClaudeCli;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.ClaudeCli;

public class ClaudeStreamJsonParserTests
{
    private static IReadOnlyList<ClaudeStreamEvent> ParseFixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Agents", "ClaudeCli", "Fixtures", name))
            .Select(ClaudeStreamJsonParser.Parse)
            .OfType<ClaudeStreamEvent>()
            .ToList();

    [Fact]
    public void WhenParsingASuccessfulRun_ThenSessionTextRateLimitAndResultAreExtracted()
    {
        // Act
        var events = ParseFixture("success.jsonl");

        // Assert
        var init = Assert.IsType<ClaudeInitEvent>(events[0]);
        Assert.Equal("d57c5029-b6b6-4e1a-8767-6df4d71bb4ba", init.SessionId);
        Assert.Equal("none", init.ApiKeySource);
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
    public void WhenParsingTextAroundAToolCall_ThenBlockStartsSeparateTheTextDeltas()
    {
        // Act
        var events = ParseFixture("text-tool-text.jsonl");

        // Assert
        var sequence = events
            .Where(streamEvent => streamEvent is ClaudeBlockStartEvent or ClaudeTextDeltaEvent)
            .Select(streamEvent => streamEvent is ClaudeTextDeltaEvent delta ? delta.Text : "|")
            .ToList();
        Assert.Equal(["|", "|", "Checking the ", "sensors.", "|", "|", "|", "Both logged."], sequence);
        Assert.Equal("Both logged.", Assert.IsType<ClaudeResultEvent>(events[^1]).Text);
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
    [InlineData("{\"type\":\"result\",\"result\":\"\\ud800\"}")]
    [InlineData("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"\\ud800\"}}}")]
    public void WhenALineIsIrrelevantOrMalformed_ThenNoEventIsReturned(string line)
    {
        // Act
        var parsed = ClaudeStreamJsonParser.Parse(line);

        // Assert
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("1.5", 1.0)]
    [InlineData("-0.2", 0.0)]
    [InlineData("1e400", null)]
    public void WhenParsingUtilization_ThenOutOfRangeIsClampedAndNonFiniteIsDropped(string utilization, double? expected)
    {
        // Arrange
        var line = "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\",\"unifiedWindows\":{\"five_hour\":{\"utilization\":" + utilization + "}}}}";

        // Act
        var parsed = ClaudeStreamJsonParser.Parse(line);

        // Assert
        Assert.Equal(expected, Assert.IsType<ClaudeRateLimitEvent>(parsed).RateLimit.FiveHourUtilization);
    }

    [Fact]
    public void WhenParsingAWebSearchRun_ThenToolUsesAndResultsAreExtracted()
    {
        // Act
        var events = ParseFixture("web-search.jsonl");

        // Assert
        var toolUses = Assert.Single(events.OfType<ClaudeToolUseEvent>()).ToolUses;
        Assert.Equal(["WebSearch", "mcp__homeblaze__get_property"], toolUses.Select(toolUse => toolUse.Name));
        Assert.Equal("""{"query":"latest released version of .NET SDK 2026"}""", toolUses[0].Arguments);
        var results = events.OfType<ClaudeToolResultEvent>().SelectMany(result => result.Results).ToList();
        Assert.Equal(["toolu_01J9wstnXj6ijoAjQpXyLuPi", "toolu_2"], results.Select(result => result.ToolUseId));
        Assert.StartsWith("Web search results for query", results[0].Content);
        Assert.Equal("21.5", results[1].Content);
        Assert.False(results[0].IsError);
    }

    [Fact]
    public void WhenAToolResultIsMarkedAsError_ThenTheResultIsAnError()
    {
        // Act
        var parsed = ClaudeStreamJsonParser.Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"Fetch failed","is_error":true}]}}""");

        // Assert
        var result = Assert.Single(Assert.IsType<ClaudeToolResultEvent>(parsed).Results);
        Assert.True(result.IsError);
        Assert.Equal("Fetch failed", result.Content);
    }

    [Fact]
    public void WhenAPerModelLimitRejects_ThenItsResetTimeAndTypeAreParsedBesideTheWindows()
    {
        // Act
        var events = ParseFixture("usage-limit-opus.jsonl");

        // Assert
        var limit = Assert.Single(events.OfType<ClaudeRateLimitEvent>());
        Assert.Equal("rejected", limit.RateLimit.Status);
        Assert.Equal(0.64, limit.RateLimit.SevenDayUtilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792000800), limit.LimitResetsAt);
        Assert.Equal("seven_day_opus", limit.LimitType);
        Assert.Equal("rate_limit", Assert.Single(events.OfType<ClaudeAssistantErrorEvent>()).Code);
    }

    [Fact]
    public void WhenTheCliUsesAnApiKey_ThenTheInitLineNamesItsSource()
    {
        // Act
        var events = ParseFixture("api-key-source.jsonl");

        // Assert
        Assert.Equal("/login managed key", Assert.IsType<ClaudeInitEvent>(events[0]).ApiKeySource);
    }

    [Fact]
    public void WhenTheRunStopsAtTheTurnLimit_ThenTheResultSubtypeIsErrorMaxTurns()
    {
        // Act
        var events = ParseFixture("max-turns.jsonl");

        // Assert
        var result = Assert.Single(events.OfType<ClaudeResultEvent>());
        Assert.Equal("error_max_turns", result.Subtype);
        Assert.True(result.IsError);
        Assert.Null(result.Text);
    }
}
