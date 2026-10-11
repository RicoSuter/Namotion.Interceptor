using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentPromptBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task WhenBuildingABackgroundSystemPrompt_ThenPreambleInstructionsAndMemoryAreInOrder()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, new FixedTimeProvider(Now.AddMinutes(-25)));
        await memory.UpdateAsync(
        [
            AgentMemoryChange.Set("trends/freezer", "rising slowly"),
            AgentMemoryChange.Set("alerts/kitchen-freezer", "notified the operator at -8 C"),
            AgentMemoryChange.Set("notes/legacy", "# Memory\n- first line\n- second line"),
        ], CancellationToken.None);

        // Act
        var prompt = AgentPromptBuilder.BuildSystemPrompt(AgentTurnKind.Background, "Watch the freezer.", memory.GetSnapshot());

        // Assert
        Assert.True(prompt.IndexOf("Watch the freezer.", StringComparison.Ordinal) < prompt.IndexOf("alerts/kitchen-freezer", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("alerts/kitchen-freezer", StringComparison.Ordinal) < prompt.IndexOf("trends/freezer", StringComparison.Ordinal));
        await Verifier.Verify(prompt);
    }

    [Fact]
    public Task WhenBuildingAChatSystemPromptWithoutMemory_ThenMemorySectionSaysEmpty()
    {
        // Act
        var prompt = AgentPromptBuilder.BuildSystemPrompt(AgentTurnKind.Chat, null, AgentMemorySnapshot.Empty);

        // Assert
        return Verifier.Verify(prompt);
    }

    [Fact]
    public Task WhenBuildingABackgroundMessage_ThenTriggerDigestAndStateAreIncluded()
    {
        // Arrange
        var recent = new[]
        {
            new AgentTimelineEntry { Kind = AgentTimelineEntryKind.BackgroundRun, Timestamp = Now.AddMinutes(-30), Text = "Freezer at -12 C, rising." }
        };
        var state = new[] { new AgentWatchedState("/Kitchen/Freezer", "Temperature: -11.5") };

        // Act
        var message = AgentPromptBuilder.BuildBackgroundMessage("change", recent, Now.AddMinutes(-15), state, Now);

        // Assert
        return Verifier.Verify(message);
    }

    [Fact]
    public Task WhenBuildingAChatMessageWithFindings_ThenFindingsPrecedeTheUserText()
    {
        // Arrange
        var findings = new[]
        {
            new AgentTimelineEntry { Kind = AgentTimelineEntryKind.BackgroundRun, Timestamp = Now.AddMinutes(-5), Text = "PV surplus 3.2 kW." }
        };

        // Act
        var message = AgentPromptBuilder.BuildChatMessage("Should I charge now?", findings);

        // Assert
        return Verifier.Verify(message);
    }

    [Theory]
    [InlineData("[quiet] nothing new", true)]
    [InlineData("  [QUIET] calm", true)]
    [InlineData("Freezer is warm [quiet]", false)]
    public void WhenCheckingForTheQuietMarker_ThenOnlyALeadingMarkerCounts(string text, bool expected)
    {
        // Act
        var isQuiet = AgentPromptBuilder.IsQuiet(text);

        // Assert
        Assert.Equal(expected, isQuiet);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
