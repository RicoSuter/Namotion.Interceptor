using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services;

/// <summary>
/// Serializes and deserializes IConfigurable instances to/from JSON.
/// Uses "$type" discriminator for polymorphic serialization via System.Text.Json.
/// Only serializes properties marked with [Configuration].
/// Uses ActivatorUtilities for DI-aware construction during deserialization.
/// </summary>
public class ConfigurableSubjectSerializer
{
    /// <summary>
    /// Name of the JSON property that holds the full type name of a serialized subject.
    /// </summary>
    public const string TypeDiscriminatorPropertyName = "$type";

    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _options;

    public ConfigurableSubjectSerializer(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        _serviceProvider = serviceProvider;
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider, serviceProvider),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Serializes an IConfigurable to JSON with $type discriminator.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
    {
        if (subject is not IConfigurable configurableSubject)
        {
            throw new ArgumentException(
                $"Subject must implement IConfigurable. Type: {subject.GetType().FullName}",
                nameof(subject));
        }

        return JsonSerializer.Serialize(configurableSubject, typeof(IConfigurable), _options);
    }

    /// <summary>
    /// Deserializes JSON to an IConfigurable using $type discriminator.
    /// Uses ActivatorUtilities for DI-aware construction.
    /// </summary>
    public IConfigurable? Deserialize(string json) => Deserialize(json, created: null);

    /// <summary>
    /// Deserializes like <see cref="Deserialize(string)"/> and hands a new subject to
    /// <paramref name="created"/> before its configuration properties are set.
    /// </summary>
    /// <remarks>
    /// The subject is attached to the context from its constructor on. When setting a configuration property
    /// throws, this method throws and the callback is the only way to get the subject, which
    /// <see cref="Release"/> then detaches. Call <see cref="Release"/> only after this method has returned or thrown.
    /// </remarks>
    public IConfigurable? Deserialize(string json, Action<IInterceptorSubject>? created)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Extract $type discriminator
        if (!root.TryGetProperty(TypeDiscriminatorPropertyName, out var typeElement))
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

        using var startup = _serviceProvider.GetService<IInterceptorSubjectContext>()?.DeferHostedServiceStarts();
        // Create instance using ActivatorUtilities for DI-aware construction
        var subject = ActivatorUtilities.CreateInstance(_serviceProvider, type) as IConfigurable;
        if (subject == null)
        {
            return null;
        }

        if (created is not null && subject is IInterceptorSubject interceptorSubject)
        {
            created(interceptorSubject);
        }

        PopulateConfigurationProperties(subject, type, root);
        return subject;
    }

    /// <summary>
    /// Releases a subject that <see cref="Deserialize(string)"/> created and that is not used after all: detaches it
    /// and the subjects in its properties from the context, which stops their hosted services. A hosted service
    /// that was not started yet is stopped without having been started.
    /// </summary>
    /// <remarks>
    /// Does nothing for a subject that a property references. Such a subject is in the graph and leaves the
    /// context when the reference is removed.
    /// </remarks>
    public void Release(IInterceptorSubject subject)
    {
        if (subject.GetReferenceCount() > 0)
        {
            return;
        }

        // The context the constructor got from the service provider is a fallback context of the subject.
        // Removing it is what detaches a subject that no property holds.
        if (_serviceProvider.GetService<IInterceptorSubjectContext>() is { } context)
        {
            subject.Context.RemoveFallbackContext(context);
        }
    }

    /// <summary>
    /// Populates [Configuration] properties on a newly created subject.
    /// Uses registry attribute lookup when available, falls back to reflection.
    /// </summary>
    private void PopulateConfigurationProperties(IConfigurable subject, Type type, JsonElement root)
    {
        if (subject is IInterceptorSubject interceptorSubject)
        {
            var registered = interceptorSubject.TryGetRegisteredSubject();
            if (registered != null)
            {
                foreach (var property in registered.Properties)
                {
                    if (property.TryGetAttribute(KnownAttributes.Configuration) == null)
                        continue;

                    var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    if (root.TryGetProperty(jsonName, out var jsonValue))
                    {
                        try
                        {
                            var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.Type, _options);
                            property.SetValue(value);
                        }
                        catch (JsonException)
                        {
                            // Skip properties that can't be deserialized
                        }
                    }
                }
                return;
            }
        }

        // Fallback to reflection for subjects without registry context
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var hasConfigAttribute = property.GetCustomAttributes(typeof(ConfigurationAttribute), true).Length > 0;
            if (!hasConfigAttribute || !property.CanWrite)
                continue;

            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                try
                {
                    var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.PropertyType, _options);
                    property.SetValue(subject, value);
                }
                catch (JsonException)
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }

    /// <summary>
    /// Checks whether a JSON value converts to the given property type with the options that
    /// <see cref="Deserialize(string)"/> and <see cref="UpdateConfiguration"/> use.
    /// </summary>
    /// <remarks>
    /// Both methods skip a property whose value does not convert without reporting it.
    /// </remarks>
    public bool CanDeserialize(string json, Type propertyType)
    {
        try
        {
            JsonSerializer.Deserialize(json, propertyType, _options);
            return true;
        }
        catch (JsonException)
        {
            return false;
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
                catch (JsonException)
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }
}
