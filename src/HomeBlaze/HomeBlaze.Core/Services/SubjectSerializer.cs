using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Serializes and deserializes InterceptorSubject instances to/from JSON.
/// Uses "Type" discriminator for polymorphic deserialization.
/// Only serializes properties marked with [Configuration].
/// </summary>
public class SubjectSerializer
{
    private const string TypeDiscriminator = "Type";

    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly IServiceProvider? _serviceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    public SubjectSerializer(SubjectTypeRegistry typeRegistry, IServiceProvider? serviceProvider = null)
    {
        _typeRegistry = typeRegistry;
        _serviceProvider = serviceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Deserializes a JSON string to an InterceptorSubject.
    /// </summary>
    public IInterceptorSubject? Deserialize(string json, IInterceptorSubjectContext context)
    {
        using var document = JsonDocument.Parse(json);
        return DeserializeElement(document.RootElement, context);
    }

    /// <summary>
    /// Deserializes a JSON element to an InterceptorSubject.
    /// </summary>
    public IInterceptorSubject? DeserializeElement(JsonElement element, IInterceptorSubjectContext context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Get the type discriminator
        if (!element.TryGetProperty(TypeDiscriminator, out var typeElement) &&
            !element.TryGetProperty("type", out typeElement))
        {
            return null;
        }

        var typeName = typeElement.GetString();
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var type = _typeRegistry.ResolveType(typeName);
        if (type == null)
            throw new InvalidOperationException($"Unknown type: {typeName}. Make sure it's registered in SubjectTypeRegistry.");

        // Create instance
        var subject = CreateInstance(type, context);
        if (subject == null)
            return null;

        // Populate properties
        PopulateProperties(subject, element, context);

        return subject;
    }

    /// <summary>
    /// Serializes an InterceptorSubject to JSON.
    /// Only includes the Type discriminator and [Configuration] properties.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        SerializeSubject(writer, subject);

        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private void SerializeSubject(Utf8JsonWriter writer, IInterceptorSubject subject)
    {
        var type = subject.GetType();

        writer.WriteStartObject();

        // Write type discriminator
        writer.WriteString(TypeDiscriminator, type.FullName);

        // Write [Configuration] properties
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<ConfigurationAttribute>() == null)
                continue;

            if (!property.CanRead)
                continue;

            var value = property.GetValue(subject);
            var propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);

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

    private IInterceptorSubject? CreateInstance(Type type, IInterceptorSubjectContext context)
    {
        // Try constructor with context parameter first
        var ctorWithContext = type.GetConstructor(new[] { typeof(IInterceptorSubjectContext) });
        if (ctorWithContext != null)
        {
            return (IInterceptorSubject)ctorWithContext.Invoke(new object[] { context });
        }

        // Try using service provider for DI (supports complex constructors)
        if (_serviceProvider != null)
        {
            try
            {
                var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
                if (instance is IInterceptorSubject subject)
                {
                    // Connect to the main context via fallback
                    subject.Context.AddFallbackContext(context);
                    return subject;
                }
            }
            catch
            {
                // Fall through to other options
            }
        }

        // Try parameterless constructor
        var parameterlessCtor = type.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor != null)
        {
            var instance = (IInterceptorSubject)parameterlessCtor.Invoke(null);
            // Connect to the main context via fallback
            instance.Context.AddFallbackContext(context);
            return instance;
        }

        throw new InvalidOperationException(
            $"Type {type.FullName} must have a constructor that accepts IInterceptorSubjectContext or a parameterless constructor, " +
            $"or all its constructor dependencies must be available in the service provider.");
    }

    private void PopulateProperties(IInterceptorSubject subject, JsonElement element, IInterceptorSubjectContext context)
    {
        var type = subject.GetType();

        foreach (var jsonProperty in element.EnumerateObject())
        {
            if (jsonProperty.Name.Equals(TypeDiscriminator, StringComparison.OrdinalIgnoreCase))
                continue;

            // Find matching property (case-insensitive)
            var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.Equals(jsonProperty.Name, StringComparison.OrdinalIgnoreCase));

            if (property == null || !property.CanWrite)
                continue;

            try
            {
                object? value;

                if (typeof(IInterceptorSubject).IsAssignableFrom(property.PropertyType))
                {
                    // Nested subject
                    value = DeserializeElement(jsonProperty.Value, context);
                }
                else
                {
                    // Regular value
                    value = JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), property.PropertyType, _jsonOptions);
                }

                property.SetValue(subject, value);
            }
            catch (Exception)
            {
                // Skip properties that can't be set
            }
        }
    }
}
