namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a subject class as JSON-serializable with [Configuration] properties.
/// Subjects with this attribute can be auto-persisted by StorageService.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class ConfigurableAttribute : Attribute
{
}
