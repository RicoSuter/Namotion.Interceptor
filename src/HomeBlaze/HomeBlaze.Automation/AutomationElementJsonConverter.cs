using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Services;

namespace HomeBlaze.Automation;

/// <summary>
/// Reads and writes a trigger or an action of a rule with its <c>$type</c> discriminator. An element that cannot be read,
/// such as one with an unknown discriminator, a key that names no setting of its type or a value of the wrong type, is read
/// as a placeholder that holds the element as it was written and the reason, so that the other elements still load,
/// the rule reports the element as a configuration error and saving writes it back unchanged.
/// </summary>
internal abstract class AutomationElementJsonConverter<T> : JsonConverter<T>
    where T : class
{
    private const string Discriminator = ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName;

    private readonly FrozenDictionary<string, Type> _typesByName;
    private readonly FrozenDictionary<Type, string> _namesByType;

    protected AutomationElementJsonConverter(params (string Name, Type Type)[] types)
    {
        _typesByName = types.ToFrozenDictionary(type => type.Name, type => type.Type, StringComparer.Ordinal);
        _namesByType = types.ToFrozenDictionary(type => type.Type, type => type.Name);
    }

    /// <summary>Creates the placeholder of an element that cannot be read.</summary>
    protected abstract T CreateUnreadable(JsonElement json, string error);

    /// <summary>Gets the element as it was written when the value is a placeholder.</summary>
    protected abstract bool TryGetUnreadable(T value, out JsonElement json);

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var element = JsonElement.ParseValue(ref reader);
        return TryRead(element, options, out var value, out var error) ? value : CreateUnreadable(element, error);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (TryGetUnreadable(value, out var json))
        {
            json.WriteTo(writer);
            return;
        }

        var type = value.GetType();
        if (!_namesByType.TryGetValue(type, out var name))
        {
            throw new NotSupportedException($"The type {type.FullName} has no discriminator.");
        }

        writer.WriteStartObject();
        writer.WriteString(Discriminator, name);
        using (var document = JsonSerializer.SerializeToDocument(value, options.GetTypeInfo(type)))
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private bool TryRead(JsonElement element, JsonSerializerOptions options, out T? value, out string error)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(Discriminator, out var discriminator) ||
            discriminator.ValueKind != JsonValueKind.String)
        {
            error = $"it has no {Discriminator}";
            return false;
        }

        var name = discriminator.GetString()!;
        if (!_typesByName.TryGetValue(name, out var type))
        {
            error = $"the type '{name}' is unknown";
            return false;
        }

        var typeInfo = options.GetTypeInfo(type);
        var comparison = options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name != Discriminator &&
                !typeInfo.Properties.Any(member => string.Equals(member.Name, property.Name, comparison)))
            {
                error = $"'{property.Name}' is no setting of {name}";
                return false;
            }
        }

        try
        {
            value = (T?)element.Deserialize(typeInfo);
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = Describe(exception);
            return false;
        }
    }

    // The position the reader appends is within the element and not within the file, so only the key is kept.
    private static string Describe(JsonException exception)
    {
        var message = exception.Message;
        var positionStart = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (positionStart < 0)
        {
            return message;
        }

        message = message[..positionStart];
        return exception.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal)
            ? $"'{path[2..]}': {message}"
            : message;
    }
}

/// <summary>Reads and writes the triggers of a rule, see <see cref="AutomationElementJsonConverter{T}"/>.</summary>
internal sealed class AutomationTriggerJsonConverter() : AutomationElementJsonConverter<AutomationTrigger>(
    ("PropertyTrigger", typeof(PropertyTrigger)),
    ("SubjectEventTrigger", typeof(SubjectEventTrigger)))
{
    protected override AutomationTrigger CreateUnreadable(JsonElement json, string error) => new UnreadableTrigger(json, error);

    protected override bool TryGetUnreadable(AutomationTrigger value, out JsonElement json)
    {
        if (value is UnreadableTrigger unreadable)
        {
            json = unreadable.Json;
            return true;
        }

        json = default;
        return false;
    }
}

/// <summary>Reads and writes the actions of a rule, see <see cref="AutomationElementJsonConverter{T}"/>.</summary>
internal sealed class AutomationActionJsonConverter() : AutomationElementJsonConverter<AutomationAction>(
    ("InvokeOperation", typeof(InvokeOperationAction)),
    ("SetProperty", typeof(SetPropertyAction)),
    ("ChangeState", typeof(ChangeStateAction)))
{
    protected override AutomationAction CreateUnreadable(JsonElement json, string error) => new UnreadableAction(json, error);

    protected override bool TryGetUnreadable(AutomationAction value, out JsonElement json)
    {
        if (value is UnreadableAction unreadable)
        {
            json = unreadable.Json;
            return true;
        }

        json = default;
        return false;
    }
}
