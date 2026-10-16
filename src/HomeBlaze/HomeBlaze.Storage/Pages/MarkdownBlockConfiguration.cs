using System.Buffers;
using System.Text;
using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Pages;

/// <summary>The configuration of a subject as its block on a page carries it.</summary>
public static class MarkdownBlockConfiguration
{
    // Indented like the serializer writes it.
    private static readonly JsonWriterOptions IndentedWriterOptions = new() { Indented = true };

    /// <summary>
    /// Serializes the configuration of <paramref name="subject"/> for the head line and the json block of its
    /// block: in the naming of a page, without the properties that are slots, because a slot is the content
    /// of the block, and without a value that equals the one of a new subject of its type, unless
    /// <paramref name="block"/> already has the key.
    /// </summary>
    /// <param name="serializer">Serializes the configuration.</param>
    /// <param name="subject">The subject of the block.</param>
    /// <param name="block">The block that the configuration is written to, or null for a new block.</param>
    public static string Serialize(ConfigurableSubjectSerializer serializer, IInterceptorSubject subject, MarkdownBlock? block)
    {
        // A subject that is not registered is a new one from a dialog: its slots are null, which is not
        // written, or empty, which a page ignores.
        var configurationJson = SlotMap.For(subject.GetType()).Named.Count == 0 || subject.TryGetRegisteredSubject() is null
            ? serializer.Serialize(subject, ConfigurationNaming.CSharpProperty)
            : serializer.Serialize(
                subject,
                static property => property.ReflectionAttributes.Any(attribute => attribute is SlotAttribute),
                ConfigurationNaming.CSharpProperty);

        return serializer.GetDefaultConfiguration(subject.GetType(), ConfigurationNaming.CSharpProperty) is { } defaults
            ? OmitDefaults(configurationJson, defaults, GetWrittenKeys(block))
            : configurationJson;
    }

    /// <summary>
    /// Removes every top-level key of <paramref name="configurationJson"/> whose value equals the one in
    /// <paramref name="defaults"/>, except the type discriminator and the keys in <paramref name="keptKeys"/>.
    /// Returns <paramref name="configurationJson"/> itself when nothing is removed.
    /// </summary>
    internal static string OmitDefaults(string configurationJson, JsonElement defaults, IReadOnlySet<string> keptKeys)
    {
        using var document = JsonDocument.Parse(configurationJson);

        var buffer = new ArrayBufferWriter<byte>(configurationJson.Length);
        var isOmitted = false;
        using (var writer = new Utf8JsonWriter(buffer, IndentedWriterOptions))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName &&
                    !keptKeys.Contains(property.Name) &&
                    defaults.TryGetProperty(property.Name, out var defaultValue) &&
                    JsonElement.DeepEquals(property.Value, defaultValue))
                {
                    isOmitted = true;
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return isOmitted ? Encoding.UTF8.GetString(buffer.WrittenSpan) : configurationJson;
    }

    // The keys of the head line and of the json block, which the user may have written on purpose.
    private static HashSet<string> GetWrittenKeys(MarkdownBlock? block)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (block?.Head is not { } head)
        {
            return keys;
        }

        foreach (var argument in head.Arguments)
        {
            keys.Add(argument.Key);
        }

        if (block.ConfigurationJson is { } fence)
        {
            try
            {
                using var document = JsonDocument.Parse(fence);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        keys.Add(property.Name);
                    }
                }
            }
            catch (JsonException)
            {
                // A block whose json block cannot be read has no subject, so nothing is written to it.
            }
        }

        return keys;
    }
}
