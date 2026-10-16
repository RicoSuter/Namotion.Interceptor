using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

// Building the configuration of a block and applying it to a new or an existing subject.
public sealed partial class MarkdownContentParser
{
    private const string TypeDiscriminator = ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName;

    // How the reader of the JSON ends the message of an exception, and what it says when a subject has no
    // type. See FormatSkippedProperty.
    private const string ReaderPathLabel = " Path: ";
    private const string ReaderPositionLabel = " | LineNumber: ";
    private const string ReaderMissingDiscriminatorText = "must specify a type discriminator";

    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> ConfigurationPropertyCache = new();

    // A JsonObject with a duplicate key parses and then throws on the first write, so the duplicate is rejected while parsing.
    private static readonly JsonDocumentOptions StrictJsonOptions = new() { AllowDuplicateProperties = false };

    // The top-level keys of the configuration last applied to a subject. See CanUpdateInPlace.
    private readonly ConditionalWeakTable<IInterceptorSubject, string[]> _appliedConfigurationKeys = new();

    // The configuration text last applied to a subject. See CreateOrUpdateAsync.
    private readonly ConditionalWeakTable<IInterceptorSubject, string> _appliedConfigurationJson = new();

    // The text of the block last applied to a subject in full, with the subjects of the blocks inside it. See
    // TryKeepUnchangedBlock.
    private readonly ConditionalWeakTable<IInterceptorSubject, AppliedBlock> _appliedBlocks = new();

    /// <summary>
    /// Creates the subject of a block from its configuration, or updates <paramref name="existing"/> in place.
    /// The error is set when a value of the configuration does not fit its property. The block is an error
    /// then, and the subject is not used: a new one is released by the caller, an existing one leaves the page.
    /// </summary>
    private async Task<(IInterceptorSubject? Subject, string? Error)> CreateOrUpdateAsync(
        Type type,
        IInterceptorSubject? existing,
        BlockConfiguration configuration,
        ParseState state,
        CancellationToken cancellationToken)
    {
        // The serializer says which values it could not convert while it applies the configuration. Converting
        // them beforehand to find that out would create the subjects inside a value a second time.
        SkippedConfigurationProperty? skipped = null;
        void RememberFirstSkipped(SkippedConfigurationProperty property) => skipped ??= property;

        IInterceptorSubject? subject;
        if (CanUpdateInPlace(existing, type, configuration.Keys))
        {
            // A block whose configuration did not change keeps the values of its subject. They can hold a change
            // that is about to be saved, which the reload inside the save of another block must not reset.
            if (_appliedConfigurationJson.TryGetValue(existing, out var appliedJson) && appliedJson == configuration.Json)
            {
                return (existing, null);
            }

            // Without the execution flow, as for the creation below: it can attach nested subjects.
            using (ExecutionContext.SuppressFlow())
            {
                _serializer.UpdateConfiguration(
                    existing, configuration.Json, RememberFirstSkipped, ConfigurationNaming.CSharpProperty);
            }

            if (skipped is null && existing is IConfigurable configurable)
            {
                await configurable.ApplyConfigurationAsync(cancellationToken);
            }

            subject = existing;
        }
        else
        {
            // The subject enters the context here, which runs lifecycle handlers. A page is parsed on the worker
            // of its storage, and work that a handler starts with the flow of that work could not call the
            // storage while it runs.
            using (ExecutionContext.SuppressFlow())
            {
                // Recorded before the configuration is set: when a setter throws, the instance is already attached
                // and this parse is the only one that knows it.
                subject = _serializer.Deserialize(
                    configuration.Json, state.AddCreated, RememberFirstSkipped, ConfigurationNaming.CSharpProperty) as IInterceptorSubject;
            }
        }

        if (skipped is not null)
        {
            return (null, FormatSkippedProperty(skipped));
        }

        if (subject is not null)
        {
            _appliedConfigurationKeys.AddOrUpdate(subject, configuration.Keys);
            _appliedConfigurationJson.AddOrUpdate(subject, configuration.Json);
        }

        return (subject, null);
    }

    /// <summary>
    /// Records the configuration that was written to the page for a subject, so that the reload
    /// which follows the write updates the subject in place. The JSON must have the naming of a page,
    /// <see cref="ConfigurationNaming.CSharpProperty"/>, because its keys are compared with those of the page.
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

