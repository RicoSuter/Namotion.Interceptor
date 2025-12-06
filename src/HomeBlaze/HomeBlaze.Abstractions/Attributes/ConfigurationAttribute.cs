namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property as configuration that should be persisted to JSON.
/// Properties without this attribute are considered runtime-only state.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class ConfigurationAttribute : Attribute
{
}
