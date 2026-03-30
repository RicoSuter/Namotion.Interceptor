using System.Text;
using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Formats MCP tool results as compact text optimized for LLM comprehension.
/// </summary>
internal static class McpTextFormatter
{
    private static readonly string[] Indents = Enumerable.Range(0, 12).Select(i => new string(' ', i * 2)).ToArray();

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
        var indentStr = indent < Indents.Length ? Indents[indent] : new string(' ', indent * 2);

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
            var attributeIndent = indentStr + "    ";

            foreach (var (propertyName, property) in node.Properties)
            {
                switch (property)
                {
                    case ScalarProperty scalar:
                        FormatScalarProperty(sb, scalar, propertyName, childIndent, attributeIndent);
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
                        FormatCollapsedProperty(sb, propertyName, collapsed.Count, collapsed.ItemType, childIndent);
                        break;

                    case SubjectDictionaryProperty { IsCollapsed: true } collapsed:
                        FormatCollapsedProperty(sb, propertyName, collapsed.Count, collapsed.ItemType, childIndent);
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
        StringBuilder sb, ScalarProperty property, string name, string indent, string attributeIndent)
    {
        sb.Append(indent);
        sb.Append(name);
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
                sb.Append(attributeIndent);
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
        switch (value)
        {
            case null:
                return "null";
            case JsonElement je:
                return FormatJsonElement(je);
            case string { Length: 0 }:
                return "\"\"";
            case string s:
                return s.Length > MaxStringValueLength ? s[..MaxStringValueLength] + "..." : s;
            case bool b:
                return b ? "true" : "false";
            default:
                return value.ToString() ?? "null";
        }
    }

    private static string FormatJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => FormatValue(element.GetString()),
        _ => element.GetRawText()
    };

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