    /// <summary>
    /// Makes the next parse apply the configuration of the block of the subject also when its text did not change.
    /// </summary>
    internal void ForgetAppliedConfiguration(IInterceptorSubject subject)
    {
        _appliedConfigurationJson.Remove(subject);
        _appliedBlocks.Remove(subject);
    }

    /// <summary>
    /// Makes the next parse apply the block of the subject in full also when its text is the one last applied, because
    /// a write-back changed that text in the file.
    /// </summary>
    internal void ForgetAppliedBlock(IInterceptorSubject subject) => _appliedBlocks.Remove(subject);

    /// <summary>
    /// Keeps the subject of a block whose text did not change since it was last applied in full, with its values,
    /// its slot values and the subjects of the blocks inside it, and registers them for the blocks of this parse.
    /// </summary>
    /// <remarks>
    /// The values in memory can hold a change that is about to be saved, such as a value added to a slot, which
    /// the reload inside the save of another block must not reset. A slot value without a block stays one, so
    /// the save of its owner inserts its block.
    /// </remarks>
    private bool TryKeepUnchangedBlock(IInterceptorSubject existing, MarkdownBlock block, ParseState state)
    {
        if (!_appliedBlocks.TryGetValue(existing, out var applied) ||
            !state.TryGetBlockSpan(block, out var start, out var length) ||
            !state.Source.AsSpan(start, length).SequenceEqual(applied.Text))
        {
            return false;
        }

        // The text inside the block is the same, so its blocks follow it at the same distance. Their names can
        // have been taken by a block before them since then, which makes them error boxes.
        foreach (var (offset, _) in applied.InnerSubjects)
        {
            var index = block.Index + offset;
            if (index >= state.Blocks.Count || !state.OwnsName(state.Blocks[index]))
            {
                return false;
            }
        }

        foreach (var (offset, subject) in applied.InnerSubjects)
        {
            var innerBlock = state.Blocks[block.Index + offset];
            state.Register(innerBlock, innerBlock.Head!.Id, subject);
        }

        state.Register(block, block.Head!.Id, existing);
        return true;
    }

    // Only a block without an error box inside is remembered: an error can come from outside the block, such as a
    // name that another block took, and must go away when that changes.
    private void RememberAppliedBlock(IInterceptorSubject subject, MarkdownBlock block, ParseState state, int registrationCount)
    {
        if (!state.TryGetBlockSpan(block, out var start, out var length))
        {
            _appliedBlocks.Remove(subject);
            return;
        }

        var innerSubjects = new List<(int Offset, IInterceptorSubject Subject)>();
        foreach (var (blockIndex, innerSubject) in state.GetRegistrationsSince(registrationCount))
        {
            if (blockIndex != block.Index)
            {
                innerSubjects.Add((blockIndex - block.Index, innerSubject));
            }
        }

        _appliedBlocks.AddOrUpdate(subject, new AppliedBlock(state.Source, start, length, innerSubjects.ToArray()));
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
            if (node is null ||
                !_serializer.CanDeserialize(node.ToJsonString(), propertyType, ConfigurationNaming.CSharpProperty))
            {
                error = FormatInvalidValue(argument.Value, argument.Key, propertyType);
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

        List<string>? emptySlotLists = null;
        foreach (var (key, value) in configuration)
        {
            if (key == TypeDiscriminator || properties.ContainsKey(key))
            {
                continue;
            }

            // Earlier versions wrote an empty list for the slot of a layout into every page they saved. It holds
            // nothing, and the next save of the block leaves it out.
            if (value is JsonArray { Count: 0 } && SlotMap.For(type).FindByPropertyName(key) is { IsList: true })
            {
                (emptySlotLists ??= []).Add(key);
                continue;
            }

            error = FormatUnknownProperty(type, key, properties);
            return null;
        }

        if (emptySlotLists is not null)
        {
            foreach (var key in emptySlotLists)
            {
                configuration.Remove(key);
            }
        }

        return configuration;
    }

    private static string FormatUnknownProperty(Type type, string key, IReadOnlyDictionary<string, Type> properties)
    {
        if (SlotMap.For(type).FindByPropertyNameIgnoringCase(key) is { } slot)
        {
            if (slot.IsDefault)
            {
                return $"'{slot.PropertyName}' is the content of the block on a page, so it cannot be set as configuration.";
            }

            return slot.IsList
                ? $"'{slot.PropertyName}' is written as blocks on a page: one '{slot.Name}' block per value inside this block."
                : $"'{slot.PropertyName}' is written as a block on a page: a '{slot.Name}' block inside this block.";
        }

        if (properties.Count == 0)
        {
            return $"{type.Name} has no configuration properties, so '{key}' cannot be set.";
        }

        var names = properties.Keys.Order(StringComparer.Ordinal).ToArray();
        var sameNameInOtherCase = Array.Find(names, name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
        var hint = sameNameInOtherCase is null ? string.Empty : $" Did you mean '{sameNameInOtherCase}'?";

        return $"{type.Name} has no configuration property '{key}'.{hint} Available: {string.Join(", ", names)}.";
    }

    private static string FormatInvalidValue(string value, string key, Type propertyType)
    {
        var expectedType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return $"'{value}' is not a valid value for '{key}'. Expected a value of type {expectedType.Name}.";
    }

    /// <summary>
    /// Gets the message for a value of the json block that the serializer could not convert. An argument of
    /// the head line never gets here, because it is checked before the configuration is applied.
    /// </summary>
    private static string FormatSkippedProperty(SkippedConfigurationProperty property)
    {
        using var document = JsonDocument.Parse(property.Json);
        var value = document.RootElement;

        // A single value for a property that takes one is the mistake an argument can have as well, so it
        // gets the message of an argument.
        var expectedType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) &&
            (expectedType.IsValueType || expectedType == typeof(string)))
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : property.Json;
            return FormatInvalidValue(text, property.PropertyName, property.PropertyType);
        }

