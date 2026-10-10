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
                Enrichments = new Dictionary<string, object?> { ["$title"] = "Root", ["$icon"] = "Storage" },
                Properties = new Dictionary<string, SubjectNodeProperty>
                {
                    ["Device"] = new SubjectObjectProperty(
                        Child: new SubjectNode
                        {
                            Path = "/Device",
                            Type = "MyApp.Device",
                            Enrichments = new Dictionary<string, object?> { ["$title"] = "Light" }
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
                    Enrichments = new Dictionary<string, object?> { ["$title"] = "Motor 1" },
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
                    Enrichments = new Dictionary<string, object?> { ["$title"] = "Motor 2" },
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
                ["/Demo/Motor1"] = new() { Path = "/Demo/Motor1", Type = "MyApp.Motor", Enrichments = new Dictionary<string, object?> { ["$title"] = "Motor 1" } },
                ["/Demo/Motor2"] = new() { Path = "/Demo/Motor2", Type = "MyApp.Motor", Enrichments = new Dictionary<string, object?> { ["$title"] = "Motor 2" } }
            },
            SubjectCount = 2,
            Truncated = true
        };

        // Act & Assert
        return Verifier.Verify(McpTextFormatter.FormatSearchResult(result));
    }
    [Fact]
    public void WhenPropertyIsRecordArray_ThenRendersCompactJson()
    {
        // Arrange
        var value = new[] { new Favorite("Radio", "x-radio:1", false), new Favorite("Album", "x-album:2", true) };

        // Act
        var line = FormatPropertyLine(new ScalarProperty(value, "array"));

        // Assert
        Assert.Equal(
            """  Value: [{"Title":"Radio","Uri":"x-radio:1","IsContainer":false},{"Title":"Album","Uri":"x-album:2","IsContainer":true}] | array""",
            line);
    }

    [Fact]
    public void WhenPropertyIsPrimitiveList_ThenRendersCompactJson()
    {
        // Arrange
        var value = new List<object?> { 1, "two", null, DayOfWeek.Monday, "Zürich" };

        // Act
        var line = FormatPropertyLine(new ScalarProperty(value, "array"));

        // Assert
        Assert.Equal("""  Value: [1,"two",null,"Monday","Zürich"] | array""", line);
    }

    [Fact]
    public void WhenPropertyIsEmptyArray_ThenRendersEmptyJsonArray()
    {
        // Act
        var line = FormatPropertyLine(new ScalarProperty(Array.Empty<Favorite>(), "array"));

        // Assert
        Assert.Equal("  Value: [] | array", line);
    }

    [Fact]
    public void WhenPropertyIsDictionary_ThenRendersCompactJsonObject()
    {
        // Arrange
        var value = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        // Act
        var line = FormatPropertyLine(new ScalarProperty(value, "object"));

        // Assert
        Assert.Equal("""  Value: {"a":1,"b":2} | object""", line);
    }

    [Fact]
    public void WhenCollectionExceedsLengthCap_ThenRendersWholeItemsAndRemainingCount()
    {
        // Arrange
        var value = Enumerable.Range(0, 1000).Select(index => $"item-{index:D4}").ToArray();

        // Act
        var line = FormatPropertyLine(new ScalarProperty(value, "array"));

        // Assert
        Assert.StartsWith("""  Value: ["item-0000","item-0001",""", line);
        Assert.Matches("""\["item-0000",.*"item-\d{4}", \.\.\. \+\d+ more\] \| array$""", line);
        Assert.True(line.Length < 600, $"Line has {line.Length} characters.");
    }

    [Fact]
    public void WhenFirstItemExceedsLengthCap_ThenTruncatesItAndCountsTheRest()
    {
        // Arrange
        var value = new[] { new string('x', 2000), "second" };

        // Act
        var line = FormatPropertyLine(new ScalarProperty(value, "array"));

        // Assert
        Assert.Matches("""^  Value: \["x+\.\.\., \.\.\. \+1 more\] \| array$""", line);
        Assert.True(line.Length < 600, $"Line has {line.Length} characters.");
    }

    [Fact]
    public void WhenPropertyIsByteArray_ThenRendersLengthOnly()
    {
        // Act
        var line = FormatPropertyLine(new ScalarProperty(new byte[] { 1, 2, 3 }, "array"));

        // Assert
        Assert.Equal("  Value: <3 bytes> | array", line);
    }

    [Fact]
    public void WhenPropertyIsStringOrPrimitive_ThenRendersAsBefore()
    {
        // Act
        var stringLine = FormatPropertyLine(new ScalarProperty("Living Room", "string"));
        var integerLine = FormatPropertyLine(new ScalarProperty(42, "integer"));
        var booleanLine = FormatPropertyLine(new ScalarProperty(false, "boolean"));

        // Assert
        Assert.Equal("  Value: Living Room | string", stringLine);
        Assert.Equal("  Value: 42 | integer", integerLine);
        Assert.Equal("  Value: false | boolean", booleanLine);
    }

    private static string FormatPropertyLine(ScalarProperty property)
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties = new Dictionary<string, SubjectNodeProperty> { ["Value"] = property }
            },
            SubjectCount = 1
        };

        return McpTextFormatter.FormatBrowseResult(result)
            .Split('\n')
            .Single(line => line.StartsWith("  Value: ", StringComparison.Ordinal))
            .TrimEnd('\r');
    }

    public sealed record Favorite(string Title, string Uri, bool IsContainer)
    {
        public override string ToString() => Title;
    }
}
