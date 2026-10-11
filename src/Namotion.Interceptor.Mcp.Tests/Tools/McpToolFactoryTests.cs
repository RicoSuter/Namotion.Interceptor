using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpToolFactoryTests
{
    [Fact]
    public async Task WhenToolDescriptionAdditionsAreConfigured_ThenTheyAreAppendedToTheNamedTools()
    {
        // Arrange
        var room = new TestRoom(InterceptorSubjectContext.Create().WithRegistry()) { Name = "Room" };
        var configuration = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            ToolProviders = { new StubToolProvider() },
            ToolDescriptionAdditions =
            {
                ["browse"] = "Pages are documents.",
                ["stub_tool"] = "Extra.",
                ["unknown_tool"] = "Ignored."
            }
        };
        var defaultTools = new McpToolFactory(room, new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            ToolProviders = { new StubToolProvider() }
        }).CreateTools().ToDictionary(tool => tool.Name);

        // Act
        var tools = new McpToolFactory(room, configuration).CreateTools().ToDictionary(tool => tool.Name);

        // Assert
        Assert.Equal(defaultTools["browse"].Description + " Pages are documents.", tools["browse"].Description);
        Assert.Equal("Stub. Extra.", tools["stub_tool"].Description);
        Assert.Equal(defaultTools["search"].Description, tools["search"].Description);
        Assert.Equal(defaultTools.Keys, tools.Keys);
        Assert.Equal(defaultTools["browse"].InputSchema.GetRawText(), tools["browse"].InputSchema.GetRawText());
        Assert.Equal("stub", await tools["stub_tool"].Handler(default, CancellationToken.None));
    }

    private sealed class StubToolProvider : IMcpToolProvider
    {
        public IEnumerable<McpToolInfo> GetTools()
        {
            yield return new McpToolInfo
            {
                Name = "stub_tool",
                Description = "Stub.",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Handler = (_, _) => Task.FromResult<object?>("stub")
            };
        }
    }

    [Theory]
    [InlineData("browse", true)]
    [InlineData("search", true)]
    [InlineData("get_property", true)]
    [InlineData("list_types", true)]
    [InlineData("set_property", false)]
    public void WhenCreatingTheCoreTools_ThenTheyAreAnnotatedAsReadOnlyOrDestructive(string toolName, bool isReadOnly)
    {
        // Arrange
        var room = new TestRoom(InterceptorSubjectContext.Create().WithRegistry()) { Name = "Room" };
        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };

        // Act
        var annotations = new McpToolFactory(room, configuration).CreateTools().Single(tool => tool.Name == toolName).Annotations;

        // Assert
        Assert.NotNull(annotations);
        Assert.Equal(isReadOnly, annotations.ReadOnlyHint);
        Assert.Equal(isReadOnly ? null : true, annotations.DestructiveHint);
        Assert.Null(annotations.IdempotentHint);
        Assert.False(annotations.OpenWorldHint);
    }
}
