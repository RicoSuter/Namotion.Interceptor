using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Serializes and deserializes IConfigurableSubject instances to/from JSON.
/// Uses "$type" discriminator for polymorphic serialization via System.Text.Json.
/// Only serializes properties marked with [Configuration].
/// Uses ActivatorUtilities for DI-aware construction during deserialization.
/// </summary>
public class ConfigurableSubjectSerializer
{
    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _subjectServiceProvider;
    private readonly JsonSerializerOptions _options;

    public ConfigurableSubjectSerializer(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        // Use wrapper to exclude IInterceptorSubjectContext from DI resolution.
        // Context will be attached later via ContextInheritanceHandler.
        _subjectServiceProvider = new SubjectCreationServiceProvider(serviceProvider);
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Serializes an IConfigurableSubject to JSON with $type discriminator.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
    {
        if (subject is not IConfigurableSubject configurableSubject)
        {
            throw new ArgumentException(
                $"Subject must implement IConfigurableSubject. Type: {subject.GetType().FullName}",
                nameof(subject));
        }

        return JsonSerializer.Serialize(configurableSubject, typeof(IConfigurableSubject), _options);
    }

    /// <summary>
    /// Deserializes JSON to an IConfigurableSubject using $type discriminator.
    /// Uses ActivatorUtilities for DI-aware construction.
    /// </summary>
    public IConfigurableSubject? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Extract $type discriminator
        if (!root.TryGetProperty("$type", out var typeElement))
        {
            return null;
        }

        var typeName = typeElement.GetString();
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        // Find the type in registered types
        var type = _typeProvider.Types.FirstOrDefault(t => t.FullName == typeName);
        if (type == null)
        {
            return null;
        }

        // Create instance using ActivatorUtilities for DI-aware construction
        var subject = ActivatorUtilities.CreateInstance(_subjectServiceProvider, type) as IConfigurableSubject;
        if (subject == null)
        {
            return null;
        }

        // Populate configuration properties from JSON using reflection
        // (can't use UpdateConfiguration because subject isn't registered in a context yet)
        PopulateConfigurationProperties(subject, type, root);
        return subject;
    }

    /// <summary>
    /// Populates [Configuration] properties on a newly created subject using reflection.
    /// Used during deserialization before subject is registered in a context.
    /// </summary>
    private void PopulateConfigurationProperties(IConfigurableSubject subject, Type type, JsonElement root)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Check if property has [Configuration] attribute
            // Use GetCustomAttributes for better compatibility with partial properties
            var hasConfigAttribute = property.GetCustomAttributes(typeof(ConfigurationAttribute), true).Length > 0;
            if (!hasConfigAttribute)
                continue;

            // Skip read-only properties
            if (!property.CanWrite)
                continue;

            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                try
                {
                    var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.PropertyType, _options);
                    property.SetValue(subject, value);
                }
                catch
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }

    /// <summary>
    /// Updates a subject's configuration properties from JSON.
    /// Uses same options as Serialize/Deserialize for consistent property handling.
    /// Note: STJ doesn't support populating existing objects, so we deserialize each property individually.
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
                    // Use same _options for consistent deserialization behavior
                    var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.Type, _options);
                    property.SetValue(value);
                }
                catch
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }
}
