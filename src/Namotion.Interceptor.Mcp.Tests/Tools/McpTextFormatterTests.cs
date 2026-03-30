using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpTextFormatterTests
{
    [Fact]
    public Task WhenBrowsingBasicTree_ThenFormatsCorrectly()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/",
                Type = "MyApp.Root",
                Title = "Root",
                Enrichments = new Dictionary<string, object?> { ["$icon"] = "Storage" },
                Properties = new Dictionary<string, SubjectNodeProperty>
                {
                    ["Device"] = new SubjectObjectProperty(
                        Child: new SubjectNode
                        {
                            Path = "/Device",
                            Type = "MyApp.Device",
                            Title = "Light"
                        }),
                    ["Sensors"] = new SubjectCollectionProperty(IsCollapsed: true, Count: 3, ItemType: "Sensor")
                }
            },
            SubjectCount = 2
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task WhenBrowsingWithPropertiesAndAttributes_ThenFormatsCorrectly()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties = new Dictionary<string, SubjectNodeProperty>
                {
                    ["Speed"] = new ScalarProperty(1500, "integer", IsWritable: true, Attributes:
                    [
                        new PropertyAttribute("Minimum", 0),
                        new PropertyAttribute("Maximum", 3000),
                        new PropertyAttribute("State", new Dictionary<string, object?> { ["Title"] = "Speed", ["Unit"] = 0 })
                    ]),
                    ["Name"] = new ScalarProperty("Motor", "string"),
                    ["IsActive"] = new ScalarProperty(true, "boolean")
                }
            },
            SubjectCount = 1
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task WhenBrowsingWithMethodsAndInterfaces_ThenFormatsCorrectly()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Methods = ["Start", "Stop", "Reset"],
                Interfaces = ["IMotor", "IDevice"]
            },
            SubjectCount = 1
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task WhenBrowsingNullEmptyAndLongValues_ThenFormatsCorrectly()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties = new Dictionary<string, SubjectNodeProperty>
                {
                    ["NullProp"] = new ScalarProperty(null, "string"),
                    ["EmptyProp"] = new ScalarProperty("", "string"),
                    ["LongProp"] = new ScalarProperty(new string('x', 150), "string")
                }
            },
            SubjectCount = 1
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task WhenBrowsingCollectionAndDictionaryChildren_ThenFormatsCorrectly()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties = new Dictionary<string, SubjectNodeProperty>
                {
                    ["Pins"] = new SubjectCollectionProperty(Children:
                    [
                        new SubjectNode { Path = "/Root/Pins[0]", Type = "MyApp.Pin" },
                        new SubjectNode { Path = "/Root/Pins[1]", Type = "MyApp.Pin" }
                    ]),
                    ["Items"] = new SubjectDictionaryProperty(Children: new Dictionary<string, SubjectNode>
                    {
                        ["A"] = new() { Path = "/Root/Items[A]", Type = "MyApp.Item" },
                        ["B"] = new() { Path = "/Root/Items[B]", Type = "MyApp.Item" }
                    })
                }
            },
            SubjectCount = 5
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task WhenBrowsingCollapsedWithoutItemType_ThenFormatsCorrectly()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties = new Dictionary<string, SubjectNodeProperty>
                {
                    ["Children"] = new SubjectDictionaryProperty(IsCollapsed: true, Count: 7)
                }
            },
            SubjectCount = 1
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task WhenResultTruncated_ThenFooterIndicatesTruncation()
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode { Path = "/", Type = "MyApp.Root" },
            SubjectCount = 3,
            Truncated = true
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Theory]
    [InlineData(0, "[0 subjects]")]
    [InlineData(1, "[1 subject]")]
    [InlineData(2, "[2 subjects]")]
    public void WhenFooterRendered_ThenSingularPluralIsCorrect(int count, string expected)
    {
        // Arrange
        var result = new BrowseResult
        {
            Result = new SubjectNode { Path = "/", Type = "R" },
            SubjectCount = count
        };

        // Act
        var text = McpTextFormatter.FormatBrowseResult(result);

        // Assert
        Assert.Contains(expected, text);
    }

    [Fact]
    public Task WhenSearchingWithProperties_ThenFormatsFlatList()
    {
        // Arrange
        var result = new SearchResult
        {
            Results = new Dictionary<string, SubjectNode>
            {
                ["/Demo/Motor1"] = new()
                {
                    Path = "/Demo/Motor1",
                    Type = "MyApp.Motor",
                    Title = "Motor 1",
                    Properties = new Dictionary<string, SubjectNodeProperty>
                    {
                        ["Speed"] = new ScalarProperty(1500, "integer", IsWritable: true),
                        ["IsRunning"] = new ScalarProperty(true, "boolean")
                    }
                },
                ["/Demo/Motor2"] = new()
                {
                    Path = "/Demo/Motor2",
                    Type = "MyApp.Motor",
                    Title = "Motor 2",
                    Properties = new Dictionary<string, SubjectNodeProperty>
                    {
                        ["Speed"] = new ScalarProperty(2400, "integer", IsWritable: true),
                        ["IsRunning"] = new ScalarProperty(false, "boolean")
                    }
                }
            },
            SubjectCount = 2
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatSearchResult(result));
    }

    [Fact]
    public Task WhenSearchingWithoutProperties_ThenFormatsMinimalList()
    {
        // Arrange
        var result = new SearchResult
        {
            Results = new Dictionary<string, SubjectNode>
            {
                ["/Demo/Motor1"] = new() { Path = "/Demo/Motor1", Type = "MyApp.Motor", Title = "Motor 1" },
                ["/Demo/Motor2"] = new() { Path = "/Demo/Motor2", Type = "MyApp.Motor", Title = "Motor 2" }
            },
            SubjectCount = 2,
            Truncated = true
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatSearchResult(result));
    }
}
