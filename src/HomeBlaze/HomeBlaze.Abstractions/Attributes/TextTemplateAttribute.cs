namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a text configuration property whose expressions between double braces, such as <c>{{ subject/Name }}</c>, the
/// widget evaluates when it renders. The configuration keeps the text as written.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TextTemplateAttribute : Attribute;
