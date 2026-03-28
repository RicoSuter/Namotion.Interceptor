namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Describes a type available in the subject registry.
/// </summary>
/// <param name="Name">Full type name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="IsInterface">True for abstraction interfaces, false for concrete types.</param>
public record McpTypeInfo(string Name, string? Description, bool IsInterface);
