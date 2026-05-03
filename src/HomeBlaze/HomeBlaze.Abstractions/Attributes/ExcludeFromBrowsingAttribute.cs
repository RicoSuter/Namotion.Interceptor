namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject type to be excluded from browsing in the UI panel and MCP tools.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ExcludeFromBrowsingAttribute : Attribute;
