using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

// Building the configuration of a block and applying it to a new or an existing subject.
public sealed partial class MarkdownContentParser
{
    private const string TypeDiscriminator = "$type";

    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> ConfigurationPropertyCache = new();

    // A JsonObject with a duplicate key parses and then throws on the first write, so the duplicate is rejected while parsing.
    private static readonly JsonDocumentOptions StrictJsonOptions = new() { AllowDuplicateProperties = false };

    // The top-level keys of the configuration last applied to a subject. See CanUpdateInPlace.
    private readonly ConditionalWeakTable<IInterceptorSubject, string[]> _appliedConfigurationKeys = new();

    private async Task<IInterceptorSubject?> CreateOrUpdateAsync(
        Type type,
        IInterceptorSubject? existing,
        BlockConfiguration configuration,
        ParseState state,
        CancellationToken cancellationToken)
    {
        IInterceptorSubject? subject;
        if (CanUpdateInPlace(existing, type, configuration.Keys))
        {
            _serializer.UpdateConfiguration(existing, configuration.Json);
            if (existing is IConfigurable configurable)
            {
                await configurable.ApplyConfigurationAsync(cancellationToken);
            }

            subject = existing;
        }
        else
        {
            subject = _serializer.Deserialize(configuration.Json) as IInterceptorSubject;
            if (subject is not null)
            {
                state.AddCreated(subject);
            }
        }

        if (subject is not null)
        {
            _appliedConfigurationKeys.AddOrUpdate(subject, configuration.Keys);
        }

        return subject;
    }

    /// <summary>
    /// Records the configuration that was written to the page for a subject, so that the reload
    /// which follows the write updates the subject in place.
    /// </summary>
    internal void RememberConfiguration(IInterceptorSubject subject, string configurationJson)
    {
        using var document = JsonDocument.Parse(configurationJson);

        var keys = new List<string>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name != TypeDiscriminator)
            {
                keys.Add(property.Name);
            }
        }

        _appliedConfigurationKeys.AddOrUpdate(subject, keys.ToArray());
    }

    // Updating in place only sets the keys that are present. A key that was removed from the page
    // would keep its old value on the instance, unlike after a restart, so the subject is recreated then.
    private bool CanUpdateInPlace([NotNullWhen(true)] IInterceptorSubject? existing, Type type, string[] keys)
    {
        if (existing is null || existing.GetType() != type)
        {
            return false;
        }

        return !_appliedConfigurationKeys.TryGetValue(existing, out var previousKeys)
            || Array.TrueForAll(previousKeys, previousKey => Array.IndexOf(keys, previousKey) >= 0);
    }

    private bool TryBuildConfiguration(
        Type type, MarkdownBlock block, out BlockConfiguration blockConfiguration, out string? error)
    {
        blockConfiguration = default;

        var properties = GetConfigurationProperties(type);
        var configuration = ParseConfigurationFence(type, block.ConfigurationJson, properties, out error);
        if (configuration is null)
        {
            return false;
        }

        foreach (var argument in block.Head!.Arguments)
        {
            if (!properties.TryGetValue(argument.Key, out var propertyType))
            {
                error = FormatUnknownProperty(type, argument.Key, properties);
                return false;
            }

            var node = CreateArgumentNode(argument.Value, propertyType);
            if (node is null || !_serializer.CanDeserialize(node.ToJsonString(), propertyType))
            {
                var expectedType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                error = $"'{argument.Value}' is not a valid value for '{argument.Key}'. Expected a value of type {expectedType.Name}.";
                return false;
            }

            configuration[argument.Key] = node;
        }

        // The fence may carry a discriminator of its own, so it is set before the keys are counted.
        configuration[TypeDiscriminator] = type.FullName;

        var keys = new string[configuration.Count - 1];
        var keyCount = 0;
        foreach (var (key, _) in configuration)
        {
            if (key != TypeDiscriminator)
            {
                keys[keyCount++] = key;
            }
        }

        blockConfiguration = new BlockConfiguration(configuration.ToJsonString(), keys);
        return true;
    }

    private static JsonObject? ParseConfigurationFence(
        Type type, string? text, IReadOnlyDictionary<string, Type> properties, out string? error)
    {
        error = null;
        if (text is null)
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: StrictJsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"The json block must contain a valid JSON object without duplicate keys. {exception.Message}";
            return null;
        }

        if (node is not JsonObject configuration)
        {
            error = "The json block must contain a JSON object.";
            return null;
        }

        foreach (var (key, _) in configuration)
        {
            if (key != TypeDiscriminator && !properties.ContainsKey(key))
            {
                error = FormatUnknownProperty(type, key, properties);
                return null;
            }
        }

        return configuration;
    }

    private static string FormatUnknownProperty(Type type, string key, IReadOnlyDictionary<string, Type> properties) =>
        $"{type.Name} has no configuration property '{key}'. Available: {string.Join(", ", properties.Keys.Order())}.";

    private static IReadOnlyDictionary<string, Type> GetConfigurationProperties(Type type) =>
        ConfigurationPropertyCache.GetOrAdd(type, static key => key
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true) is not null)
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => property.PropertyType));

    // The literal an argument stands for: a bare literal for a bool or numeric property, a JSON string for every
    // other type. Null when the text is no such literal.
    private static JsonNode? CreateArgumentNode(string value, Type propertyType)
    {
        var target = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (target == typeof(bool))
        {
            return bool.TryParse(value, out var boolean) ? JsonValue.Create(boolean) : null;
        }

        var isNumeric = target == typeof(decimal) || (target.IsPrimitive && target != typeof(char));
        if (!isNumeric)
        {
            return JsonValue.Create(value);
        }

        try
        {
            var node = JsonNode.Parse(value);
            return node?.GetValueKind() == JsonValueKind.Number ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The merged configuration of one block and its top-level keys without the type discriminator.</summary>
    private readonly record struct BlockConfiguration(string Json, string[] Keys);
}
