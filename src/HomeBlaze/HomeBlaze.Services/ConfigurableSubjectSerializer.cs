using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Serialization;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Serializes and deserializes IConfigurableSubject instances to/from JSON.
/// Uses "type" discriminator for polymorphic deserialization.
/// Only serializes properties marked with [Configuration].
/// </summary>
public class ConfigurableSubjectSerializer
{
    private const string TypeDiscriminator = "type";

    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurableSubjectSerializer(SubjectTypeRegistry typeRegistry, IServiceProvider serviceProvider)
    {
        _typeRegistry = typeRegistry;
        _serviceProvider = serviceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Deserializes a JSON string to an InterceptorSubject.
    /// </summary>
    public IInterceptorSubject? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DeserializeElement(document.RootElement);
    }

    /// <summary>
    /// Deserializes a JSON element to an InterceptorSubject.
    /// </summary>
    public IInterceptorSubject? DeserializeElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(TypeDiscriminator, out var typeElement))
        {
            return null;
        }

        var typeName = typeElement.GetString();

        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var type = _typeRegistry.ResolveType(typeName);
        if (type == null)
            throw new InvalidOperationException($"Unknown type: {typeName}. Make sure it's registered in SubjectTypeRegistry.");

        if (!typeof(IConfigurableSubject).IsAssignableFrom(type))
            return null;

        var subject = CreateInstance(type);
        if (subject == null)
            return null;

        PopulateProperties(subject, element);

        return subject;
    }

    /// <summary>
    /// Serializes an InterceptorSubject to JSON.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        SerializeSubject(writer, subject);

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Updates a subject's configuration properties from JSON.
    /// </summary>
    public void UpdateConfiguration(IInterceptorSubject subject, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var property in subject.GetConfigurationProperties())
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                try
                {
                    var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.Type);
                    property.SetValue(value);
                }
                catch
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }

    private void SerializeSubject(Utf8JsonWriter writer, IInterceptorSubject subject)
    {
        var type = subject.GetType();

        writer.WriteStartObject();
        writer.WriteString(TypeDiscriminator, type.FullName);

        foreach (var registeredProperty in subject.GetConfigurationProperties())
        {
            var value = registeredProperty.GetValue();
            var propertyName = JsonNamingPolicy.CamelCase.ConvertName(registeredProperty.Name);

            if (value == null)
            {
                writer.WriteNull(propertyName);
            }
            else if (value is IInterceptorSubject nestedSubject)
            {
                writer.WritePropertyName(propertyName);
                SerializeSubject(writer, nestedSubject);
            }
            else
            {
                writer.WritePropertyName(propertyName);
                JsonSerializer.Serialize(writer, value, value.GetType(), _jsonOptions);
            }
        }

        writer.WriteEndObject();
    }

    private IInterceptorSubject? CreateInstance(Type type)
    {
        var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
        if (instance is IInterceptorSubject subject)
        {
            return subject;
        }

        throw new InvalidOperationException(
            $"Type {type.FullName} must implement IInterceptorSubject.");
    }

    private void PopulateProperties(IInterceptorSubject subject, JsonElement element)
    {
        var subjectType = subject.GetType();
        var properties = subjectType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var jsonProperty in element.EnumerateObject())
        {
            if (jsonProperty.Name == TypeDiscriminator)
                continue;

            var propertyInfo = properties
                .FirstOrDefault(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == jsonProperty.Name &&
                                     p.GetCustomAttribute<ConfigurationAttribute>() is not null);

            if (propertyInfo != null && propertyInfo.CanWrite)
            {
                try
                {
                    object? value;

                    if (typeof(IInterceptorSubject).IsAssignableFrom(propertyInfo.PropertyType))
                    {
                        value = DeserializeElement(jsonProperty.Value);
                    }
                    else
                    {
                        value = JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), propertyInfo.PropertyType, _jsonOptions);
                    }

                    propertyInfo.SetValue(subject, value);
                }
                catch
                {
                    // Skip properties that can't be set
                }
            }
        }
    }
}
