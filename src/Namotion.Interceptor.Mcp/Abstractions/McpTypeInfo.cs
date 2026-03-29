using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Describes a type available in the subject registry.
/// </summary>
/// <param name="Name">Full type name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="IsInterface">True for abstraction interfaces, false for concrete types.</param>
/// <param name="Type">The CLR type (excluded from JSON serialization).</param>
public record McpTypeInfo(
    string Name,
    string? Description,
    bool IsInterface,
    [property: JsonIgnore] Type Type);
