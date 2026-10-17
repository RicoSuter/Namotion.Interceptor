namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property whose value a page expression never shows, as if the property did not exist, because the value can
/// hold secret configuration values: for example the raw source of a file.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class ExcludeFromExpressionsAttribute : Attribute;
