using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class McpTextFormatterTests
{
    [Fact]
    public Task Browse_basic_tree()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/",
                Type = "MyApp.Root",
                Title = "Root",
                Enrichments = new Dictionary<string, object?> { ["$icon"] = "Storage" },
                Properties =
                [
                    new SubjectObjectProperty("Device",
                        Child: new SubjectNode
                        {
                            Path = "/Device",
                            Type = "MyApp.Device",
                            Title = "Light"
                        }),
                    new SubjectCollectionProperty("Sensors", IsCollapsed: true, Count: 3, ItemType: "Sensor")
                ]
            },
            SubjectCount = 2
        };

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task Browse_with_properties_and_attributes()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties =
                [
                    new ScalarProperty("Speed", 1500, "integer", IsWritable: true, Attributes:
                    [
                        new PropertyAttribute("Minimum", 0),
                        new PropertyAttribute("Maximum", 3000),
                        new PropertyAttribute("State", new Dictionary<string, object?> { ["Title"] = "Speed", ["Unit"] = 0 })
                    ]),
                    new ScalarProperty("Name", "Motor", "string"),
                    new ScalarProperty("IsActive", true, "boolean")
                ]
            },
            SubjectCount = 1
        };

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task Browse_with_methods_and_interfaces()
    {
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

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task Browse_null_and_empty_and_long_values()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties =
                [
                    new ScalarProperty("NullProp", null, "string"),
                    new ScalarProperty("EmptyProp", "", "string"),
                    new ScalarProperty("LongProp", new string('x', 150), "string")
                ]
            },
            SubjectCount = 1
        };

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task Browse_collection_and_dictionary_children()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties =
                [
                    new SubjectCollectionProperty("Pins", Children:
                    [
                        new SubjectNode { Path = "/Root/Pins[0]", Type = "MyApp.Pin" },
                        new SubjectNode { Path = "/Root/Pins[1]", Type = "MyApp.Pin" }
                    ]),
                    new SubjectDictionaryProperty("Items", Children: new Dictionary<string, SubjectNode>
                    {
                        ["A"] = new SubjectNode { Path = "/Root/Items[A]", Type = "MyApp.Item" },
                        ["B"] = new SubjectNode { Path = "/Root/Items[B]", Type = "MyApp.Item" }
                    })
                ]
            },
            SubjectCount = 5
        };

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task Browse_collapsed_without_item_type()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties =
                [
                    new SubjectDictionaryProperty("Children", IsCollapsed: true, Count: 7)
                ]
            },
            SubjectCount = 1
        };

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Fact]
    public Task Browse_truncated_footer()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode { Path = "/", Type = "MyApp.Root" },
            SubjectCount = 3,
            Truncated = true
        };

        return Verifier.Verify(McpTextFormatter.FormatBrowseResult(result));
    }

    [Theory]
    [InlineData(0, "[0 subjects]")]
    [InlineData(1, "[1 subject]")]
    [InlineData(2, "[2 subjects]")]
    public void Footer_singular_plural(int count, string expected)
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode { Path = "/", Type = "R" },
            SubjectCount = count
        };

        var text = McpTextFormatter.FormatBrowseResult(result);
        Assert.Contains(expected, text);
    }

    [Fact]
    public Task Search_flat_list_with_properties()
    {
        var result = new SearchResult
        {
            Results = new Dictionary<string, SubjectNode>
            {
                ["/Demo/Motor1"] = new SubjectNode
                {
                    Path = "/Demo/Motor1",
                    Type = "MyApp.Motor",
                    Title = "Motor 1",
                    Properties =
                    [
                        new ScalarProperty("Speed", 1500, "integer", IsWritable: true),
                        new ScalarProperty("IsRunning", true, "boolean")
                    ]
                },
                ["/Demo/Motor2"] = new SubjectNode
                {
                    Path = "/Demo/Motor2",
                    Type = "MyApp.Motor",
                    Title = "Motor 2",
                    Properties =
                    [
                        new ScalarProperty("Speed", 2400, "integer", IsWritable: true),
                        new ScalarProperty("IsRunning", false, "boolean")
                    ]
                }
            },
            SubjectCount = 2
        };

        return Verifier.Verify(McpTextFormatter.FormatSearchResult(result));
    }

    [Fact]
    public Task Search_minimal_no_properties()
    {
        var result = new SearchResult
        {
            Results = new Dictionary<string, SubjectNode>
            {
                ["/Demo/Motor1"] = new SubjectNode { Path = "/Demo/Motor1", Type = "MyApp.Motor", Title = "Motor 1" },
                ["/Demo/Motor2"] = new SubjectNode { Path = "/Demo/Motor2", Type = "MyApp.Motor", Title = "Motor 2" }
            },
            SubjectCount = 2,
            Truncated = true
        };

        return Verifier.Verify(McpTextFormatter.FormatSearchResult(result));
    }
}