        // The reader of the JSON appends where it stopped: a path from the value on and a position in JSON
        // text that is not the text of the page. The path is kept, counted from the property.
        var message = property.Exception.Message;
        var reason = message;
        var path = (property.Exception as JsonException)?.Path;
        var tailStart = message.LastIndexOf(ReaderPathLabel, StringComparison.Ordinal);
        if (tailStart >= 0)
        {
            reason = message[..tailStart];

            // Only a JsonException has the path as a property.
            var pathStart = tailStart + ReaderPathLabel.Length;
            var pathEnd = message.IndexOf(ReaderPositionLabel, pathStart, StringComparison.Ordinal);
            if (path is null && pathEnd >= 0)
            {
                path = message[pathStart..pathEnd];
            }
        }

        // The reader has no exception type of its own for this, and its message names an interface. It
        // says the same when the type is there but is not the first name of the subject.
        if (property.Exception is NotSupportedException && reason.Contains(ReaderMissingDiscriminatorText, StringComparison.Ordinal))
        {
            reason = $"A subject in the json block needs a '{TypeDiscriminator}' with its full type name, as the first name in its braces.";
        }

        var place = path is { Length: > 1 } && path[0] == '$'
            ? $" at {property.PropertyName}{path[1..]}"
            : string.Empty;

        return $"The value of '{property.PropertyName}' in the json block cannot be read{place}: {reason}";
    }

    // A slot is filled by the content of the block, also when the same property is configuration in a file.
    private static IReadOnlyDictionary<string, Type> GetConfigurationProperties(Type type) =>
        ConfigurationPropertyCache.GetOrAdd(type, static key =>
        {
            var slots = SlotMap.For(key);
            return key
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true) is not null &&
                                   slots.FindByPropertyName(property.Name) is null)
                .ToDictionary(property => property.Name, property => property.PropertyType);
        });

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

    /// <summary>
    /// Where the text of a block is in the text of the page it was parsed from, which all blocks of that parse share
    /// instead of a copy each, and the subjects of the blocks inside it by their distance from the block.
    /// </summary>
    private sealed record AppliedBlock(string Source, int Start, int Length, (int Offset, IInterceptorSubject Subject)[] InnerSubjects)
    {
        public ReadOnlySpan<char> Text => Source.AsSpan(Start, Length);
    }

    /// <summary>The merged configuration of one block and its top-level keys without the type discriminator.</summary>
    private readonly record struct BlockConfiguration(string Json, string[] Keys);
}
