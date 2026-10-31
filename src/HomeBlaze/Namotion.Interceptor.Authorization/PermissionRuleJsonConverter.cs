using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Authorization;

/// <summary>
/// Reads and writes a <see cref="PermissionRule"/> as an object with the string arrays <c>paths</c>, <c>types</c>,
/// <c>members</c> and <c>tags</c>, written only when not empty, and the array of action names <c>actions</c>, always
/// written. The names do not follow the naming policy of the options; reading compares them ignoring case, reads a
/// missing selector as empty and rejects unknown or repeated properties.
/// </summary>
public sealed class PermissionRuleJsonConverter : JsonConverter<PermissionRule>
{
    // Longer than every known name even when fully escaped; a longer name is unknown and read as a string for the message.
    private const int MaxStackNameLength = 64;

    private static readonly PermissionActionJsonConverter ActionConverter = new();

    private static readonly JsonEncodedText PathsName = JsonEncodedText.Encode("paths");
    private static readonly JsonEncodedText TypesName = JsonEncodedText.Encode("types");
    private static readonly JsonEncodedText MembersName = JsonEncodedText.Encode("members");
    private static readonly JsonEncodedText TagsName = JsonEncodedText.Encode("tags");
    private static readonly JsonEncodedText ActionsName = JsonEncodedText.Encode("actions");

    public override PermissionRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A permission rule is written as an object.");
        }

        var values = new RuleValues();
        var seen = Property.None;
        Span<char> nameBuffer = stackalloc char[MaxStackNameLength];
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = ReadPropertyName(ref reader, nameBuffer, ref seen);
            reader.Read();
            values.Read(ref reader, property, options);
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("A permission rule is written as an object.");
        }

        return new PermissionRule { Paths = values.Paths, Types = values.Types, Members = values.Members, Tags = values.Tags, Actions = values.Actions };
    }

    public override void Write(Utf8JsonWriter writer, PermissionRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteStrings(writer, PathsName, value.Paths);
        WriteStrings(writer, TypesName, value.Types);
        WriteStrings(writer, MembersName, value.Members);
        WriteStrings(writer, TagsName, value.Tags);

        writer.WriteStartArray(ActionsName);
        if (!value.Actions.IsDefault)
        {
            foreach (var action in value.Actions)
            {
                ActionConverter.Write(writer, action, options);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static Property ReadPropertyName(ref Utf8JsonReader reader, scoped Span<char> nameBuffer, ref Property seen)
    {
        // The unescaped name has at most as many characters as its raw text has bytes, so it fits the buffer.
        var rawLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        ReadOnlySpan<char> name = rawLength <= nameBuffer.Length
            ? nameBuffer[..reader.CopyString(nameBuffer)]
            : reader.GetString();

        var property = GetProperty(name);
        if (property == Property.None)
        {
            throw new JsonException($"A permission rule has no property '{name}'; expected paths, types, members, tags or actions.");
        }

        if ((seen & property) != 0)
        {
            throw new JsonException($"A permission rule has the property '{name}' more than once.");
        }

        seen |= property;
        return property;
    }

    private static Property GetProperty(ReadOnlySpan<char> name)
    {
        if (name.Equals("paths", StringComparison.OrdinalIgnoreCase)) return Property.Paths;
        if (name.Equals("types", StringComparison.OrdinalIgnoreCase)) return Property.Types;
        if (name.Equals("members", StringComparison.OrdinalIgnoreCase)) return Property.Members;
        if (name.Equals("tags", StringComparison.OrdinalIgnoreCase)) return Property.Tags;
        if (name.Equals("actions", StringComparison.OrdinalIgnoreCase)) return Property.Actions;
        return Property.None;
    }

    private static void WriteStrings(Utf8JsonWriter writer, JsonEncodedText propertyName, ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new JsonException($"A selector value in '{propertyName}' of a permission rule is null.");
            }

            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private struct RuleValues
    {
        public ImmutableArray<string> Paths = [];
        public ImmutableArray<string> Types = [];
        public ImmutableArray<string> Members = [];
        public ImmutableArray<string> Tags = [];
        public ImmutableArray<PermissionAction> Actions = [];

        public RuleValues()
        {
        }

        public void Read(ref Utf8JsonReader reader, Property property, JsonSerializerOptions options)
        {
            switch (property)
            {
                case Property.Paths: Paths = ReadStrings(ref reader, "paths"); break;
                case Property.Types: Types = ReadStrings(ref reader, "types"); break;
                case Property.Members: Members = ReadStrings(ref reader, "members"); break;
                case Property.Tags: Tags = ReadStrings(ref reader, "tags"); break;
                default: Actions = ReadActions(ref reader, options); break;
            }
        }

        private static ImmutableArray<string> ReadStrings(ref Utf8JsonReader reader, string propertyName)
        {
            var count = CountItems(ref reader, propertyName);
            if (count == 0)
            {
                reader.Read();
                return [];
            }

            var items = new string[count];
            for (var index = 0; index < count; index++)
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException($"The '{propertyName}' of a permission rule is an array of strings.");
                }

                items[index] = reader.GetString()!;
            }

            reader.Read();
            return ImmutableCollectionsMarshal.AsImmutableArray(items);
        }

        private static ImmutableArray<PermissionAction> ReadActions(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            var count = CountItems(ref reader, "actions");
            if (count == 0)
            {
                reader.Read();
                return [];
            }

            var items = new PermissionAction[count];
            for (var index = 0; index < count; index++)
            {
                reader.Read();
                items[index] = ActionConverter.Read(ref reader, typeof(PermissionAction), options);
            }

            reader.Read();
            return ImmutableCollectionsMarshal.AsImmutableArray(items);
        }

        /// <summary>Counts the items of the array at the reader without moving it; a converter is given the whole value, so a copy can read ahead.</summary>
        private static int CountItems(ref Utf8JsonReader reader, string propertyName)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"The '{propertyName}' of a permission rule is an array, not {reader.TokenType}.");
            }

            var lookahead = reader;
            var count = 0;
            while (lookahead.Read() && lookahead.TokenType != JsonTokenType.EndArray)
            {
                lookahead.Skip();
                count++;
            }

            return count;
        }
    }

    [Flags]
    private enum Property
    {
        None = 0,
        Paths = 1,
        Types = 2,
        Members = 4,
        Tags = 8,
        Actions = 16
    }
}
