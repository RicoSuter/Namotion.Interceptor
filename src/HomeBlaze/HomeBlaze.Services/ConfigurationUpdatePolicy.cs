using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Decides whether a configurable subject takes a new configuration in place or is created again from it.
/// </summary>
public static class ConfigurationUpdatePolicy
{
    private static readonly ConcurrentDictionary<Type, string[]> RecreateOnChangeNames = new();

    /// <summary>
    /// Gets whether <paramref name="existing"/> can take <paramref name="json"/> in place: it has the type
    /// <paramref name="type"/>, no key of the configuration applied last is missing from <paramref name="json"/>, and
    /// no property marked <see cref="ConfigurationAttribute.RecreateOnChange"/> changed its value.
    /// </summary>
    /// <param name="existing">The subject that took the configuration applied last, or null.</param>
    /// <param name="type">The type that <paramref name="json"/> describes.</param>
    /// <param name="json">The new configuration, a JSON object.</param>
    /// <param name="appliedJson">
    /// The configuration applied last, or null when it is not known. The <see cref="ConfigurationAttribute.RecreateOnChange"/>
    /// comparison needs it: without it only the keys are checked.
    /// </param>
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

        foreach (var declaredName in GetRecreateOnChangeNames(type))
        {
            var name = ConfigurableSubjectSerializer.GetJsonName(declaredName, naming);
            var hasValue = root.TryGetProperty(name, out var value);
            var hadValue = appliedRoot.TryGetProperty(name, out var appliedValue);

            // A missing key keeps the value on update but takes the default on create, so absent after null is
            // unchanged (the serializer omits nulls), while null after absent can replace a non-null default.
            var isUnchanged = hasValue
                ? hadValue && JsonElement.DeepEquals(value, appliedValue)
                : !hadValue || appliedValue.ValueKind == JsonValueKind.Null;
            if (!isUnchanged)
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

    // Read from the type and not the registry, so the answer does not depend on whether the subject is attached.
    private static string[] GetRecreateOnChangeNames(Type type) =>
        RecreateOnChangeNames.GetOrAdd(type, static type => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true)?.RecreateOnChange == true)
            .Select(property => property.Name)
            .ToArray());
}
