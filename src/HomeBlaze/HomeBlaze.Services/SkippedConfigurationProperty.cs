using System.Text.Json;

namespace HomeBlaze.Services;

/// <summary>
/// A configuration property that <see cref="ConfigurableSubjectSerializer"/> left as it was, because the
/// JSON value for it does not convert to the type of the property.
/// </summary>
/// <param name="PropertyName">The name of the property as it is declared, whatever the naming of the JSON is.</param>
/// <param name="PropertyType">The type of the property.</param>
/// <param name="Json">The JSON text of the value.</param>
/// <param name="Exception">Why the value does not convert.</param>
public sealed record SkippedConfigurationProperty(
    string PropertyName, Type PropertyType, string Json, JsonException Exception);
