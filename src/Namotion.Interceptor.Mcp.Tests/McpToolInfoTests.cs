using System.Text.Json;

namespace Namotion.Interceptor.Mcp.Tests;

public class McpToolInfoTests
{
    [Fact]
    public void WhenCopyingATool_ThenEveryPropertyIsCopied()
    {
        // Arrange
        var source = new McpToolInfo
        {
            Name = "tool",
            Description = "Description",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = (_, _) => Task.FromResult<object?>("result"),
            Annotations = McpToolAnnotations.ReadOnly
        };

        // Act
        var copy = new McpToolInfo(source);

        // Assert
        Assert.All(typeof(McpToolInfo).GetProperties(), property =>
        {
            var sourceValue = property.GetValue(source);
            Assert.NotNull(sourceValue);
            var copyValue = property.GetValue(copy);
            if (sourceValue is JsonElement sourceElement)
            {
                Assert.Equal(sourceElement.GetRawText(), ((JsonElement)copyValue!).GetRawText());
            }
            else
            {
                Assert.Same(sourceValue, copyValue);
            }
        });
    }

    [Fact]
    public void WhenCopyingAToolWithAnInitializer_ThenTheInitializerOverridesTheCopiedValue()
    {
        // Arrange
        var source = new McpToolInfo
        {
            Name = "tool",
            Description = "Description",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = (_, _) => Task.FromResult<object?>("result")
        };

        // Act
        var copy = new McpToolInfo(source) { Description = "Changed" };

        // Assert
        Assert.Equal("Changed", copy.Description);
        Assert.Equal("tool", copy.Name);
        Assert.Same(source.Handler, copy.Handler);
    }
}
