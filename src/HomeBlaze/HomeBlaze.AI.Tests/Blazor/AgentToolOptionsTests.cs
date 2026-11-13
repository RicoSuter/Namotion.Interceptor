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
        var offeredTools = new[] { CreateTool("custom_tool", "Custom description"), CreateTool("update_memory", "Memory"), CreateTool("other_tool", "Other description") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, ["custom_tool"]);

        // Assert
        Assert.Equal(["custom_tool", "other_tool"], options.Select(option => option.Name));
        Assert.Equal(["Custom description", "Other description"], options.Select(option => option.Description));
        Assert.All(options, option => Assert.True(option.IsOffered));
    }

    [Fact]
    public void WhenStateChangingToolsAreOffered_ThenTheyAreMarkedAndListedAfterReadOnlyTools()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("set_property"), CreateTool("browse"), CreateTool("invoke_operation"), CreateTool("search") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, []);

        // Assert
        Assert.Equal(["browse", "search", "set_property", "invoke_operation"], options.Select(option => option.Name));
        Assert.Equal([false, false, true, true], options.Select(option => option.CanChangeState));
    }

    [Fact]
    public void WhenTheAgentTypeSupportsWebTools_ThenTheyFollowTheGraphToolsWithTheirSummaryAndWebWarning()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("set_property"), CreateTool("browse") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, ["web_search", "removed_tool"], AgentBackendTool.WebTools);

        // Assert
        Assert.Equal(["browse", "set_property", "web_search", "web_fetch", "removed_tool"], options.Select(option => option.Name));
        Assert.Equal([false, false, true, true, false], options.Select(option => option.LoadsWebContent));
        Assert.Equal([false, true, false, false, false], options.Select(option => option.CanChangeState));
        Assert.Equal(["Searches the web.", "Loads a web page."], options.Skip(2).Take(2).Select(option => option.Description));
        Assert.True(options[2].IsOffered);
    }

    [Fact]
    public void WhenTheAgentTypeSupportsNoWebTool_ThenAnAllowedWebToolIsListedAsNotOffered()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("browse") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, ["web_search"]);

        // Assert
        Assert.Equal(["browse", "web_search"], options.Select(option => option.Name));
        Assert.False(options[1].IsOffered);
        Assert.False(options[1].LoadsWebContent);
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
    public void WhenAKnownToolIsOffered_ThenItsShortSummaryIsTheDescription()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("browse", "Browse the subject tree at a path with configurable depth. Use depth=0 ...") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, []);

        // Assert
        Assert.Equal("Browses the subject tree and reads the properties of subjects.", Assert.Single(options).Description);
    }

    [Fact]
    public void WhenInvokeQueryIsOffered_ThenItIsListedWithTheReadOnlyToolsAndASummary()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("invoke_operation"), CreateTool("invoke_query", "Call a query method ...") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, []);

        // Assert
        Assert.Equal(["invoke_query", "invoke_operation"], options.Select(option => option.Name));
        Assert.False(options[0].CanChangeState);
        Assert.Equal("Calls read-only query methods, for example to read Markdown pages.", options[0].Description);
        Assert.True(options[1].CanChangeState);
    }

    [Fact]
    public void WhenMarkdownToolsAreOffered_ThenTheyAreListedAfterTheReadOnlyToolsAsChangingPages()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("create_markdown"), CreateTool("edit_markdown"), CreateTool("browse"), CreateTool("write_markdown"), CreateTool("invoke_operation") };

        // Act
        var options = AgentToolOptions.Create(offeredTools, []);

        // Assert
        Assert.Equal(["browse", "create_markdown", "edit_markdown", "write_markdown", "edit_page_layout", "invoke_operation"], options.Select(option => option.Name));
        Assert.Equal([false, true, true, true, true, true], options.Select(option => option.CanChangeState));
        Assert.Equal([false, true, true, true, false, false], options.Select(option => option.ChangesPages));
        Assert.Equal([false, false, false, false, true, false], options.Select(option => option.ChangesPageLayout));
        Assert.Equal(
            ["Creates a Markdown page or document in a folder.", "Replaces a passage of a Markdown page or document.", "Replaces the whole text of a Markdown page or document."],
            options.Skip(1).Take(3).Select(option => option.Description));
    }

    [Fact]
    public void WhenNoMarkdownToolIsOffered_ThenThePermissionToEditPageLayoutIsNotListedUnlessItIsAllowed()
    {
        // Arrange
        var offeredTools = new[] { CreateTool("browse"), CreateTool("invoke_operation") };

        // Act
        var withoutPermission = AgentToolOptions.Create(offeredTools, []);
        var withPermission = AgentToolOptions.Create(offeredTools, [AgentToolSet.EditPageLayoutPermission]);

        // Assert
        Assert.DoesNotContain(withoutPermission, option => option.ChangesPageLayout);
        var option = Assert.Single(withPermission, option => option.ChangesPageLayout);
        Assert.False(option.IsOffered);
        Assert.True(option.CanChangeState);
    }

    [Theory]
    [InlineData("invoke_operation", false)]
    [InlineData("edit_markdown", true)]
    public void WhenAStateChangingToolIsAllowedButNotOffered_ThenItIsStillMarkedAsChangingState(string name, bool expectedChangesPages)
    {
        // Act
        var options = AgentToolOptions.Create([CreateTool("browse")], [name]);

        // Assert
        var option = Assert.Single(options, option => option.Name == name);
        Assert.False(option.IsOffered);
        Assert.True(option.CanChangeState);
        Assert.Equal(expectedChangesPages, option.ChangesPages);
    }

    [Fact]
    public void WhenListingTheToolsFails_ThenTheFallbackNamesAreListed()
    {
        // Arrange
        var toolSource = new ThrowingToolSource();

        // Act
        var options = AgentToolOptions.Create(toolSource, AgentToolSet.DefaultAllowedTools);

        // Assert
        Assert.Equal(
            AgentToolOptions.FallbackToolNames.Append(AgentToolSet.EditPageLayoutPermission).Order(), options.Select(option => option.Name).Order());
        Assert.Contains("invoke_query", AgentToolOptions.FallbackToolNames);
        Assert.Contains("invoke_operation", AgentToolOptions.FallbackToolNames);
        Assert.DoesNotContain("invoke_method", AgentToolOptions.FallbackToolNames);
        Assert.Contains("edit_markdown", AgentToolOptions.FallbackToolNames);
        Assert.All(options, option => Assert.False(string.IsNullOrEmpty(option.Description)));
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
