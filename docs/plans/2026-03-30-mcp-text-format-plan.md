# MCP Text Format Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `format` parameter to `browse` and `search` MCP tools with `text` (default) and `json` options, using typed DTOs as the internal model. Text is a purpose-built compact format optimized for LLM comprehension. JSON is pretty-printed and structured via DTO serialization.

**Architecture:** Typed DTO model (`SubjectNode`, `SubjectNodeProperty` hierarchy) replaces `Dictionary<string, object?>`. BrowseTool and SearchTool build DTOs during traversal. For text format, `McpTextFormatter` renders DTOs to compact text. For JSON format, DTOs are serialized directly via `System.Text.Json` with `WriteIndented`. `ServiceCollectionExtensions` handles string (text) vs object (json) results.

**Tech Stack:** C# / .NET 9.0, System.Text.Json polymorphic serialization, xUnit, Verify.Xunit 31.3.0 (snapshot testing)

**Design doc:** `docs/plans/2026-03-30-mcp-text-format-design.md`

---

### Task 1: Create DTO model classes

Define the typed model that replaces `Dictionary<string, object?>` for tool results. Properties are polymorphic: scalar values, subject references, collections, and dictionaries — each with an `IsCollapsed` flag for depth-boundary behavior.

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/Models/SubjectNode.cs`
- Create: `src/Namotion.Interceptor.Mcp/Models/SubjectNodeProperty.cs`
- Create: `src/Namotion.Interceptor.Mcp/Models/BrowseResult.cs`
- Create: `src/Namotion.Interceptor.Mcp/Models/SearchResult.cs`

**Step 1: Create the DTO files**

Create `src/Namotion.Interceptor.Mcp/Models/SubjectNode.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Models;

