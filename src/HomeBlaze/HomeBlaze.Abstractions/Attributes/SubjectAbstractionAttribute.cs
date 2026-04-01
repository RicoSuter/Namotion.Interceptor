namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks an interface as a subject abstraction eligible for MCP type discovery.
/// Only interfaces with this attribute are included in the list_types tool output.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public class SubjectAbstractionAttribute : Attribute;
