using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Shared helper methods for MCP tool implementations.
/// </summary>
internal static class McpToolHelper
{
    internal static object? BuildPropertyValue(RegisteredSubjectProperty property, bool includeAttributes)
    {
        if (!includeAttributes)
        {
            return new { value = property.GetValue() };
        }

        var attributes = new Dictionary<string, object?>();
        foreach (var attribute in property.Attributes)
        {
            attributes[attribute.BrowseName] = attribute.GetValue();
        }

        var result = new Dictionary<string, object?>
        {
            ["value"] = property.GetValue()
        };

        if (attributes.Count > 0)
        {
            result["attributes"] = attributes;
        }

        return result;
    }
}
