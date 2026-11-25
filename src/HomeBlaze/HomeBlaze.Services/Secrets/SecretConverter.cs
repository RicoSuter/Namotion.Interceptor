using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace HomeBlaze.Services.Secrets;

/// <summary>
/// Replaces plaintext secret values in configuration JSON with references to new keys of the <see cref="SecretStore"/>,
/// finding secret properties through the type metadata of the serializer.
/// </summary>
internal sealed class SecretConverter
{
    private readonly SecretStore _store;

    public SecretConverter(SecretStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Replaces the plaintext secrets in <paramref name="root"/>, an object of <paramref name="type"/>, and stores them
    /// with one write. Returns false and changes nothing when there is none.
    /// </summary>
    /// <exception cref="IOException">The store cannot be written. Nothing is stored.</exception>
    /// <exception cref="UnauthorizedAccessException">The store cannot be written. Nothing is stored.</exception>
    /// <exception cref="InvalidOperationException">A secret property is not a string property of a subject.</exception>
    public bool TryConvert(JsonObject root, Type type, JsonSerializerOptions options, ConfigurationNaming naming)
    {
        List<KeyValuePair<string, string>>? values = null;
        VisitProperties(root, type, options.GetTypeInfo(type), options, isRoot: true, naming, ref values);
        if (values is null)
        {
            return false;
        }

        _store.SetMany(values);
        return true;
    }

    private static void VisitValue(
        JsonNode? node, Type type, JsonSerializerOptions options, ConfigurationNaming naming,
        ref List<KeyValuePair<string, string>>? values)
    {
        if (node is null || node is JsonValue)
        {
            return;
        }

        var typeInfo = options.GetTypeInfo(type);

        // A property of a polymorphic type names the type of its value, as the serializer reads it.
        if (node is JsonObject discriminated &&
            typeInfo.PolymorphismOptions is { } polymorphism &&
            discriminated[ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName] is JsonValue discriminator &&
            discriminator.TryGetValue<string>(out var typeName))
        {
            foreach (var derivedType in polymorphism.DerivedTypes)
            {
                if (derivedType.TypeDiscriminator is string name && name == typeName)
                {
                    type = derivedType.DerivedType;
                    typeInfo = options.GetTypeInfo(type);
                    break;
                }
            }
        }

        switch (typeInfo.Kind)
        {
            case JsonTypeInfoKind.Object when node is JsonObject value:
                VisitProperties(value, type, typeInfo, options, isRoot: false, naming, ref values);
                break;
            case JsonTypeInfoKind.Enumerable when node is JsonArray items && typeInfo.ElementType is { } itemType:
                foreach (var item in items)
                {
                    VisitValue(item, itemType, options, naming, ref values);
                }

                break;
            case JsonTypeInfoKind.Dictionary when node is JsonObject entries && typeInfo.ElementType is { } entryType:
                foreach (var (_, entry) in entries)
                {
                    VisitValue(entry, entryType, options, naming, ref values);
                }

                break;
        }
    }

    private static void VisitProperties(
        JsonObject value, Type type, JsonTypeInfo typeInfo, JsonSerializerOptions options, bool isRoot,
        ConfigurationNaming naming, ref List<KeyValuePair<string, string>>? values)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is not PropertyInfo propertyInfo)
            {
                continue;
            }

            // The serializer matches the properties of the root by their declared name.
            var name = isRoot ? ConfigurableSubjectSerializer.GetJsonName(propertyInfo.Name, naming) : property.Name;

            var isSecret = SecretReferences.IsSecret(propertyInfo);
            if (isSecret)
            {
                SecretReferences.EnsureSupported(type, propertyInfo.Name, propertyInfo.PropertyType);
            }

            if (!value.TryGetPropertyValue(name, out var propertyValue) || propertyValue is null)
            {
                continue;
            }

            if (isSecret)
            {
                if (propertyValue is JsonValue text &&
                    text.TryGetValue<string>(out var plaintext) &&
                    plaintext.Length > 0 &&
                    !SecretReferences.IsReference(plaintext))
                {
                    var key = SecretStore.CreateKey();
                    (values ??= []).Add(new(key, plaintext));
                    value[name] = SecretReferences.Prefix + key;
                }

                continue;
            }

            VisitValue(propertyValue, property.PropertyType, options, naming, ref values);
        }
    }
}
