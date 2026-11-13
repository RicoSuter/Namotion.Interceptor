using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services;

/// <summary>
/// Decides whether a configurable subject takes a new configuration in place or is created again from it.
/// </summary>
public static class ConfigurationUpdatePolicy
{
    /// <summary>
    /// Gets whether <paramref name="existing"/> can take <paramref name="json"/> in place: it has the type
    /// <paramref name="type"/>, no key of the configuration applied last is missing from <paramref name="json"/>, and
    /// no property marked <see cref="ConfigurationAttribute.RecreateOnChange"/> changed its value.
    /// </summary>
    /// <param name="existing">The subject that took the configuration applied last, or null.</param>
    /// <param name="type">The type that <paramref name="json"/> describes.</param>
    /// <param name="json">The new configuration, a JSON object.</param>
    /// <param name="appliedJson">The configuration applied last, or null when it is not known.</param>
    /// <param name="appliedKeys">The top-level keys applied last, or null to take those of <paramref name="appliedJson"/>.</param>
    /// <param name="naming">The naming of the keys of both configurations.</param>
    /// <exception cref="JsonException">A configuration is not valid JSON.</exception>
    public static bool CanUpdateInPlace(
        IInterceptorSubject? existing,
        Type type,
        string json,
        string? appliedJson,
        IReadOnlyCollection<string>? appliedKeys,
        ConfigurationNaming naming)
    {
        if (existing is null || existing.GetType() != type)
        {
            return false;
        }

        if (appliedJson is null && appliedKeys is null)
        {
            return true;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var appliedDocument = appliedJson is null ? null : JsonDocument.Parse(appliedJson);
        var applied = appliedDocument?.RootElement;

        // Updating in place sets only the keys that are present, so a removed key would keep its old value.
        foreach (var key in appliedKeys ?? GetKeys(applied!.Value))
        {
            if (!root.TryGetProperty(key, out _))
            {
                return false;
            }
        }

        if (applied is not { ValueKind: JsonValueKind.Object } appliedRoot)
        {
            return true;
        }

        foreach (var name in GetRecreateOnChangeNames(existing, naming))
        {
            var hasValue = root.TryGetProperty(name, out var value);
            var hadValue = appliedRoot.TryGetProperty(name, out var appliedValue);
            if (hasValue != hadValue || (hasValue && !JsonElement.DeepEquals(value, appliedValue)))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> GetKeys(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name != ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName)
            : [];

    // Read from the registry, which holds the attributes of every property of an attached subject without reflection.
    private static IEnumerable<string> GetRecreateOnChangeNames(IInterceptorSubject subject, ConfigurationNaming naming) =>
        subject.TryGetRegisteredSubject() is { } registered
            ? registered.Properties
                .Where(property => !property.IsAttribute &&
                    property.ReflectionAttributes.OfType<ConfigurationAttribute>().Any(attribute => attribute.RecreateOnChange))
                .Select(property => naming == ConfigurationNaming.JsonProperty ? JsonNamingPolicy.CamelCase.ConvertName(property.Name) : property.Name)
            : [];
}
