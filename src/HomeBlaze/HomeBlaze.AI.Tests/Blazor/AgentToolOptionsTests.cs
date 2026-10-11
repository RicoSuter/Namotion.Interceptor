using System.Text.Json;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using Namotion.Interceptor.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentToolOptionsTests
{
    [Fact]
    public void WhenToolsAreOffered_ThenEachIsListedWithItsDescriptionExceptUpdateMemory()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("browse", "Browse the tree"), CreateTool("update_memory", "Memory"), CreateTool("search", "Find subjects") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, ["browse"]);

        // Assert
        Assert.Equal(["browse", "search"], options.Select(option => option.Name));
        Assert.Equal(["Browse the tree", "Find subjects"], options.Select(option => option.Description));
        Assert.All(options, option => Assert.True(option.IsOffered));
    }

    [Fact]
    public void WhenStateChangingToolsAreOffered_ThenTheyAreMarkedAndListedAfterReadOnlyTools()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("set_property"), CreateTool("browse"), CreateTool("invoke_method"), CreateTool("search") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, []);

        // Assert
        Assert.Equal(["browse", "search", "set_property", "invoke_method"], options.Select(option => option.Name));
        Assert.Equal([false, false, true, true], options.Select(option => option.CanChangeState));
    }

    [Fact]
    public void WhenAnAllowedToolIsNotOffered_ThenItIsListedLastAsNotOffered()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("browse") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, ["removed_tool", "browse", "update_memory"]);

        // Assert
        Assert.Equal(["browse", "removed_tool"], options.Select(option => option.Name));
        Assert.False(options[1].IsOffered);
    }

    [Fact]
    public void WhenListingTheToolsFails_ThenTheFallbackNamesAreListed()
    {
        // Arrange
        var toolSource = new ThrowingToolSource();

        // Act
        var options = AgentToolOptions.Create(toolSource, AgentToolSet.DefaultAllowedTools);

        // Assert
        Assert.Equal(AgentToolOptions.FallbackToolNames.Order(), options.Select(option => option.Name).Order());
        Assert.All(options, option => Assert.Null(option.Description));
        Assert.All(options, option => Assert.True(option.IsOffered));
    }

    private static McpToolInfo CreateTool(string name, string description = "Description") => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        Handler = (_, _) => Task.FromResult<object?>(null)
    };

    private sealed class ThrowingToolSource : IAgentToolSource
    {
        public IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly) =>
            throw new InvalidOperationException("The root subject is still loading, try again shortly.");
    }
}