/// <summary>
/// Represents a subject in the MCP tool output tree.
/// </summary>
public class SubjectNode
{
    [JsonPropertyName("$path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("$type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("$title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>
    /// Additional enrichments (e.g., $icon, $customField).
    /// Merged as top-level JSON properties via JsonExtensionData.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Enrichments { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Methods { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Interfaces { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubjectNodeProperty>? Properties { get; init; }
}
```

Create `src/Namotion.Interceptor.Mcp/Models/SubjectNodeProperty.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Models;

/// <summary>
/// Base class for subject node properties. Mirrors the RegisteredSubjectProperty model:
/// scalar properties have values, subject properties contain other subjects.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ScalarProperty), "value")]
[JsonDerivedType(typeof(SubjectObjectProperty), "object")]
[JsonDerivedType(typeof(SubjectCollectionProperty), "collection")]
[JsonDerivedType(typeof(SubjectDictionaryProperty), "dictionary")]
public abstract record SubjectNodeProperty(string Name);

/// <summary>
/// A scalar property with a value (string, number, boolean, etc.).
/// </summary>
public record ScalarProperty(
    string Name,
    object? Value,
    string Type,
    bool IsWritable = false,
    List<PropertyAttribute>? Attributes = null
) : SubjectNodeProperty(Name);

/// <summary>
/// A property referencing a single subject.
/// </summary>
public record SubjectObjectProperty(
    string Name,
    SubjectNode? Child,
    bool IsCollapsed = false
) : SubjectNodeProperty(Name);

/// <summary>
/// A property containing an ordered collection of subjects.
/// </summary>
public record SubjectCollectionProperty(
    string Name,
    List<SubjectNode>? Children = null,
    int? Count = null,
    string? ItemType = null,
    bool IsCollapsed = false
) : SubjectNodeProperty(Name);

/// <summary>
/// A property containing a keyed dictionary of subjects.
/// </summary>
public record SubjectDictionaryProperty(
    string Name,
    Dictionary<string, SubjectNode>? Children = null,
    int? Count = null,
    string? ItemType = null,
    bool IsCollapsed = false
) : SubjectNodeProperty(Name);

/// <summary>
/// An attribute attached to a scalar property.
/// </summary>
public record PropertyAttribute(string Name, object? Value);
```

Create `src/Namotion.Interceptor.Mcp/Models/BrowseResult.cs`:

```csharp
namespace Namotion.Interceptor.Mcp.Models;

public class BrowseResult
{
    public required SubjectNode Result { get; init; }
    public int SubjectCount { get; init; }
    public bool Truncated { get; init; }
}
```

Create `src/Namotion.Interceptor.Mcp/Models/SearchResult.cs`:

```csharp
namespace Namotion.Interceptor.Mcp.Models;

public class SearchResult
{
    public required Dictionary<string, SubjectNode> Results { get; init; }
    public int SubjectCount { get; init; }
    public bool Truncated { get; init; }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Mcp`
Expected: Success

**Step 3: Commit**

```
feat(mcp): add DTO model classes for typed tool results
```

---

### Task 2: Create McpTextFormatter using DTOs

Build the text formatter that renders DTOs to the compact text format. This is a pure function: DTOs in, string out. Tested with Verify snapshots.

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp.Tests/Namotion.Interceptor.Mcp.Tests.csproj` (add Verify.Xunit)
- Create: `src/Namotion.Interceptor.Mcp/Tools/McpTextFormatter.cs`
- Create: `src/Namotion.Interceptor.Mcp.Tests/Tools/McpTextFormatterTests.cs`
- Verified snapshot files will be created automatically by Verify

**Step 0: Add Verify.Xunit to test project**

Add to `src/Namotion.Interceptor.Mcp.Tests/Namotion.Interceptor.Mcp.Tests.csproj`:
```xml
<PackageReference Include="Verify.Xunit" Version="31.3.0" />
```

**Step 1: Write snapshot tests**

Create `src/Namotion.Interceptor.Mcp.Tests/Tools/McpTextFormatterTests.cs`:

```csharp
using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

[UsesVerify]
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatBrowseResult(result));
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

        return Verify(McpTextFormatter.FormatSearchResult(result));
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

        return Verify(McpTextFormatter.FormatSearchResult(result));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "McpTextFormatterTests" -v n`
Expected: FAIL — `McpTextFormatter` does not exist

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.StartsWith("# ", text);
        Assert.Contains("[1 subject]", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_subject_line_with_enrichments()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Demo",
                Type = "MyApp.VirtualFolder",
                Title = "Demo",
                Enrichments = new Dictionary<string, object?> { ["$icon"] = "Folder" }
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("/Demo [MyApp.VirtualFolder] \"Demo\" $icon=Folder", text);
        // $type should NOT appear as enrichment (it's in [brackets])
        Assert.DoesNotContain("$type=", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_scalar_properties()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties =
                [
                    new ScalarProperty("Speed", 1500, "integer", IsWritable: true),
                    new ScalarProperty("Name", "Motor", "string")
                ]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("Speed: 1500 | integer, writable", text);
        Assert.Contains("Name: Motor | string", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_null_and_empty_string()
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
                    new ScalarProperty("EmptyProp", "", "string")
                ]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("NullProp: null | string", text);
        Assert.Contains("EmptyProp: \"\" | string", text);
    }

    [Fact]
    public void FormatBrowseResult_truncates_long_string_values()
    {
        var longValue = new string('x', 150);
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties = [new ScalarProperty("Desc", longValue, "string")]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("...", text);
        Assert.DoesNotContain(longValue, text);
    }

    [Fact]
    public void FormatBrowseResult_renders_attributes()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Properties =
                [
                    new ScalarProperty("Speed", 1500, "integer", Attributes:
                    [
                        new PropertyAttribute("Minimum", 0),
                        new PropertyAttribute("Maximum", 3000),
                        new PropertyAttribute("State", new Dictionary<string, object?> { ["Title"] = "Speed", ["Unit"] = 0 })
                    ])
                ]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("@Minimum: 0", text);
        Assert.Contains("@Maximum: 3000", text);
        Assert.Contains("@State: {", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_methods_and_interfaces()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Device",
                Type = "MyApp.Device",
                Methods = ["Start", "Stop"],
                Interfaces = ["IMotor", "IDevice"]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("methods: Start() Stop()", text);
        Assert.Contains("interfaces: IMotor, IDevice", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_child_subject_indented()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties =
                [
                    new SubjectObjectProperty("Device",
                        Child: new SubjectNode { Path = "/Root/Device", Type = "MyApp.Device" })
                ]
            },
            SubjectCount = 2
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("  /Root/Device [MyApp.Device]", text);
        Assert.Contains("[2 subjects]", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_collection_children()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties =
                [
                    new SubjectDictionaryProperty("Items", Children: new Dictionary<string, SubjectNode>
                    {
                        ["A"] = new SubjectNode { Path = "/Root/Items[A]", Type = "MyApp.Item" },
                        ["B"] = new SubjectNode { Path = "/Root/Items[B]", Type = "MyApp.Item" }
                    })
                ]
            },
            SubjectCount = 3
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("  /Root/Items[A] [MyApp.Item]", text);
        Assert.Contains("  /Root/Items[B] [MyApp.Item]", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_collapsed_collection()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties =
                [
                    new SubjectCollectionProperty("Sensors", IsCollapsed: true, Count: 5, ItemType: "Sensor")
                ]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("Sensors/ (5x Sensor)", text);
    }

    [Fact]
    public void FormatBrowseResult_renders_collapsed_without_item_type()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode
            {
                Path = "/Root",
                Type = "MyApp.Root",
                Properties =
                [
                    new SubjectCollectionProperty("Children", IsCollapsed: true, Count: 7)
                ]
            },
            SubjectCount = 1
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("Children/ (7 children)", text);
    }

    [Fact]
    public void FormatBrowseResult_footer_singular_plural()
    {
        var node = new SubjectNode { Path = "/", Type = "R" };

        var zero = McpTextFormatter.FormatBrowseResult(new BrowseResult { Result = node, SubjectCount = 0 });
        var one = McpTextFormatter.FormatBrowseResult(new BrowseResult { Result = node, SubjectCount = 1 });
        var two = McpTextFormatter.FormatBrowseResult(new BrowseResult { Result = node, SubjectCount = 2 });

        Assert.Contains("[0 subjects]", zero);
        Assert.Contains("[1 subject]", one);
        Assert.Contains("[2 subjects]", two);
    }

    [Fact]
    public void FormatBrowseResult_truncated_footer()
    {
        var result = new BrowseResult
        {
            Result = new SubjectNode { Path = "/", Type = "R" },
            SubjectCount = 3,
            Truncated = true
        };

        var text = McpTextFormatter.FormatBrowseResult(result);

        Assert.Contains("[3 subjects, truncated]", text);
    }

    [Fact]
    public void FormatSearchResult_renders_flat_list_with_blank_lines()
    {
        var result = new SearchResult
        {
            Results = new Dictionary<string, SubjectNode>
            {
                ["/Demo/Motor1"] = new SubjectNode { Path = "/Demo/Motor1", Type = "MyApp.Motor", Title = "Motor 1" },
                ["/Demo/Motor2"] = new SubjectNode { Path = "/Demo/Motor2", Type = "MyApp.Motor", Title = "Motor 2" }
            },
            SubjectCount = 2
        };

        var text = McpTextFormatter.FormatSearchResult(result);

        Assert.Contains("/Demo/Motor1 [MyApp.Motor] \"Motor 1\"", text);
        Assert.Contains("/Demo/Motor2 [MyApp.Motor] \"Motor 2\"", text);
        Assert.Contains("[2 subjects]", text);

        // Blank line between subjects
        var idx1 = text.IndexOf("/Demo/Motor1");
        var idx2 = text.IndexOf("/Demo/Motor2");
        var between = text[idx1..idx2];
        Assert.Contains("\n\n", between);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "McpTextFormatter" -v n`
Expected: FAIL — `McpTextFormatter` does not exist

**Step 3: Implement McpTextFormatter**

Create `src/Namotion.Interceptor.Mcp/Tools/McpTextFormatter.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Formats MCP tool results as compact text optimized for LLM comprehension.
/// </summary>
internal static class McpTextFormatter
{
    private const string Legend =
        "# path [Type] \"title\" $key=value | prop: value | type | @attr: value | Collection/ (Nx Type)\n" +
        "# Use get_property for exact values or browse with format=json for structured data.";

    private const int MaxStringValueLength = 100;

    public static string FormatBrowseResult(BrowseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Legend);
        sb.AppendLine();
        FormatSubjectNode(sb, result.Result, indent: 0);
        AppendFooter(sb, result.SubjectCount, result.Truncated);
        return sb.ToString();
    }

    public static string FormatSearchResult(SearchResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Legend);
        sb.AppendLine();

        var first = true;
        foreach (var (_, node) in result.Results)
        {
            if (!first)
            {
                sb.AppendLine();
            }

            FormatSubjectNode(sb, node, indent: 0);
            first = false;
        }

        AppendFooter(sb, result.SubjectCount, result.Truncated);
        return sb.ToString();
    }

    private static void FormatSubjectNode(StringBuilder sb, SubjectNode node, int indent)
    {
        var indentStr = new string(' ', indent * 2);

        // Subject line: path [Type] "title" $enrichments
        sb.Append(indentStr);
        if (node.Path is not null)
        {
            sb.Append(node.Path);
        }

        if (node.Type is not null)
        {
            sb.Append($" [{node.Type}]");
        }

        if (node.Title is not null)
        {
            sb.Append($" \"{node.Title}\"");
        }

        if (node.Enrichments is not null)
        {
            foreach (var (key, value) in node.Enrichments)
            {
                sb.Append($" {key}={value}");
            }
        }

        sb.AppendLine();

        // Properties
        if (node.Properties is not null)
        {
            var childIndent = indentStr + "  ";
            var attrIndent = indentStr + "    ";

            foreach (var property in node.Properties)
            {
                switch (property)
                {
                    case ScalarProperty scalar:
                        FormatScalarProperty(sb, scalar, childIndent, attrIndent);
                        break;

                    case SubjectObjectProperty { IsCollapsed: false } obj:
                        if (obj.Child is not null)
                        {
                            FormatSubjectNode(sb, obj.Child, indent + 1);
                        }
                        break;

                    case SubjectCollectionProperty { IsCollapsed: false } collection:
                        if (collection.Children is not null)
                        {
                            foreach (var child in collection.Children)
                            {
                                FormatSubjectNode(sb, child, indent + 1);
                            }
                        }
                        break;

                    case SubjectDictionaryProperty { IsCollapsed: false } dictionary:
                        if (dictionary.Children is not null)
                        {
                            foreach (var (_, child) in dictionary.Children)
                            {
                                FormatSubjectNode(sb, child, indent + 1);
                            }
                        }
                        break;

                    case SubjectCollectionProperty { IsCollapsed: true } collapsed:
                        FormatCollapsedProperty(sb, collapsed.Name, collapsed.Count, collapsed.ItemType, childIndent);
                        break;

                    case SubjectDictionaryProperty { IsCollapsed: true } collapsed:
                        FormatCollapsedProperty(sb, collapsed.Name, collapsed.Count, collapsed.ItemType, childIndent);
                        break;

                    case SubjectObjectProperty { IsCollapsed: true }:
                        // Single reference collapsed — nothing meaningful to show
                        break;
                }
            }
        }

        // Methods
        if (node.Methods is { Length: > 0 })
        {
            sb.Append(indentStr);
            sb.Append("  methods: ");
            sb.AppendLine(string.Join(" ", node.Methods.Select(m => $"{m}()")));
        }

        // Interfaces
        if (node.Interfaces is { Length: > 0 })
        {
            sb.Append(indentStr);
            sb.Append("  interfaces: ");
            sb.AppendLine(string.Join(", ", node.Interfaces));
        }
    }

    private static void FormatScalarProperty(
        StringBuilder sb, ScalarProperty property, string indent, string attrIndent)
    {
        sb.Append(indent);
        sb.Append(property.Name);
        sb.Append(": ");
        sb.Append(FormatValue(property.Value));
        sb.Append(" | ");
        sb.Append(property.Type);
        if (property.IsWritable)
        {
            sb.Append(", writable");
        }

        sb.AppendLine();

        if (property.Attributes is not null)
        {
            foreach (var attribute in property.Attributes)
            {
                sb.Append(attrIndent);
                sb.Append('@');
                sb.Append(attribute.Name);
                sb.Append(": ");
                sb.AppendLine(FormatAttributeValue(attribute.Value));
            }
        }
    }

    private static void FormatCollapsedProperty(
        StringBuilder sb, string name, int? count, string? itemType, string indent)
    {
        sb.Append(indent);
        sb.Append(name);
        sb.Append("/ (");

        if (itemType is not null)
        {
            sb.Append($"{count}x {itemType}");
        }
        else
        {
            sb.Append($"{count} children");
        }

        sb.Append(')');
        sb.AppendLine();
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "null";
        if (value is string s)
        {
            if (s.Length == 0) return "\"\"";
            return s.Length > MaxStringValueLength ? s[..MaxStringValueLength] + "..." : s;
        }
        if (value is bool b) return b ? "true" : "false";
        return value.ToString() ?? "null";
    }

    private static string FormatAttributeValue(object? value)
    {
        if (value is null or string or bool or int or long or double or float or decimal or short or byte)
        {
            return FormatValue(value);
        }

        return JsonSerializer.Serialize(value);
    }

    private static void AppendFooter(StringBuilder sb, int subjectCount, bool truncated)
    {
        var noun = subjectCount == 1 ? "subject" : "subjects";
        sb.Append(truncated
            ? $"[{subjectCount} {noun}, truncated]"
            : $"[{subjectCount} {noun}]");
    }
}
```

**Step 4: Run tests and accept snapshots**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "McpTextFormatterTests" -v n`
Expected: First run creates `.received.txt` files. Review them for correctness, then rename to `.verified.txt` to accept. Re-run to verify all pass.

**Step 5: Commit (include .verified.txt snapshot files)**

```
feat(mcp): add McpTextFormatter for compact text output using DTOs
```

---

### Task 3: Refactor McpToolHelper to build DTOs

Replace the `Dictionary<string, object?>` building methods with DTO-producing equivalents. Keep the existing dict methods temporarily until BrowseTool/SearchTool are migrated.

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp/Tools/McpToolHelper.cs`

**Step 1: Add DTO builder methods to McpToolHelper**

Add to `McpToolHelper.cs` alongside the existing methods:

```csharp
using Namotion.Interceptor.Mcp.Models;

// ... existing code stays until Task 4/5 migrate the tools ...

internal static SubjectNode BuildSubjectNodeDto(
    RegisteredSubject subject,
    PathProviderBase pathProvider,
    IInterceptorSubject rootSubject,
    McpServerConfiguration configuration,
    bool includeProperties,
    bool includeAttributes,
    bool includeMethods,
    bool includeInterfaces)
{
    var path = TryGetSubjectPath(subject, pathProvider, rootSubject, configuration.PathPrefix);

    // Collect enrichments and extract known fields
    var enrichments = new Dictionary<string, object?>();
    foreach (var enricher in configuration.SubjectEnrichers)
    {
        foreach (var kvp in enricher.GetSubjectEnrichments(subject))
        {
            enrichments[kvp.Key] = kvp.Value;
        }
    }

    // Extract known metadata from enrichments
    var type = subject.Subject.GetType().FullName;
    if (enrichments.Remove("$type", out var typeOverride) && typeOverride is string typeStr)
    {
        type = typeStr;
    }

    string? title = null;
    if (enrichments.Remove("$title", out var titleObj) && titleObj is string titleStr)
    {
        title = titleStr;
    }

    string[]? methods = null;
    if (includeMethods && enrichments.Remove("$methods", out var methodsObj))
    {
        methods = ToStringArray(methodsObj);
    }
    else
    {
        enrichments.Remove("$methods");
    }

    string[]? interfaces = null;
    if (includeInterfaces && enrichments.Remove("$interfaces", out var ifacesObj))
    {
        interfaces = ToStringArray(ifacesObj);
    }
    else
    {
        enrichments.Remove("$interfaces");
    }

    // Build scalar properties
    List<SubjectNodeProperty>? properties = null;
    if (includeProperties)
    {
        properties = [];
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property) || property.CanContainSubjects)
            {
                continue;
            }

            var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
            properties.Add(BuildScalarPropertyDto(property, segment, includeAttributes, configuration.IsReadOnly));
        }
    }

    return new SubjectNode
    {
        Path = path,
        Type = type,
        Title = title,
        Enrichments = enrichments.Count > 0 ? enrichments : null,
        Methods = methods,
        Interfaces = interfaces,
        Properties = properties
    };
}

internal static ScalarProperty BuildScalarPropertyDto(
    RegisteredSubjectProperty property, string name, bool includeAttributes, bool isReadOnly)
{
    List<PropertyAttribute>? attributes = null;
    if (includeAttributes)
    {
        attributes = [];
        foreach (var attribute in property.Attributes)
        {
            attributes.Add(new PropertyAttribute(attribute.BrowseName, attribute.GetValue()));
        }

        if (attributes.Count == 0)
        {
            attributes = null;
        }
    }

    return new ScalarProperty(
        name,
        property.GetValue(),
        JsonSchemaTypeMapper.ToJsonSchemaType(property.Type) ?? "object",
        IsWritable: !isReadOnly && property.HasSetter,
        Attributes: attributes);
}

private static string[]? ToStringArray(object? value)
{
    if (value is string[] array) return array;
    if (value is IEnumerable<object?> enumerable)
        return enumerable.Select(item => item?.ToString() ?? "").ToArray();
    return null;
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Mcp`
Expected: Success

**Step 3: Commit**

```
refactor(mcp): add DTO builder methods to McpToolHelper
```

---

### Task 4: Refactor BrowseTool to use DTOs and add format parameter

Replace dict-building with DTO-building. Add `format` parameter to schema and handler.

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp/Tools/BrowseTool.cs`
- Modify: `src/Namotion.Interceptor.Mcp.Tests/Tools/BrowseToolTests.cs`
- Modify: `src/Namotion.Interceptor.Mcp.Tests/Tools/BrowseToolEdgeCaseTests.cs`

**Step 1: Write snapshot tests for text and json format**

Add to `BrowseToolTests.cs` (class must have `[UsesVerify]` attribute):

```csharp
[Fact]
public async Task Browse_format_text_snapshot()
{
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
    room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

    var config = new McpServerConfiguration
    {
        PathProvider = DefaultPathProvider.Instance,
        IsReadOnly = false
    };
    var factory = new McpToolFactory(room, config);
    var browseTool = factory.CreateTools().First(t => t.Name == "browse");

    // Default format is text
    var input = JsonSerializer.SerializeToElement(new { depth = 1, includeProperties = true });
    var result = await browseTool.Handler(input, CancellationToken.None);

    Assert.IsType<string>(result);
    await Verify((string)result!);
}

[Fact]
public async Task Browse_format_json_snapshot()
{
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
    room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

    var config = new McpServerConfiguration
    {
        PathProvider = DefaultPathProvider.Instance,
        IsReadOnly = false
    };
    var factory = new McpToolFactory(room, config);
    var browseTool = factory.CreateTools().First(t => t.Name == "browse");

    var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeProperties = true });
    var result = await browseTool.Handler(input, CancellationToken.None);

    Assert.IsNotType<string>(result);
    await Verify(result);
}
```

**Step 2: Rewrite BrowseTool to use DTOs**

Rewrite `src/Namotion.Interceptor.Mcp/Tools/BrowseTool.cs`. The traversal logic stays the same but produces `SubjectNode` and its properties instead of `Dictionary<string, object?>`:

Key changes:
- `BuildSubjectNode` returns `SubjectNode` instead of `Dictionary<string, object?>`
- `BuildSubjectTree` populates `node.Properties` with `SubjectObjectProperty`, `SubjectCollectionProperty`, `SubjectDictionaryProperty`, and `CollapsedProperty` entries instead of adding to a dict
- Add `format` parameter to Schema and HandleBrowseAsync
- Return `McpTextFormatter.FormatBrowseResult(...)` for text format, `BrowseResult` for json format
- Update tool description to mention format

The Schema should add:
```csharp
format = new { type = "string", @enum = new[] { "text", "json" }, description = "Output format: 'text' (default) for LLM-readable overview, 'json' for exact structured data" },
```

The handler should start with:
```csharp
var format = input.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : "text";
```

And return:
```csharp
var browseResult = new BrowseResult { Result = result, SubjectCount = subjectCount, Truncated = truncated };

if (format == "json")
{
    return Task.FromResult<object?>(browseResult);
}

return Task.FromResult<object?>(McpTextFormatter.FormatBrowseResult(browseResult));
```

The `BuildSubjectTree` method should populate the subject's Properties list with subject-containing properties:

```csharp
private void BuildSubjectTree(
    SubjectNode node,
    RegisteredSubject subject,
    PathProviderBase pathProvider,
    int remainingDepth,
    bool includeProperties,
    bool includeAttributes,
    bool includeMethods,
    bool includeInterfaces,
    string[]? excludeTypes,
    HashSet<IInterceptorSubject> visited,
    int maxSubjects,
    ref int subjectCount,
    ref bool truncated)
{
    node.Properties ??= [];

    foreach (var property in subject.Properties)
    {
        if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property))
        {
            continue;
        }

        var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

        if (property.CanContainSubjects)
        {
            if (remainingDepth > 0)
            {
                if (property.IsSubjectReference)
                {
                    var child = property.Children.FirstOrDefault();
                    var childSubject = child.Subject?.TryGetRegisteredSubject();
                    if (child.Subject is not null &&
                        childSubject is not null &&
                        !McpToolHelper.ShouldExcludeByType(childSubject, excludeTypes) &&
                        visited.Add(child.Subject))
                    {
                        if (subjectCount >= maxSubjects)
                        {
                            truncated = true;
                            continue;
                        }

                        subjectCount++;
                        var childNode = BuildSubjectNode(childSubject, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes,
                            includeMethods, includeInterfaces, excludeTypes,
                            visited, maxSubjects, ref subjectCount, ref truncated);
                        node.Properties.Add(new SubjectObjectProperty(segment, childNode));
                    }
                }
                else if (property.IsSubjectDictionary)
                {
                    var children = new Dictionary<string, SubjectNode>();
                    foreach (var child in property.Children)
                    {
                        var childRegistered = child.Subject.TryGetRegisteredSubject();
                        if (childRegistered is null ||
                            McpToolHelper.ShouldExcludeByType(childRegistered, excludeTypes) ||
                            !visited.Add(child.Subject))
                        {
                            continue;
                        }

                        if (subjectCount >= maxSubjects)
                        {
                            truncated = true;
                            break;
                        }

                        subjectCount++;
                        var key = child.Index?.ToString() ?? child.Subject.GetHashCode().ToString();
                        children[key] = BuildSubjectNode(childRegistered, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes,
                            includeMethods, includeInterfaces, excludeTypes,
                            visited, maxSubjects, ref subjectCount, ref truncated);
                    }

                    if (children.Count > 0)
                    {
                        node.Properties.Add(new SubjectDictionaryProperty(segment, Children: children));
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    var children = new List<SubjectNode>();
                    foreach (var child in property.Children)
                    {
                        var childRegistered = child.Subject.TryGetRegisteredSubject();
                        if (childRegistered is null ||
                            McpToolHelper.ShouldExcludeByType(childRegistered, excludeTypes) ||
                            !visited.Add(child.Subject))
                        {
                            continue;
                        }

                        if (subjectCount >= maxSubjects)
                        {
                            truncated = true;
                            break;
                        }

                        subjectCount++;
                        children.Add(BuildSubjectNode(childRegistered, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes,
                            includeMethods, includeInterfaces, excludeTypes,
                            visited, maxSubjects, ref subjectCount, ref truncated));
                    }

                    if (children.Count > 0)
                    {
                        node.Properties.Add(new SubjectCollectionProperty(segment, Children: children));
                    }
                }
            }
            else if (property.Children.Length > 0)
            {
                // Collapsed at depth boundary
                var count = property.Children.Length;
                string? itemType = null;
                var firstType = property.Children[0].Subject.GetType();
                if (property.Children.All(child => child.Subject.GetType() == firstType))
                {
                    itemType = firstType.Name;
                }

                if (property.IsSubjectReference)
                {
                    node.Properties.Add(new SubjectObjectProperty(segment, Child: null, IsCollapsed: true));
                }
                else if (property.IsSubjectDictionary)
                {
                    node.Properties.Add(new SubjectDictionaryProperty(segment, IsCollapsed: true, Count: count, ItemType: itemType));
                }
                else
                {
                    node.Properties.Add(new SubjectCollectionProperty(segment, IsCollapsed: true, Count: count, ItemType: itemType));
                }
            }
        }
        else if (includeProperties)
        {
            node.Properties.Add(
                McpToolHelper.BuildScalarPropertyDto(property, segment, includeAttributes, _configuration.IsReadOnly));
        }
    }
}
```

**Step 3: Fix existing BrowseToolTests**

All existing tests that parse JSON from the handler need `format = "json"` in their input, and assertions need updating for the new DTO-based JSON structure. Key changes:

- `json.GetProperty("result")` still works (BrowseResult has `Result` property)
- Properties are now in a `properties` array with `kind` discriminator instead of flat dict keys
- `$type` is now always present (comes from the DTO, not enrichers)
- Child subjects are nested inside `properties` array entries with `kind: "object"/"collection"/"dictionary"`

Update each test to use `format = "json"` and adjust JSON path assertions.

**Step 4: Run all browse tests**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "BrowseTool" -v n`
Expected: All PASS

**Step 5: Commit**

```
refactor(mcp): rewrite BrowseTool to use DTO model with format parameter
```

---

### Task 5: Refactor SearchTool to use DTOs and add format parameter

Same pattern as Task 4 for the search tool.

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp/Tools/SearchTool.cs`
- Modify: `src/Namotion.Interceptor.Mcp.Tests/Tools/SearchToolTests.cs`
- Modify: `src/Namotion.Interceptor.Mcp.Tests/Tools/SearchToolEdgeCaseTests.cs`

**Step 1: Write snapshot tests for text and json format**

Add to `SearchToolTests.cs` (class must have `[UsesVerify]` attribute):

```csharp
[Fact]
public async Task Search_format_text_snapshot()
{
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
    room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

    var config = new McpServerConfiguration
    {
        PathProvider = DefaultPathProvider.Instance,
        IsReadOnly = false
    };
    var factory = new McpToolFactory(room, config);
    var tool = factory.CreateTools().First(t => t.Name == "search");

    var input = JsonSerializer.SerializeToElement(new
    {
        types = new[] { "TestDevice" },
        includeProperties = true
    });
    var result = await tool.Handler(input, CancellationToken.None);

    Assert.IsType<string>(result);
    await Verify((string)result!);
}

[Fact]
public async Task Search_format_json_snapshot()
{
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
    room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

    var config = new McpServerConfiguration
    {
        PathProvider = DefaultPathProvider.Instance
    };
    var factory = new McpToolFactory(room, config);
    var tool = factory.CreateTools().First(t => t.Name == "search");

    var input = JsonSerializer.SerializeToElement(new
    {
        format = "json",
        types = new[] { "TestDevice" }
    });
    var result = await tool.Handler(input, CancellationToken.None);

    Assert.IsNotType<string>(result);
    await Verify(result);
}
```

**Step 2: Refactor SearchTool to use DTOs**

Key changes to `SearchTool.cs`:
- Add `format` parameter to Schema
- Build `SubjectNode` via `McpToolHelper.BuildSubjectNodeDto()` instead of dict methods
- For scalar properties, use `McpToolHelper.BuildScalarPropertyDto()`
- Collect results as `Dictionary<string, SubjectNode>` instead of `Dictionary<string, object?>`
- Return `McpTextFormatter.FormatSearchResult(...)` for text, `SearchResult` for json
- Update tool description

The handler should build subjects like:

```csharp
var subjects = new Dictionary<string, SubjectNode>();

foreach (var (_, registered) in registry.KnownSubjects)
{
    // ... existing filter logic (type, exclude, path prefix, text match) ...
    // But use McpToolHelper.BuildSubjectNodeDto for enrichment extraction:

    var node = McpToolHelper.BuildSubjectNodeDto(
        registered, pathProvider, rootSubject, _configuration,
        includeProperties, includeAttributes, includeMethods, includeInterfaces);

    // Text filter — match against path and title
    if (!string.IsNullOrEmpty(text))
    {
        var matchesPath = node.Path?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        var matchesTitle = node.Title?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        if (!matchesPath && !matchesTitle)
        {
            continue;
        }
    }

    if (node.Path is null)
    {
        continue;
    }

    subjects[node.Path] = node;
}

var searchResult = new SearchResult
{
    Results = subjects,
    SubjectCount = subjects.Count,
    Truncated = truncated
};

if (format == "json")
{
    return Task.FromResult<object?>(searchResult);
}

return Task.FromResult<object?>(McpTextFormatter.FormatSearchResult(searchResult));
```

**Step 3: Fix existing SearchToolTests**

Add `format = "json"` to all existing test inputs and adjust assertions for the new DTO-based JSON structure.

**Step 4: Run all search tests**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "SearchTool" -v n`
Expected: All PASS

**Step 5: Commit**

```
refactor(mcp): rewrite SearchTool to use DTO model with format parameter
```

---

### Task 6: Update ServiceCollectionExtensions and clean up McpToolHelper

Handle string results (text format) in the MCP handler. Enable WriteIndented for JSON. Remove old dict-building methods from McpToolHelper.

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Namotion.Interceptor.Mcp/Tools/McpToolHelper.cs`

**Step 1: Update ServiceCollectionExtensions**

In `ServiceCollectionExtensions.cs`, update SerializerOptions (line 14-17):

```csharp
internal static readonly JsonSerializerOptions SerializerOptions = new()
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
```

Update the handler result serialization (line 71-75):

```csharp
var result = await tool.Handler(input, cancellationToken);
var text = result is string stringResult
    ? stringResult
    : JsonSerializer.Serialize(result, SerializerOptions);
return new CallToolResult
{
    Content = [new TextContentBlock { Text = text }]
};
```

**Step 2: Remove old dict-building methods from McpToolHelper**

Remove the following methods that are no longer used:
- `BuildPropertyValue` (replaced by `BuildScalarPropertyDto`)
- `BuildSubjectNode` (dict version, replaced by `BuildSubjectNodeDto`)
- `ApplyEnrichments` (inlined into `BuildSubjectNodeDto`)
- `FilterEnrichments` (handled by DTO builder)
- `ApplyProperties` (handled by DTO builder)

Keep:
- `TryGetSubjectPath` (still used)
- `ShouldExcludeByType` (still used)

**Step 3: Run full test suite**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests -v n`
Expected: All PASS

**Step 4: Commit**

```
refactor(mcp): update serialization, remove old dict-building methods
```

---

### Task 7: Update design doc, finalize docs, and validate

**Files:**
- Modify: `docs/plans/2026-03-30-mcp-text-format-design.md` (update with DTO model, new JSON structure)
- Modify: `docs/mcp.md` (update JSON examples for DTO-based structure)

**Step 1: Update design doc**

Update `docs/plans/2026-03-30-mcp-text-format-design.md`:
- Replace the "Implementation Notes" section with the DTO model description
- Update JSON examples to show the new structured format with `properties` array and `kind` discriminator
- Document the `SubjectNodeProperty` hierarchy: `ScalarProperty`, `SubjectObjectProperty`, `SubjectCollectionProperty`, `SubjectDictionaryProperty`
- Note that `IsCollapsed` replaces the separate `CollapsedProperty` type

**Step 2: Update docs/mcp.md JSON examples**

Update the JSON examples in `docs/mcp.md` to reflect the new DTO-based structure:
- Browse JSON example: show `properties` array with `kind` discriminator
- Search JSON example: same structure per subject node
- Verify text format examples are still accurate

**Step 3: Build the full solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Success

**Step 4: Run the full test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" -v n`
Expected: All PASS

**Step 5: Commit**

```
docs: update design doc and MCP documentation for DTO-based output format
```
